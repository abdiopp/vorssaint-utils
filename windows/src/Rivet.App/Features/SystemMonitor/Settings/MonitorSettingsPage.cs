// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.App.Features.SystemMonitor.Readouts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Settings;

/// <summary>
/// Settings → Monitor (spec §3.15, §3.16, §3.17, §4): readings, what the panel
/// shows, graphs, the Windows readouts (tray tooltip and mini monitor),
/// alerts and the drives "Eject all" leaves alone.
/// </summary>
public sealed class MonitorSettingsPage : SettingsPage
{
    private readonly IServiceProvider _services;
    private readonly FeatureRuntime _runtime;
    private readonly SystemMonitorService _monitor;
    private readonly StackPanel _tokens = new() { Spacing = 2 };
    private readonly StackPanel _exclusions = new() { Spacing = 2 };
    private readonly ReadoutStrip _preview = new();
    private readonly TextBlock _notificationsNote;

    public MonitorSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _services = services;
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _monitor = services.GetRequiredService<SystemMonitorService>();
        var hasBattery = _monitor.HasBattery;
        _notificationsNote = Note(L.Get("monitorAlerts.notificationsDenied"), "WarningBrush");

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildTokens),
            [.. ReadoutTokens.DefaultOrder.Select(t => (SettingDefinition)ReadoutTokens.Setting(t)), MonitorSettings.ReadoutOrder]));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildExclusions), MonitorSettings.DiskEjectExcluded));
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(UpdateNotificationNote), [.. MonitorSettings.AlertSwitches]));
        _monitor.SnapshotPublished += OnSnapshot;
        DetachedFromVisualTree += (_, _) => _monitor.SnapshotPublished -= OnSnapshot;

        bool Has(string id) => _runtime.IsAvailable(id);
        Content = Stack(
            Header("Strings.tabMonitor", "settingsPages.monitorDescription"),
            ReadingsCard(),
            Has(FeatureIds.MonitorCpu) ? SensorHelperCard() : null,
            PanelCard(Has, hasBattery),
            GraphsCard(Has, hasBattery),
            ReadoutsCard(),
            AlertsCard(Has, hasBattery),
            Has(FeatureIds.MonitorDisk) ? DisksCard() : null);
        RebuildTokens();
        RebuildExclusions();
        UpdateNotificationNote();
        UpdatePreview(_monitor.Latest);
    }

    private SettingsCard ReadingsCard() => Card("monitorLayout.shared",
        Choice(MonitorSettings.IntervalSeconds, "Timer", "Strings.monitorIntervalLabel", null,
            [(1, L.Get("Strings.monitorInterval1")), (2, L.Get("Strings.monitorInterval2")), (5, L.Get("Strings.monitorInterval5"))]),
        Choice(MonitorSettings.TemperatureUnit, "Temperature", "win.systemMonitor.temperatureUnit", null,
            [("celsius", L.Get("win.systemMonitor.celsius")), ("fahrenheit", L.Get("win.systemMonitor.fahrenheit"))]),
        Choice(MonitorSettings.NetworkSpeedUnit, "TopSpeed", "monitorLayout.networkSpeedUnit", null,
            [("bytes", "B/s"), ("bits", "bit/s")]),
        Choice(MonitorSettings.MemoryMetric, "Ram", "Strings.monitorMemoryMetricLabel", null,
            [("used", L.Get("Strings.memoryMetricUsed")), ("app", L.Get("Strings.memoryMetricApp"))]));

    /// <summary>
    /// The elevated helper that reads the processor's own temperature sensors
    /// (Windows on x64 only; absent elsewhere).
    /// </summary>
    private SettingsCard? SensorHelperCard()
    {
        if (_services.GetService<ISensorHelper>() is not { } helper || helper.State == SensorHelperState.Unsupported)
        {
            return null;
        }

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = label, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        var status = Note(string.Empty);
        var getDriver = ActionButton(L.Get("win.systemMonitor.sensorHelperGetDriver"), () => _services.GetService<IShellService>()?.OpenUrl("https://pawnio.eu"), "Open");
        getDriver.HorizontalAlignment = HorizontalAlignment.Left;
        var details = new StackPanel { Spacing = 8, Margin = new Thickness(40, 0, 0, 4), Children = { status, getDriver } };
        var busy = false;
        var failed = false;

        void Refresh()
        {
            var installed = helper.State == SensorHelperState.Installed;
            label.Text = L.Get(installed ? "Strings.actionRemove" : "Strings.homebrewInstall");
            button.IsEnabled = !busy;
            getDriver.IsVisible = !helper.DriverInstalled;
            status.Text = failed ? L.Get("win.systemMonitor.sensorHelperFailed")
                : !helper.DriverInstalled ? L.Get("win.systemMonitor.sensorHelperDriverMissing")
                : installed ? L.Get("win.systemMonitor.sensorHelperInstalled")
                : null;
            status.IsVisible = status.Text is not null;
            details.IsVisible = status.IsVisible || getDriver.IsVisible;
        }

        button.Click += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            failed = false;
            Refresh();
            var result = SensorHelperResult.Failed;
            try
            {
                result = await (helper.State == SensorHelperState.Installed ? helper.RemoveAsync() : helper.InstallAsync());
            }
            finally
            {
                busy = false;
                failed = result == SensorHelperResult.Failed;
                Refresh();
            }
        };

        void OnHelperChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);
        helper.StateChanged += OnHelperChanged;
        DetachedFromVisualTree += (_, _) => helper.StateChanged -= OnHelperChanged;
        Refresh();
        return Card("win.systemMonitor.sensorHelperCard",
            Row("Temperature", L.Get("win.systemMonitor.sensorHelperTitle"), L.Get("win.systemMonitor.sensorHelperCaption"), button),
            details);
    }

    private SettingsCard PanelCard(Func<string, bool> has, bool hasBattery)
    {
        var rows = new List<Control?>
        {
            SubHeader("Strings.systemSection"),
            has(FeatureIds.MonitorCpu) || has(FeatureIds.MonitorGpu) ? Toggle(MonitorSettings.SysTemps, "Temperature", "Strings.temperatures") : null,
            has(FeatureIds.MonitorCpu) ? Toggle(MonitorSettings.SysCpu, "DeveloperBoard", "Strings.monitorShowCPU") : null,
            has(FeatureIds.MonitorCpu) ? Toggle(MonitorSettings.SysCpuCores, "DataBarVertical", "cpuCores.perCore", "cpuCores.hint") : null,
            has(FeatureIds.MonitorGpu) ? Toggle(MonitorSettings.SysGpu, "DeveloperBoardSearch", "Strings.monitorShowGPU") : null,
            has(FeatureIds.MonitorMemory) ? Toggle(MonitorSettings.SysMemory, "Ram", "Strings.monitorShowMemory") : null,
            Toggle(MonitorSettings.SysUptime, "Clock", "Strings.monitorItemUptime"),
            has(FeatureIds.ConnectedDevices) ? Toggle(MonitorSettings.SysConnectedDevices, "UsbPlug", "connectedDevices.title", "connectedDevices.hubDescription") : null,
        };
        if (has(FeatureIds.MonitorNetwork))
        {
            rows.AddRange(
            [
                SubHeader("Strings.networkSection"),
                Toggle(MonitorSettings.NetSpeed, "TopSpeed", "Strings.monitorItemNetSpeed"),
                Toggle(MonitorSettings.NetTotals, "DataUsage", "Strings.monitorItemNetTotals"),
                Toggle(MonitorSettings.NetAddresses, "Globe", "Strings.networkIPAddresses"),
                Toggle(MonitorSettings.NetTest, "Gauge", "Strings.monitorItemNetTest"),
            ]);
        }

        if (has(FeatureIds.MonitorDisk))
        {
            rows.AddRange(
            [
                SubHeader("Strings.diskSection"),
                Toggle(MonitorSettings.DiskUsage, "Storage", "Strings.monitorItemDiskUsage"),
                Toggle(MonitorSettings.DiskActivity, "DataArea", "Strings.monitorItemDiskActivity"),
                Toggle(MonitorSettings.DiskProtection, "ArrowEject", "Strings.monitorItemDiskProtection"),
                Toggle(MonitorSettings.DiskTools, "Wrench", "Strings.monitorItemDiskTools"),
            ]);
        }

        if (has(FeatureIds.MonitorPower))
        {
            rows.Add(SubHeader("Strings.powerSection"));
            if (hasBattery)
            {
                rows.AddRange(
                [
                    Toggle(MonitorSettings.SysBattery, "Battery10", "Strings.batteryCharge"),
                    Toggle(MonitorSettings.PwrTemperature, "Temperature", "Strings.monitorShowBatteryTemperature"),
                ]);
            }

            rows.Add(Toggle(MonitorSettings.PwrSystem, "Flash", "Strings.powerSystem"));
            if (hasBattery)
            {
                rows.AddRange(
                [
                    Toggle(MonitorSettings.PwrBattery, "BatteryCharge", "Strings.powerBattery"),
                    Toggle(MonitorSettings.PwrTimeRemaining, "Clock", "batteryTime.title"),
                    Toggle(MonitorSettings.PwrHealth, "Heart", "Strings.powerHealth"),
                ]);
            }
        }

        return Card("Strings.monitorPanelSection", [.. rows]);
    }

    private SettingsCard GraphsCard(Func<string, bool> has, bool hasBattery) => Card("Strings.monitorGraphsSection",
        has(FeatureIds.MonitorCpu) ? Toggle(MonitorSettings.GraphCpu, "DataLine", "Strings.monitorShowCPU") : null,
        has(FeatureIds.MonitorGpu) ? Toggle(MonitorSettings.GraphGpu, "DataLine", "Strings.monitorShowGPU") : null,
        has(FeatureIds.MonitorMemory) ? Toggle(MonitorSettings.GraphMemory, "DataLine", "Strings.monitorShowMemory") : null,
        has(FeatureIds.MonitorNetwork) ? Toggle(MonitorSettings.GraphNetwork, "DataLine", "Strings.monitorShowNetwork") : null,
        has(FeatureIds.MonitorDisk) ? Toggle(MonitorSettings.GraphDisk, "DataLine", "Strings.diskSection") : null,
        has(FeatureIds.MonitorPower) ? Toggle(MonitorSettings.GraphPower, "DataLine", "Strings.monitorShowPowerLabel") : null,
        has(FeatureIds.MonitorPower) && hasBattery ? Toggle(MonitorSettings.GraphBattery, "DataLine", "Strings.batteryLabel") : null,
        Toggle(MonitorSettings.GraphScale, "DataArea", "GraphScaleStrings.title"));

    private SettingsCard ReadoutsCard()
    {
        var reset = ActionButton(L.Get("win.systemMonitor.miniMonitorResetPosition"), () =>
        {
            Settings.Reset(MonitorSettings.MiniMonitorX.Key);
            Settings.Reset(MonitorSettings.MiniMonitorY.Key);
            _services.GetService<ReadoutController>()?.Refresh();
        }, "ArrowCounterclockwise");
        var previewBox = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 5), HorizontalAlignment = HorizontalAlignment.Left, Child = _preview };
        previewBox.Bind(Border.BackgroundProperty, previewBox.GetResourceObservable("HudBackgroundBrush").ToBinding());
        previewBox.Bind(Border.BorderBrushProperty, previewBox.GetResourceObservable("PanelBorderBrush").ToBinding());

        var card = Card("win.systemMonitor.readoutsTitle",
            Toggle(MonitorSettings.TrayTooltipReadouts, "PanelBottom", "win.systemMonitor.trayTooltipTitle", "win.systemMonitor.trayTooltipCaption"),
            Toggle(MonitorSettings.MiniMonitorEnabled, "WindowNew", "win.systemMonitor.miniMonitorTitle", "win.systemMonitor.miniMonitorCaption"),
            Toggle(MonitorSettings.MiniMonitorClickThrough, "CursorClick", "win.systemMonitor.miniMonitorClickThrough", "win.systemMonitor.miniMonitorClickThroughCaption"),
            Toggle(MonitorSettings.MiniMonitorHideInFullScreen, "ArrowMaximize", "win.systemMonitor.miniMonitorHideFullScreen"),
            Row("ArrowMove", L.Get("win.systemMonitor.miniMonitorPosition"), null, reset),
            Row("Eye", L.Get("win.systemMonitor.readoutPreview"), null, previewBox),
            Row("TextBulletListSquareEdit", L.Get("win.systemMonitor.readoutTokens"), L.Get("win.systemMonitor.readoutTokensCaption")),
            _tokens,
            Choice(MonitorSettings.ReadoutAppearance, "DataBarVertical", "menuBarAppearance.label", "menuBarAppearance.caption",
                [("values", L.Get("menuBarAppearance.values")), ("bars", L.Get("menuBarAppearance.bars"))]),
            Toggle(MonitorSettings.CombineTemperatures, "Temperature", "Strings.monitorCombineTemperatures", "Strings.monitorCombineTemperaturesCaption"),
            Toggle(MonitorSettings.NetworkUploadFirst, "ArrowUp", "Strings.monitorNetworkUploadFirst"),
            Choice(MonitorSettings.MemoryStyle, "Ram", "Strings.monitorMemoryStyleLabel", null,
                [("percent", L.Get("Strings.memoryStylePercent")), ("dot", L.Get("Strings.memoryStyleDot")), ("both", L.Get("Strings.memoryStyleBoth"))]),
            Choice(MonitorSettings.DiskStyle, "Storage", "Strings.diskMenuBarStyleLabel", null,
                [("percent", L.Get("Strings.diskMenuBarUsedPercentage")), ("free", L.Get("Strings.diskMenuBarAvailableSpace")), ("used", L.Get("Strings.diskMenuBarUsedSpace"))]),
            Choice(MonitorSettings.ReadoutSpacing, "ArrowAutofitWidth", "Strings.menuBarSpacingLabel", null,
                [("compact", L.Get("Strings.menuBarSpacingCompact")), ("standard", L.Get("Strings.menuBarSpacingStandard"))]),
            BarsRow(),
            _services.GetService<IReadoutCountdownSource>() is not null
                ? Toggle(MonitorSettings.ShowCountdown, "Timer", "Strings.showCountdown")
                : null);
        card.Description = L.Get("win.systemMonitor.readoutsCaption");
        return card;
    }

    /// <summary>Bars mode thresholds and colours (spec §3.16.3).</summary>
    private Control BarsRow()
    {
        ComboBox Threshold(Setting<int> setting, int min, int max)
        {
            var values = Enumerable.Range(min, max - min + 1).Where(v => v % 5 == 0 || v == min || v == max).ToList();
            var combo = new ComboBox { MinWidth = 90, ItemsSource = values.Select(v => v + "%").ToList() };
            var current = Settings.Get(setting);
            combo.SelectedIndex = Math.Max(0, values.IndexOf(values.OrderBy(v => Math.Abs(v - current)).First()));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                {
                    Settings.Set(setting, values[combo.SelectedIndex]);
                }
            };
            return combo;
        }

        Control ColorButton(Setting<string> setting, string labelKey)
        {
            var picker = new ColorPicker { Color = Color.Parse(Settings.Get(setting)), IsAlphaVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
            picker.ColorChanged += (_, e) => Settings.Set(setting, $"#{e.NewColor.R:X2}{e.NewColor.G:X2}{e.NewColor.B:X2}");
            AutomationProperties.SetName(picker, L.Get(labelKey));
            return new StackPanel { Spacing = 3, Children = { new TextBlock { Text = L.Get(labelKey), Classes = { "caption" } }, picker } };
        }

        var thresholds = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new StackPanel { Spacing = 3, Children = { new TextBlock { Text = L.Get("menuBarAppearance.mediumFrom"), Classes = { "caption" } }, Threshold(MonitorSettings.BarMediumThreshold, 1, 99) } },
                new StackPanel { Spacing = 3, Children = { new TextBlock { Text = L.Get("menuBarAppearance.highFrom"), Classes = { "caption" } }, Threshold(MonitorSettings.BarHighThreshold, 2, 100) } },
            },
        };
        var colors = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                ColorButton(MonitorSettings.BarNormalColor, "menuBarAppearance.normalColor"),
                ColorButton(MonitorSettings.BarElevatedColor, "menuBarAppearance.mediumColor"),
                ColorButton(MonitorSettings.BarCriticalColor, "menuBarAppearance.highColor"),
            },
        };
        return new Rivet.App.Features.SystemMonitor.Controls.Fold(L.Get("menuBarAppearance.customize"),
            () => new StackPanel { Spacing = 10, Margin = new Thickness(0, 4), Children = { thresholds, colors } },
            initiallyOpen: false, fontSize: 14, indent: 40, secondary: false);
    }

    private void RebuildTokens()
    {
        _tokens.Children.Clear();
        var order = ReadoutTokens.ParseOrder(Settings.Get(MonitorSettings.ReadoutOrder));
        var shown = order.Where(t => _runtime.IsAvailable(ReadoutTokens.FeatureId(t)) && (!ReadoutTokens.NeedsBattery(t) || _monitor.HasBattery)).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var token = shown[i];
            var index = order.IndexOf(token);
            var up = new Button { Classes = { "icon" }, IsEnabled = i > 0, Content = new SymbolIcon { Symbol = Symbol.ChevronUp, FontSize = 13 } };
            var down = new Button { Classes = { "icon" }, IsEnabled = i < shown.Count - 1, Content = new SymbolIcon { Symbol = Symbol.ChevronDown, FontSize = 13 } };
            ToolTip.SetTip(up, L.Get("Strings.monitorOrderHint"));
            ToolTip.SetTip(down, L.Get("Strings.monitorOrderHint"));
            AutomationProperties.SetName(up, L.Get("Strings.monitorOrderHint"));
            AutomationProperties.SetName(down, L.Get("Strings.monitorOrderHint"));
            var neighbourUp = i > 0 ? shown[i - 1] : token;
            var neighbourDown = i < shown.Count - 1 ? shown[i + 1] : token;
            up.Click += (_, _) => Swap(order, token, neighbourUp);
            down.Click += (_, _) => Swap(order, token, neighbourDown);
            var setting = ReadoutTokens.Setting(token);
            var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = Settings.Get(setting) };
            toggle.IsCheckedChanged += (_, _) => Settings.Set(setting, toggle.IsChecked == true);
            AutomationProperties.SetName(toggle, ReadoutTokens.Title(token));
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 6, Margin = new Thickness(40, 0, 0, 0) };
            grid.Children.Add(up);
            Grid.SetColumn(down, 1);
            grid.Children.Add(down);
            var text = new TextBlock { Text = ReadoutTokens.Title(token), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
            Grid.SetColumn(toggle, 3);
            grid.Children.Add(toggle);
            _ = index;
            _tokens.Children.Add(grid);
        }
    }

    private void Swap(List<ReadoutToken> order, ReadoutToken a, ReadoutToken b)
    {
        if (a == b)
        {
            return;
        }

        var ia = order.IndexOf(a);
        var ib = order.IndexOf(b);
        (order[ia], order[ib]) = (order[ib], order[ia]);
        Settings.Set(MonitorSettings.ReadoutOrder, ReadoutTokens.ToCsv(order));
    }

    private void OnSnapshot(object? sender, MonitorSnapshot snapshot) => Dispatcher.UIThread.Post(() => UpdatePreview(snapshot));

    private void UpdatePreview(MonitorSnapshot snapshot)
    {
        var tokens = ReadoutComposer.EnabledTokens(Settings, _runtime, _monitor.HasBattery);
        var blocks = ReadoutComposer.Compose(tokens, snapshot, ReadoutStyle.From(Settings));
        _preview.Update(blocks, null, Settings);
        if (blocks.Count == 0)
        {
            _preview.Update([], "–", Settings);
        }
    }

    private SettingsCard AlertsCard(Func<string, bool> has, bool hasBattery)
    {
        var unit = MetricFormat.ParseTemperatureUnit(Settings.Get(MonitorSettings.TemperatureUnit));
        var culture = CultureInfo.CurrentCulture;
        string Percent(int v) => v.ToString(culture) + "%";
        string Degrees(int v) => MetricFormat.Temperature(v, unit, culture);
        var cooldown = Choice(MonitorSettings.AlertCooldownMinutes, "ArrowRepeatAll", "monitorAlerts.cooldown", null,
            [(2, L.Get("monitorAlerts.cooldown2")), (5, L.Get("monitorAlerts.cooldown5")), (15, L.Get("monitorAlerts.cooldown15")), (30, L.Get("monitorAlerts.cooldown30")), (60, L.Get("monitorAlerts.cooldown60"))]);
        void UpdateCooldown() => cooldown.IsEnabled = MonitorSettings.AlertSwitches.Any(Settings.Get);
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(UpdateCooldown), [.. MonitorSettings.AlertSwitches]));
        UpdateCooldown();

        var card = Card("monitorAlerts.section",
            has(FeatureIds.MonitorCpu) ? AlertRow(MonitorSettings.AlertCpu, "DeveloperBoard", "monitorAlerts.cpu", MonitorSettings.AlertCpuThreshold, "monitorAlerts.cpuThreshold", 50, 100, Percent) : null,
            has(FeatureIds.MonitorCpu) ? AlertRow(MonitorSettings.AlertCpuTemperature, "Temperature", "monitorAlerts.cpuTemperature", MonitorSettings.AlertCpuTemperatureThreshold, "monitorAlerts.cpuTemperatureThreshold", 70, 105, Degrees) : null,
            has(FeatureIds.MonitorPower) && hasBattery ? AlertRow(MonitorSettings.AlertBatteryTemperature, "Temperature", "monitorAlerts.batteryTemperature", MonitorSettings.AlertBatteryTemperatureThreshold, "monitorAlerts.batteryTemperatureThreshold", 30, 50, Degrees) : null,
            has(FeatureIds.MonitorMemory) ? Toggle(MonitorSettings.AlertMemory, "Ram", "monitorAlerts.memory") : null,
            has(FeatureIds.MonitorDisk) ? AlertRow(MonitorSettings.AlertDisk, "Storage", "monitorAlerts.disk", MonitorSettings.AlertDiskFreePercent, "monitorAlerts.diskThreshold", 5, 30, Percent) : null,
            has(FeatureIds.MonitorPower) && hasBattery ? AlertRow(MonitorSettings.AlertBattery, "Battery2", "monitorAlerts.battery", MonitorSettings.AlertBatteryPercent, "monitorAlerts.batteryThreshold", 5, 50, Percent) : null,
            cooldown,
            new StackPanel { Spacing = 6, Children = { Note(L.Get("monitorAlerts.caption")), _notificationsNote } });
        return card;
    }

    /// <summary>A switch plus its limit ("CPU above 90%"), as a drop-down of the allowed steps.</summary>
    private Control AlertRow(Setting<bool> enabled, string icon, string titleKey, Setting<int> threshold, string thresholdKey, int min, int max, Func<int, string> format)
    {
        var values = Enumerable.Range(0, ((max - min) / 5) + 1).Select(i => min + (i * 5)).ToList();
        var combo = new ComboBox { MinWidth = 110, ItemsSource = values.Select(format).ToList(), SelectedIndex = Math.Max(0, values.IndexOf(Settings.Get(threshold))) };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                Settings.Set(threshold, values[combo.SelectedIndex]);
            }
        };
        AutomationProperties.SetName(combo, L.Get(thresholdKey));
        var limit = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(40, 0, 0, 0), Children = { new TextBlock { Text = L.Get(thresholdKey), VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } }, combo } };
        var property = Track(Settings.Bind(enabled));
        combo.IsEnabled = property.Value;
        property.PropertyChanged += (_, _) => combo.IsEnabled = property.Value;
        return new StackPanel { Spacing = 4, Children = { Toggle(enabled, icon, titleKey), limit } };
    }

    private void UpdateNotificationNote()
    {
        var notifications = _services.GetService<INotificationService>();
        _notificationsNote.IsVisible = notifications is { IsEnabled: false } && MonitorSettings.AlertSwitches.Any(Settings.Get);
    }

    private SettingsCard DisksCard()
    {
        var add = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new SymbolIcon { Symbol = Symbol.Add, FontSize = 14 }, new TextBlock { Text = L.Get("diskExclusions.addButton") } } } };
        add.Click += (_, _) => ShowAddMenu(add);
        var card = Card("diskExclusions.listTitle",
            new StackPanel { Spacing = 8, Children = { Note(L.Get("diskExclusions.caption")), _exclusions, add } });
        return card;
    }

    private void RebuildExclusions()
    {
        _exclusions.Children.Clear();
        var list = Settings.Get(MonitorSettings.DiskEjectExcluded);
        _exclusions.IsVisible = list.Count > 0;
        foreach (var entry in list)
        {
            var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.Subtract, FontSize = 13 } };
            ToolTip.SetTip(remove, L.Get("diskExclusions.removeButton"));
            AutomationProperties.SetName(remove, L.Get("diskExclusions.removeButton"));
            var value = entry;
            remove.Click += (_, _) => Settings.Set(MonitorSettings.DiskEjectExcluded, Settings.Get(MonitorSettings.DiskEjectExcluded).Where(e => !string.Equals(e, value, StringComparison.OrdinalIgnoreCase)).ToList());
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            grid.Children.Add(remove);
            var text = new TextBlock { Text = entry, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            _exclusions.Children.Add(grid);
        }
    }

    private void ShowAddMenu(Control anchor)
    {
        var excluded = Settings.Get(MonitorSettings.DiskEjectExcluded);
        var tracker = _services.GetService<DiskEjectTracker>();
        var menu = new MenuFlyout();
        foreach (var disk in _monitor.Latest.Disks.Select(d => d.Info).Where(v => v.IsEjectable && tracker?.IsExcluded(v) != true))
        {
            var item = new MenuItem { Header = DisksSection.DisplayName(disk) };
            var name = disk.Name;
            item.Click += (_, _) => Settings.Set(MonitorSettings.DiskEjectExcluded, [.. excluded, name]);
            menu.Items.Add(item);
        }

        var other = new MenuItem { Header = L.Get("diskExclusions.otherDrive") };
        other.Click += (_, _) => ShowCustomEntry(anchor);
        menu.Items.Add(other);
        menu.ShowAt(anchor);
    }

    private void ShowCustomEntry(Control anchor)
    {
        var box = new TextBox { PlaceholderText = L.Get("diskExclusions.customPlaceholder"), MinWidth = 220 };
        var ok = new Button { Content = L.Get("diskExclusions.addButton").TrimEnd('…', '.'), Classes = { "accent" } };
        var flyout = new Flyout { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, ok } } };
        void Commit()
        {
            var text = box.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                Settings.Set(MonitorSettings.DiskEjectExcluded, [.. Settings.Get(MonitorSettings.DiskEjectExcluded), text]);
            }

            flyout.Hide();
        }

        ok.Click += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Commit();
            }
        };
        flyout.ShowAt(anchor);
        box.Focus();
    }

    private static TextBlock SubHeader(string key) =>
        new() { Text = L.Get(key).ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(0, 6, 0, 0) };
}
