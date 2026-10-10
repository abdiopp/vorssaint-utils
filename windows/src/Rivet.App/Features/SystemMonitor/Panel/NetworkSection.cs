// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>
/// The Network tab (spec §3.7.3): live speed with graph, session totals,
/// local IPv4 addresses and the speed test. Per-app network needs ETW and
/// administrator rights on Windows, so that block is not offered.
/// </summary>
internal sealed class NetworkSection : MonitorSectionBase
{
    public NetworkSection(IServiceProvider services)
        : base(services, MonitorSurface.Network, "Strings.networkSection", MonitorSettings.NetworkOrder)
    {
    }

    protected override string SectionId => SystemMonitorModule.NetworkSectionId;

    protected override IEnumerable<SettingDefinition> LayoutKeys => [MonitorSettings.GraphNetwork];

    protected override IReadOnlyList<BlockSpec> Blocks =>
    [
        new("speed", "Strings.monitorItemNetSpeed", "TopSpeed", [("Strings.monitorItemNetSpeed", MonitorSettings.NetSpeed)], () => true, () => new SpeedBlock(this)),
        new("apps", "Strings.networkApps", "AppsList", [("Strings.networkApps", MonitorSettings.NetApps)], () => false, () => throw new NotSupportedException()),
        new("totals", "Strings.monitorItemNetTotals", "DataUsage", [("Strings.monitorItemNetTotals", MonitorSettings.NetTotals)], () => true, () => new TotalsBlock()),
        new("addresses", "Strings.networkIPAddresses", "Globe", [("Strings.networkIPAddresses", MonitorSettings.NetAddresses)], () => true, () => new AddressesBlock(this)),
        new("test", "Strings.monitorItemNetTest", "Gauge", [("Strings.monitorItemNetTest", MonitorSettings.NetTest)], () => true, () => new SpeedTestBlock(Services)),
    ];

    private sealed class SpeedBlock : BlockView
    {
        private readonly NetworkSection _owner;
        private readonly TextBlock _down = MonitorUi.Value(null, 13.5, FontWeight.SemiBold);
        private readonly TextBlock _up = MonitorUi.Value(null, 13.5, FontWeight.SemiBold);
        private readonly Sparkline _graph = MonitorUi.Graph("AccentBrush", 30, "MetricGreenBrush");
        private readonly Button _unit;

        public SpeedBlock(NetworkSection owner)
        {
            _owner = owner;
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            columns.Children.Add(Column("ArrowDown", "AccentBrush", _down, L.Get("Strings.networkDownload")));
            var up = Column("ArrowUp", "MetricGreenBrush", _up, L.Get("Strings.networkUpload"));
            Grid.SetColumn(up, 1);
            columns.Children.Add(up);
            _unit = new Button { Classes = { "icon" }, Padding = new Thickness(6, 1), Content = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold } };
            _unit.Bind(Button.BackgroundProperty, _unit.GetResourceObservable("ChipBrush").ToBinding());
            ToolTip.SetTip(_unit, L.Get("win.systemMonitor.networkUnitTooltip"));
            AutomationProperties.SetName(_unit, L.Get("monitorLayout.networkSpeedUnit"));
            _unit.Click += (_, _) => owner.Settings.Set(MonitorSettings.NetworkSpeedUnit, owner.Settings.Get(MonitorSettings.NetworkSpeedUnit) == "bits" ? "bytes" : "bits");
            var details = MonitorUi.IconButton("ChevronRight", L.Get("Strings.homebrewOperationShowDetails"), () => owner.ShowDetail(MetricKind.Network));
            Root = MonitorUi.Block(L.Get("Strings.monitorItemNetSpeed"), new StackPanel { Spacing = 6, Children = { columns, _graph } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _unit, details } });
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
        {
            ((TextBlock)_unit.Content!).Text = f.Bits ? "bit/s" : "B/s";
            _down.Text = f.Rate(s.NetDownBytesPerSec);
            _up.Text = f.Rate(s.NetUpBytesPerSec);
            var peak = Math.Max(1, Math.Max(MonitorUi.Peak(s.Histories.NetDown), MonitorUi.Peak(s.Histories.NetUp)));
            var ceiling = GraphScale.NetworkCeiling(peak, f.Bits);
            MonitorUi.SetGraph(_graph, s.Histories.NetDown, ceiling, GraphScale.NetworkLabel(ceiling, f.Bits, f.Culture), f.Scale, s.Histories.NetUp);
            _graph.IsVisible &= _owner.Settings.Get(MonitorSettings.GraphNetwork);
        }

        private static Control Column(string icon, string brush, TextBlock value, string label) => new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { MonitorUi.Icon(icon, 13, brush), value } },
                MonitorUi.Caption(label),
            },
        };
    }

    private sealed class TotalsBlock : BlockView
    {
        private readonly TextBlock _value = MonitorUi.Value("-", 12);

        public TotalsBlock() =>
            Root = MonitorUi.Card(MonitorUi.Row("DataUsage", "AccentBrush", L.Get("Strings.networkThisSession"), _value));

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f) =>
            _value.Text = $"↓{MetricFormat.Bytes(s.NetTotalDown, f.Culture)}  ↑{MetricFormat.Bytes(s.NetTotalUp, f.Culture)}";
    }

    /// <summary>Local IPv4 addresses, read only while this block is visible (on appear and on each snapshot).</summary>
    private sealed class AddressesBlock : BlockView
    {
        private readonly INetworkSensor _sensor;
        private readonly SelectableTextBlock _addresses = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFeatures = MonitorUi.TabularDigits };
        private bool _reading;
        private double _lastRead = double.NegativeInfinity;

        public AddressesBlock(NetworkSection owner)
        {
            _sensor = owner.Services.GetRequiredService<INetworkSensor>();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,Auto,*"), ColumnSpacing = 6 };
            grid.Children.Add(MonitorUi.Icon("Globe", 14, "AccentBrush"));
            var title = MonitorUi.Text(L.Get("Strings.networkLocalIP"), 12.5, FontWeight.Medium);
            title.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            _addresses.HorizontalAlignment = HorizontalAlignment.Right;
            _addresses.TextAlignment = TextAlignment.Right;
            Grid.SetColumn(_addresses, 2);
            grid.Children.Add(_addresses);
            Root = MonitorUi.Card(grid);
            Refresh(0);
        }

        public override Control Root { get; }

        public override void Update(MonitorSnapshot s, MonitorUi.Formats f) => Refresh(s.Timestamp);

        private void Refresh(double now)
        {
            if (_reading || now - _lastRead < 1)
            {
                return;
            }

            _reading = true;
            _lastRead = now;
            Task.Run(() =>
            {
                IReadOnlyList<LocalAddress> list;
                try
                {
                    list = _sensor.ReadLocalAddresses();
                }
                catch (Exception)
                {
                    list = [];
                }

                var text = string.Join('\n', list.Select(a => a.InterfaceName is { Length: > 0 } name ? $"{a.Address} ({name})" : a.Address));
                Dispatcher.UIThread.Post(() =>
                {
                    _reading = false;
                    _addresses.Text = text.Length == 0 ? "-" : text;
                });
            });
        }
    }
}

/// <summary>The speed test card (spec §3.7.5 UI): button, progress, results, latency or failure.</summary>
internal sealed class SpeedTestBlock : BlockView
{
    private readonly SpeedTest _test;
    private readonly Button _button;
    private readonly TextBlock _buttonText = new() { FontSize = 12 };
    private readonly ProgressBar _spinner = new() { Width = 48, MinWidth = 48, Height = 4, MinHeight = 4, IsIndeterminate = true, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _testing = MonitorUi.Caption(L.Get("Strings.speedTestTesting"));
    private readonly TextBlock _result = MonitorUi.Value(null, 13, FontWeight.SemiBold);
    private readonly TextBlock _latency = MonitorUi.Caption(null);

    public SpeedTestBlock(IServiceProvider services)
    {
        _test = services.GetRequiredService<SpeedTest>();
        _button = new Button { Content = _buttonText, Padding = new Thickness(10, 3), MinHeight = 26 };
        _button.Click += (_, _) => _test.Start();
        var progress = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _spinner, _testing } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        top.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _result, _latency, progress } });
        Grid.SetColumn(_button, 1);
        top.Children.Add(_button);
        Root = MonitorUi.Block(L.Get("Strings.monitorItemNetTest"), top);
        Root.AttachedToVisualTree += (_, _) => _test.StateChanged += OnStateChanged;
        Root.DetachedFromVisualTree += (_, _) => _test.StateChanged -= OnStateChanged;
        Render();
    }

    public override Control Root { get; }

    public override void Update(MonitorSnapshot s, MonitorUi.Formats f)
    {
    }

    private void OnStateChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Render);

    private void Render()
    {
        var state = _test.State;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        _spinner.IsVisible = _testing.IsVisible = state.IsRunning;
        _button.IsEnabled = !state.IsRunning;
        _buttonText.Text = L.Get(state.Phase == SpeedTestPhase.Idle && state.DownloadMbps is null ? "Strings.speedTestRun" : "Strings.speedTestAgain");
        if (state.DownloadMbps is { } down && state.UploadMbps is { } up)
        {
            _result.Text = $"↓{MetricFormat.Mbps(down, culture)} ↑{MetricFormat.Mbps(up, culture)} Mbps";
            _result.IsVisible = true;
        }
        else
        {
            _result.IsVisible = false;
        }

        if (state.Phase == SpeedTestPhase.Failed)
        {
            _latency.Text = L.Get("Strings.speedTestFailed");
            _latency.Bind(TextBlock.ForegroundProperty, _latency.GetResourceObservable("WarningBrush").ToBinding());
            _latency.IsVisible = true;
        }
        else if (state.LatencyMs is { } ms && !state.IsRunning)
        {
            _latency.Text = $"{L.Get("Strings.speedTestLatency")}: {Math.Round(ms).ToString("0", culture)} ms";
            _latency.Bind(TextBlock.ForegroundProperty, _latency.GetResourceObservable("TextSecondaryBrush").ToBinding());
            _latency.IsVisible = true;
        }
        else
        {
            _latency.IsVisible = false;
        }
    }
}
