// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>
/// The Power tab (spec §3.9.3): charge, battery temperature, system draw,
/// battery flow, time remaining, health and peripheral batteries. Windows has
/// no adapter or whole-system meter on AC, so those rows stay hidden there.
/// </summary>
internal sealed class PowerSection : MonitorSectionBase
{
    private bool _empty;

    public PowerSection(IServiceProvider services)
        : base(services, MonitorSurface.Power, "Strings.powerSection", MonitorSettings.PowerOrder)
    {
    }

    protected override string SectionId => SystemMonitorModule.PowerSectionId;

    protected override string? EmptyText => L.Get("Strings.powerUnavailable");

    protected override IEnumerable<SettingDefinition> LayoutKeys =>
        [MonitorSettings.GraphPower, MonitorSettings.GraphBattery, MonitorSettings.ReadoutPeripheralBattery];

    protected override IReadOnlyList<BlockSpec> Blocks =>
    [
        new("charge", "Strings.batteryCharge", "Battery10", [("Strings.batteryCharge", MonitorSettings.SysBattery)], () => Monitor.HasBattery, () => new ChargeBlock(this)),
        new("temperature", "Strings.monitorShowBatteryTemperature", "Temperature", [("Strings.monitorShowBatteryTemperature", MonitorSettings.PwrTemperature)], () => Monitor.HasBattery, () => new TemperatureBlock()),
        new("system", "Strings.powerSystem", "Flash", [("Strings.powerSystem", MonitorSettings.PwrSystem)], () => true, () => new SystemBlock(this)),
        new("adapter", "Strings.powerAdapter", "PlugConnected", [("Strings.powerAdapter", MonitorSettings.PwrAdapter)], () => false, () => throw new NotSupportedException()),
        new("battery", "Strings.powerBattery", "BatteryCharge", [("Strings.powerBattery", MonitorSettings.PwrBattery)], () => Monitor.HasBattery, () => new FlowBlock()),
        new("remaining", "batteryTime.title", "Clock", [("batteryTime.title", MonitorSettings.PwrTimeRemaining)], () => Monitor.HasBattery, () => new RemainingBlock()),
        new("health", "Strings.powerHealth", "Heart", [("Strings.powerHealth", MonitorSettings.PwrHealth)], () => Monitor.HasBattery, () => new HealthBlock()),
        new("peripherals", "Strings.monitorShowPeripheralBattery", "Bluetooth", [], () => Settings.Get(MonitorSettings.ReadoutPeripheralBattery), () => new PeripheralsBlock()),
    ];

    protected override bool ShowEmptyState(MonitorSnapshot s) => s.Plan.Power && IsEmpty(s);

    protected override void AfterUpdate(MonitorSnapshot snapshot)
    {
        var empty = ShowEmptyState(snapshot);
        if (empty != _empty)
        {
            _empty = empty;
            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private static bool IsEmpty(MonitorSnapshot s) =>
        s.Power is null || (!s.Power.HasBattery && s.Power.SystemWatts is null && s.PeripheralBatteries.Count == 0);

    /// <summary>"Charging", "Plugged in", "On battery" or "unavailable" (spec §3.9.2).</summary>
    public static string State(PowerReading? power) => power switch
    {
        { IsCharging: true } => L.Get("Strings.powerCharging"),
        { ExternalConnected: true } => L.Get("Strings.powerPluggedIn"),
        { HasBattery: true } => L.Get("Strings.powerOnBattery"),
        _ => L.Get("Strings.powerUnavailable"),
    };

    public static string BatteryIcon(PowerReading power) =>
        power.IsCharging || power.ExternalConnected ? "BatteryCharge" : $"Battery{BatteryMath.GlyphLevel(power.ChargePercent ?? 0) / 10}";

    private sealed class ChargeBlock : BlockView
    {
        private readonly PowerSection _owner;
        private readonly UsageBar _bar = new() { Height = 6 };
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);
        private readonly FluentIcons.Avalonia.SymbolIcon _icon = MonitorUi.Icon("Battery10", 15, "MetricGreenBrush");
        private readonly Sparkline _graph = MonitorUi.Graph("MetricGreenBrush");

        public ChargeBlock(PowerSection owner)
        {
            _owner = owner;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,Auto,*,44"), ColumnSpacing = 8 };
            grid.Children.Add(_icon);
            var title = MonitorUi.Text(L.Get("Strings.batteryLabel"), 12.5, FontWeight.Medium);
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            Grid.SetColumn(_bar, 2);
            grid.Children.Add(_bar);
            _value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_value, 3);
            grid.Children.Add(_value);
            var energy = new Fold(L.Get("Strings.energyAppsTitle"), () => new ProcessListView(owner.Services, ProcessListKind.Energy, emptyKey: "Strings.energyAppsIdle"), initiallyOpen: false);
            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Battery));
            Root = MonitorUi.Block(L.Get("Strings.batteryCharge"), new StackPanel { Spacing = 5, Children = { grid, _graph, energy } }, details);
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var power = s.Power;
            Root.IsVisible = power is { HasBattery: true, ChargePercent: not null };
            if (power?.ChargePercent is not { } charge)
            {
                return;
            }

            _icon.Symbol = Rivet.App.Controls.IconConverter.Parse(BatteryIcon(power));
            _bar.Fraction = charge / 100.0;
            _bar.FillKey = MonitorBrushes.KeyFor(BatteryMath.ChargeTone(charge));
            _value.Text = MetricFormat.Percent(charge / 100.0, f.Culture);
            MonitorUi.SetGraph(_graph, s.Histories.Battery, 1, "100%", f.Scale);
            _graph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphBattery);
        }
    }

    private sealed class TemperatureBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);

        public TemperatureBlock() =>
            Root = MonitorUi.Card(MonitorUi.Row("Temperature", "MetricOrangeBrush", L.Get("Strings.monitorShowBatteryTemperature"), _value));

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            Root.IsVisible = s.BatteryTemperature is not null;
            _value.Text = f.Temperature(s.BatteryTemperature);
        }
    }

    private sealed class SystemBlock : BlockView
    {
        private readonly PowerSection _owner;
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);
        private readonly Sparkline _graph = MonitorUi.Graph("MetricOrangeBrush");

        public SystemBlock(PowerSection owner)
        {
            _owner = owner;
            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Power));
            var row = MonitorUi.Row("Flash", "MetricOrangeBrush", L.Get("Strings.powerSystem"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _value, details } });
            Root = MonitorUi.Card(new StackPanel { Spacing = 4, Children = { row, _graph } });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            Root.IsVisible = s.Power?.SystemWatts is not null;
            if (s.Power?.SystemWatts is not { } watts)
            {
                return;
            }

            _value.Text = MetricFormat.Watts(watts, f.Culture);
            var ceiling = GraphScale.Ceiling(Math.Max(1, MonitorUi.Peak(s.Histories.SystemPower)), 1000);
            MonitorUi.SetGraph(_graph, s.Histories.SystemPower, ceiling, MetricFormat.Watts(ceiling, f.Culture), f.Scale);
            _graph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphPower);
        }
    }

    private sealed class FlowBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);
        private readonly TextBlock _caption = MonitorUi.Caption(null);

        public FlowBlock()
        {
            var texts = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Right, Children = { _value, _caption } };
            _caption.HorizontalAlignment = HorizontalAlignment.Right;
            Root = MonitorUi.Card(MonitorUi.Row("BatteryCharge", "AccentBrush", L.Get("Strings.powerBattery"), texts));
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            Root.IsVisible = s.Power?.BatteryWatts is not null;
            if (s.Power?.BatteryWatts is not { } watts)
            {
                return;
            }

            _value.Text = MetricFormat.Watts(Math.Abs(watts), f.Culture);
            var charging = watts >= 0;
            _caption.Text = L.Get(charging ? "Strings.powerCharging" : "Strings.powerOnBattery");
            _caption.Bind(TextBlock.ForegroundProperty, _caption.GetResourceObservable(charging ? "MetricGreenBrush" : "TextSecondaryBrush").ToBinding());
        }
    }

    private sealed class RemainingBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);
        private readonly TextBlock _caption = MonitorUi.Caption(null);

        public RemainingBlock()
        {
            _caption.HorizontalAlignment = HorizontalAlignment.Right;
            var texts = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Right, Children = { _value, _caption } };
            Root = MonitorUi.Card(MonitorUi.Row("Clock", "MetricGreenBrush", L.Get("batteryTime.title"), texts));
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var power = s.Power;
            Root.IsVisible = power is { HasBattery: true, ExternalConnected: false, IsCharging: false };
            if (!Root.IsVisible)
            {
                return;
            }

            var seconds = power!.TimeRemainingSeconds;
            _value.Text = seconds is { } t ? MetricFormat.BatteryTime(t) : "...";
            _caption.Text = L.Get(seconds is null ? "batteryTime.calculating" : "batteryTime.systemEstimate");
        }
    }

    private sealed class HealthBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12.5);
        private readonly TextBlock _caption = MonitorUi.Caption(null);

        public HealthBlock()
        {
            _caption.HorizontalAlignment = HorizontalAlignment.Right;
            var texts = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Right, Children = { _value, _caption } };
            Root = MonitorUi.Card(MonitorUi.Row("Heart", "MetricPinkBrush", L.Get("Strings.powerHealth"), texts));
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            Root.IsVisible = s.Power?.HealthPercent is not null;
            if (s.Power?.HealthPercent is not { } health)
            {
                return;
            }

            _value.Text = health.ToString(f.Culture) + "%";
            _caption.Text = s.Power.CycleCount is { } cycles and > 0 ? $"{cycles.ToString(f.Culture)} {L.Get("Strings.powerCycles")}" : null;
            _caption.IsVisible = _caption.Text is not null;
        }
    }

    /// <summary>Up to five accessory batteries (shown while the peripheral readout drives their sampling).</summary>
    internal sealed class PeripheralsBlock : BlockView
    {
        private readonly StackPanel _rows = new() { Spacing = 3 };

        public PeripheralsBlock() => Root = MonitorUi.Block(L.Get("Strings.monitorShowPeripheralBattery"), _rows);

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var devices = PeripheralBatteries.Sort(s.PeripheralBatteries);
            Root.IsVisible = devices.Count > 0;
            _rows.Children.Clear();
            foreach (var device in devices.Take(5))
            {
                _rows.Children.Add(MonitorUi.Row(KindIcon(device.Kind), "TextSecondaryBrush", device.Name, MonitorUi.Value(device.Percent.ToString(f.Culture) + "%", 12)));
            }

            if (devices.Count > 5)
            {
                _rows.Children.Add(MonitorUi.Caption("+" + (devices.Count - 5).ToString(f.Culture)));
            }
        }

        public static string KindIcon(PeripheralKind kind) => kind switch
        {
            PeripheralKind.Keyboard => "Keyboard",
            PeripheralKind.Mouse => "CursorClick",
            PeripheralKind.Trackpad => "Cursor",
            PeripheralKind.Audio => "Headphones",
            _ => "Bluetooth",
        };
    }
}
