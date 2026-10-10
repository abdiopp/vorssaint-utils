// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Uninstaller;
using Rivet.App.Tests.Maintenance;
using Rivet.Core.Features;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Cleaner;
using Rivet.Platform.Fake.Uninstaller;
using Xunit;

namespace Rivet.App.Tests.Uninstaller;

public sealed class UninstallerViewTests : IDisposable
{
    private readonly string _backups = Path.Combine(Path.GetTempPath(), "rivet-uninstaller-backups-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_backups))
        {
            Directory.Delete(_backups, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task List_selection_review_and_done_states_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, service, fs) = Fresh();
            await service.LoadAppsAsync();
            Assert.Contains(service.Apps, a => a.DisplayName == "Contoso Studio");
            Assert.Contains(service.Apps, a => a.Kind == InstalledAppKind.Msix);
            await WarmIconsAsync(services, service);
            MaintenanceSnapshots.Panel(() => new UninstallerView(services, compact: true), $"uninstaller-panel-list-{theme}", "Strings.uninstallerName", "Delete", variant);

            var studio = service.Apps.Single(a => a.DisplayName == "Contoso Studio");
            await service.SelectAsync(studio);
            Assert.Equal(UninstallerPhase.Selected, service.Phase);
            MaintenanceSnapshots.Panel(() => new UninstallerView(services, compact: true), $"uninstaller-panel-selected-{theme}", "Strings.uninstallerName", "Delete", variant);

            var run = service.UninstallAsync();
            await MaintenanceSnapshots.WaitUntil(() => service.Phase == UninstallerPhase.Uninstalling && service.Progress is > 0);
            MaintenanceSnapshots.Panel(() => new UninstallerView(services, compact: true), $"uninstaller-panel-uninstalling-{theme}", "Strings.uninstallerName", "Delete", variant);
            await run;
            await MaintenanceSnapshots.WaitUntil(() => service.Phase == UninstallerPhase.Results);

            // Studio's own data, its plugin folder and keys are offered; the sibling app's data is not.
            Assert.Contains(service.Leftovers, i => i.Path.EndsWith(@"Roaming\Contoso\Studio", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(service.Leftovers, i => i.Path.EndsWith(@"Program Files\Contoso\Studio", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(service.Leftovers, i => i.Kind == LeftoverKind.RegistryKey && i.Path.EndsWith(@"Contoso\Studio", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(service.Leftovers, i => i.Path.Contains("Paint", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(service.Leftovers, i => i.IsReviewOnly);
            Assert.DoesNotContain(service.Leftovers.Where(i => i.IsReviewOnly), service.IsIncluded);
            MaintenanceSnapshots.Panel(() => new UninstallerView(services, compact: true), $"uninstaller-panel-results-{theme}", "Strings.uninstallerName", "Delete", variant);

            // Only exact matches start ticked; name-based guesses wait for the person.
            var roaming = service.Leftovers.Single(i => i.Path.EndsWith(@"Roaming\Contoso\Studio", StringComparison.OrdinalIgnoreCase));
            Assert.False(roaming.Exact);
            Assert.False(service.IsIncluded(roaming));
            Assert.All(service.Leftovers.Where(service.IsIncluded), i => Assert.True(i.Exact));
            service.SetIncluded(roaming, true);
            var userKey = service.Leftovers.Single(i => i.Kind == LeftoverKind.RegistryKey && i.Path == @"HKCU\Software\Contoso\Studio");
            service.SetIncluded(userKey, true);
            await service.RemoveSelectedAsync();
            Assert.Equal(UninstallerPhase.Done, service.Phase);
            Assert.True(service.LastResult?.Succeeded);
            Assert.Null(fs.Stat(@"C:\Users\Alex\AppData\Roaming\Contoso\Studio\settings.json"));
            Assert.NotNull(fs.Stat(@"C:\Users\Alex\AppData\Roaming\Contoso\Paint\settings.json"));
            Assert.Null(fs.Stat(@"C:\Program Files\Contoso\Studio\plugins\render.dll"));
            Assert.NotEmpty(Directory.GetFiles(_backups, "*.reg", SearchOption.AllDirectories));
            MaintenanceSnapshots.Panel(() => new UninstallerView(services, compact: true), $"uninstaller-panel-done-{theme}", "Strings.uninstallerName", "Delete", variant);
        }
    }

    [AvaloniaFact]
    public async Task Settings_page_renders_list_and_review()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, service, _) = Fresh();
            await service.LoadAppsAsync();
            await WarmIconsAsync(services, service);
            MaintenanceSnapshots.SettingsPage(services, UninstallerModule.PageId, $"settings-uninstaller-{theme}", variant, height: 1100);

            await service.SelectAsync(service.Apps.Single(a => a.DisplayName == "Contoso Studio"));
            await service.UninstallAsync();
            await MaintenanceSnapshots.WaitUntil(() => service.Phase == UninstallerPhase.Results);
            MaintenanceSnapshots.SettingsPage(services, UninstallerModule.PageId, $"settings-uninstaller-review-{theme}", variant, height: 1300);
        }
    }

    [Fact]
    public void Command_bar_scores_prefixes_above_word_and_substring_matches()
    {
        Assert.Equal(1, UninstallerSearchProvider.Score("Contoso Studio", "cont"));
        Assert.Equal(0.8, UninstallerSearchProvider.Score("Contoso Studio", "stu"));
        Assert.Equal(0.6, UninstallerSearchProvider.Score("Contoso Studio", "tudi"));
        Assert.Equal(0, UninstallerSearchProvider.Score("Contoso Studio", "paint"));
        Assert.Equal(0, UninstallerSearchProvider.Score("Contoso Studio", string.Empty));
    }

    /// <summary>Renders the list once and waits for every icon lookup, so snapshots are stable.</summary>
    private static async Task WarmIconsAsync(IServiceProvider services, UninstallerService service)
    {
        var window = new Window { Content = new UninstallerView(services, compact: true), Width = 340, Height = 700 };
        window.Show();
        await MaintenanceSnapshots.WaitUntil(() => UninstallerView.IconsCached(service.Apps.Select(a => a.Key)));
        window.Close();
    }

    /// <summary>An uninstaller over a fresh sample PC (apps, files, registry), so tests never share state.</summary>
    private (IServiceProvider Services, UninstallerService Service, InMemoryFileSystem Fs) Fresh()
    {
        var host = TestApp.Host.Services;
        var settings = SettingsStore.InMemory();
        var fs = new InMemoryFileSystem();
        var apps = new FakeInstalledApps();
        var platform = new FakeUninstallerPlatform(apps, fs);
        var cleanerPlatform = new FakeCleanerPlatform(fs, apps);
        var processes = host.GetRequiredService<IProcessPlatform>();
        var service = new UninstallerService(
            apps, platform, cleanerPlatform, fs, new FakeRecycler(fs), new FakeKnownFolders(), processes,
            new ProcessService(processes, settings), settings, () => Directory.CreateDirectory(_backups).FullName);
        host.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Uninstaller, true);
        var services = new OverrideServices(host,
            (typeof(ISettingsStore), settings),
            (typeof(UninstallerService), service),
            (typeof(IInstalledAppsProvider), apps),
            (typeof(IUninstallerPlatform), platform),
            (typeof(ICleanerFileSystem), fs),
            (typeof(ICleanerPlatform), cleanerPlatform));
        return (services, service, fs);
    }
}
