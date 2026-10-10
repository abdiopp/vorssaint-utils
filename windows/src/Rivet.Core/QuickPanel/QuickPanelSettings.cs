// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.QuickPanel;

/// <summary>Quick panel (code name QuickLauncher) settings; keys match the macOS app (spec 06 §4).</summary>
public static class QuickPanelSettings
{
    /// <summary>The quick panel's shortcut is on by default, as on macOS.</summary>
    public static readonly Setting<bool> ShortcutEnabled = new("quickLauncherShortcutEnabled", true);

    public static readonly Setting<string> Shortcut = new("quickLauncherShortcut", string.Empty);

    /// <summary>Ctrl+Alt+Win+Q (spec 05 §6.1; Win+Ctrl+V is taken by Windows).</summary>
    public static readonly KeyChord DefaultShortcut = new(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, 'Q');

    public static readonly Setting<string> ItemOrder = new("quickLauncherItemOrder", string.Empty);

    public static readonly Setting<string> HiddenItems = new("quickLauncherHiddenItems", string.Empty);

    /// <summary>The spec's default tile order (spec 06 §3.9); tiles of other features join after them.</summary>
    public static readonly IReadOnlyList<string> DefaultOrder =
    [
        "keepAwake", "cleaner", "toggles", "micMute", "screenOCR", "colorPicker", "clipboard", "windowLayout", "cleaning",
        "homebrew", "media", "urlCleaner", "uninstaller", "screenshot", "screenRecorder", "cameraPreview", "scratchpad",
    ];

    /// <summary>Known ids once in stored order, then every known id that was missing (new tools join at the end).</summary>
    public static List<string> SanitizeOrder(string stored, IReadOnlyList<string> known)
    {
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (knownSet.Contains(id) && !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }

        result.AddRange(known.Where(id => !result.Contains(id, StringComparer.Ordinal)));
        return result;
    }

    public static HashSet<string> ParseHidden(string stored) =>
        stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Hidden tiles are written as a sorted comma list.</summary>
    public static string FormatHidden(IEnumerable<string> hidden) => string.Join(',', hidden.Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal));

    /// <summary>
    /// Keyboard movement in the 3-column grid: arrows move one cell, clamped,
    /// no wrap (← at column 0 and → at the last column stay put; ↑/↓ by 3).
    /// </summary>
    public static int Move(int index, int count, int dx, int dy, int columns = 3)
    {
        if (count <= 0)
        {
            return -1;
        }

        index = Math.Clamp(index, 0, count - 1);
        if (dx != 0)
        {
            var column = index % columns;
            var target = index + dx;
            if ((dx < 0 && column == 0) || (dx > 0 && (column == columns - 1 || target >= count)))
            {
                return index;
            }

            return target;
        }

        if (dy != 0)
        {
            var target = index + (dy * columns);
            return target >= 0 && target < count ? target : index;
        }

        return index;
    }
}
