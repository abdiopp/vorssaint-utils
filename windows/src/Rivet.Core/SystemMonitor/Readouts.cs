// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.Core.SystemMonitor;

/// <summary>The pinnable readouts (macOS "menu bar metrics", spec §3.16.1). Fan speed is not on Windows.</summary>
public enum ReadoutToken
{
    Cpu,
    CpuTemperature,
    Gpu,
    GpuTemperature,
    Memory,
    Battery,
    BatteryTime,
    BatteryTemperature,
    PeripheralBattery,
    Network,
    DiskUsage,
    DiskActivity,
    ConnectedDevices,
    Power,
}

/// <summary>One readout block: a small label over a value, or a two-line rate.</summary>
public sealed record ReadoutBlock
{
    public required ReadoutToken Token { get; init; }

    /// <summary>Small upper label ("CPU", "RAM", "CPU°C"); null for stacked rate blocks.</summary>
    public string? Label { get; init; }

    /// <summary>The value ("42%", "42% 61°", "↓1.2M"); empty when only a dot or gauge shows.</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>Second line of stacked blocks ("↑320K", "W2.1M").</summary>
    public string? SecondLine { get; init; }

    /// <summary>Reserved text that keeps the block width stable ("100%", "999°").</summary>
    public string Reserve { get; init; } = string.Empty;

    /// <summary>Bars mode: gauge fill 0…1 (quantized), or null for a dash.</summary>
    public double? Gauge { get; init; }

    public bool IsGauge { get; init; }

    public MetricTone Tone { get; init; }

    /// <summary>Memory pressure dot before the value.</summary>
    public MemoryPressure? PressureDot { get; init; }

    /// <summary>Battery glyph level (0/25/50/75/100) and charging bolt.</summary>
    public int? BatteryLevel { get; init; }

    public bool BatteryBolt { get; init; }

    /// <summary>Plain-text form for the tray tooltip ("CPU 42%", "↓1.2M ↑320K").</summary>
    public required string Summary { get; init; }

    /// <summary>The detail view a click opens.</summary>
    public MetricKind Detail => ReadoutTokens.DetailFor(Token);
}

public static class ReadoutTokens
{
    public static IReadOnlyList<ReadoutToken> DefaultOrder { get; } = Enum.GetValues<ReadoutToken>();

    public static string StorageName(ReadoutToken token) => token switch
    {
        ReadoutToken.Cpu => "cpu",
        ReadoutToken.CpuTemperature => "cpuTemperature",
        ReadoutToken.Gpu => "gpu",
        ReadoutToken.GpuTemperature => "gpuTemperature",
        ReadoutToken.Memory => "memory",
        ReadoutToken.Battery => "battery",
        ReadoutToken.BatteryTime => "batteryTime",
        ReadoutToken.BatteryTemperature => "batteryTemperature",
        ReadoutToken.PeripheralBattery => "peripheralBattery",
        ReadoutToken.Network => "network",
        ReadoutToken.DiskUsage => "diskUsage",
        ReadoutToken.DiskActivity => "diskActivity",
        ReadoutToken.ConnectedDevices => "connectedDevices",
        _ => "power",
    };

    public static Setting<bool> Setting(ReadoutToken token) => token switch
    {
        ReadoutToken.Cpu => MonitorSettings.ReadoutCpu,
        ReadoutToken.CpuTemperature => MonitorSettings.ReadoutCpuTemperature,
        ReadoutToken.Gpu => MonitorSettings.ReadoutGpu,
        ReadoutToken.GpuTemperature => MonitorSettings.ReadoutGpuTemperature,
        ReadoutToken.Memory => MonitorSettings.ReadoutMemory,
        ReadoutToken.Battery => MonitorSettings.ReadoutBattery,
        ReadoutToken.BatteryTime => MonitorSettings.ReadoutBatteryTime,
        ReadoutToken.BatteryTemperature => MonitorSettings.ReadoutBatteryTemperature,
        ReadoutToken.PeripheralBattery => MonitorSettings.ReadoutPeripheralBattery,
        ReadoutToken.Network => MonitorSettings.ReadoutNetwork,
        ReadoutToken.DiskUsage => MonitorSettings.ReadoutDiskUsage,
        ReadoutToken.DiskActivity => MonitorSettings.ReadoutDiskActivity,
        ReadoutToken.ConnectedDevices => MonitorSettings.ReadoutConnectedDevices,
        _ => MonitorSettings.ReadoutPower,
    };

    public static string FeatureId(ReadoutToken token) => token switch
    {
        ReadoutToken.Cpu or ReadoutToken.CpuTemperature => FeatureIds.MonitorCpu,
        ReadoutToken.Gpu or ReadoutToken.GpuTemperature => FeatureIds.MonitorGpu,
        ReadoutToken.Memory => FeatureIds.MonitorMemory,
        ReadoutToken.Network => FeatureIds.MonitorNetwork,
        ReadoutToken.DiskUsage or ReadoutToken.DiskActivity => FeatureIds.MonitorDisk,
        ReadoutToken.ConnectedDevices => FeatureIds.ConnectedDevices,
        _ => FeatureIds.MonitorPower,
    };

    /// <summary>Battery, battery time and battery temperature need an internal battery.</summary>
    public static bool NeedsBattery(ReadoutToken token) =>
        token is ReadoutToken.Battery or ReadoutToken.BatteryTime or ReadoutToken.BatteryTemperature;

    public static MetricKind DetailFor(ReadoutToken token) => token switch
    {
        ReadoutToken.Cpu or ReadoutToken.CpuTemperature => MetricKind.Cpu,
        ReadoutToken.Gpu or ReadoutToken.GpuTemperature => MetricKind.Gpu,
        ReadoutToken.Memory => MetricKind.Memory,
        ReadoutToken.Network => MetricKind.Network,
        ReadoutToken.DiskUsage or ReadoutToken.DiskActivity => MetricKind.Disk,
        ReadoutToken.Battery or ReadoutToken.BatteryTemperature or ReadoutToken.PeripheralBattery => MetricKind.Battery,
        ReadoutToken.BatteryTime or ReadoutToken.Power => MetricKind.Power,
        _ => MetricKind.ConnectedDevices,
    };

    /// <summary>Saved order: known tokens once, legacy "temperature" expanded, missing ones appended.</summary>
    public static List<ReadoutToken> ParseOrder(string csv)
    {
        var result = new List<ReadoutToken>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "temperature")
            {
                foreach (var t in new[] { ReadoutToken.CpuTemperature, ReadoutToken.GpuTemperature, ReadoutToken.BatteryTemperature })
                {
                    if (!result.Contains(t)) result.Add(t);
                }

                continue;
            }

            var match = DefaultOrder.Where(t => StorageName(t) == raw).Cast<ReadoutToken?>().FirstOrDefault();
            if (match is { } token && !result.Contains(token))
            {
                result.Add(token);
            }
        }

        result.AddRange(DefaultOrder.Where(t => !result.Contains(t)));
        return result;
    }

    public static string ToCsv(IEnumerable<ReadoutToken> order) => string.Join(',', order.Select(StorageName));

    /// <summary>Localized title of a token (Settings rows, mini monitor tooltips).</summary>
    public static string Title(ReadoutToken token) => token switch
    {
        ReadoutToken.Cpu => L.Get("Strings.monitorShowCPU"),
        ReadoutToken.CpuTemperature => L.Get("Strings.monitorShowCPUTemperature"),
        ReadoutToken.Gpu => L.Get("Strings.monitorShowGPU"),
        ReadoutToken.GpuTemperature => L.Get("Strings.monitorShowGPUTemperature"),
        ReadoutToken.Memory => L.Get("Strings.monitorShowMemory"),
        ReadoutToken.Battery => L.Get("Strings.batteryLabel"),
        ReadoutToken.BatteryTime => L.Get("batteryTime.title"),
        ReadoutToken.BatteryTemperature => L.Get("Strings.monitorShowBatteryTemperature"),
        ReadoutToken.PeripheralBattery => L.Get("Strings.monitorShowPeripheralBattery"),
        ReadoutToken.Network => L.Get("Strings.monitorShowNetwork"),
        ReadoutToken.DiskUsage => L.Get("Strings.monitorItemDiskUsage"),
        ReadoutToken.DiskActivity => L.Get("Strings.monitorItemDiskActivity"),
        ReadoutToken.ConnectedDevices => L.Get("connectedDevices.title"),
        _ => L.Get("Strings.monitorShowPowerLabel"),
    };
}

/// <summary>Readout appearance settings, read once per composition.</summary>
public sealed record ReadoutStyle
{
    public bool Bars { get; init; }

    public bool CombineTemperatures { get; init; } = true;

    public bool UploadFirst { get; init; }

    public bool NetworkBits { get; init; }

    /// <summary>"percent", "dot" or "both".</summary>
    public string MemoryStyle { get; init; } = "percent";

    /// <summary>"percent", "free" or "used".</summary>
    public string DiskStyle { get; init; } = "percent";

    public bool AppMemory { get; init; }

    public TemperatureUnit Unit { get; init; }

    public int MediumThreshold { get; init; } = 70;

    public int HighThreshold { get; init; } = 90;

    public static ReadoutStyle From(ISettingsStore s)
    {
        var medium = s.Get(MonitorSettings.BarMediumThreshold);
        var high = s.Get(MonitorSettings.BarHighThreshold);
        if (high <= medium)
        {
            (medium, high) = (70, 90);
        }

        return new ReadoutStyle
        {
            Bars = s.Get(MonitorSettings.ReadoutAppearance) == "bars",
            CombineTemperatures = s.Get(MonitorSettings.CombineTemperatures),
            UploadFirst = s.Get(MonitorSettings.NetworkUploadFirst),
            NetworkBits = s.Get(MonitorSettings.NetworkSpeedUnit) == "bits",
            MemoryStyle = s.Get(MonitorSettings.MemoryStyle),
            DiskStyle = s.Get(MonitorSettings.DiskStyle),
            AppMemory = s.Get(MonitorSettings.MemoryMetric) == "app",
            Unit = MetricFormat.ParseTemperatureUnit(s.Get(MonitorSettings.TemperatureUnit)),
            MediumThreshold = medium,
            HighThreshold = high,
        };
    }
}

/// <summary>
/// Builds the readout blocks (spec §3.16.2–3.16.3) shown by the mini monitor
/// and summarized in the tray tooltip. Information content matches the macOS
/// menu bar blocks; only the surface differs.
/// </summary>
public static class ReadoutComposer
{
    public const int GaugeSteps = 15;

    /// <summary>Tokens that should render: pinned, family installed, hardware present.</summary>
    public static List<ReadoutToken> EnabledTokens(ISettingsStore settings, FeatureRuntime runtime, bool hasBattery) =>
        ReadoutTokens.ParseOrder(settings.Get(MonitorSettings.ReadoutOrder))
            .Where(t => settings.Get(ReadoutTokens.Setting(t))
                        && runtime.IsAvailable(ReadoutTokens.FeatureId(t))
                        && (!ReadoutTokens.NeedsBattery(t) || hasBattery))
            .ToList();

    public static List<ReadoutBlock> Compose(IReadOnlyList<ReadoutToken> tokens, MonitorSnapshot s, ReadoutStyle style, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var blocks = new List<ReadoutBlock>();
        var enabled = tokens.ToHashSet();
        var combine = style.CombineTemperatures && !style.Bars;
        foreach (var token in tokens)
        {
            switch (token)
            {
                case ReadoutToken.Cpu:
                    blocks.AddIfNotNull(Usage(token, "CPU", s.CpuUsage, combine && enabled.Contains(ReadoutToken.CpuTemperature) ? s.CpuTemperature : null, style, culture));
                    break;
                case ReadoutToken.CpuTemperature when !(combine && enabled.Contains(ReadoutToken.Cpu)):
                    blocks.AddIfNotNull(TemperatureBlock(token, "CPU", s.CpuTemperature, style, culture));
                    break;
                case ReadoutToken.Gpu:
                    blocks.AddIfNotNull(Usage(token, "GPU", s.GpuUsage, combine && enabled.Contains(ReadoutToken.GpuTemperature) ? s.GpuTemperature : null, style, culture));
                    break;
                case ReadoutToken.GpuTemperature when !(combine && enabled.Contains(ReadoutToken.Gpu)):
                    blocks.AddIfNotNull(TemperatureBlock(token, "GPU", s.GpuTemperature, style, culture));
                    break;
                case ReadoutToken.Memory:
                    blocks.Add(MemoryBlock(s, style, culture));
                    break;
                case ReadoutToken.Battery:
                    blocks.AddIfNotNull(BatteryBlock(s, combine && enabled.Contains(ReadoutToken.BatteryTemperature) ? s.BatteryTemperature : null, style, culture));
                    break;
                case ReadoutToken.BatteryTemperature when !(combine && enabled.Contains(ReadoutToken.Battery)):
                    blocks.AddIfNotNull(TemperatureBlock(token, "BAT", s.BatteryTemperature, style, culture));
                    break;
                case ReadoutToken.BatteryTime:
                    blocks.AddIfNotNull(BatteryTimeBlock(s));
                    break;
                case ReadoutToken.PeripheralBattery:
                    blocks.AddIfNotNull(PeripheralBlock(s, culture));
                    break;
                case ReadoutToken.Network:
                    blocks.AddIfNotNull(NetworkBlock(s, style, culture));
                    break;
                case ReadoutToken.DiskUsage:
                    blocks.AddIfNotNull(DiskUsageBlock(s, style, culture));
                    break;
                case ReadoutToken.DiskActivity:
                    blocks.AddIfNotNull(DiskActivityBlock(s, culture));
                    break;
                case ReadoutToken.ConnectedDevices:
                    var count = (s.ConnectedDevices?.Count ?? 0).ToString(culture);
                    var usb = L.Get("connectedDevices.menuBarLabel");
                    blocks.Add(new ReadoutBlock { Token = token, Label = usb, Value = count, Reserve = "99", Summary = $"{usb} {count}" });
                    break;
                case ReadoutToken.Power when s.Power?.SystemWatts is { } watts:
                    var w = MetricFormat.WattsCompact(watts, culture);
                    blocks.Add(new ReadoutBlock { Token = token, Label = "PWR", Value = w, Reserve = "99W", Summary = $"PWR {w}" });
                    break;
            }
        }

        return blocks;
    }

    /// <summary>The tray tooltip line: "CPU 42% · RAM 61% · ↓1.2M ↑320K", cut to fit <paramref name="maxLength"/>.</summary>
    public static string TooltipLine(IEnumerable<ReadoutBlock> blocks, int maxLength = 100)
    {
        var parts = new List<string>();
        var length = 0;
        foreach (var block in blocks)
        {
            var add = (parts.Count > 0 ? 3 : 0) + block.Summary.Length;
            if (length + add > maxLength)
            {
                break;
            }

            parts.Add(block.Summary);
            length += add;
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Gauge colour level for a usage fraction (bars mode thresholds).</summary>
    public static MetricTone GaugeTone(double fraction, ReadoutStyle style)
    {
        var percent = fraction * 100;
        return percent >= style.HighThreshold ? MetricTone.Critical : percent >= style.MediumThreshold ? MetricTone.Elevated : MetricTone.Normal;
    }

    /// <summary>Fill quantized to 15 steps.</summary>
    public static double Quantize(double fraction) => Math.Round(Math.Clamp(fraction, 0, 1) * GaugeSteps) / GaugeSteps;

    private static ReadoutBlock? Usage(ReadoutToken token, string label, double? usage, double? temperature, ReadoutStyle style, CultureInfo culture)
    {
        if (style.Bars)
        {
            return new ReadoutBlock
            {
                Token = token,
                Label = label,
                IsGauge = true,
                Gauge = usage is { } u ? Quantize(u) : null,
                Tone = usage is { } v ? GaugeTone(v, style) : MetricTone.Normal,
                Summary = $"{label} {(usage is { } p ? MetricFormat.Percent(p, culture) : "--")}",
            };
        }

        if (usage is not { } value)
        {
            return null;
        }

        var text = MetricFormat.Percent(value, culture);
        if (temperature is { } t)
        {
            text += " " + MetricFormat.TemperatureCompact(t, style.Unit, culture);
        }

        return new ReadoutBlock
        {
            Token = token,
            Label = label,
            Value = text,
            Reserve = temperature is null ? "100%" : "100% 999°",
            Summary = $"{label} {text}",
        };
    }

    private static ReadoutBlock? TemperatureBlock(ReadoutToken token, string prefix, double? celsius, ReadoutStyle style, CultureInfo culture)
    {
        if (celsius is not { } c)
        {
            return null;
        }

        var label = prefix + (style.Unit == TemperatureUnit.Fahrenheit ? "°F" : "°C");
        var value = MetricFormat.TemperatureCompact(c, style.Unit, culture);
        return new ReadoutBlock { Token = token, Label = label, Value = value, Reserve = "999°", Summary = $"{label} {value}" };
    }

    private static ReadoutBlock MemoryBlock(MonitorSnapshot s, ReadoutStyle style, CultureInfo culture)
    {
        var fraction = s.Memory?.ChosenFraction(style.AppMemory);
        var dot = style.MemoryStyle is "dot" or "both" ? s.Memory?.Pressure ?? MemoryPressure.Unknown : (MemoryPressure?)null;
        var percent = fraction is { } f ? MetricFormat.Percent(f, culture) : "--%";
        if (style.Bars)
        {
            return new ReadoutBlock
            {
                Token = ReadoutToken.Memory,
                Label = "RAM",
                IsGauge = true,
                Gauge = fraction is { } g ? Quantize(g) : null,
                Tone = fraction is { } v ? GaugeTone(v, style) : MetricTone.Normal,
                PressureDot = dot,
                Summary = $"RAM {percent}",
            };
        }

        return new ReadoutBlock
        {
            Token = ReadoutToken.Memory,
            Label = "RAM",
            Value = style.MemoryStyle == "dot" ? string.Empty : percent,
            Reserve = style.MemoryStyle == "dot" ? string.Empty : "100%",
            PressureDot = dot,
            Summary = $"RAM {percent}",
        };
    }

    private static ReadoutBlock? BatteryBlock(MonitorSnapshot s, double? temperature, ReadoutStyle style, CultureInfo culture)
    {
        if (s.Power is not { HasBattery: true, ChargePercent: { } charge } power)
        {
            return null;
        }

        var text = charge.ToString(culture) + "%";
        if (temperature is { } t)
        {
            text += " " + MetricFormat.TemperatureCompact(t, style.Unit, culture);
        }

        return new ReadoutBlock
        {
            Token = ReadoutToken.Battery,
            Label = "BAT",
            Value = text,
            Reserve = temperature is null ? "100%" : "100% 999°",
            BatteryLevel = BatteryMath.GlyphLevel(charge),
            BatteryBolt = power.IsCharging || power.ExternalConnected,
            Summary = $"BAT {text}",
        };
    }

    private static ReadoutBlock? BatteryTimeBlock(MonitorSnapshot s)
    {
        if (s.Power is not { HasBattery: true } power || power.ExternalConnected || power.IsCharging)
        {
            return null;
        }

        var value = power.TimeRemainingSeconds is { } seconds ? MetricFormat.BatteryTime(seconds) : "...";
        return new ReadoutBlock { Token = ReadoutToken.BatteryTime, Label = "BAT", Value = value, Reserve = "99h 59m", Summary = $"BAT {value}" };
    }

    private static ReadoutBlock? PeripheralBlock(MonitorSnapshot s, CultureInfo culture)
    {
        var devices = PeripheralBatteries.Sort(s.PeripheralBatteries);
        if (devices.Count == 0)
        {
            return null;
        }

        var lowest = devices[0];
        var label = PeripheralBatteries.ShortLabel(lowest.Kind);
        var value = lowest.Percent.ToString(culture) + "%" + (devices.Count > 1 ? "+" + Math.Min(9, devices.Count - 1).ToString(culture) : string.Empty);
        return new ReadoutBlock { Token = ReadoutToken.PeripheralBattery, Label = label, Value = value, Reserve = "100%+9", Summary = $"{label} {value}" };
    }

    private static ReadoutBlock? NetworkBlock(MonitorSnapshot s, ReadoutStyle style, CultureInfo culture)
    {
        if (s.NetDownBytesPerSec is not { } down || s.NetUpBytesPerSec is not { } up)
        {
            return null;
        }

        var downText = "↓" + MetricFormat.NetworkRateCompact(down, style.NetworkBits, culture);
        var upText = "↑" + MetricFormat.NetworkRateCompact(up, style.NetworkBits, culture);
        var (first, second) = style.UploadFirst ? (upText, downText) : (downText, upText);
        return new ReadoutBlock { Token = ReadoutToken.Network, Value = first, SecondLine = second, Reserve = "↓00000", Summary = $"{first} {second}" };
    }

    private static ReadoutBlock? DiskUsageBlock(MonitorSnapshot s, ReadoutStyle style, CultureInfo culture)
    {
        if (s.PrimaryDisk is not { } disk)
        {
            return null;
        }

        var info = disk.Info;
        if (style.Bars && style.DiskStyle == "percent")
        {
            return new ReadoutBlock
            {
                Token = ReadoutToken.DiskUsage,
                Label = "DSK",
                IsGauge = true,
                Gauge = Quantize(info.UsedFraction),
                Tone = GaugeTone(info.UsedFraction, style),
                Summary = $"DSK {MetricFormat.Percent(info.UsedFraction, culture)}",
            };
        }

        var value = style.DiskStyle switch
        {
            "free" => MetricFormat.DiskBytes(info.Free, culture),
            "used" => MetricFormat.DiskBytes(info.Used, culture),
            _ => MetricFormat.Percent(info.UsedFraction, culture),
        };
        return new ReadoutBlock
        {
            Token = ReadoutToken.DiskUsage,
            Label = "DSK",
            Value = value,
            Reserve = style.DiskStyle == "percent" ? "100%" : "1000 GB",
            Summary = $"DSK {value}",
        };
    }

    private static ReadoutBlock? DiskActivityBlock(MonitorSnapshot s, CultureInfo culture)
    {
        if (s.DiskReadBytesPerSec is not { } read || s.DiskWriteBytesPerSec is not { } write)
        {
            return null;
        }

        var r = "R" + MetricFormat.BytesPerSecCompact(read, culture);
        var w = "W" + MetricFormat.BytesPerSecCompact(write, culture);
        return new ReadoutBlock { Token = ReadoutToken.DiskActivity, Value = r, SecondLine = w, Reserve = "R00000", Summary = $"{r} {w}" };
    }

    private static void AddIfNotNull(this List<ReadoutBlock> list, ReadoutBlock? block)
    {
        if (block is not null)
        {
            list.Add(block);
        }
    }
}

/// <summary>Peripheral battery rules (spec §3.10).</summary>
public static class PeripheralBatteries
{
    /// <summary>Lowest percent first, then kind, then name.</summary>
    public static List<PeripheralBattery> Sort(IEnumerable<PeripheralBattery> devices) =>
        devices.OrderBy(d => d.Percent).ThenBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static string ShortLabel(PeripheralKind kind) => kind switch
    {
        PeripheralKind.Keyboard => "KBD",
        PeripheralKind.Mouse => "MOU",
        PeripheralKind.Trackpad => "TRK",
        PeripheralKind.Audio => "AUD",
        _ => "PER",
    };

    /// <summary>Kind from the device name (keyboard, mouse, trackpad, audio), else device.</summary>
    public static PeripheralKind KindFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("keyboard", StringComparison.Ordinal)) return PeripheralKind.Keyboard;
        if (n.Contains("mouse", StringComparison.Ordinal)) return PeripheralKind.Mouse;
        if (n.Contains("trackpad", StringComparison.Ordinal) || n.Contains("track pad", StringComparison.Ordinal)) return PeripheralKind.Trackpad;
        if (n.Contains("airpods", StringComparison.Ordinal) || n.Contains("headphone", StringComparison.Ordinal)
            || n.Contains("headset", StringComparison.Ordinal) || n.Contains("buds", StringComparison.Ordinal)) return PeripheralKind.Audio;
        return PeripheralKind.Device;
    }

    /// <summary>A percent must be an exact integer 0…100 ("85", "85%", 85.0).</summary>
    public static int? ParsePercent(object? value)
    {
        double number;
        switch (value)
        {
            case null:
                return null;
            case string text:
                text = text.Trim().TrimEnd('%').Trim();
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
                break;
            case IConvertible convertible:
                try
                {
                    number = convertible.ToDouble(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }

                break;
            default:
                return null;
        }

        if (!double.IsFinite(number))
        {
            return null;
        }

        var rounded = Math.Round(number);
        return rounded is >= 0 and <= 100 ? (int)rounded : null;
    }

    /// <summary>One row per identity, then per (lower-case name, kind); the first source wins.</summary>
    public static List<PeripheralBattery> Merge(IEnumerable<PeripheralBattery> preferred, IEnumerable<PeripheralBattery> fallback)
    {
        var result = new List<PeripheralBattery>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<(string, PeripheralKind)>();
        foreach (var device in preferred.Concat(fallback))
        {
            var key = (device.Name.Trim().ToLowerInvariant(), device.Kind);
            if (!ids.Add(device.Id) || !names.Add(key))
            {
                continue;
            }

            result.Add(device);
        }

        return Sort(result);
    }
}

/// <summary>
/// The keep-awake countdown shown first among the readouts when "Show remaining
/// time" is on. Implemented by the Keep Awake module; absent when it is not installed.
/// </summary>
public interface IReadoutCountdownSource
{
    /// <summary>"1:05", "12 min" or "∞" while a session runs and the option is on; otherwise null.</summary>
    string? Countdown { get; }

    event EventHandler? Changed;
}
