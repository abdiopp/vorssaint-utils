// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>
/// Metric detail mode (spec §3.17.6, 05 §3.4.11): a back row, a big value
/// with a secondary line and a 38-high graph, detail rows and, where it
/// applies, the per-app card. Leases the sampler for its kind while visible.
/// </summary>
internal sealed class MetricDetailView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly SystemMonitorService _monitor;
    private readonly ISettingsStore _settings;
    private readonly MetricKind _kind;
    private readonly TextBlock _big = MonitorUi.Value("-", 28, FontWeight.SemiBold);
    private readonly TextBlock _secondary = MonitorUi.Caption(null);
    private readonly Sparkline _graph = MonitorUi.Graph("AccentBrush", 38);
    private readonly StackPanel _rows = new() { Spacing = 5 };
    private IDisposable? _lease;

    public MetricDetailView(IServiceProvider services, MetricKind kind, Action back)
    {
        _services = services;
        _monitor = services.GetRequiredService<SystemMonitorService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _kind = kind;
        _big.FontFamily = new FontFamily("fonts:Inter#Inter, Segoe UI Variable Display, Segoe UI");
        _secondary.FontSize = 12;
        _graph.ColorKey = kind switch
        {
            MetricKind.Gpu => "MetricCyanBrush",
            MetricKind.Memory => "MetricMintBrush",
            MetricKind.Battery => "MetricGreenBrush",
            MetricKind.Power => "MetricOrangeBrush",
            _ => "AccentBrush",
        };
        _graph.SecondColorKey = kind == MetricKind.Disk ? "MetricPinkBrush" : "MetricGreenBrush";

        var backButton = new Button { Classes = { "icon" }, Width = 26, Height = 26, Padding = new Thickness(0), Content = MonitorUi.Icon("ChevronLeft", 13) };
        backButton.Bind(Button.BackgroundProperty, backButton.GetResourceObservable("ChipBrush").ToBinding());
        ToolTip.SetTip(backButton, L.Get("Strings.actionBack"));
        AutomationProperties.SetName(backButton, L.Get("Strings.actionBack"));
        backButton.Click += (_, _) => back();
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { backButton, MonitorUi.Icon(Icon(kind), 14, "AccentBrush"), MonitorUi.Text(Title(kind), 12.5, FontWeight.SemiBold) },
        };

        var summary = MonitorUi.Card(new StackPanel { Spacing = 2, Children = { _big, _secondary, _graph } });
        var stack = new StackPanel { Spacing = 8, Children = { header, summary } };
        stack.Children.Add(MonitorUi.Card(_rows));
        if (ProcessCard() is { } card)
        {
            stack.Children.Add(card);
        }

        Content = stack;
    }

    public static string Title(MetricKind kind) => kind switch
    {
        MetricKind.Cpu => L.Get("Strings.cpuLabel"),
        MetricKind.Gpu => L.Get("Strings.gpuLabel"),
        MetricKind.Memory => L.Get("Strings.memorySection"),
        MetricKind.Network => L.Get("Strings.networkSection"),
        MetricKind.Disk => L.Get("Strings.diskSection"),
        MetricKind.Battery => L.Get("Strings.batteryLabel"),
        MetricKind.Power => L.Get("Strings.powerSection"),
        _ => L.Get("connectedDevices.title"),
    };

    public static string Icon(MetricKind kind) => kind switch
    {
        MetricKind.Cpu => "DeveloperBoard",
        MetricKind.Gpu => "DeveloperBoardSearch",
        MetricKind.Memory => "Ram",
        MetricKind.Network => "Globe",
        MetricKind.Disk => "Storage",
        MetricKind.Battery => "Battery10",
        MetricKind.Power => "Flash",
        _ => "UsbPlug",
    };

    /// <summary>Called by the hosting section with its on-screen state.</summary>
    public void SetVisible(bool visible)
    {
        if (visible && _lease is null)
        {
            _lease = _monitor.AcquireDetail(_kind);
        }
        else if (!visible && _lease is not null)
        {
            _lease.Dispose();
            _lease = null;
        }
    }

    public void Detach() => SetVisible(false);

    private Control? ProcessCard()
    {
        if (_kind == MetricKind.Network)
        {
            // No per-app network on Windows without administrator rights: the card hosts the speed test.
            return new SpeedTestBlock(_services).Root;
        }

        ProcessListKind? list = _kind switch
        {
            MetricKind.Cpu => ProcessListKind.Cpu,
            MetricKind.Gpu => ProcessListKind.Gpu,
            MetricKind.Memory => ProcessListKind.Memory,
            MetricKind.Power => ProcessListKind.Energy,
            _ => null,
        };
        if (list is not { } kind)
        {
            return null;
        }

        var title = kind switch
        {
            ProcessListKind.Memory => L.Get("Strings.memorySection"),
            ProcessListKind.Energy => L.Get("Strings.energyAppsTitle"),
            _ => L.Get("Strings.usageSection"),
        };
        var empty = kind == ProcessListKind.Energy ? "Strings.energyAppsIdle" : null;
        return MonitorUi.Block(title, new ProcessListView(_services, kind, ProcessUsageService.PanelLimit, empty));
    }

    public void Update(MonitorSnapshot s, MonitorUi.Formats f)
    {
        _rows.Children.Clear();
        switch (_kind)
        {
            case MetricKind.Cpu:
                _big.Text = s.CpuUsage is { } cpu ? MetricFormat.Percent(cpu, f.Culture) : "-";
                _secondary.Text = s.CpuTemperature is { } ct ? $"{L.Get("Strings.monitorShowCPUTemperature")} {f.Temperature(ct)}" : L.Get("Strings.temperatures");
                MonitorUi.SetGraph(_graph, s.Histories.Cpu, 1, "100%", f.Scale);
                AddRow(L.Get("Strings.usageSection"), s.CpuUsage is { } u ? MetricFormat.Percent(u, f.Culture) : "-", "DeveloperBoard");
                AddRow(L.Get("Strings.temperatures"), s.CpuTemperature is { } t ? f.Temperature(t) : L.Get("Strings.monitorUnavailable"), "Temperature");
                AddRow(L.Get("Strings.systemUptime"), MetricFormat.Uptime(s.Uptime.TotalSeconds), "Clock");
                break;

            case MetricKind.Gpu:
                _big.Text = s.GpuUsage is { } gpu ? MetricFormat.Percent(gpu, f.Culture) : "-";
                _secondary.Text = s.GpuTemperature is { } gt ? $"{L.Get("Strings.monitorShowGPUTemperature")} {f.Temperature(gt)}" : L.Get("Strings.temperatures");
                MonitorUi.SetGraph(_graph, s.Histories.Gpu, 1, "100%", f.Scale);
                AddRow(L.Get("Strings.usageSection"), s.GpuUsage is { } g ? MetricFormat.Percent(g, f.Culture) : "-", "DeveloperBoardSearch");
                foreach (var adapter in s.GpuAdapters)
                {
                    var memory = adapter.DedicatedUsed is { } used && adapter.DedicatedTotal is { } total and > 0
                        ? L.Format("win.systemMonitor.gpuMemoryFormat", $"{MetricFormat.MemoryBytes(used, f.Culture)} / {MetricFormat.MemoryBytes(total, f.Culture)}")
                        : null;
                    _rows.Children.Add(MonitorUi.Row(null, null, adapter.Name, MonitorUi.Value(MetricFormat.Percent(adapter.Usage, f.Culture), 12, FontWeight.Normal), memory, reserveIcon: true));
                }

                AddRow(L.Get("Strings.temperatures"), s.GpuTemperature is { } temp ? f.Temperature(temp) : L.Get("Strings.monitorUnavailable"), "Temperature");
                break;

            case MetricKind.Memory:
                var memoryReading = s.Memory;
                _big.Text = memoryReading is null ? "-" : MetricFormat.Percent(memoryReading.ChosenFraction(f.AppMemory), f.Culture);
                _secondary.Text = memoryReading is null ? null : $"{MetricFormat.MemoryBytes(memoryReading.Chosen(f.AppMemory), f.Culture)} / {MetricFormat.MemoryBytes(memoryReading.Total, f.Culture)}";
                MonitorUi.SetGraph(_graph, f.AppMemory && s.Histories.MemoryApp.Length > 0 ? s.Histories.MemoryApp : s.Histories.Memory, 1, "100%", f.Scale);
                if (memoryReading is not null)
                {
                    AddRow(L.Get(f.AppMemory ? "Strings.memoryMetricApp" : "Strings.memoryMetricUsed"), _secondary.Text ?? "-", "Ram");
                    _rows.Children.Add(MonitorUi.Row("Gauge", "TextSecondaryBrush", L.Get("Strings.memoryPressure"), new PressurePill { Pressure = memoryReading.Pressure }));
                    AddOptional(L.Get("Strings.memoryCompressed"), memoryReading.Compressed, f);
                    AddOptional(L.Get("Strings.memoryCachedFiles"), memoryReading.Cached, f);
                    AddOptional(L.Get("Strings.memorySwapUsed"), memoryReading.SwapUsed, f);
                }

                break;

            case MetricKind.Network:
                _big.Text = s.NetDownBytesPerSec is { } down ? MetricFormat.NetworkRateCompact(down, f.Bits, f.Culture) : "-";
                _secondary.Text = $"{L.Get("Strings.networkUpload")} {(s.NetUpBytesPerSec is { } up ? MetricFormat.NetworkRateCompact(up, f.Bits, f.Culture) : "-")}";
                var peak = Math.Max(1, Math.Max(MonitorUi.Peak(s.Histories.NetDown), MonitorUi.Peak(s.Histories.NetUp)));
                var ceiling = GraphScale.NetworkCeiling(peak, f.Bits);
                MonitorUi.SetGraph(_graph, s.Histories.NetDown, ceiling, GraphScale.NetworkLabel(ceiling, f.Bits, f.Culture), f.Scale, s.Histories.NetUp);
                AddRow(L.Get("Strings.networkDownload"), f.Rate(s.NetDownBytesPerSec), "ArrowDown");
                AddRow(L.Get("Strings.networkUpload"), f.Rate(s.NetUpBytesPerSec), "ArrowUp");
                AddRow(L.Get("Strings.networkThisSession"), $"↓{MetricFormat.Bytes(s.NetTotalDown, f.Culture)}  ↑{MetricFormat.Bytes(s.NetTotalUp, f.Culture)}", "DataUsage");
                break;

            case MetricKind.Disk:
                var disk = s.PrimaryDisk;
                _big.Text = disk is null ? "-" : MetricFormat.Percent(disk.Info.UsedFraction, f.Culture);
                _secondary.Text = disk is null ? L.Get("Strings.diskNoDisks") : $"{MetricFormat.DiskBytes(disk.Info.Free, f.Culture)} {L.Get("Strings.diskAvailable")}";
                var diskPeak = Math.Max(1, Math.Max(MonitorUi.Peak(s.Histories.DiskRead), MonitorUi.Peak(s.Histories.DiskWrite)));
                var diskCeiling = GraphScale.Ceiling(diskPeak, 1024);
                MonitorUi.SetGraph(_graph, s.Histories.DiskRead, diskCeiling, MetricFormat.BytesPerSec(diskCeiling, f.Culture), f.Scale, s.Histories.DiskWrite);
                if (disk is not null)
                {
                    AddRow(DisksSection.DisplayName(disk.Info), $"{MetricFormat.Percent(disk.Info.UsedFraction, f.Culture)} {L.Get("Strings.diskUsed")}", disk.Info.IsInternal ? "HardDrive" : "UsbStick");
                    AddRow(L.Get("Strings.diskAvailable"), MetricFormat.DiskBytes(disk.Info.Free, f.Culture), null);
                }

                AddRow(L.Get("Strings.diskRead"), s.DiskReadBytesPerSec is { } r ? MetricFormat.BytesPerSec(r, f.Culture) : L.Get("Strings.networkMeasuring"), "ArrowDown");
                AddRow(L.Get("Strings.diskWrite"), s.DiskWriteBytesPerSec is { } w ? MetricFormat.BytesPerSec(w, f.Culture) : L.Get("Strings.networkMeasuring"), "ArrowUp");
                break;

            case MetricKind.Battery:
                var power = s.Power;
                var lowest = PeripheralBatteries.Sort(s.PeripheralBatteries).FirstOrDefault();
                if (power is { HasBattery: true, ChargePercent: { } charge })
                {
                    _big.Text = charge.ToString(f.Culture) + "%";
                    _secondary.Text = PowerSection.State(power);
                    AddRow(L.Get("Strings.batteryCharge"), charge.ToString(f.Culture) + "%", PowerSection.BatteryIcon(power));
                    if (power.BatteryWatts is { } flow)
                    {
                        AddRow(L.Get("Strings.powerBattery"), $"{MetricFormat.Watts(Math.Abs(flow), f.Culture)} · {L.Get(flow >= 0 ? "Strings.powerCharging" : "Strings.powerOnBattery")}", "BatteryCharge");
                    }

                    if (s.BatteryTemperature is { } bt)
                    {
                        AddRow(L.Get("Strings.temperatures"), f.Temperature(bt), "Temperature");
                    }

                    if (power.HealthPercent is { } health)
                    {
                        AddRow(L.Get("Strings.powerHealth"), health.ToString(f.Culture) + "%", "Heart");
                    }

                    if (power.CycleCount is { } cycles and > 0)
                    {
                        AddRow(L.Get("Strings.powerCycles"), cycles.ToString(f.Culture), null);
                    }
                }
                else
                {
                    _big.Text = lowest is null ? "-" : lowest.Percent.ToString(f.Culture) + "%";
                    _secondary.Text = lowest?.Name ?? PowerSection.State(power);
                }

                MonitorUi.SetGraph(_graph, s.Histories.Battery, 1, "100%", f.Scale);
                var devices = PeripheralBatteries.Sort(s.PeripheralBatteries);
                if (devices.Count == 0)
                {
                    AddRow(L.Get("Strings.monitorShowPeripheralBattery"), L.Get("Strings.peripheralBatteryNoDevices"), "Bluetooth");
                }

                foreach (var device in devices.Take(5))
                {
                    AddRow(device.Name, device.Percent.ToString(f.Culture) + "%", PowerSection.PeripheralsBlock.KindIcon(device.Kind));
                }

                break;

            case MetricKind.Power:
                var reading = s.Power;
                _big.Text = reading?.SystemWatts is { } watts ? MetricFormat.WattsCompact(watts, f.Culture) : "-";
                _secondary.Text = PowerSection.State(reading);
                var powerCeiling = GraphScale.Ceiling(Math.Max(1, MonitorUi.Peak(s.Histories.SystemPower)), 1000);
                MonitorUi.SetGraph(_graph, s.Histories.SystemPower, powerCeiling, MetricFormat.Watts(powerCeiling, f.Culture), f.Scale);
                if (reading?.SystemWatts is { } system)
                {
                    AddRow(L.Get("Strings.powerSystem"), MetricFormat.Watts(system, f.Culture), "Flash");
                }

                if (reading?.BatteryWatts is { } battery)
                {
                    AddRow(L.Get("Strings.powerBattery"), MetricFormat.Watts(Math.Abs(battery), f.Culture), "BatteryCharge");
                }

                if (reading is { HasBattery: true, ExternalConnected: false, IsCharging: false })
                {
                    AddRow(L.Get("batteryTime.title"), reading.TimeRemainingSeconds is { } t2 ? MetricFormat.BatteryTime(t2) : L.Get("batteryTime.calculating"), "Clock");
                }

                break;

            case MetricKind.ConnectedDevices:
                var list = s.ConnectedDevices ?? [];
                _big.Text = list.Count.ToString(f.Culture);
                _secondary.Text = list.Count == 1 ? L.Get("connectedDevices.oneConnected") : L.Format("connectedDevices.devicesConnectedFormat", list.Count);
                _graph.IsVisible = false;
                if (list.Count == 0)
                {
                    _rows.Children.Add(MonitorUi.Caption(L.Get("connectedDevices.noDevices")));
                }

                foreach (var device in list)
                {
                    _rows.Children.Add(MonitorUi.Row("UsbPlug", "TextSecondaryBrush", device.Name ?? L.Get("connectedDevices.unnamedDevice"), MonitorUi.Caption(device.Vendor ?? string.Empty)));
                }

                break;
        }

        _graph.IsVisible &= GraphEnabled();
        _rows.IsVisible = _rows.Children.Count > 0;
    }

    private bool GraphEnabled() => _kind switch
    {
        MetricKind.Cpu => _settings.Get(MonitorSettings.GraphCpu),
        MetricKind.Gpu => _settings.Get(MonitorSettings.GraphGpu),
        MetricKind.Memory => _settings.Get(MonitorSettings.GraphMemory),
        MetricKind.Network => _settings.Get(MonitorSettings.GraphNetwork),
        MetricKind.Disk => _settings.Get(MonitorSettings.GraphDisk),
        MetricKind.Battery => _settings.Get(MonitorSettings.GraphBattery),
        MetricKind.Power => _settings.Get(MonitorSettings.GraphPower),
        _ => false,
    };

    private void AddRow(string title, string value, string? icon, bool indent = false)
    {
        var row = MonitorUi.Row(icon, icon is null ? null : "TextSecondaryBrush", title, MonitorUi.Value(value, 12, FontWeight.Normal), reserveIcon: !indent);
        if (indent)
        {
            row.Margin = new Thickness(18, 0, 0, 0);
        }

        _rows.Children.Add(row);
    }

    private void AddOptional(string title, ulong? bytes, MonitorUi.Formats f)
    {
        if (bytes is { } b)
        {
            AddRow(title, MetricFormat.MemoryBytes(b, f.Culture), null, indent: true);
        }
    }
}
