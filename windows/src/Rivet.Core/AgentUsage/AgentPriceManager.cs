// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;

namespace Rivet.Core.Agents;

/// <summary>
/// The price list in use (spec 07 §3.8.9): the newer of the bundled list and
/// the cached download by its "updated" day (the download wins a tie). When
/// "Keep prices up to date" is on, the public list is downloaded at most once
/// a day (six hours after a failure): no cookies, no cache, 15 s per request,
/// at most 256 KiB, redirects followed only to https on the same host. A list
/// that fails validation is ignored and the last good one stays.
/// </summary>
public sealed class AgentPriceManager
{
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterSuccess = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    private readonly string _cachePath;
    private readonly ISettingsStore _settings;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly object _gate = new();
    private PriceList _current;
    private DateOnly? _cachedUpdated;
    private int _downloading;

    public AgentPriceManager(string cachePath, ISettingsStore settings, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _cachePath = cachePath;
        _settings = settings;
        _handlerFactory = handlerFactory ?? (() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        _current = Choose(PriceList.Bundled, LoadCache(out _cachedUpdated));
    }

    /// <summary>Raised (on a worker thread) when a newer list was installed.</summary>
    public event EventHandler? Changed;

    public PriceList Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The public list: the app's own repository, which mirrors the macOS app's Resources folder.</summary>
    public static Uri Source => new($"https://raw.githubusercontent.com/{(string.IsNullOrEmpty(AppIdentity.UpdateRepository) ? "vorssaint/vorssaint-utils" : AppIdentity.UpdateRepository)}/main/Resources/agent-prices.json");

    /// <summary>The newer list by "updated" day; on the same day the downloaded one wins.</summary>
    public static PriceList Choose(PriceList bundled, PriceList? cached) =>
        cached is not null && cached.Updated >= bundled.Updated ? cached : bundled;

    /// <summary>Whether a download is due now.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (!_settings.Get(AgentUsageSettings.PriceUpdates))
        {
            return false;
        }

        DateTime? saved = File.Exists(_cachePath) ? File.GetLastWriteTimeUtc(_cachePath) : null;
        if (saved is { } s && now.UtcDateTime - s < CacheMaxAge)
        {
            return false;
        }

        var lastAttempt = DateTimeOffset.FromUnixTimeMilliseconds(_settings.Get(AgentUsageSettings.PriceLastAttempt));
        var wait = _settings.Get(AgentUsageSettings.PriceLastFailed) ? RetryAfterFailure : RetryAfterSuccess;
        return now - lastAttempt >= wait;
    }

    /// <summary>Downloads the list when due. Returns true when a newer list was installed.</summary>
    public async Task<bool> UpdateIfDueAsync(DateTimeOffset now, CancellationToken cancel = default)
    {
        if (!IsDue(now) || Interlocked.Exchange(ref _downloading, 1) == 1)
        {
            return false;
        }

        try
        {
            _settings.Set(AgentUsageSettings.PriceLastAttempt, now.ToUnixTimeMilliseconds());
            var bytes = await DownloadAsync(cancel).ConfigureAwait(false);
            var list = bytes is null ? null : PriceList.Parse(bytes, out var error);
            if (list is null)
            {
                _settings.Set(AgentUsageSettings.PriceLastFailed, true);
                return false;
            }

            _settings.Set(AgentUsageSettings.PriceLastFailed, false);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes!, cancel).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
            bool changed;
            lock (_gate)
            {
                _cachedUpdated = list.Updated;
                var chosen = Choose(PriceList.Bundled, list);
                changed = !ReferenceEquals(chosen, _current) && chosen.Updated >= _current.Updated;
                if (changed)
                {
                    _current = chosen;
                }
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return changed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or OperationCanceledException)
        {
            Log.Info("agents", $"Price list download failed: {ex.Message}");
            _settings.Set(AgentUsageSettings.PriceLastFailed, true);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _downloading, 0);
        }
    }

    private async Task<byte[]?> DownloadAsync(CancellationToken cancel)
    {
        using var client = new HttpClient(_handlerFactory(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        var uri = Source;
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Id, AppIdentity.VersionString));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (next.Scheme != Uri.UriSchemeHttps || !string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                uri = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > PriceList.MaxBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > PriceList.MaxBytes)
                {
                    return null;
                }
            }

            return memory.ToArray();
        }

        return null;
    }

    private PriceList? LoadCache(out DateOnly? updated)
    {
        updated = null;
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            var list = PriceList.Parse(File.ReadAllBytes(_cachePath), out _);
            updated = list?.Updated;
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
