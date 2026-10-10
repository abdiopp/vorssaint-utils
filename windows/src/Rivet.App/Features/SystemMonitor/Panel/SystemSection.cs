// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>Which per-app breakdown is open (only one of CPU, GPU and Memory at a time, spec §3.17.5).</summary>
internal enum Breakdown
{
    None,
    Cpu,
    Gpu,
    Memory,
}

/// <summary>The System tab (spec §3.17.1): temperatures, hardware usage, memory, uptime, connected devices.</summary>
internal sealed class SystemSection : MonitorSectionBase
{
    private Breakdown _expanded;

    public SystemSection(IServiceProvider services)
        : base(services, MonitorSurface.System, "Strings.systemSection", MonitorSettings.SystemOrder)
    {
    }

    protected override string SectionId => SystemMonitorModule.SystemSectionId;

    internal Breakdown Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            Rebuild();
        }
    }

    protected override IEnumerable<SettingDefinition> LayoutKeys =>
        [MonitorSettings.GraphCpu, MonitorSettings.GraphGpu, MonitorSettings.GraphMemory];

    protected override string? EmptyText => L.Get("Strings.monitorUnavailable");

    protected override IReadOnlyList<BlockSpec> Blocks =>
    [
        new("temps", "Strings.temperatures", "Temperature", [("Strings.temperatures", MonitorSettings.SysTemps)],
            () => Runtime.IsAnyAvailable(FeatureIds.MonitorCpu, FeatureIds.MonitorGpu), () => new TemperaturesBlock(this)),
        new("usage", "Strings.usageSection", "DeveloperBoard", [("Strings.cpuLabel", MonitorSettings.SysCpu), ("Strings.gpuLabel", MonitorSettings.SysGpu)],
            () => Runtime.IsAnyAvailable(FeatureIds.MonitorCpu, FeatureIds.MonitorGpu), () => new UsageBlock(this)),
        new("memory", "Strings.memorySection", "Ram", [("Strings.memorySection", MonitorSettings.SysMemory)],
            () => Runtime.IsAvailable(FeatureIds.MonitorMemory), () => new MemoryBlock(this)),
        new("uptime", "Strings.monitorItemUptime", "Clock", [("Strings.monitorItemUptime", MonitorSettings.SysUptime)],
            () => true, () => new UptimeBlock()),
        new("connectedDevices", "connectedDevices.title", "UsbPlug", [("connectedDevices.title", MonitorSettings.SysConnectedDevices)],
            () => Runtime.IsAvailable(FeatureIds.ConnectedDevices), () => new DevicesBlock(this)),
    ];

    private bool Has(string feature) => Runtime.IsAvailable(feature);

    /// <summary>CPU and GPU temperature tiles; an explanation when Windows does not report them.</summary>
    private sealed class TemperaturesBlock : BlockView
    {
        private readonly TextBlock _cpu = MonitorUi.Value("-", 15);
        private readonly TextBlock _gpu = MonitorUi.Value("-", 15);
        private readonly TextBlock _note = MonitorUi.Note(null);
        private readonly Grid _tiles = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        private readonly SystemSection _owner;

        public TemperaturesBlock(SystemSection owner)
        {
            _owner = owner;
            var cpuTile = Tile("DeveloperBoard", L.Get("Strings.cpuLabel"), _cpu);
            var gpuTile = Tile("DeveloperBoardSearch", L.Get("Strings.gpuLabel"), _gpu);
            cpuTile.IsVisible = owner.Has(FeatureIds.MonitorCpu);
            gpuTile.IsVisible = owner.Has(FeatureIds.MonitorGpu);
            Grid.SetColumn(gpuTile, cpuTile.IsVisible ? 1 : 0);
            _tiles.Children.Add(cpuTile);
            _tiles.Children.Add(gpuTile);
            Root = MonitorUi.Block(L.Get("Strings.temperatures"), new StackPanel { Spacing = 6, Children = { _tiles, _note } });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            _cpu.Text = f.Temperature(s.CpuTemperature);
            _gpu.Text = f.Temperature(s.GpuTemperature);
            string? note = null;
            if (s.CpuTemperature is null && _owner.Has(FeatureIds.MonitorCpu) && s.Plan.CpuTemperature)
            {
                note = L.Get(s.CpuTemperatureAvailability switch
                {
                    SensorAvailability.NeedsAdministrator => "win.systemMonitor.cpuTemperatureNeedsAdmin",
                    SensorAvailability.NeedsSensorDriver => "win.systemMonitor.cpuTemperatureNeedsDriver",
                    SensorAvailability.NeedsSensorHelper => "win.systemMonitor.cpuTemperatureNeedsHelper",
                    _ => "win.systemMonitor.cpuTemperatureUnavailable",
                });
            }

            if (s.CpuTemperature is null && s.GpuTemperature is null && s.Plan.AnyTemperature)
            {
                note = L.Get("Strings.monitorUnavailable") + (note is null ? string.Empty : " " + note);
            }

            _note.Text = note;
            _note.IsVisible = note is not null;
        }

        private static Border Tile(string icon, string label, TextBlock value)
        {
            var tile = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 7),
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { MonitorUi.Icon(icon, 12, "TextSecondaryBrush"), MonitorUi.Caption(label) } },
                        value,
                    },
                },
            };
            tile.Bind(Border.BackgroundProperty, tile.GetResourceObservable("ChipBrush").ToBinding());
            return tile;
        }
    }

    /// <summary>"Hardware usage": CPU and GPU rows with bars, graphs and the expandable app lists.</summary>
    private sealed class UsageBlock : BlockView
    {
        private readonly SystemSection _owner;
        private readonly UsageRow? _cpu;
        private readonly UsageRow? _gpu;
        private readonly Sparkline _cpuGraph = MonitorUi.Graph("AccentBrush");
        private readonly Sparkline _gpuGraph = MonitorUi.Graph("MetricCyanBrush");
        private readonly CoreMatrixView? _cores;
        private readonly StackPanel? _adapters;

        public UsageBlock(SystemSection owner)
        {
            _owner = owner;
            var settings = owner.Settings;
            var stack = new StackPanel { Spacing = 5 };
            var showCpu = settings.Get(MonitorSettings.SysCpu) && owner.Has(FeatureIds.MonitorCpu);
            var showGpu = settings.Get(MonitorSettings.SysGpu) && owner.Has(FeatureIds.MonitorGpu);
            if (showCpu)
            {
                _cpu = new UsageRow(L.Get("Strings.cpuLabel"), owner.Expanded == Breakdown.Cpu, () => owner.Expanded = owner.Expanded == Breakdown.Cpu ? Breakdown.None : Breakdown.Cpu);
                stack.Children.Add(_cpu.Root);
                _cpuGraph.IsVisible = settings.Get(MonitorSettings.GraphCpu);
                stack.Children.Add(_cpuGraph);
                if (owner.Expanded == Breakdown.Cpu)
                {
                    stack.Children.Add(CpuBreakdown(settings, out _cores));
                }
            }

            if (showGpu)
            {
                _gpu = new UsageRow(L.Get("Strings.gpuLabel"), owner.Expanded == Breakdown.Gpu, () => owner.Expanded = owner.Expanded == Breakdown.Gpu ? Breakdown.None : Breakdown.Gpu);
                stack.Children.Add(_gpu.Root);
                _gpuGraph.IsVisible = settings.Get(MonitorSettings.GraphGpu);
                stack.Children.Add(_gpuGraph);
                if (owner.Expanded == Breakdown.Gpu)
                {
                    _adapters = new StackPanel { Spacing = 3, Margin = new Thickness(18, 2, 0, 0) };
                    stack.Children.Add(_adapters);
                    stack.Children.Add(Indented(new ProcessListView(owner.Services, ProcessListKind.Gpu)));
                    stack.Children.Add(Indented(DetailLink(MetricKind.Gpu)));
                }
            }

            var taskManager = MonitorUi.IconButton("Open", L.Get("Strings.monitorOpenActivityMonitor"), () =>
            {
                var control = owner.Services.GetRequiredService<IProcessControl>();
                Task.Run(control.OpenTaskManager);
            });
            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Cpu));
            details.IsVisible = showCpu;
            Root = MonitorUi.Block(L.Get("Strings.usageSection"), stack, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { taskManager, details } });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            _cpu?.Set(s.CpuUsage);
            _gpu?.Set(s.GpuUsage);
            MonitorUi.SetGraph(_cpuGraph, s.Histories.Cpu, 1, "100%", f.Scale);
            _cpuGraph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphCpu);
            MonitorUi.SetGraph(_gpuGraph, s.Histories.Gpu, 1, "100%", f.Scale);
            _gpuGraph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphGpu);
            _cores?.Update(s.CoreGroups, s.CpuCoreUsage);
            if (_adapters is not null)
            {
                _adapters.Children.Clear();
                foreach (var adapter in s.GpuAdapters)
                {
                    var memory = adapter.DedicatedUsed is { } used && adapter.DedicatedTotal is { } total and > 0
                        ? $"{MetricFormat.MemoryBytes(used, f.Culture)} / {MetricFormat.MemoryBytes(total, f.Culture)}"
                        : null;
                    _adapters.Children.Add(MonitorUi.Row(null, null, adapter.Name, MonitorUi.Value(MetricFormat.Percent(adapter.Usage, f.Culture), 11.5, FontWeight.Normal, "TextSecondaryBrush"),
                        memory is null ? null : L.Format("win.systemMonitor.gpuMemoryFormat", memory)));
                }
            }
        }

        private Control CpuBreakdown(ISettingsStore settings, out CoreMatrixView? cores)
        {
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            cores = null;
            if (settings.Get(MonitorSettings.SysCpuCores))
            {
                cores = new CoreMatrixView { Margin = new Thickness(0, 4, 0, 0) };
                AutomationProperties.SetHelpText(cores, L.Get("cpuCores.hint"));
                ToolTip.SetTip(cores, L.Get("cpuCores.hint"));
                var box = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(8), Child = cores };
                box.Bind(Border.BackgroundProperty, box.GetResourceObservable("ChipBrush").ToBinding());
                stack.Children.Add(box);
                stack.Children.Add(new Fold(L.Get("cpuCores.apps"), () => new ProcessListView(_owner.Services, ProcessListKind.Cpu), initiallyOpen: true));
            }
            else
            {
                stack.Children.Add(Indented(new ProcessListView(_owner.Services, ProcessListKind.Cpu)));
            }

            return stack;
        }

        private Control DetailLink(MetricKind kind)
        {
            var link = new Button { Classes = { "link" }, Content = new TextBlock { Text = L.Get("Strings.homebrewOperationShowDetails"), FontSize = 11.5 } };
            link.Click += (_, _) => _owner.ShowDetail(kind);
            return link;
        }
    }

    /// <summary>"Memory": pressure pill and used/total, secondary rows, graph and the memory app list.</summary>
    private sealed class MemoryBlock : BlockView
    {
        private readonly SystemSection _owner;
        private readonly PressurePill _pill = new();
        private readonly TextBlock _value = MonitorUi.Value("-", 12);
        private readonly Grid _compressed;
        private readonly Grid _cached;
        private readonly Grid _swap;
        private readonly TextBlock _compressedValue = MonitorUi.Value("-", 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly TextBlock _cachedValue = MonitorUi.Value("-", 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly TextBlock _swapValue = MonitorUi.Value("-", 11.5, FontWeight.Normal, "TextSecondaryBrush");
        private readonly Sparkline _graph = MonitorUi.Graph("MetricMintBrush");

        public MemoryBlock(SystemSection owner)
        {
            _owner = owner;
            var expanded = owner.Expanded == Breakdown.Memory;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("14,Auto,Auto,*"), ColumnSpacing = 6 };
            header.Children.Add(MonitorUi.Icon(expanded ? "ChevronDown" : "ChevronRight", 11, "TextSecondaryBrush"));
            var title = MonitorUi.Text(L.Get("Strings.memoryPressure"), 12.5, FontWeight.Medium);
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            Grid.SetColumn(_pill, 2);
            header.Children.Add(_pill);
            _value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_value, 3);
            header.Children.Add(_value);
            var toggle = new Button { Classes = { "row" }, Padding = new Thickness(2, 3), Content = header };
            AutomationProperties.SetName(toggle, L.Get("Strings.memorySection"));
            toggle.Click += (_, _) => owner.Expanded = expanded ? Breakdown.None : Breakdown.Memory;

            _compressed = Secondary(L.Get("Strings.memoryCompressed"), _compressedValue);
            _cached = Secondary(L.Get("Strings.memoryCachedFiles"), _cachedValue);
            _swap = Secondary(L.Get("Strings.memorySwapUsed"), _swapValue);
            var stack = new StackPanel { Spacing = 3, Children = { toggle, _compressed, _cached, _swap, _graph } };
            if (expanded)
            {
                stack.Children.Add(Indented(new ProcessListView(owner.Services, ProcessListKind.Memory)));
            }

            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Memory));
            Root = MonitorUi.Block(L.Get("Strings.memorySection"), stack, details);
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            var memory = s.Memory;
            _pill.Pressure = memory?.Pressure ?? MemoryPressure.Unknown;
            _value.Text = memory is null ? "-" : $"{MetricFormat.MemoryBytes(memory.Chosen(f.AppMemory), f.Culture)} / {MetricFormat.MemoryBytes(memory.Total, f.Culture)}";
            Set(_compressed, _compressedValue, memory?.Compressed, f);
            Set(_cached, _cachedValue, memory?.Cached, f);
            Set(_swap, _swapValue, memory?.SwapUsed, f);
            MonitorUi.SetGraph(_graph, f.AppMemory && s.Histories.MemoryApp.Length > 0 ? s.Histories.MemoryApp : s.Histories.Memory, 1, "100%", f.Scale);
            _graph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphMemory);
        }

        private static void Set(Grid row, TextBlock value, ulong? bytes, MonitorUi.Formats f)
        {
            row.IsVisible = bytes is not null;
            value.Text = bytes is { } b ? MetricFormat.MemoryBytes(b, f.Culture) : "-";
        }

        private static Grid Secondary(string title, TextBlock value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(20, 0, 2, 0) };
            grid.Children.Add(MonitorUi.Caption(title));
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            return grid;
        }
    }

    private sealed class UptimeBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12);

        public UptimeBlock() =>
            Root = MonitorUi.Card(MonitorUi.Row("Clock", "AccentBrush", L.Get("Strings.systemUptime"), _value));

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f) =>
            _value.Text = MetricFormat.Uptime(s.Uptime.TotalSeconds);
    }

    /// <summary>"Connected Devices  N  ›": opens the device list.</summary>
    private sealed class DevicesBlock : BlockView
    {
        private readonly TextBlock _count = MonitorUi.Value("-", 12);

        public DevicesBlock(SystemSection owner)
        {
            var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _count, MonitorUi.Icon("ChevronRight", 11, "TextTertiaryBrush") } };
            var row = MonitorUi.Row("UsbPlug", "AccentBrush", L.Get("connectedDevices.title"), trailing);
            var button = new Button { Classes = { "row" }, Padding = new Thickness(0), Content = row };
            AutomationProperties.SetName(button, L.Get("connectedDevices.title"));
            button.Click += (_, _) => owner.ShowDetail(MetricKind.ConnectedDevices);
            Root = MonitorUi.Card(button, new Thickness(10, 5));
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f) =>
            _count.Text = s.ConnectedDevices is { } devices ? devices.Count.ToString(f.Culture) : "-";
    }

    private static Control Indented(Control child)
    {
        child.Margin = new Thickness(14, 0, 0, 0);
        return child;
    }
}

/// <summary>A usage row: chevron, title, bar and percentage; the whole row toggles the app list.</summary>
internal sealed class UsageRow
{
    private readonly UsageBar _bar = new() { Width = 120 };
    private readonly TextBlock _value = MonitorUi.Value("-", 12);

    public UsageRow(string title, bool expanded, Action toggle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,44,*,48"), ColumnSpacing = 6 };
        grid.Children.Add(MonitorUi.Icon(expanded ? "ChevronDown" : "ChevronRight", 11, "TextSecondaryBrush"));
        var label = MonitorUi.Text(title, 12.5, FontWeight.Medium);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        _bar.Width = double.NaN;
        Grid.SetColumn(_bar, 2);
        grid.Children.Add(_bar);
        _value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_value, 3);
        grid.Children.Add(_value);
        var button = new Button { Classes = { "row" }, Padding = new Thickness(2, 3), Content = grid };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => toggle();
        Root = button;
    }

    public Control Root { get; }

    public void Set(double? fraction)
    {
        _bar.Fraction = fraction;
        _bar.FillKey = MonitorBrushes.KeyFor(fraction is { } f ? UsageTone.ForUsage(f) : MetricTone.Normal);
        _value.Text = fraction is { } v ? MetricFormat.Percent(v) : "-";
        AutomationProperties.SetItemStatus(Root, _value.Text);
    }
}
