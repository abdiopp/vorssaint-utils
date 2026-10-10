// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.App.Features.SystemMonitor.Readouts;
using Rivet.App.Features.SystemMonitor.Settings;
using Rivet.App.Modules;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;
using Rivet.Platform.Fake.SystemMonitor;
using Xunit;

namespace Rivet.App.Tests.SystemMonitor;

/// <summary>Renders the monitor's panel tabs, detail views, readouts and settings page (light and dark).</summary>
public class MonitorRenderTests
{
    /// <summary>Fills the fake sensors' histories: 60 refreshes, 2 s apart on the fake clock.</summary>
    internal static void Prime(IServiceProvider services, int samples = 60)
    {
        var clock = services.GetRequiredService<FakeMonitorClock>();
        var monitor = services.GetRequiredService<SystemMonitorService>();
        var everything = new SamplingPlan
        {
            Cpu = true, CpuCores = true, Memory = true, Network = true, Disk = true, Power = true, PeripheralBattery = true,
            GpuUsage = true, CpuTemperature = true, GpuTemperature = true, BatteryTemperature = true, ConnectedDevices = true,
        };
        for (var i = 0; i < samples; i++)
        {
            clock.Advance(2);
            monitor.RefreshWith(everything);
        }
    }

    private static Control Panel(Control content)
    {
        var surface = new Border { Width = 340, Padding = new Thickness(12), Child = content };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        return surface;
    }

    private static ThemeVariant Theme(string theme) => theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Sections_render(string theme)
    {
        var services = TestApp.Host.Services;
        Prime(services);
        TestApp.Snapshot(Panel(new SystemSection(services)), $"monitor-system-{theme}", 340, theme: Theme(theme));
        TestApp.Snapshot(Panel(new NetworkSection(services)), $"monitor-network-{theme}", 340, theme: Theme(theme));
        TestApp.Snapshot(Panel(new DisksSection(services)), $"monitor-disks-{theme}", 340, theme: Theme(theme));
        TestApp.Snapshot(Panel(new PowerSection(services)), $"monitor-power-{theme}", 340, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void System_section_with_cpu_breakdown_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Prime(services);
        var usage = services.GetRequiredService<ProcessUsageService>();
        var clock = services.GetRequiredService<FakeMonitorClock>();
        var monitor = services.GetRequiredService<SystemMonitorService>();
        using (usage.Acquire(ProcessListKind.Cpu))
        using (monitor.AcquireSurface(MonitorSurface.System))
        {
            for (var i = 0; i < 100 && usage.Rows(ProcessListKind.Cpu) is null; i++)
            {
                clock.Advance(2);
                monitor.RefreshNow();
                Thread.Sleep(20);
            }

            var section = new SystemSection(services);
            section.Expanded = Breakdown.Cpu;
            TestApp.Snapshot(Panel(section), $"monitor-system-cpu-{theme}", 340, theme: Theme(theme));
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Detail_views_render(string theme)
    {
        var services = TestApp.Host.Services;
        Prime(services);
        var monitor = services.GetRequiredService<SystemMonitorService>();
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            var view = new MetricDetailView(services, kind, () => { });
            view.Update(monitor.Latest, MonitorUi.Formats.From(services.GetRequiredService<ISettingsStore>()));
            TestApp.Snapshot(Panel(view), $"monitor-detail-{kind.ToString().ToLowerInvariant()}-{theme}", 340, theme: Theme(theme));
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Edit_mode_renders(string theme)
    {
        var services = TestApp.Host.Services;
        Prime(services, 4);
        var section = new SystemSection(services);
        var window = new Window { Width = 340, SizeToContent = SizeToContent.Height, Content = Panel(section), RequestedThemeVariant = Theme(theme) };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("WindowBackgroundBrush").ToBinding());
        window.Show();
        var edit = FindButton(section, "win.shell.editPanel");
        Assert.NotNull(edit);
        edit!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(FindButton(section, "win.shell.reset"));
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
#pragma warning disable CS0618
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"monitor-system-edit-{theme}.png"));
#pragma warning restore CS0618
        window.Close();
    }

    private static Button? FindButton(Control root, string tooltipKey)
    {
        var text = Core.Localization.L.Get(tooltipKey);
        return root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => ToolTip.GetTip(b) as string == text);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Readout_strip_renders_values_and_bars(string theme)
    {
        var services = TestApp.Host.Services;
        Prime(services, 6);
        var monitor = services.GetRequiredService<SystemMonitorService>();
        var settings = SettingsStore.InMemory();
        var all = ReadoutTokens.DefaultOrder.ToList();
        var stack = new StackPanel { Spacing = 8 };
        foreach (var bars in new[] { false, true })
        {
            settings.Set(MonitorSettings.ReadoutAppearance, bars ? "bars" : "values");
            settings.Set(MonitorSettings.MemoryStyle, "both");
            var strip = new ReadoutStrip();
            strip.Update(ReadoutComposer.Compose(all, monitor.Latest, ReadoutStyle.From(settings)), bars ? null : "1:05", settings);
            var box = new Border { Padding = new Thickness(9, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = strip };
            box.Bind(Border.BackgroundProperty, box.GetResourceObservable("HudBackgroundBrush").ToBinding());
            box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("PanelBorderBrush").ToBinding());
            stack.Children.Add(box);
        }

        TestApp.Snapshot(new Border { Padding = new Thickness(12), Child = stack }, $"monitor-readouts-{theme}", 900, theme: Theme(theme));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var services = TestApp.Host.Services;
        var settings = services.GetRequiredService<ISettingsStore>();
        settings.Set(MonitorSettings.ReadoutCpu, true);
        settings.Set(MonitorSettings.ReadoutMemory, true);
        settings.Set(MonitorSettings.ReadoutNetwork, true);
        settings.Set(MonitorSettings.AlertCpu, true);
        Prime(services, 4);
        try
        {
            var page = new MonitorSettingsPage(services);
            TestApp.Snapshot(new Border { Padding = new Thickness(24), Child = page }, $"settings-monitor-page-{theme}", 820, 5000, Theme(theme));
        }
        finally
        {
            settings.Reset(MonitorSettings.ReadoutCpu.Key);
            settings.Reset(MonitorSettings.ReadoutMemory.Key);
            settings.Reset(MonitorSettings.ReadoutNetwork.Key);
            settings.Reset(MonitorSettings.AlertCpu.Key);
        }
    }

    [AvaloniaFact]
    public void Sensor_helper_card_installs_and_removes()
    {
        var services = TestApp.Host.Services;
        var helper = services.GetRequiredService<FakeSensorHelper>();
        helper.State = SensorHelperState.NotInstalled;
        helper.DriverInstalled = false;
        helper.NextResult = SensorHelperResult.Done;
        try
        {
            var page = new MonitorSettingsPage(services);
            var window = new Window { Width = 820, Height = 900, Content = page };
            window.Show();
            Button Action() => page.GetLogicalDescendants().OfType<Button>()
                .First(b => b.Content is TextBlock { Text: var text } && (text == L.Get("Strings.homebrewInstall") || text == L.Get("Strings.actionRemove")));
            bool Shows(string key) => page.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == L.Get(key));

            Assert.Equal(L.Get("Strings.homebrewInstall"), ((TextBlock)Action().Content!).Text);
            Assert.True(Shows("win.systemMonitor.sensorHelperDriverMissing"));
            TestApp.Snapshot(new Border { Padding = new Thickness(24), Child = new MonitorSettingsPage(services) }, "settings-monitor-sensor-helper", 820, 620);

            helper.DriverInstalled = true;
            Action().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(SensorHelperState.Installed, helper.State);
            Assert.Equal(L.Get("Strings.actionRemove"), ((TextBlock)Action().Content!).Text);
            Assert.True(Shows("win.systemMonitor.sensorHelperInstalled"));

            // A failed removal (or a declined prompt) leaves it installed and says so.
            helper.NextResult = SensorHelperResult.Failed;
            Action().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(SensorHelperState.Installed, helper.State);
            Assert.True(Shows("win.systemMonitor.sensorHelperFailed"));

            helper.NextResult = SensorHelperResult.Done;
            Action().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(SensorHelperState.NotInstalled, helper.State);
            Assert.False(Shows("win.systemMonitor.sensorHelperFailed"));
            window.Close();
        }
        finally
        {
            helper.State = SensorHelperState.NotInstalled;
            helper.DriverInstalled = true;
            helper.NextResult = SensorHelperResult.Done;
        }
    }

    [AvaloniaFact]
    public void Monitor_sections_and_page_are_registered()
    {
        var services = TestApp.Host.Services;
        var panel = services.GetRequiredService<PanelRegistry>();
        Assert.Contains(panel.Sections, s => s.Id == SystemMonitorModule.SystemSectionId && s.Order == 10);
        Assert.Contains(panel.Sections, s => s.Id == SystemMonitorModule.NetworkSectionId && s.Order == 20);
        Assert.Contains(panel.Sections, s => s.Id == SystemMonitorModule.DiskSectionId && s.Order == 30);
        Assert.Contains(panel.Sections, s => s.Id == SystemMonitorModule.PowerSectionId && s.Order == 40);
        Assert.NotNull(services.GetRequiredService<SettingsPageRegistry>().Find(SystemMonitorModule.SettingsPageId));
    }

    [AvaloniaFact]
    public void Sampler_is_idle_without_consumers()
    {
        var services = TestApp.Host.Services;
        var monitor = services.GetRequiredService<SystemMonitorService>();
        Assert.False(monitor.RefreshNow().Plan.Any);
    }

    [AvaloniaFact]
    public void Tray_tooltip_carries_the_pinned_readouts()
    {
        var services = TestApp.Host.Services;
        var settings = services.GetRequiredService<ISettingsStore>();
        var readouts = services.GetRequiredService<ReadoutController>();
        try
        {
            settings.Set(MonitorSettings.ReadoutCpu, true);
            settings.Set(MonitorSettings.ReadoutMemory, true);
            Prime(services, 4);
            readouts.Refresh();
            Assert.Contains(readouts.CurrentBlocks, b => b.Token == ReadoutToken.Memory);
            Assert.True(services.GetRequiredService<SystemMonitorService>().RefreshNow().Plan.Memory);
        }
        finally
        {
            settings.Reset(MonitorSettings.ReadoutCpu.Key);
            settings.Reset(MonitorSettings.ReadoutMemory.Key);
            readouts.Refresh();
        }

        Assert.Empty(readouts.CurrentBlocks);
    }
}
