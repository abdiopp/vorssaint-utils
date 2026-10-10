// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Settings;

namespace Rivet.Core.Displays;

/// <summary>Display brightness preferences (spec 03 §4.7; keys shared with macOS).</summary>
public static class DisplaySettings
{
    /// <summary>"Control displays": the feature's enable key (defined by the shell).</summary>
    public static Setting<bool> Enabled => FeatureKeys.BrightnessControlEnabled;

    /// <summary>
    /// macOS: brightness keys follow the pointer. Windows cannot take over the
    /// brightness keys, so the key now means "the shortcuts act on the display
    /// under the pointer" (otherwise they act on the main display).
    /// </summary>
    public static readonly Setting<bool> FollowPointer = new("brightnessKeysEnabled", false);

    public static readonly Setting<bool> OsdEnabled = new("brightnessOSDEnabled", false);

    public static readonly Setting<string> KeyStep = new("brightnessKeyStep", "standard", Sanitize.OneOfStrings("standard", "standard", "half", "quarter"));

    public static readonly Setting<bool> ShortcutsEnabled = new("displayBrightnessShortcutsEnabled", false);

    public static readonly Setting<string> DecreaseShortcut = new("displayBrightnessDecreaseShortcut", string.Empty);

    public static readonly Setting<string> IncreaseShortcut = new("displayBrightnessIncreaseShortcut", string.Empty);

    /// <summary>Connections whose monitor accepted DDC writes but never answered a read (≤ 16).</summary>
    public static readonly Setting<List<string>> WriteOnlyPaths = new("brightnessDDCWriteOnlyPaths", [], PathList.Clean, machineState: true);

    /// <summary>"Dim the picture" choices (≤ 16).</summary>
    public static readonly Setting<List<string>> ForcedSoftwarePaths = new("brightnessForcedSoftwarePaths", [], PathList.Clean);

    /// <summary>"Extra dimming" choices (≤ 16).</summary>
    public static readonly Setting<List<string>> ExtendedDimmingPaths = new("brightnessExtendedDimmingPaths", [], PathList.Clean);

    public static IReadOnlyList<SettingDefinition> ChoiceKeys { get; } = [ForcedSoftwarePaths, ExtendedDimmingPaths];
}

/// <summary>The small path lists the brightness feature remembers (spec: at most 16 entries).</summary>
public static class PathList
{
    public const int Capacity = 16;

    public static List<string> Clean(List<string> paths)
    {
        var result = new List<string>();
        foreach (var path in paths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result.Count <= Capacity ? result : result[^Capacity..];
    }

    /// <summary>Adds (or refreshes) <paramref name="path"/> as the newest entry; the oldest drops past the capacity.</summary>
    public static List<string> Add(IEnumerable<string> paths, string path)
    {
        var list = paths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(path);
        return list.Count <= Capacity ? list : list[^Capacity..];
    }

    public static List<string> Remove(IEnumerable<string> paths, string path) =>
        paths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList();

    public static bool Contains(IEnumerable<string> paths, string path) =>
        paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}
