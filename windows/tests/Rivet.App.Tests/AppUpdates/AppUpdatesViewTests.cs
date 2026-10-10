// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.AppUpdates;
using Rivet.App.Tests.Maintenance;
using Rivet.Core.Features;
using Rivet.Core.Maintenance.AppUpdates;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.PackageManager;
using Xunit;

namespace Rivet.App.Tests.AppUpdates;

public sealed class AppUpdatesViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rivet-appupdates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Check_results_render_with_sources_rules_and_icons()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, updates, _) = Fresh();
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(services, compact: true), $"appUpdates-panel-unchecked-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);

            // The view started a check when it appeared; CheckAsync then only queues one more.
            await updates.CheckAsync();
            await MaintenanceSnapshots.WaitUntil(() => !updates.IsChecking && updates.CheckedThisSession);
            var rows = updates.Rows;
            // winget rows (pinned/explicit and local ones are not offered), the Store row, the Electron feed row.
            Assert.Contains(rows, r => r.Kind == AppUpdateKind.PackageManager && r.PackageId == "Git.Git");
            Assert.Contains(rows, r => r.Kind == AppUpdateKind.Store && r.PackageId == "9N0DX20HK701");
            var desk = Assert.Single(rows, r => r.Kind == AppUpdateKind.Online);
            Assert.Equal("2.4.0", desk.LatestVersion);
            Assert.Equal(rows.Count(r => r.IsSelectable), updates.SelectedCount);
            await WarmIconsAsync(services, rows);
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(services, compact: true), $"appUpdates-panel-results-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);

            updates.SkipVersion(rows.Single(r => r.PackageId == "Zoom.Zoom"));
            updates.ExcludeApp(rows.Single(r => r.PackageId == "Microsoft.Edge"));
            await MaintenanceSnapshots.WaitUntil(() => updates.Rows.Count == rows.Count - 2);
            Assert.Equal(2, updates.Rules.Count);
            AppUpdatesView.RulesExpanded = true;
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(services, compact: true), $"appUpdates-panel-rules-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);
            AppUpdatesView.RulesExpanded = false;
        }
    }

    [AvaloniaFact]
    public async Task Updating_the_selection_runs_winget_and_hands_store_rows_to_the_store()
    {
        var (services, updates, shell) = Fresh();
        await updates.CheckAsync();
        updates.ClearSelection();
        updates.SetSelected(AppUpdateRow.PackageRowId("Git.Git"), true);
        updates.SetSelected(AppUpdateRow.StoreRowId("9N0DX20HK701"), true);
        Assert.Equal(2, updates.SelectedCount);

        var run = updates.UpdateSelectedAsync();
        await MaintenanceSnapshots.WaitUntil(() => updates.Lane.IsRunning);
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(services, compact: true), $"appUpdates-panel-updating-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);
        }

        await run;
        Assert.Equal(OperationResult.Succeeded, updates.Lane.Current?.Result);
        Assert.Equal("ms-windows-store://pdp/?productid=9N0DX20HK701", Assert.Single(shell.Opened));
        // The list checks itself again after an upgrade it started.
        await MaintenanceSnapshots.WaitUntil(() => !updates.IsChecking && updates.Rows.All(r => r.PackageId != "Git.Git"));
    }

    [AvaloniaFact]
    public async Task Empty_missing_and_agreement_states_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, updates, _) = Fresh(accepted: false);
            await updates.CheckAsync();
            Assert.True(updates.NeedsAgreement);
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(services, compact: true), $"appUpdates-panel-agreement-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);

            var (missingServices, missing, _) = Fresh(locator: new NoWinget());
            missingServices.GetRequiredService<ISettingsStore>().Set(AppUpdatesSettings.IncludeOnline, false);
            await missing.CheckAsync();
            Assert.True(missing.PackageManagerMissing);
            Assert.Empty(missing.Rows);
            MaintenanceSnapshots.Panel(() => new AppUpdatesView(missingServices, compact: true), $"appUpdates-panel-missing-{theme}", "appUpdates.pageTitle", "ArrowDownload", variant);
        }
    }

    [AvaloniaFact]
    public async Task Settings_page_renders()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, updates, _) = Fresh();
            var settings = services.GetRequiredService<ISettingsStore>();
            settings.Set(AppUpdatesSettings.CheckFrequency, "weekly");
            await updates.CheckAsync();
            await WarmIconsAsync(services, updates.Rows);
            MaintenanceSnapshots.SettingsPage(services, AppUpdatesModule.PageId, $"settings-appUpdates-{theme}", variant, height: 1500);
            settings.Set(AppUpdatesSettings.CheckFrequency, "off");
        }
    }

    [AvaloniaFact]
    public void The_last_source_cannot_be_switched_off()
    {
        var (services, _, _) = Fresh();
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(AppUpdatesSettings.IncludeStore, false);
        settings.Set(AppUpdatesSettings.IncludeOnline, false);
        var page = new AppUpdatesSettingsPage(services);
        var window = new Window { Content = page, Width = 800, Height = 900 };
        window.Show();
        MaintenanceSnapshots.Settle();
        var switches = page.GetLogicalDescendants().OfType<ToggleSwitch>().ToList();
        var winget = switches.Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == Rivet.Core.Localization.L.Get("appUpdates.includeHomebrewToggle"));
        Assert.False(winget.IsEnabled);
        settings.Set(AppUpdatesSettings.IncludeOnline, true);
        MaintenanceSnapshots.Settle();
        Assert.True(winget.IsEnabled);
        window.Close();
    }

    [Fact]
    public void Rows_borrow_icons_from_installed_apps_by_key_or_name()
    {
        var apps = new[]
        {
            new InstalledApp { Key = "arp:User:Desk", Kind = InstalledAppKind.Win32, DisplayName = "Desk", RegistryKeyName = "Desk" },
            new InstalledApp { Key = "arp:Machine64:Zoom", Kind = InstalledAppKind.Win32, DisplayName = "Zoom Workplace" },
            new InstalledApp { Key = "arp:Machine64:VC", Kind = InstalledAppKind.Win32, DisplayName = "Microsoft Visual C++ 2015-2022 Redistributable (x64)" },
        };
        AppUpdateRow Row(AppUpdateKind kind, string name, string key) => new() { Id = key, Kind = kind, Name = name, InstalledVersion = "1", LatestVersion = "2", RuleKey = key };

        Assert.Equal("Desk", AppUpdatesView.MatchApp(Row(AppUpdateKind.Online, "Desk", "app:Desk"), apps)?.DisplayName);
        Assert.Equal("Zoom Workplace", AppUpdatesView.MatchApp(Row(AppUpdateKind.PackageManager, "zoom workplace", "Zoom.Zoom"), apps)?.DisplayName);
        Assert.Equal("arp:Machine64:VC", AppUpdatesView.MatchApp(Row(AppUpdateKind.PackageManager, "Microsoft Visual C++ 2015-2022…", "VC"), apps)?.Key);
        Assert.Null(AppUpdatesView.MatchApp(Row(AppUpdateKind.PackageManager, "Zo…", "Z"), apps));
        Assert.Null(AppUpdatesView.MatchApp(Row(AppUpdateKind.PackageManager, "Git", "Git.Git"), apps));
    }

    /// <summary>Renders once and waits until every row's icon lookup finished, so snapshots are stable.</summary>
    private static async Task WarmIconsAsync(IServiceProvider services, IReadOnlyList<AppUpdateRow> rows)
    {
        var window = new Window { Content = new AppUpdatesView(services, compact: true), Width = 340, Height = 600 };
        window.Show();
        await MaintenanceSnapshots.WaitUntil(() => AppUpdatesView.IconsCached(rows.Select(r => r.Id)));
        window.Close();
    }

    /// <summary>App updates over their own pretend winget, settings and one Electron app with a feed.</summary>
    private (IServiceProvider Services, AppUpdatesService Updates, RecordingShell Shell) Fresh(bool accepted = true, IWingetLocator? locator = null)
    {
        var host = TestApp.Host.Services;
        var settings = SettingsStore.InMemory();
        settings.Set(PackageManagerSettings.SourceAgreementsAccepted, accepted);
        var runner = new FakeWingetRunner();
        var client = new WingetClient(runner, locator ?? new FakeWingetLocator(), settings);
        var lane = new PackageOperationLane(client, runner);
        var manager = new PackageManagerService(client, lane, settings);

        // An installed Electron app whose resources\app-update.yml points at a generic feed.
        var desk = Path.Combine(_root, Guid.NewGuid().ToString("N"), "Desk");
        Directory.CreateDirectory(Path.Combine(desk, "resources"));
        File.WriteAllText(Path.Combine(desk, "resources", "app-update.yml"), "provider: generic\nurl: https://updates.example.com/desk/\n");
        File.WriteAllText(Path.Combine(desk, "Desk.exe"), string.Empty);
        var installed = new ExtraApps(host.GetRequiredService<IInstalledAppsProvider>(), new InstalledApp
        {
            Key = "arp:User:Desk", Kind = InstalledAppKind.Win32, DisplayName = "Desk", Publisher = "Acme", Version = "2.1.3",
            InstallLocation = desk, RegistryKeyName = "Desk", Scope = RegistryScope.User,
        });
        var online = new OnlineUpdateSource(new StubFeeds("version: 2.4.0\npath: Desk-Setup-2.4.0.exe\nreleaseDate: '2026-09-30'\n"));
        var shell = new RecordingShell();
        var updates = new AppUpdatesService(client, lane, settings, host.GetRequiredService<INotificationService>(), installed, online, shell);
        updates.Sync(true);
        host.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.AppUpdates, true);
        var services = new OverrideServices(host,
            (typeof(ISettingsStore), settings),
            (typeof(WingetClient), client),
            (typeof(PackageOperationLane), lane),
            (typeof(PackageManagerService), manager),
            (typeof(AppUpdatesService), updates),
            (typeof(IInstalledAppsProvider), installed),
            (typeof(IShellService), shell));
        return (services, updates, shell);
    }

    private sealed class NoWinget : IWingetLocator
    {
        public string? Locate() => null;
    }

    private sealed class StubFeeds(string body) : IFeedFetcher
    {
        public Task<FeedResponse> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(url == "https://updates.example.com/desk/latest.yml" ? new FeedResponse(FeedStatus.Ok, body) : new FeedResponse(FeedStatus.Absent, null));
    }

    private sealed class ExtraApps(IInstalledAppsProvider inner, params InstalledApp[] extra) : IInstalledAppsProvider
    {
        public IReadOnlyList<InstalledApp> Enumerate() => [.. inner.Enumerate(), .. extra];

        public PixelBuffer? LoadIcon(InstalledApp app, int size = 32) => inner.LoadIcon(app, size);
    }

    internal sealed class RecordingShell : IShellService
    {
        public List<string> Opened { get; } = [];

        public void OpenUrl(string url) => Opened.Add(url);

        public void OpenFile(string path) => Opened.Add(path);

        public void RevealInExplorer(string path) => Opened.Add(path);

        public void OpenSystemSettings(string uri) => Opened.Add(uri);

        public bool MoveToRecycleBin(IEnumerable<string> paths) => false;

        public void OpenWith(string path) => Opened.Add(path);
    }
}
