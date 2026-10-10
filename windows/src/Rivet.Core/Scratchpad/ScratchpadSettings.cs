// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Modules.Scratchpad;

public enum ScratchpadRetention
{
    Never,
    Day,
    Week,
    Month,
}

/// <summary>Scratchpad preferences (raw keys match the macOS app; the notes themselves are a separate file).</summary>
public static class ScratchpadSettings
{
    public static readonly Setting<bool> ShortcutEnabled = new("scratchpadShortcutEnabled", false);

    /// <summary>Smart-toggle hotkey (VK storage, empty = the default Ctrl+Alt+Win+N).</summary>
    public static readonly Setting<string> Shortcut = new("scratchpadShortcut", string.Empty);

    public static readonly Setting<string> Retention =
        new("scratchpadRetention", "never", Sanitize.OneOfStrings("never", "never", "day", "week", "month"));

    /// <summary>Hide on outside click; the pin state at every show is its inverse.</summary>
    public static readonly Setting<bool> CloseOnClickOutside = new("scratchpadCloseOnClickOutside", true);

    /// <summary>Opaque fill over the blur, 0…1 (non-finite → 1).</summary>
    public static readonly Setting<double> BackgroundOpacity = new("scratchpadBackgroundOpacity", 0.0, SanitizeOpacity);

    /// <summary>Editor and preview body size, rounded and clamped 10…22 (non-finite → 13).</summary>
    public static readonly Setting<double> TextSize = new("scratchpadTextSize", 13.0, SanitizeTextSize);

    /// <summary>Windows addition: keep the pad above other windows (macOS always floats it).</summary>
    public static readonly Setting<bool> AlwaysOnTop = new("scratchpadAlwaysOnTop", true);

    public const double MinTextSize = 10;
    public const double MaxTextSize = 22;

    public static double SanitizeOpacity(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1.0;

    public static double SanitizeTextSize(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value), MinTextSize, MaxTextSize) : 13.0;

    public static ScratchpadRetention ParseRetention(string value) => value switch
    {
        "day" => ScratchpadRetention.Day,
        "week" => ScratchpadRetention.Week,
        "month" => ScratchpadRetention.Month,
        _ => ScratchpadRetention.Never,
    };

    public static string ToStorage(ScratchpadRetention retention) => retention switch
    {
        ScratchpadRetention.Day => "day",
        ScratchpadRetention.Week => "week",
        ScratchpadRetention.Month => "month",
        _ => "never",
    };
}
