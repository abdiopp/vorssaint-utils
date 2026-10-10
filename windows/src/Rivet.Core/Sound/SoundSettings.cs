// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Sound;

/// <summary>What the headphone guard lowered, so it can give the level back (spec §3.12).</summary>
public sealed record HeadphoneGuardRestore(string DeviceId, double PreviousLevel, double AppliedLevel);

/// <summary>
/// Preferences of the sound features. Keys match the macOS app (spec 04 §4.2)
/// wherever the setting exists there; Windows-only keys are marked. Maps keyed
/// by app or device are stored as JSON objects; "absent" and "empty" stay
/// distinct where the spec says so (<see cref="MicMuteMutedDevices"/>).
/// </summary>
public static class SoundSettings
{
    /// <summary>Most entries a priority list keeps (spec §6.10).</summary>
    public const int MaxPriorityEntries = 64;

    // ── Volume mixer ────────────────────────────────────────────────────

    /// <summary>Saved per-app volume by persistence id; 100 % entries are removed. Windows caps at 1.0 (no boost).</summary>
    public static readonly Setting<IReadOnlyDictionary<string, double>> AppVolumes =
        new("appVolumes", Empty<double>(), SanitizeVolumes);

    /// <summary>Saved per-app output: persistence id → endpoint id.</summary>
    public static readonly Setting<IReadOnlyDictionary<string, string>> AppOutputDevices =
        new("appOutputDevices", Empty<string>(), SanitizeIdMap);

    /// <summary>Last successful manual "all apps" output.</summary>
    public static readonly Setting<string> UniversalOutputDevice =
        new("mixerUniversalOutputDevice", string.Empty, SanitizeIdOrEmpty);

    /// <summary>Windows: the permanent System sounds row (the analog of the macOS Finder row, <c>mixerShowFinder</c>).</summary>
    public static readonly Setting<bool> ShowSystemSounds = new("mixerShowSystemSounds", true);

    /// <summary>JSON <c>{"order":[ids],"pinned":[ids]}</c>, stored as a string like on macOS.</summary>
    public static readonly Setting<string> AppArrangement = new("mixerAppArrangement", string.Empty);

    public static readonly Setting<bool> HideInactiveApps = new("mixerHideInactiveApps", false);

    /// <summary>Apps taken off the list: persistence id → display name.</summary>
    public static readonly Setting<IReadOnlyDictionary<string, string>> HiddenApps =
        new("mixerHiddenApps", Empty<string>(), SanitizeNameMap);

    public static readonly Setting<bool> LowerVolumeOnHeadphonesDisconnect = new("mixerLowerVolumeOnHeadphonesDisconnect", false);

    /// <summary>Level after headphones disconnect, 10–100 %. Values below 10 migrate to 25 (it prevents a blast, it never silences).</summary>
    public static readonly Setting<int> HeadphonesDisconnectVolumePercent =
        new("mixerHeadphonesDisconnectVolumePercent", 25, v => v < 10 ? 25 : Math.Min(v, 100));

    /// <summary>Windows: the pending give-back of the headphone guard, kept across a relaunch (machine state).</summary>
    public static readonly Setting<HeadphoneGuardRestore?> HeadphoneGuardPending =
        new("mixerHeadphoneGuardRestore", null, SanitizeGuardRecord, machineState: true);

    public static readonly Setting<bool> PreciseVolumeRollerEnabled = new("preciseVolumeRollerEnabled", false);

    /// <summary>Windows: the fine step in percent (Windows' own keys step 2 %).</summary>
    public static readonly Setting<double> PreciseVolumeRollerStepPercent =
        new("preciseVolumeRollerStepPercent", 1.0, v => v is 0.5 or 1.0 ? v : 1.0);

    /// <summary>Preferred microphone (endpoint id); empty = no preference.</summary>
    public static readonly Setting<string> PreferredInputDevice =
        new("preferredInputDevice", string.Empty, SanitizeIdOrEmpty);

    /// <summary>Windows: the input that was default before the preference first took over (machine state).</summary>
    public static readonly Setting<string> PreferredInputOriginalDevice =
        new("preferredInputOriginalDevice", string.Empty, SanitizeIdOrEmpty, machineState: true);

    // ── Output switcher ─────────────────────────────────────────────────

    /// <summary>Storage of the cycle shortcut (<see cref="Shortcuts.KeyChord"/> format; empty = default Ctrl+Alt+Win+S).</summary>
    public static readonly Setting<string> OutputSwitcherShortcut = new("soundOutputSwitcherShortcut", string.Empty);

    /// <summary>Outputs in the cycle, in order. Null = never chosen.</summary>
    public static readonly Setting<IReadOnlyList<string>?> OutputSwitcherDeviceIds =
        new("soundOutputSwitcherDeviceUIDs", null, v => v is null ? null : SanitizeIdList(v, int.MaxValue));

    // ── Audio device priority ───────────────────────────────────────────

    public static readonly Setting<IReadOnlyList<string>> PriorityOutputIds =
        new("audioPriorityOutputUIDs", [], v => SanitizeIdList(v, MaxPriorityEntries));

    public static readonly Setting<IReadOnlyList<string>> PriorityInputIds =
        new("audioPriorityInputUIDs", [], v => SanitizeIdList(v, MaxPriorityEntries));

    /// <summary>Last-known names of listed devices, shown while they are disconnected.</summary>
    public static readonly Setting<IReadOnlyDictionary<string, string>> PriorityDeviceNames =
        new("audioPriorityDeviceNames", Empty<string>(), SanitizeNameMap);

    // ── Mute microphone ─────────────────────────────────────────────────

    public static readonly Setting<bool> MicMuteShortcutEnabled = new("micMuteShortcutEnabled", false);

    /// <summary>Storage of the mute shortcut (empty = default Ctrl+Alt+Win+M).</summary>
    public static readonly Setting<string> MicMuteShortcut = new("micMuteShortcut", string.Empty);

    /// <summary>Red badge on the tray icon while muted (the key keeps its macOS name).</summary>
    public static readonly Setting<bool> MicMuteTrayIndicator = new("micMuteMenuBarIndicator", true);

    /// <summary>Mute is on; survives relaunch.</summary>
    public static readonly Setting<bool> MicMuteActive = new("micMuteActive", false, machineState: true);

    /// <summary>Legacy single saved level.</summary>
    public static readonly Setting<double> MicMuteSavedVolume =
        new("micMuteSavedVolume", 0.75, v => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.75, machineState: true);

    /// <summary>Levels to restore, by endpoint id.</summary>
    public static readonly Setting<IReadOnlyDictionary<string, double>> MicMuteSavedVolumes =
        new("micMuteSavedVolumes", Empty<double>(), SanitizeLevels, machineState: true);

    /// <summary>Channel levels to restore: endpoint id → {channel index: level}.</summary>
    public static readonly Setting<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>> MicMuteSavedChannelVolumes =
        new("micMuteSavedChannelVolumes", new Dictionary<string, IReadOnlyDictionary<string, double>>(), SanitizeChannelLevels, machineState: true);

    /// <summary>
    /// Devices this app silenced. Null (never tracked) ≠ empty (nothing this
    /// app may open): unmuting with a missing list restores every device.
    /// </summary>
    public static readonly Setting<IReadOnlyList<string>?> MicMuteMutedDevices =
        new("micMuteMutedDevices", null, v => v is null ? null : SanitizeIdList(v, int.MaxValue), machineState: true);

    // ── Sanitizers ──────────────────────────────────────────────────────

    /// <summary>Device and app ids: trimmed, non-empty, ≤ 512 characters, no control characters.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && id.Trim().Length == id.Length && !id.Any(char.IsControl);

    public static IReadOnlyList<string> SanitizeIdList(IReadOnlyList<string>? ids, int max)
    {
        if (ids is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(Math.Min(ids.Count, max));
        foreach (var raw in ids)
        {
            var id = raw?.Trim();
            if (IsValidId(id) && seen.Add(id!))
            {
                result.Add(id!);
                if (result.Count == max)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string SanitizeIdOrEmpty(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return IsValidId(trimmed) ? trimmed : string.Empty;
    }

    private static IReadOnlyDictionary<string, T> Empty<T>() => new Dictionary<string, T>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, double> SanitizeVolumes(IReadOnlyDictionary<string, double> map)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? Empty<double>())
        {
            var volume = VolumeMath.ClampAppVolume(value);
            if (IsValidId(key) && !VolumeMath.IsUnity(volume))
            {
                result[key] = volume;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, double> SanitizeLevels(IReadOnlyDictionary<string, double> map)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? Empty<double>())
        {
            if (IsValidId(key) && double.IsFinite(value))
            {
                result[key] = Math.Clamp(value, 0, 1);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> SanitizeChannelLevels(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> map)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (device, channels) in map ?? new Dictionary<string, IReadOnlyDictionary<string, double>>())
        {
            if (!IsValidId(device) || channels is null)
            {
                continue;
            }

            var clean = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (channel, level) in channels)
            {
                if (int.TryParse(channel, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    && index is >= 0 and < 64 && double.IsFinite(level))
                {
                    clean[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Math.Clamp(level, 0, 1);
                }
            }

            if (clean.Count > 0)
            {
                result[device] = clean;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> SanitizeIdMap(IReadOnlyDictionary<string, string> map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? Empty<string>())
        {
            if (IsValidId(key) && IsValidId(value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> SanitizeNameMap(IReadOnlyDictionary<string, string> map)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? Empty<string>())
        {
            var name = value?.Trim() ?? string.Empty;
            if (IsValidId(key) && name.Length is > 0 and <= 512 && !name.Any(char.IsControl))
            {
                result[key] = name;
            }
        }

        return result;
    }

    private static HeadphoneGuardRestore? SanitizeGuardRecord(HeadphoneGuardRestore? record) =>
        record is not null && IsValidId(record.DeviceId)
        && double.IsFinite(record.PreviousLevel) && double.IsFinite(record.AppliedLevel)
            ? record with { PreviousLevel = Math.Clamp(record.PreviousLevel, 0, 1), AppliedLevel = Math.Clamp(record.AppliedLevel, 0, 1) }
            : null;
}
