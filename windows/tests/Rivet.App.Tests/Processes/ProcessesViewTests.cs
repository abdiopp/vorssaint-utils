// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Processes;
using Rivet.App.Tests.Maintenance;
using Rivet.Core.Features;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Processes;
using Xunit;

namespace Rivet.App.Tests.Processes;

public class ProcessesViewTests
{
    [AvaloniaFact]
    public async Task Kill_process_panel_and_settings_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, processes, _, _) = Fresh();
            await processes.RefreshAsync(force: true);

            // Grouped: one Chrome row for its five processes; critical system processes are protected.
            var chrome = Assert.Single(processes.Rows, r => r.ImageName == "chrome.exe");
            Assert.Equal(5, chrome.MemberCount);
            Assert.True(processes.Rows.Single(r => r.ImageName == "lsass.exe").IsProtected);
            Assert.DoesNotContain(processes.Rows, r => r.Pid == 99999);
            MaintenanceSnapshots.Panel(() => new KillProcessView(services, compact: true), $"killProcess-panel-{theme}", "killProcess.pageTitle", "ErrorCircle", variant);
            MaintenanceSnapshots.SettingsPage(services, KillProcessModule.PageId, $"settings-killProcess-{theme}", variant, height: 1000);
        }
    }

    [AvaloniaFact]
    public async Task Port_manager_panel_and_settings_render()
    {
        foreach (var (theme, variant) in MaintenanceSnapshots.Themes)
        {
            var (services, _, ports, settings) = Fresh();
            await ports.RefreshAsync();
            Assert.Contains(ports.Rows, r => r.Port == 5432 && r.ProcessName.Contains("postgres", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(ports.Rows, r => r.Protocol == PortProtocol.Udp);
            MaintenanceSnapshots.Panel(() => new PortManagerView(services, compact: true), $"portManager-panel-{theme}", "portManager.title", "PlugConnected", variant);

            settings.Set(ProcessSettings.PortManagerShowUdp, true);
            await ports.RefreshAsync();
            Assert.Contains(ports.Rows, r => r.Protocol == PortProtocol.Udp && r.Port == 5353);
            MaintenanceSnapshots.SettingsPage(services, PortManagerModule.PageId, $"settings-portManager-{theme}", variant, height: 1000);
        }
    }

    [AvaloniaFact]
    public async Task Force_kill_through_the_service_removes_the_process()
    {
        var (_, processes, _, _) = Fresh();
        await processes.RefreshAsync(force: true);
        var spotify = processes.Rows.Single(r => r.ImageName == "Spotify.exe");
        var report = await processes.KillAsync(spotify, KillMode.ForceKill);
        Assert.True(report.Succeeded);
        await processes.RefreshAsync(force: true);
        Assert.DoesNotContain(processes.Rows, r => r.ImageName == "Spotify.exe");
    }

    [Fact]
    public void Command_bar_matches_pid_then_prefix_then_substring()
    {
        var row = new ProcessRow { Identity = new ProcessIdentity(7020, 1), Name = "Visual Studio Code", ImageName = "Code.exe" };
        Assert.Equal(1, ProcessSearchProvider.Score(row, "7020"));
        Assert.Equal(0.9, ProcessSearchProvider.Score(row, "visual"));
        Assert.Equal(0.9, ProcessSearchProvider.Score(row, "code.e"));
        Assert.Equal(0.6, ProcessSearchProvider.Score(row, "studio"));
        Assert.Equal(0, ProcessSearchProvider.Score(row, "702"));
    }

    /// <summary>Process and port services over their own fake platform and settings, so tests never share state.</summary>
    private static (IServiceProvider Services, ProcessService Processes, PortService Ports, ISettingsStore Settings) Fresh()
    {
        var host = TestApp.Host.Services;
        var settings = SettingsStore.InMemory();
        var platform = new FakeProcessPlatform();
        var processes = new ProcessService(platform, settings);
        var ports = new PortService(new FakePortTable(), platform, settings);
        var runtime = host.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.KillProcess, true);
        runtime.SetAvailable(FeatureIds.PortManager, true);
        var services = new OverrideServices(host,
            (typeof(ISettingsStore), settings),
            (typeof(IProcessPlatform), platform),
            (typeof(ProcessService), processes),
            (typeof(PortService), ports));
        return (services, processes, ports, settings);
    }
}
