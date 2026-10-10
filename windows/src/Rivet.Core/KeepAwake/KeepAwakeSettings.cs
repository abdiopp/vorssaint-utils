// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Awake;

/// <summary>Keep Awake preferences (spec 03 §4.6); keys match the macOS app.</summary>
public static class KeepAwakeSettings
{
    /// <summary>Presets in minutes; 0 means indefinitely.</summary>
    public static readonly IReadOnlyList<int> Durations = [15, 30, 60, 120, 240, 480, 0];

    public static readonly Setting<int> DefaultDurationMinutes = new("defaultDurationMinutes", 0, Sanitize.OneOf(0, 0, 15, 30, 60, 120, 240, 480));

    /// <summary>Battery protection: end the session at or below this charge on battery (0 = never).</summary>
    public static readonly Setting<int> BatteryLimitPercent = new("batteryLimitPercent", 10, Sanitize.OneOf(10, 0, 5, 10, 15, 20));

    public static readonly Setting<bool> AutoStart = new("keepAwakeAutoStart", false);

    public static readonly Setting<bool> AllowDisplaySleep = new("keepAwakeAllowDisplaySleep", false);

    public static readonly Setting<bool> AutomationExternalDisplay = new("keepAwakeExternalDisplay", false);

    public static readonly Setting<bool> AutomationPower = new("keepAwakeConnectedToPower", false);

    public static readonly Setting<bool> AutomationApps = new("keepAwakeRunningApps", false);

    /// <summary>
    /// App identities for the Applications condition. On Windows these are
    /// executable file names ("zoom.exe"), matched ignoring case; bundle ids
    /// from a macOS backup simply never match.
    /// </summary>
    public static readonly Setting<List<string>> AutomationAppList = new("keepAwakeRunningAppBundleIDs", [], CleanApps);

    public static readonly Setting<bool> AutomationRequireAll = new("keepAwakeAutomationRequireAll", false);

    public static readonly Setting<bool> PauseWhenLocked = new("keepAwakePauseWhenLocked", false);

    public static readonly Setting<bool> SwitchUsesUntil = new("keepAwakeSwitchUsesUntil", false);

    /// <summary>Last picked end time, seconds since 2001-01-01 UTC (the macOS reference date); 0 = none.</summary>
    public static readonly Setting<double> UntilTime = new("keepAwakeUntilTime", 0d, v => double.IsFinite(v) && v >= 0 ? v : 0);

    public static readonly Setting<bool> MouseJiggle = new("keepAwakeMouseJiggleEnabled", false);

    public static readonly Setting<int> MouseJiggleIntervalMinutes = new("keepAwakeMouseJiggleIntervalMinutes", 5, Sanitize.OneOf(5, 1, 2, 5, 10, 15));

    /// <summary>The global Keep Awake shortcut is registered.</summary>
    public static readonly Setting<bool> HotkeyEnabled = new("hotkeyEnabled", true);

    /// <summary>Stored chord ("ctrl+alt+win:0x4B"); empty = the default Ctrl+Alt+Win+K.</summary>
    public static readonly Setting<string> Shortcut = new("keepAwakeShortcut", string.Empty);

    public static readonly Setting<string> IconTint = new("keepAwakeIconTint", "orange", Sanitize.OneOfStrings("orange", "orange", "green", "blue", "purple", "pink", "none"));

    /// <summary>Kept for backups; the Windows tray glyph cannot change shape yet (see docs).</summary>
    public static readonly Setting<string> ActiveIcon = new("keepAwakeActiveIcon", "vorssaint", Sanitize.OneOfStrings("vorssaint", "vorssaint", "coffee", "eye", "moon", "light"));

    /// <summary>Show the remaining time with the readouts and in the tray tooltip.</summary>
    public static readonly Setting<bool> ShowCountdown = new("showCountdownInMenuBar", false);

    /// <summary>"Keep going with the lid closed": the power plan's lid action becomes "Do nothing" during sessions.</summary>
    public static readonly Setting<bool> LidModePreferred = new("clamshellPreferred", false);

    /// <summary>Write-ahead marker: the lid action was changed and must be restored (machine state).</summary>
    public static readonly Setting<LidRecovery?> LidRecoveryMarker = new("vorssDisabledSleep", null, machineState: true);

    public static IReadOnlyList<SettingDefinition> AutomationKeys { get; } =
        [AutomationExternalDisplay, AutomationPower, AutomationApps, AutomationAppList, AutomationRequireAll];

    /// <summary>Sanitizes minutes to a preset (anything else = indefinitely).</summary>
    public static int SanitizeMinutes(int minutes) => Durations.Contains(minutes) ? minutes : 0;

    /// <summary>Lower-case file names, trimmed and unique.</summary>
    internal static List<string> CleanApps(List<string> values)
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

    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static double ToReferenceSeconds(DateTimeOffset time) => (time - ReferenceDate).TotalSeconds;

    public static DateTimeOffset FromReferenceSeconds(double seconds) => ReferenceDate.AddSeconds(seconds);
}

/// <summary>What the lid action was before the app changed it (restored after a crash too).</summary>
public sealed record LidRecovery
{
    public required Guid Scheme { get; init; }

    public required uint OriginalAc { get; init; }

    public required uint OriginalDc { get; init; }

    /// <summary>The value the app wrote (restore only if it is still there).</summary>
    public uint Written { get; init; }
}
