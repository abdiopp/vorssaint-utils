// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Rivet.Core.Diagnostics;
using Rivet.Core.Util;

namespace Rivet.Core.SystemMonitor;

public enum SpeedTestPhase
{
    Idle,
    Latency,
    Download,
    Upload,
    Done,
    Failed,
}

public sealed record SpeedTestState
{
    public SpeedTestPhase Phase { get; init; }

    public double? LatencyMs { get; init; }

    public double? DownloadMbps { get; init; }

    public double? UploadMbps { get; init; }

    /// <summary>Technical reason of a failure (logged; the UI shows "Test failed").</summary>
    public string? Error { get; init; }

    public bool IsRunning => Phase is SpeedTestPhase.Latency or SpeedTestPhase.Download or SpeedTestPhase.Upload;

    public static SpeedTestState Idle { get; } = new();
}

/// <summary>Constants of spec §3.7.5 / §6.9; tests shorten the time boxes.</summary>
public sealed record SpeedTestOptions
{
    public Uri DownloadEndpoint { get; init; } = new("https://speed.cloudflare.com/__down");

    public Uri UploadEndpoint { get; init; } = new("https://speed.cloudflare.com/__up");

    public int LatencyProbes { get; init; } = 5;

    public TimeSpan PhaseDuration { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>90 MB per download request: the endpoint returns almost nothing for 100 MB or more.</summary>
    public long DownloadChunkBytes { get; init; } = 90_000_000;

    public long UploadBytes { get; init; } = 100_000_000;
}

/// <summary>
/// The user-triggered internet speed test against Cloudflare (spec §3.7.5):
/// latency = fastest of 5 sequential empty downloads; download = repeated
/// 90 MB requests, one at a time, for a 5 s time box; upload = one 100 MB
/// POST of zeros for a 5 s time box. Mbps = bytes × 8 / elapsed / 10⁶. No
/// cookies, no cache; nothing about the user is sent. Stale continuations of
/// a cancelled run are ignored (generation counter).
/// </summary>
public sealed class SpeedTest : IDisposable
{
    private readonly HttpClient _client;
    private readonly SpeedTestOptions _options;
    private readonly object _gate = new();
    private int _generation;
    private CancellationTokenSource? _run;
    private SpeedTestState _state = SpeedTestState.Idle;

    public SpeedTest(HttpMessageHandler? handler = null, SpeedTestOptions? options = null)
    {
        _options = options ?? new SpeedTestOptions();
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        }, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public SpeedTestState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised on the UI thread after every state change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>The running test's task (tests await it).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Starts a test; ignored while one runs. Results reset at start.</summary>
    public void Start()
    {
        int generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_state.IsRunning)
            {
                return;
            }

            generation = ++_generation;
            _run?.Dispose();
            _run = new CancellationTokenSource();
            token = _run.Token;
        }

        Set(generation, new SpeedTestState { Phase = SpeedTestPhase.Latency });
        Completion = Task.Run(() => RunAsync(generation, token));
    }

    /// <summary>Stops the test and returns to idle.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _generation++;
            _run?.Cancel();
            _state = SpeedTestState.Idle;
        }

        UiThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    private async Task RunAsync(int generation, CancellationToken cancel)
    {
        try
        {
            var latency = await MeasureLatencyAsync(cancel).ConfigureAwait(false);
            if (!Set(generation, State with { Phase = SpeedTestPhase.Download, LatencyMs = latency }))
            {
                return;
            }

            var download = await MeasureDownloadAsync(cancel).ConfigureAwait(false);
            if (!Set(generation, State with { Phase = SpeedTestPhase.Upload, DownloadMbps = download }))
            {
                return;
            }

            var upload = await MeasureUploadAsync(cancel).ConfigureAwait(false);
            Set(generation, State with { Phase = SpeedTestPhase.Done, UploadMbps = upload });
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled by the user: Cancel() already reset the state.
        }
        catch (Exception ex)
        {
            Log.Info("monitor", $"Speed test failed: {ex.Message}");
            Set(generation, State with { Phase = SpeedTestPhase.Failed, Error = ex.Message });
        }
    }

    private async Task<double> MeasureLatencyAsync(CancellationToken cancel)
    {
        var best = double.MaxValue;
        for (var i = 0; i < _options.LatencyProbes; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(_options.RequestTimeout);
            var stopwatch = Stopwatch.StartNew();
            using var request = Request(HttpMethod.Get, Bytes(0));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            EnsureSuccess(response);
            best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    private async Task<double> MeasureDownloadAsync(CancellationToken cancel)
    {
        // The time box starts before the first request, so it includes connection and TTFB.
        var stopwatch = Stopwatch.StartNew();
        using var timeBox = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeBox.CancelAfter(_options.PhaseDuration);
        long bytes = 0;
        var buffer = new byte[64 * 1024];
        try
        {
            while (!timeBox.IsCancellationRequested)
            {
                using var request = Request(HttpMethod.Get, Bytes(_options.DownloadChunkBytes));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(timeBox.Token);
                timeout.CancelAfter(_options.RequestTimeout);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                EnsureSuccess(response);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    bytes += read;
                }
            }
        }
        catch (OperationCanceledException) when (timeBox.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            // The time box fired: the in-flight request is cancelled, not an error.
        }
        catch (HttpRequestException) when (bytes > 0)
        {
            // A transport error after data arrived ends the phase with what was measured.
        }
        catch (IOException) when (bytes > 0)
        {
        }

        cancel.ThrowIfCancellationRequested();
        var elapsed = Math.Min(stopwatch.Elapsed.TotalSeconds, _options.PhaseDuration.TotalSeconds);
        if (bytes == 0)
        {
            throw new HttpRequestException("No data received.");
        }

        return SpeedTestMath.Mbps(bytes, elapsed);
    }

    private async Task<double> MeasureUploadAsync(CancellationToken cancel)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeBox = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeBox.CancelAfter(_options.PhaseDuration);
        var content = new ZeroContent(_options.UploadBytes);
        double elapsed;
        try
        {
            using var request = Request(HttpMethod.Post, null);
            request.Content = content;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(timeBox.Token);
            timeout.CancelAfter(_options.RequestTimeout);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            elapsed = stopwatch.Elapsed.TotalSeconds;
            EnsureSuccess(response);
        }
        catch (OperationCanceledException) when (timeBox.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            elapsed = _options.PhaseDuration.TotalSeconds;
        }
        catch (HttpRequestException) when (content.Sent > 0)
        {
            elapsed = stopwatch.Elapsed.TotalSeconds;
        }

        cancel.ThrowIfCancellationRequested();
        if (content.Sent == 0)
        {
            throw new HttpRequestException("No data sent.");
        }

        return SpeedTestMath.Mbps(content.Sent, Math.Min(elapsed, _options.PhaseDuration.TotalSeconds));
    }

    private Uri Bytes(long count) => new($"{_options.DownloadEndpoint}?bytes={count}");

    private HttpRequestMessage Request(HttpMethod method, Uri? uri)
    {
        var request = new HttpRequestMessage(method, uri ?? _options.UploadEndpoint)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    /// <summary>A non-2xx status fails the test (never swallowed like a transport error); its body is never counted.</summary>
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new StatusException((int)response.StatusCode);
        }
    }

    /// <summary>Applies a state for <paramref name="generation"/>; false when the run is stale.</summary>
    private bool Set(int generation, SpeedTestState state)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return false;
            }

            _state = state;
        }

        UiThread.Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _generation++;
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
        }

        _client.Dispose();
    }

    private sealed class StatusException(int status) : Exception($"HTTP {status}");

    /// <summary>The upload body: zeros, counting what was handed to the network stack.</summary>
    private sealed class ZeroContent(long length) : HttpContent
    {
        private long _sent;

        public long Sent => Interlocked.Read(ref _sent);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
                Interlocked.Add(ref _sent, count);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length1)
        {
            length1 = length;
            return true;
        }
    }
}

public static class SpeedTestMath
{
    /// <summary>bytes × 8 / elapsed / 1,000,000.</summary>
    public static double Mbps(long bytes, double elapsedSeconds) =>
        elapsedSeconds <= 0 ? 0 : bytes * 8 / elapsedSeconds / 1_000_000;
}
