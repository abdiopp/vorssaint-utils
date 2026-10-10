// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Update;

public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

public sealed record UpdateInfo(SemVer Version, string Tag, string ReleasePageUrl, string Notes, bool IsPrerelease, UpdateAsset? Asset);

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null);

/// <summary>
/// Checks GitHub Releases of <see cref="AppIdentity.UpdateRepository"/>.
/// Stable channel: <c>/releases/latest</c>; beta channel: the ten newest
/// releases. Drafts are ignored; the Windows asset is
/// <c>&lt;prefix&gt;-&lt;version&gt;-win-&lt;arch&gt;-setup.exe</c>.
/// </summary>
public sealed class UpdateChecker(HttpClient http)
{
    public static string AssetNameFor(SemVer version, string architecture) =>
        $"{AppIdentity.UpdateAssetPrefix}-{version}-win-{architecture}-setup.exe";

    public async Task<UpdateCheckResult> CheckAsync(SemVer current, bool includePrerelease, string architecture, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AppIdentity.UpdateRepository))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "No update feed configured.");
        }

        var url = includePrerelease
            ? $"https://api.github.com/repos/{AppIdentity.UpdateRepository}/releases?per_page=10"
            : $"https://api.github.com/repos/{AppIdentity.UpdateRepository}/releases/latest";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Id, AppIdentity.VersionString));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var releases = includePrerelease
                ? await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? []
                : [await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new GitHubRelease()];
            return Select(releases, current, includePrerelease, architecture);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn("update", "Update check failed.", ex);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Picks the newest eligible release newer than <paramref name="current"/>.</summary>
    public static UpdateCheckResult Select(IEnumerable<GitHubRelease> releases, SemVer current, bool includePrerelease, string architecture)
    {
        UpdateInfo? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || !SemVer.TryParse(release.TagName, out var version))
            {
                continue;
            }

            var prerelease = release.Prerelease || version.IsPrerelease;
            if (prerelease && !includePrerelease)
            {
                continue;
            }

            if (version <= current || (best is not null && version <= best.Version))
            {
                continue;
            }

            var assetName = AssetNameFor(version, architecture);
            var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            best = new UpdateInfo(
                version,
                release.TagName ?? version.ToString(),
                release.HtmlUrl ?? $"https://github.com/{AppIdentity.UpdateRepository}/releases",
                release.Body ?? string.Empty,
                prerelease,
                asset is null ? null : new UpdateAsset(asset.Name ?? assetName, asset.BrowserDownloadUrl ?? string.Empty, asset.Size));
        }

        return best is null ? new UpdateCheckResult(UpdateCheckStatus.UpToDate) : new UpdateCheckResult(UpdateCheckStatus.Available, best);
    }

    public sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    public sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
