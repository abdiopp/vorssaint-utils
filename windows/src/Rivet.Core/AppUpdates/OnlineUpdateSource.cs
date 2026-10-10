// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Uninstaller;

namespace Rivet.Core.Maintenance.AppUpdates;

public enum FeedStatus
{
    Ok,

    /// <summary>HTTP 404/410: the feed is gone.</summary>
    Absent,

    /// <summary>Anything else: the app stays notChecked.</summary>
    Failed,
}

public sealed record FeedResponse(FeedStatus Status, string? Body);

public interface IFeedFetcher
{
    Task<FeedResponse> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches a feed the careful way: no cookies, credentials or cache, at most
/// 5 redirects each to a public https URL, 10 s to the headers and 20 s in
/// total, and a body of at most 2 MB (checked on the declared length and
/// while reading).
/// </summary>
public sealed class HttpFeedFetcher : IFeedFetcher, IDisposable
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxRedirects = 5;
    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;

    public HttpFeedFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"{App.AppIdentity.Id}/{App.AppIdentity.VersionString}");
        _client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    public async Task<FeedResponse> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TotalTimeout);
        var current = url;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!ElectronFeeds.IsPublicUrl(current))
                {
                    return new FeedResponse(FeedStatus.Failed, null);
                }

                using var headers = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                headers.CancelAfter(HeaderTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    current = new Uri(new Uri(current), location).AbsoluteUri;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    return new FeedResponse(FeedStatus.Absent, null);
                }

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBodyBytes)
                {
                    return new FeedResponse(FeedStatus.Failed, null);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, total.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBodyBytes)
                    {
                        return new FeedResponse(FeedStatus.Failed, null);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return new FeedResponse(FeedStatus.Ok, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
            }

            return new FeedResponse(FeedStatus.Failed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException)
        {
            Log.Debug("appUpdates", $"Feed {url} failed: {ex.Message}");
            return new FeedResponse(FeedStatus.Failed, null);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>The online source's answer: rows, plus the apps it could not check.</summary>
public sealed record OnlineCheckResult(IReadOnlyList<AppUpdateRow> Rows, IReadOnlyList<string> UncheckedNames);

/// <summary>
/// Source 3: installed Win32 apps that ship an electron-updater config and
/// that winget does not cover. One request per distinct feed, four in flight,
/// a 60 s deadline for the whole pass (feeds not started by then stay notChecked).
/// </summary>
public sealed class OnlineUpdateSource(IFeedFetcher fetcher)
{
    public static readonly TimeSpan PassDeadline = TimeSpan.FromSeconds(60);
    public const int Parallelism = 4;

    public async Task<OnlineCheckResult> CheckAsync(IReadOnlyList<InstalledApp> apps, Func<InstalledApp, bool> skip, CancellationToken cancellationToken)
    {
        var candidates = new List<(InstalledApp App, string Url)>();
        foreach (var app in apps)
        {
            if (app.Kind == InstalledAppKind.Msix || skip(app) || !ElectronFeeds.IsStable(app.Version) || app.InstallLocation is not { } location)
            {
                continue;
            }

            if (ReadConfig(location) is { } url)
            {
                candidates.Add((app, url));
            }
        }

        var rows = new List<AppUpdateRow>();
        var notChecked = new List<string>();
        if (candidates.Count == 0)
        {
            return new OnlineCheckResult(rows, notChecked);
        }

        var deadline = DateTime.UtcNow + PassDeadline;
        using var gate = new SemaphoreSlim(Parallelism);
        var feeds = candidates.GroupBy(c => c.Url, StringComparer.Ordinal).ToList();
        var tasks = feeds.Select(async feed =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow > deadline)
                {
                    return (Feed: feed, Response: new FeedResponse(FeedStatus.Failed, null));
                }

                return (Feed: feed, Response: await fetcher.FetchAsync(feed.Key, cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var (feed, response) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            var version = response.Status == FeedStatus.Ok && response.Body is { } body ? ElectronFeeds.ManifestVersion(body) : null;
            foreach (var (app, _) in feed)
            {
                if (response.Status == FeedStatus.Failed || (response.Status == FeedStatus.Ok && version is null))
                {
                    notChecked.Add(app.DisplayName);
                    continue;
                }

                if (version is not null && ElectronFeeds.IsEligible(version, app.Version))
                {
                    rows.Add(new AppUpdateRow
                    {
                        Id = AppUpdateRow.OnlineRowId(app.Key),
                        Kind = AppUpdateKind.Online,
                        Name = app.DisplayName,
                        InstalledVersion = app.Version!,
                        LatestVersion = version,
                        RuleKey = RuleKeyFor(app),
                        AppPath = MainExecutable(app),
                    });
                }
            }
        }

        return new OnlineCheckResult(rows, notChecked);
    }

    /// <summary>Rules for online rows are keyed by the app's registry key name (no whitespace or '/').</summary>
    public static string RuleKeyFor(InstalledApp app)
    {
        var raw = app.RegistryKeyName ?? app.Key;
        var clean = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '/').ToArray());
        return clean.Length > 0 ? "app:" + clean : app.Key.Replace('/', '_');
    }

    /// <summary>The program to open: the icon's exe inside the install folder, else the first app exe there.</summary>
    public static string? MainExecutable(InstalledApp app)
    {
        if (app.InstallLocation is not { } folder)
        {
            return null;
        }

        var icon = IconPath(app.Icon);
        if (icon is not null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && icon.StartsWith(folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) && File.Exists(icon))
        {
            return icon;
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*.exe")
                .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
                            && !Path.GetFileName(f).Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"C:\App\app.exe,0" or "\"C:\App\app.exe\"" → the path.</summary>
    public static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var text = displayIcon.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? text[1..close] : null;
        }

        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out _))
        {
            text = text[..comma];
        }

        return text.Trim();
    }

    private static string? ReadConfig(string installLocation)
    {
        try
        {
            var file = Path.Combine(installLocation, "resources", "app-update.yml");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > ElectronFeeds.MaxConfigBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            var config = ElectronFeeds.ParseFlatYaml(File.ReadAllText(file));
            return config is null ? null : ElectronFeeds.ManifestUrl(config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
