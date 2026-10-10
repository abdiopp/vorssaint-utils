// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.PackageManager;
using Rivet.App.Tests.Maintenance;
using Rivet.Core.Features;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.PackageManager;
using Xunit;

namespace Rivet.App.Tests.PackageManager;

public class PackageManagerViewTests
{
    [AvaloniaFact]
    public async Task Installed_details_and_operation_states_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, manager) = Fresh(accepted: true);
            PackageManagerView.SearchMode = false;
            await manager.DetectAsync();
            await manager.LoadInstalledAsync();
            Assert.Equal(WingetAvailability.Available, manager.Availability);
            Assert.Contains(manager.Installed, p => p.Id == "Git.Git" && p.Available == "2.47.0");
            Assert.Contains(manager.Installed, p => p.IsLocal && p.Name == "Contoso Helper");
            MaintenanceSnapshots.Panel(() => new PackageManagerView(services, compact: true), $"packageManager-panel-installed-{theme}", "win.shell.packageManagerTitle", "Box", variant);

            await manager.SelectAsync(manager.InstalledEntry("Git.Git"));
            Assert.Equal("The Git Development Community", manager.Details?.Publisher);
            MaintenanceSnapshots.Panel(() => new PackageManagerView(services, compact: true), $"packageManager-panel-details-{theme}", "win.shell.packageManagerTitle", "Box", variant);

            var run = manager.RunAsync(PackageOperationKind.Upgrade, manager.InstalledEntry("Git.Git"));
            await MaintenanceSnapshots.WaitUntil(() => manager.Lane.Current is { Phase: OperationPhase.Downloading, Progress: not null });
            MaintenanceSnapshots.Panel(() => new PackageManagerView(services, compact: true), $"packageManager-panel-running-{theme}", "win.shell.packageManagerTitle", "Box", variant);
            Assert.Equal(OperationResult.Succeeded, await run);
            await MaintenanceSnapshots.WaitUntil(() => !manager.LoadingInstalled && manager.InstalledEntry("Git.Git") is { HasUpdate: false });
            MaintenanceSnapshots.Panel(() => new PackageManagerView(services, compact: true), $"packageManager-panel-done-{theme}", "win.shell.packageManagerTitle", "Box", variant);
        }
    }

    [AvaloniaFact]
    public async Task Search_missing_and_agreement_states_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, manager) = Fresh(accepted: true);
            await manager.DetectAsync();
            await manager.LoadInstalledAsync();
            await manager.SearchAsync("code");
            Assert.Contains(manager.SearchResults, p => p.Id == "Microsoft.VisualStudioCode.Insiders");
            PackageManagerView.SearchMode = true;
            MaintenanceSnapshots.Panel(() => new PackageManagerView(services, compact: true), $"packageManager-panel-search-{theme}", "win.shell.packageManagerTitle", "Box", variant);
            PackageManagerView.SearchMode = false;

            var (agreementServices, agreement) = Fresh(accepted: false);
            await agreement.DetectAsync();
            await agreement.LoadInstalledAsync();
            Assert.False(agreement.InstalledLoaded);
            MaintenanceSnapshots.Panel(() => new PackageManagerView(agreementServices, compact: true), $"packageManager-panel-agreement-{theme}", "win.shell.packageManagerTitle", "Box", variant);

            var (missingServices, missing) = Fresh(accepted: true, locator: new NoWinget());
            await missing.DetectAsync();
            Assert.Equal(WingetAvailability.Missing, missing.Availability);
            MaintenanceSnapshots.Panel(() => new PackageManagerView(missingServices, compact: true), $"packageManager-panel-missing-{theme}", "win.shell.packageManagerTitle", "Box", variant);
        }
    }

    [AvaloniaFact]
    public async Task Settings_page_renders()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, manager) = Fresh(accepted: true);
            PackageManagerView.SearchMode = false;
            await manager.DetectAsync();
            await manager.LoadInstalledAsync();
            await manager.LoadSourcesAsync();
            Assert.Contains(manager.Sources, s => s.Name == "msstore");
            await manager.SelectAsync(manager.InstalledEntry("Microsoft.VisualStudioCode"));
            MaintenanceSnapshots.SettingsPage(services, PackageManagerModule.PageId, $"settings-packageManager-{theme}", variant, height: 1300);
        }
    }

    /// <summary>A package manager over its own pretend winget and settings, so tests never share state.</summary>
    internal static (IServiceProvider Services, PackageManagerService Manager) Fresh(bool accepted, IWingetLocator? locator = null)
    {
        var host = TestApp.Host.Services;
        var settings = SettingsStore.InMemory();
        settings.Set(PackageManagerSettings.SourceAgreementsAccepted, accepted);
        var runner = new FakeWingetRunner();
        var client = new WingetClient(runner, locator ?? new FakeWingetLocator(), settings);
        var lane = new PackageOperationLane(client, runner);
        var manager = new PackageManagerService(client, lane, settings);
        host.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.PackageManager, true);
        var services = new OverrideServices(host,
            (typeof(WingetClient), client),
            (typeof(PackageOperationLane), lane),
            (typeof(PackageManagerService), manager));
        return (services, manager);
    }

    private sealed class NoWinget : IWingetLocator
    {
        public string? Locate() => null;
    }
}
