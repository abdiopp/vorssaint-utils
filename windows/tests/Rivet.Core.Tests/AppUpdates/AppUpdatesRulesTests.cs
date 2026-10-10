// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.AppUpdates;
using Xunit;

namespace Rivet.Core.Tests.AppUpdates;

public class AppUpdatesRulesTests
{
    // The vectors pinned by the macOS tests (spec §3.3.10).
    [Theory]
    [InlineData("1.130.0", "1.129.0", 1)]
    [InlineData("0.730.0.7300790", "0.731.0", -1)]
    [InlineData("26.084.0504", "26.119.0622.0003", -1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2", "1.2.1", -1)]
    [InlineData("00123", "123", 0)]
    [InlineData("2024.1", "2024.10", -1)]
    [InlineData("1.10", "1.9a", 1)]
    [InlineData("3.5", "3.5beta", 1)]
    [InlineData("1.9b", "1.9a", 1)]
    [InlineData("1.0Beta", "1.0beta", 0)]
    [InlineData("v2.0.11.1", "2.0.11.1", 0)]
    [InlineData("v2.0.11.2", "2.0.11.1", 1)]
    [InlineData("3.5.262,260717d", "3.5.262", 0)]
    [InlineData("129.0.2792.79", "129.0.2792.65", 1)]
    public void Versions_compare_like_the_macos_app(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(VersionComparer.Compare(b, a)));
    }

    [Fact]
    public void Version_core_rules()
    {
        Assert.Equal("version1", VersionComparer.Core("version1"));
        Assert.Equal("2.0", VersionComparer.Core("  v2.0 "));
        Assert.Equal("3.5.262", VersionComparer.Core("3.5.262,260717d"));
        Assert.True(VersionComparer.IsUncomparable("latest"));
        Assert.True(VersionComparer.IsUncomparable(" "));
        Assert.False(VersionComparer.IsUncomparable("1.0"));
        Assert.True(VersionComparer.IsNewer("1.2.1", "1.2"));
        Assert.False(VersionComparer.IsNewer("1.2.0", "1.2"));
    }

    [Fact]
    public void Rules_decoding_drops_invalid_entries_and_duplicates()
    {
        const string json = """
            [
              {"bundleID": "Git.Git", "name": "Git", "version": "2.46.0"},
              {"bundleID": "Git.Git", "name": "Git again"},
              {"bundleID": "", "name": "Empty"},
              {"bundleID": "Has Space", "name": "Space"},
              {"bundleID": "a/b", "name": "Slash"},
              {"bundleID": "Contoso.App", "name": ""},
              {"bundleID": "Zoom.Zoom", "name": "Zoom", "version": "latest"},
              {"bundleID": "Spotify.Spotify", "name": "Spotify"}
            ]
            """;
        var rules = UpdateRules.Decode(json);
        Assert.Equal(2, rules.Count);
        Assert.Equal("Git", rules[0].Name);
        Assert.Equal("2.46.0", rules[0].Version);
        Assert.True(rules[1].IsExclusion);
        Assert.Empty(UpdateRules.Decode("not json"));
        Assert.Empty(UpdateRules.Decode(null));
    }

    [Fact]
    public void Rules_round_trip_in_the_macos_shape()
    {
        var json = UpdateRules.Encode([new UpdateRule { Key = "Git.Git", Name = "Git", Version = "2.46.0" }, new UpdateRule { Key = "Zoom.Zoom", Name = "Zoom" }]);
        Assert.Equal("""[{"bundleID":"Git.Git","name":"Git","version":"2.46.0"},{"bundleID":"Zoom.Zoom","name":"Zoom"}]""", json);
        Assert.Equal(2, UpdateRules.Decode(json).Count);
    }

    [Fact]
    public void A_skip_hides_only_that_release_and_an_exclusion_hides_the_app()
    {
        var rules = UpdateRules.WithRule([], new UpdateRule { Key = "Git.Git", Name = "Git", Version = "v2.46.0" });
        rules = UpdateRules.WithRule(rules, new UpdateRule { Key = "Zoom.Zoom", Name = "Zoom" });
        var rows = new[]
        {
            Row("Git.Git", "2.46.0"),
            Row("Zoom.Zoom", "6.2.0"),
            Row("Spotify.Spotify", "1.2.47"),
        };
        var visible = UpdateRules.Apply(rules, rows);
        Assert.Equal(["Spotify.Spotify"], visible.Select(r => r.RuleKey));

        var newer = UpdateRules.Apply(rules, [Row("Git.Git", "2.46.1")]);
        Assert.Single(newer);

        var replaced = UpdateRules.WithRule(rules, new UpdateRule { Key = "Git.Git", Name = "Git" });
        Assert.Equal(2, replaced.Count);
        Assert.True(replaced.Single(r => r.Key == "Git.Git").IsExclusion);
    }

    [Fact]
    public void Merge_orders_by_source_then_name()
    {
        var merged = AppUpdateList.Merge(
        [
            Row("z", "1", AppUpdateKind.Online, "Zed"),
            Row("b", "1", AppUpdateKind.Store, "beta"),
            Row("a", "1", AppUpdateKind.PackageManager, "Zoom"),
            Row("c", "1", AppUpdateKind.PackageManager, "alpha"),
            Row("c", "1", AppUpdateKind.PackageManager, "alpha"),
        ]);
        Assert.Equal(["alpha", "Zoom", "beta", "Zed"], merged.Select(r => r.Name));
    }

    [Fact]
    public void New_selectable_rows_arrive_selected_and_gone_rows_leave()
    {
        var previous = new[] { Row("Git.Git", "2"), Row("Zoom.Zoom", "6") };
        var current = new[] { Row("Zoom.Zoom", "6"), Row("New.App", "1"), Row("Online", "1", AppUpdateKind.Online) };
        var selection = AppUpdateList.Reconcile(new HashSet<string> { previous[0].Id }, previous, current);
        Assert.Equal(new HashSet<string> { current[1].Id }, selection);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(1, 40)]
    [InlineData(3, 130)]
    [InlineData(9, 175)]
    public void Compact_list_height_ends_on_a_whole_row(int count, double expected) =>
        Assert.Equal(expected, AppUpdateList.CompactHeight(count));

    [Fact]
    public void Background_schedule_waits_for_the_interval_or_180_seconds()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddSeconds(180), AppUpdatesService.NextCheck(null, TimeSpan.FromHours(24), now));
        Assert.Equal(now.AddSeconds(180), AppUpdatesService.NextCheck(now.AddDays(-2), TimeSpan.FromHours(24), now));
        Assert.Equal(now.AddHours(20), AppUpdatesService.NextCheck(now.AddHours(-4), TimeSpan.FromHours(24), now));
        Assert.Null(AppUpdatesService.Interval("off"));
        Assert.Equal(TimeSpan.FromDays(7), AppUpdatesService.Interval("weekly"));
    }

    private static AppUpdateRow Row(string key, string latest, AppUpdateKind kind = AppUpdateKind.PackageManager, string? name = null) => new()
    {
        Id = kind switch
        {
            AppUpdateKind.Store => AppUpdateRow.StoreRowId(key),
            AppUpdateKind.Online => AppUpdateRow.OnlineRowId(key),
            _ => AppUpdateRow.PackageRowId(key),
        },
        Kind = kind,
        Name = name ?? key,
        InstalledVersion = "0.1",
        LatestVersion = latest,
        RuleKey = key,
        PackageId = key,
    };
}
