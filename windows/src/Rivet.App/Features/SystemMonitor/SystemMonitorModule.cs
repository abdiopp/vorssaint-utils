// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.App.Features.SystemMonitor.Readouts;
using Rivet.App.Features.SystemMonitor.Settings;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor;

/// <summary>
/// The system monitor: demand-driven sampler, the System / Network / Disks /
/// Power panel tabs with detail views, alerts, the tray tooltip summary and
/// the mini monitor window, and Settings → Monitor.
/// </summary>
public sealed class SystemMonitorModule : IFeatureModule
{
    public const string SystemSectionId = "system";
    public const string NetworkSectionId = "network";
    public const string DiskSectionId = "disk";
    public const string PowerSectionId = "power";
    public const string SettingsPageId = "monitor";

    public static readonly string[] Families =
    [
        FeatureIds.MonitorCpu, FeatureIds.MonitorGpu, FeatureIds.MonitorMemory, FeatureIds.MonitorNetwork,
        FeatureIds.MonitorDisk, FeatureIds.MonitorPower, FeatureIds.ConnectedDevices,
    ];

    public string Id => "systemMonitor";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new MonitorSensors(
            sp.GetRequiredService<IMonitorClock>(),
            sp.GetRequiredService<ICpuSensor>(),
            sp.GetRequiredService<IMemorySensor>(),
            sp.GetRequiredService<IGpuSensor>(),
            sp.GetRequiredService<INetworkSensor>(),
            sp.GetRequiredService<IDiskSensor>(),
            sp.GetRequiredService<IPowerSensor>(),
            sp.GetRequiredService<ITemperatureSensor>(),
            sp.GetRequiredService<IUsbSensor>(),
            sp.GetRequiredService<IPeripheralBatterySensor>(),
            sp.GetRequiredService<IProcessSampler>()));
        services.AddSingleton<SystemMonitorService>();
        services.AddSingleton<ProcessUsageService>();
        services.AddSingleton(_ => new SpeedTest());
        services.AddSingleton(sp => new MonitorAlertService(
            sp.GetRequiredService<SystemMonitorService>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<FeatureRuntime>(),
            sp.GetRequiredService<INotificationService>()));
        services.AddSingleton<ReadoutController>();
        services.AddSingleton<MonitorNavigation>();
        services.AddSingleton(sp => new DiskEjectTracker(
            sp.GetRequiredService<IDiskActions>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<SystemMonitorService>()));
    }

    public void Initialize(ModuleContext context)
    {
        var runtime = context.Features;
        var settings = context.Settings;
        var monitor = context.Get<SystemMonitorService>();
        var readouts = context.Get<ReadoutController>();
        // Created now so it follows every published snapshot.
        context.Get<MonitorAlertService>();

        // Every family re-plans the sampler; the readouts follow "any family installed".
        var controller = new DelegateFeatureController(_ =>
        {
            monitor.Invalidate();
            readouts.Sync(Families.Any(runtime.IsAvailable));
        });
        foreach (var family in Families)
        {
            runtime.RegisterController(family, controller);
        }

        var panel = context.Panel;
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = SystemSectionId,
            TitleKey = "Strings.systemSection",
            Icon = "DeveloperBoard",
            FeatureIds = [FeatureIds.MonitorCpu, FeatureIds.MonitorGpu, FeatureIds.MonitorMemory, FeatureIds.ConnectedDevices],
            Order = 10,
            CreateView = sp => new SystemSection(sp),
            SettingsPageId = SettingsPageId,
        });
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = NetworkSectionId,
            TitleKey = "Strings.networkSection",
            Icon = "Globe",
            FeatureIds = [FeatureIds.MonitorNetwork],
            Order = 20,
            CreateView = sp => new NetworkSection(sp),
            SettingsPageId = SettingsPageId,
        });
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = DiskSectionId,
            TitleKey = "Strings.diskSection",
            Icon = "Storage",
            FeatureIds = [FeatureIds.MonitorDisk],
            Order = 30,
            CreateView = sp => new DisksSection(sp),
            SettingsPageId = SettingsPageId,
        });
        panel.AddSection(new PanelSectionDescriptor
        {
            Id = PowerSectionId,
            TitleKey = "Strings.powerSection",
            Icon = "Flash",
            FeatureIds = [FeatureIds.MonitorPower],
            Order = 40,
            CreateView = sp => new PowerSection(sp),
            // Desktops without a battery have nothing to show here (no whole-system meter on Windows).
            IsVisible = () => monitor.HasBattery || settings.Get(MonitorSettings.ReadoutPeripheralBattery),
            SettingsPageId = SettingsPageId,
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageId,
            TitleKey = "Strings.tabMonitor",
            Icon = "DataTrending",
            Category = SettingsCategory.Monitor,
            Order = 0,
            FeatureIds = Families,
            CreateView = sp => new MonitorSettingsPage(sp),
            KeywordKeys =
            [
                "monitorAlerts.section", "Strings.monitorIntervalLabel", "Strings.monitorGraphsSection", "win.systemMonitor.readoutsTitle",
                "win.systemMonitor.miniMonitorTitle", "Strings.cpuLabel", "Strings.gpuLabel", "Strings.memorySection", "Strings.networkSection",
                "Strings.diskSection", "Strings.powerSection", "connectedDevices.title", "diskExclusions.listTitle", "Strings.temperatures",
            ],
            Keywords = ["CPU", "GPU", "RAM", "temperature", "battery", "network", "disk", "USB", "alert"],
        });

        var actions = context.Actions;
        actions.Register(new AppAction
        {
            Id = "systemMonitor.toggleMiniMonitor",
            FeatureId = FeatureIds.MonitorCpu,
            TitleKey = "win.systemMonitor.toggleMiniMonitor",
            Icon = "WindowNew",
            ClosesPanel = false,
            Keywords = ["mini monitor", "overlay"],
            Run = _ =>
            {
                settings.Set(MonitorSettings.MiniMonitorEnabled, !settings.Get(MonitorSettings.MiniMonitorEnabled));
                return Task.CompletedTask;
            },
        });
        actions.Register(new AppAction
        {
            Id = "systemMonitor.openTaskManager",
            FeatureId = FeatureIds.MonitorCpu,
            TitleKey = "Strings.monitorOpenActivityMonitor",
            Icon = "Open",
            Keywords = ["Task Manager", "taskmgr"],
            Run = _ =>
            {
                var control = context.Get<IProcessControl>();
                return Task.Run(control.OpenTaskManager);
            },
        });
    }
}
