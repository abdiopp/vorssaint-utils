// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.AppUpdates;
using Rivet.Core.Maintenance.Uninstaller;
using Xunit;

namespace Rivet.Core.Tests.AppUpdates;

public class ElectronFeedsTests
{
    [Fact]
    public void Flat_yaml_decodes_quotes_and_rejects_duplicates()
    {
        var values = ElectronFeeds.ParseFlatYaml("provider: \"github\"\nowner: 'acme'\nrepo: desk # comment\nupdaterCacheDirName: acme-updater\nfiles:\n  - url: a.exe\n")!;
        Assert.Equal("github", values["provider"]);
        Assert.Equal("acme", values["owner"]);
        Assert.Equal("desk", values["repo"]);
        Assert.Equal(string.Empty, values["files"]);
        Assert.Null(ElectronFeeds.ParseFlatYaml("provider: generic\nprovider: github\n"));
    }

    [Theory]
    [InlineData("https://downloads.example.com/desktop", true)]
    [InlineData("http://downloads.example.com/desktop", false)]
    [InlineData("https://user:pass@example.com/x", false)]
    [InlineData("https://example.com/x#frag", false)]
    [InlineData("https://example.com:8443/x", false)]
    [InlineData("https://localhost/x", false)]
    [InlineData("https://updates.local/x", false)]
    [InlineData("https://192.168.1.10/x", false)]
    [InlineData("https://[::1]/x", false)]
    [InlineData("https://bad_label.example.com/x", false)]
    public void Only_public_https_feeds_are_used(string url, bool expected) =>
        Assert.Equal(expected, ElectronFeeds.IsPublicUrl(url));

    [Fact]
    public void Manifest_urls_follow_the_provider()
    {
        Assert.Equal("https://dl.example.com/app/latest.yml",
            ElectronFeeds.ManifestUrl(Config("provider: generic\nurl: https://dl.example.com/app/\n")));
        Assert.Equal("https://github.com/acme/desk/releases/latest/download/latest.yml",
            ElectronFeeds.ManifestUrl(Config("provider: github\nowner: acme\nrepo: desk\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: github\nowner: acme\nrepo: desk\nprivate: true\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: github\nowner: acme\nrepo: desk\ntoken: abc\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: generic\nurl: https://dl.example.com/app?key=1\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: generic\nurl: https://dl.example.com/app\nchannel: beta\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: github\nhost: git.example.com\nowner: acme\nrepo: desk\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: s3\nbucket: x\n")));
        Assert.Null(ElectronFeeds.ManifestUrl(Config("provider: github\nowner: ..\nrepo: desk\n")));
    }

    [Fact]
    public void Latest_yml_needs_a_version_and_files()
    {
        const string latest = "version: 4.40.1\nfiles:\n  - url: Slack-4.40.1-setup.exe\n    sha512: abc\npath: Slack-4.40.1-setup.exe\nreleaseDate: '2024-09-12T10:00:00.000Z'\n";
        Assert.Equal("4.40.1", ElectronFeeds.ManifestVersion(latest));
        Assert.Null(ElectronFeeds.ManifestVersion("version: 1.0\n"));
        Assert.True(ElectronFeeds.IsEligible("4.40.1", "4.39.95"));
        Assert.False(ElectronFeeds.IsEligible("4.40.1-beta", "4.39.95"));
        Assert.False(ElectronFeeds.IsEligible("4.40.1", "4.40.1"));
    }

    [Fact]
    public async Task Online_source_reads_app_update_yml_and_groups_requests_per_feed()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-feeds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var slack = CreateApp(root, "Slack", "provider: generic\nurl: https://downloads.slack-edge.com/desktop-releases/windows/x64\n");
            var team = CreateApp(root, "Team", "provider: github\nowner: acme\nrepo: team\n");
            var gone = CreateApp(root, "Gone", "provider: github\nowner: acme\nrepo: gone\n");
            var fetcher = new FakeFetcher(new Dictionary<string, FeedResponse>
            {
                ["https://downloads.slack-edge.com/desktop-releases/windows/x64/latest.yml"] = new(FeedStatus.Ok, "version: 4.40.1\npath: Slack.exe\n"),
                ["https://github.com/acme/team/releases/latest/download/latest.yml"] = new(FeedStatus.Failed, null),
                ["https://github.com/acme/gone/releases/latest/download/latest.yml"] = new(FeedStatus.Absent, null),
            });
            var apps = new[]
            {
                App("Slack", "4.39.95", slack),
                App("Slack (second entry)", "4.39.95", slack),
                App("Team", "1.0.0", team),
                App("Gone", "1.0.0", gone),
                App("Covered", "1.0.0", slack),
            };
            var result = await new OnlineUpdateSource(fetcher).CheckAsync(apps, a => a.DisplayName == "Covered", CancellationToken.None);

            Assert.Equal(2, result.Rows.Count);
            Assert.All(result.Rows, r => Assert.Equal("4.40.1", r.LatestVersion));
            Assert.All(result.Rows, r => Assert.False(r.IsSelectable));
            Assert.Equal(["Team"], result.UncheckedNames);
            Assert.Equal(1, fetcher.Requests.Count(u => u.Contains("slack", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("C:\\Program Files\\Slack\\slack.exe,0", "C:\\Program Files\\Slack\\slack.exe")]
    [InlineData("\"C:\\Program Files\\Slack\\slack.exe\",1", "C:\\Program Files\\Slack\\slack.exe")]
    [InlineData("C:\\Apps\\icon.ico", "C:\\Apps\\icon.ico")]
    [InlineData("", null)]
    public void Display_icon_paths_parse(string value, string? expected) =>
        Assert.Equal(expected, OnlineUpdateSource.IconPath(value));

    private static IReadOnlyDictionary<string, string> Config(string yaml) => ElectronFeeds.ParseFlatYaml(yaml)!;

    private static string CreateApp(string root, string name, string config)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(folder, "resources"));
        File.WriteAllText(Path.Combine(folder, "resources", "app-update.yml"), config);
        File.WriteAllText(Path.Combine(folder, name + ".exe"), "MZ");
        return folder;
    }

    private static InstalledApp App(string name, string version, string location) => new()
    {
        Key = "arp:User:" + name,
        Kind = InstalledAppKind.Win32,
        DisplayName = name,
        Version = version,
        InstallLocation = location,
        RegistryKeyName = name.Replace(" ", string.Empty, StringComparison.Ordinal),
        UninstallString = Path.Combine(location, "Uninstall.exe"),
    };

    private sealed class FakeFetcher(IReadOnlyDictionary<string, FeedResponse> responses) : IFeedFetcher
    {
        public List<string> Requests { get; } = [];

        public Task<FeedResponse> FetchAsync(string url, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(url);
            }

            return Task.FromResult(responses.TryGetValue(url, out var response) ? response : new FeedResponse(FeedStatus.Failed, null));
        }
    }
}
