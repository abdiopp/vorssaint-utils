// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

/// <summary>Speed test contracts of spec §8.4.</summary>
public class SpeedTestTests
{
    private static readonly SpeedTestOptions Fast = new()
    {
        DownloadEndpoint = new Uri("https://speed.test/__down"),
        UploadEndpoint = new Uri("https://speed.test/__up"),
        PhaseDuration = TimeSpan.FromMilliseconds(300),
        RequestTimeout = TimeSpan.FromSeconds(5),
        DownloadChunkBytes = 200_000,
        UploadBytes = 2_000_000,
    };

    [Fact]
    public async Task Success_measures_latency_download_and_upload()
    {
        var handler = new FakeHandler();
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        await test.Completion;
        var state = test.State;
        Assert.Equal(SpeedTestPhase.Done, state.Phase);
        Assert.NotNull(state.LatencyMs);
        Assert.True(state.DownloadMbps > 0);
        Assert.True(state.UploadMbps > 0);
        Assert.Equal(5, handler.Requests.Count(r => r.Contains("bytes=0", StringComparison.Ordinal)));
        Assert.Contains(handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Latency_error_fails_and_requests_nothing_else()
    {
        var handler = new FakeHandler { LatencyStatus = HttpStatusCode.InternalServerError };
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        await test.Completion;
        Assert.Equal(SpeedTestPhase.Failed, test.State.Phase);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Download_404_fails_without_counting_the_error_body()
    {
        var handler = new FakeHandler { DownloadStatus = HttpStatusCode.NotFound };
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        await test.Completion;
        Assert.Equal(SpeedTestPhase.Failed, test.State.Phase);
        Assert.Null(test.State.DownloadMbps);
        Assert.DoesNotContain(handler.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_503_fails_after_a_successful_download()
    {
        var handler = new FakeHandler { UploadStatus = HttpStatusCode.ServiceUnavailable };
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        await test.Completion;
        Assert.Equal(SpeedTestPhase.Failed, test.State.Phase);
        Assert.NotNull(test.State.DownloadMbps);
        Assert.Null(test.State.UploadMbps);
    }

    [Fact]
    public async Task Cancel_returns_to_idle_and_stale_continuations_change_nothing()
    {
        var handler = new FakeHandler { Delay = TimeSpan.FromMilliseconds(150) };
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        test.Cancel();
        Assert.Equal(SpeedTestPhase.Idle, test.State.Phase);
        await test.Completion;
        Assert.Equal(SpeedTestPhase.Idle, test.State.Phase);
    }

    [Fact]
    public async Task Starting_while_running_is_ignored()
    {
        var handler = new FakeHandler();
        using var test = new SpeedTest(handler, Fast);
        test.Start();
        var first = test.Completion;
        test.Start();
        Assert.Same(first, test.Completion);
        await first;
    }

    [Fact]
    public void Mbps_math() => Assert.Equal(80, SpeedTestMath.Mbps(50_000_000, 5), 6);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private static readonly byte[] Payload = new byte[64 * 1024];

        public List<string> Requests { get; } = [];

        public HttpStatusCode LatencyStatus { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode DownloadStatus { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode UploadStatus { get; init; } = HttpStatusCode.OK;

        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add($"{request.Method} {request.RequestUri}");
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (request.Method == HttpMethod.Post)
            {
                await request.Content!.CopyToAsync(Stream.Null, cancellationToken);
                return new HttpResponseMessage(UploadStatus) { Content = new StringContent("error body") };
            }

            var query = request.RequestUri!.Query;
            if (query.Contains("bytes=0", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(LatencyStatus) { Content = new ByteArrayContent([]) };
            }

            if (DownloadStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(DownloadStatus) { Content = new ByteArrayContent(Payload) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        }
    }
}
