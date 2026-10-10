// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.SystemMonitor;

/// <summary>
/// Preferences of the system monitor. Keys and defaults match the macOS app
/// (spec 03 §4) wherever the setting exists there. The macOS "menu bar"
/// readout keys are reused for the Windows readouts (tray tooltip summary and
/// the mini monitor window), so a token pinned on one platform means the same
/// thing on the other.
/// </summary>
public static class MonitorSettings
{
    // ── Sampling and readings (§4.1) ────────────────────────────────────
    public static readonly Setting<int> IntervalSeconds = new("monitorIntervalSeconds", 2, Sanitize.OneOf(2, 1, 2, 5));

    public static readonly Setting<string> TemperatureUnit = new("temperatureUnit", "celsius", Sanitize.OneOfStrings("celsius", "celsius", "fahrenheit"));

    /// <summary>"bytes" or "bits"; only the exact value "bits" means bits.</summary>
    public static readonly Setting<string> NetworkSpeedUnit = new("networkSpeedUnit", "bytes", v => v == "bits" ? "bits" : "bytes");

    public static readonly Setting<string> MemoryMetric = new("monitorMemoryMetric", "used", Sanitize.OneOfStrings("used", "used", "app"));

    // ── Readouts (§4.2, menu bar keys reused for the tray and mini monitor) ─
    public static readonly Setting<bool> ReadoutCpu = new("menuBarCPU", false);
    public static readonly Setting<bool> ReadoutCpuTemperature = new("menuBarCPUTemperature", false);
    public static readonly Setting<bool> ReadoutGpu = new("menuBarGPU", false);
    public static readonly Setting<bool> ReadoutGpuTemperature = new("menuBarGPUTemperature", false);
    public static readonly Setting<bool> ReadoutMemory = new("menuBarMemory", false);
    public static readonly Setting<bool> ReadoutBattery = new("menuBarBattery", false);
    public static readonly Setting<bool> ReadoutBatteryTime = new("menuBarBatteryTime", false);
    public static readonly Setting<bool> ReadoutBatteryTemperature = new("menuBarBatteryTemperature", false);
    public static readonly Setting<bool> ReadoutPeripheralBattery = new("menuBarPeripheralBattery", false);
    public static readonly Setting<bool> ReadoutNetwork = new("menuBarNetwork", false);
    public static readonly Setting<bool> ReadoutDiskUsage = new("menuBarDiskUsage", false);
    public static readonly Setting<bool> ReadoutDiskActivity = new("menuBarDiskActivity", false);
    public static readonly Setting<bool> ReadoutConnectedDevices = new("menuBarConnectedDevices", false);
    public static readonly Setting<bool> ReadoutPower = new("menuBarPower", false);

    /// <summary>Token order (CSV). Unknown tokens are ignored, missing ones appended in default order.</summary>
    public static readonly Setting<string> ReadoutOrder = new("menuBarMetricOrder", string.Empty);

    public static readonly Setting<string> ReadoutAppearance = new("menuBarMetricAppearance", "values", Sanitize.OneOfStrings("values", "values", "bars"));

    public static readonly Setting<bool> CombineTemperatures = new("menuBarCombineTemperatures", true);

    public static readonly Setting<string> ReadoutSpacing = new("menuBarMetricSpacing", "compact", Sanitize.OneOfStrings("compact", "standard", "compact"));

    public static readonly Setting<bool> NetworkUploadFirst = new("menuBarNetworkUploadFirst", false);

    public static readonly Setting<string> MemoryStyle = new("menuBarMemoryStyle", "percent", Sanitize.OneOfStrings("percent", "dot", "percent", "both"));

    public static readonly Setting<string> DiskStyle = new("menuBarDiskStyle", "percent", Sanitize.OneOfStrings("percent", "percent", "free", "used"));

    public static readonly Setting<string> BarNormalColor = new("menuBarUsageBarNormalColor", "#64D2FF", SanitizeHexColor("#64D2FF"));
    public static readonly Setting<string> BarElevatedColor = new("menuBarUsageBarElevatedColor", "#FFD60A", SanitizeHexColor("#FFD60A"));
    public static readonly Setting<string> BarCriticalColor = new("menuBarUsageBarCriticalColor", "#FF453A", SanitizeHexColor("#FF453A"));
    public static readonly Setting<int> BarMediumThreshold = new("menuBarUsageBarMediumThreshold", 70, v => v is >= 1 and <= 99 ? v : 70);
    public static readonly Setting<int> BarHighThreshold = new("menuBarUsageBarHighThreshold", 90, v => v is >= 2 and <= 100 ? v : 90);

    /// <summary>Show the keep-awake countdown with the readouts ("Show remaining time next to the icon").</summary>
    public static Setting<bool> ShowCountdown => Awake.KeepAwakeSettings.ShowCountdown;

    // ── Windows readout surfaces ────────────────────────────────────────
    /// <summary>Pinned readouts also appear as one line of the tray icon's tooltip.</summary>
    public static readonly Setting<bool> TrayTooltipReadouts = new("monitorTrayTooltipReadouts", true);

    /// <summary>The small always-on-top window that replaces the macOS menu bar readouts.</summary>
    public static readonly Setting<bool> MiniMonitorEnabled = new("monitorMiniMonitorEnabled", false);

    /// <summary>Clicks pass through the mini monitor (it can then only be moved after switching this off).</summary>
    public static readonly Setting<bool> MiniMonitorClickThrough = new("monitorMiniMonitorClickThrough", false);

    /// <summary>Hide the mini monitor while a full-screen app, game or presentation is in front.</summary>
    public static readonly Setting<bool> MiniMonitorHideInFullScreen = new("monitorMiniMonitorHideInFullScreen", true);

    /// <summary>Last position of the mini monitor in physical pixels; int.MinValue = not placed yet.</summary>
    public static readonly Setting<int> MiniMonitorX = new("monitorMiniMonitorX", int.MinValue, machineState: true);

    public static readonly Setting<int> MiniMonitorY = new("monitorMiniMonitorY", int.MinValue, machineState: true);

    // ── Panel items (§4.3) ──────────────────────────────────────────────
    public static readonly Setting<bool> SysTemps = new("monitorSysTemps", true);
    public static readonly Setting<bool> SysCpu = new("monitorSysCPU", true);
    public static readonly Setting<bool> SysCpuCores = new("monitorSysCPUCores", true);
    public static readonly Setting<bool> SysGpu = new("monitorSysGPU", true);
    public static readonly Setting<bool> SysMemory = new("monitorSysMemory", true);
    public static readonly Setting<bool> SysUptime = new("monitorSysUptime", true);
    public static readonly Setting<bool> SysConnectedDevices = new("monitorSysConnectedDevices", true);

    /// <summary>Power → Charge row (named "Sys" historically).</summary>
    public static readonly Setting<bool> SysBattery = new("monitorSysBattery", true);

    public static readonly Setting<bool> NetSpeed = new("monitorNetSpeed", true);
    public static readonly Setting<bool> NetApps = new("monitorNetApps", true);
    public static readonly Setting<bool> NetTotals = new("monitorNetTotals", true);
    public static readonly Setting<bool> NetAddresses = new("monitorNetAddresses", true);
    public static readonly Setting<bool> NetTest = new("monitorNetTest", true);

    public static readonly Setting<bool> DiskUsage = new("monitorDiskUsage", true);
    public static readonly Setting<bool> DiskActivity = new("monitorDiskActivity", true);
    public static readonly Setting<bool> DiskSmart = new("monitorDiskSMART", true);
    public static readonly Setting<bool> DiskProtection = new("monitorDiskProtection", true);
    public static readonly Setting<bool> DiskTools = new("monitorDiskTools", true);

    public static readonly Setting<bool> PwrTemperature = new("monitorPwrTemperature", true);
    public static readonly Setting<bool> PwrSystem = new("monitorPwrSystem", true);
    public static readonly Setting<bool> PwrAdapter = new("monitorPwrAdapter", true);
    public static readonly Setting<bool> PwrBattery = new("monitorPwrBattery", true);
    public static readonly Setting<bool> PwrTimeRemaining = new("monitorPwrTimeRemaining", true);
    public static readonly Setting<bool> PwrHealth = new("monitorPwrHealth", true);

    public static readonly Setting<bool> GraphCpu = new("monitorGraphCPU", true);
    public static readonly Setting<bool> GraphGpu = new("monitorGraphGPU", true);
    public static readonly Setting<bool> GraphMemory = new("monitorGraphMemory", true);
    public static readonly Setting<bool> GraphNetwork = new("monitorGraphNetwork", true);
    public static readonly Setting<bool> GraphDisk = new("monitorGraphDisk", true);
    public static readonly Setting<bool> GraphPower = new("monitorGraphPower", true);
    public static readonly Setting<bool> GraphBattery = new("monitorGraphBattery", true);
    public static readonly Setting<bool> GraphScale = new("monitorGraphScale", true);

    public static readonly Setting<string> SystemOrder = new("panelSystemOrder", string.Empty);
    public static readonly Setting<string> NetworkOrder = new("panelNetworkOrder", string.Empty);
    public static readonly Setting<string> DiskOrder = new("panelDiskOrder", string.Empty);
    public static readonly Setting<string> PowerOrder = new("panelPowerOrder", string.Empty);

    // ── Alerts (§4.4). Out-of-range stored thresholds fall back to the default. ─
    public static readonly Setting<bool> AlertCpu = new("monitorAlertCPU", false);
    public static readonly Setting<int> AlertCpuThreshold = new("monitorAlertCPUThreshold", 90, Stepped(90, 50, 100, 5));
    public static readonly Setting<bool> AlertCpuTemperature = new("monitorAlertCPUTemperature", false);
    public static readonly Setting<int> AlertCpuTemperatureThreshold = new("monitorAlertCPUTemperatureThreshold", 90, Stepped(90, 70, 105, 5));
    public static readonly Setting<bool> AlertBatteryTemperature = new("monitorAlertBatteryTemperature", false);
    public static readonly Setting<int> AlertBatteryTemperatureThreshold = new("monitorAlertBatteryTemperatureThreshold", 40, Stepped(40, 30, 50, 5));
    public static readonly Setting<bool> AlertMemory = new("monitorAlertMemory", false);
    public static readonly Setting<bool> AlertDisk = new("monitorAlertDisk", false);
    public static readonly Setting<int> AlertDiskFreePercent = new("monitorAlertDiskFreePercent", 10, Stepped(10, 5, 30, 5));
    public static readonly Setting<bool> AlertBattery = new("monitorAlertBattery", false);
    public static readonly Setting<int> AlertBatteryPercent = new("monitorAlertBatteryPercent", 15, Stepped(15, 5, 50, 5));
    public static readonly Setting<int> AlertCooldownMinutes = new("monitorAlertCooldownMinutes", 15, Sanitize.OneOf(15, 2, 5, 15, 30, 60));

    // ── Disks (§4.5) ────────────────────────────────────────────────────
    public static readonly Setting<List<string>> DiskEjectExcluded = new("diskEjectExcludedVolumes", [], CleanList);

    /// <summary>Every key whose change can alter what the sampler reads.</summary>
    public static IReadOnlyList<SettingDefinition> PlanKeys { get; } =
    [
        IntervalSeconds, SysTemps, SysCpu, SysCpuCores, SysGpu, SysMemory, SysConnectedDevices, SysBattery, PwrTemperature,
        ReadoutCpu, ReadoutCpuTemperature, ReadoutGpu, ReadoutGpuTemperature, ReadoutMemory, ReadoutBattery, ReadoutBatteryTime,
        ReadoutBatteryTemperature, ReadoutPeripheralBattery, ReadoutNetwork, ReadoutDiskUsage, ReadoutDiskActivity,
        ReadoutConnectedDevices, ReadoutPower, TrayTooltipReadouts, MiniMonitorEnabled,
        AlertCpu, AlertCpuTemperature, AlertBatteryTemperature, AlertMemory, AlertDisk, AlertBattery,
    ];

    /// <summary>Any alert switched on (before checking which families are installed).</summary>
    public static IReadOnlyList<Setting<bool>> AlertSwitches { get; } =
        [AlertCpu, AlertCpuTemperature, AlertBatteryTemperature, AlertMemory, AlertDisk, AlertBattery];

    private static Func<int, int> Stepped(int fallback, int min, int max, int step) =>
        v => v >= min && v <= max && (v - min) % step == 0 ? v : fallback;

    private static Func<string, string> SanitizeHexColor(string fallback) =>
        v => v.Length == 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit) ? v.ToUpperInvariant() : fallback;

    /// <summary>Trimmed, non-empty, unique ignoring case (first spelling wins).</summary>
    internal static List<string> CleanList(List<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (!string.IsNullOrEmpty(value) && seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}
