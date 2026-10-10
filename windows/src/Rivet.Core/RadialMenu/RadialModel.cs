// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Modules.RadialMenu;

/// <summary>What a slice does (raw values match the macOS app).</summary>
public enum RadialItemKind
{
    App,
    File,
    Url,
    Shortcut,

    /// <summary>One of the app's own tools; on Windows the payload is an action id or a feature id.</summary>
    Tool,

    QuickToggle,
    WindowLayout,
    Media,
    Submenu,
}

public enum RadialColor
{
    Accent,
    Blue,
    Purple,
    Pink,
    Red,
    Orange,
    Yellow,
    Green,
    Mint,
    Cyan,
    Indigo,
    Graphite,
}

public enum RadialPreset
{
    General,
    Media,
    Tools,
    WindowLayout,
    QuickToggles,
    Blank,
}

/// <summary>One slice. Submenus hold up to 12 children; other kinds have none.</summary>
public sealed record RadialItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required RadialItemKind Kind { get; init; }

    /// <summary>Custom name; empty means automatic.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Custom Fluent icon name; empty means automatic.</summary>
    public string SymbolName { get; init; } = string.Empty;

    /// <summary>Per kind: path (app/file, "~" allowed), normalized URL, shortcut storage, or an id.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>Base64 PNG (≤ 64 KiB, 64×64), e.g. a fetched website icon.</summary>
    public string? CustomIconData { get; init; }

    public IReadOnlyList<RadialItem> Children { get; init; } = [];

    public bool IsSubmenu => Kind == RadialItemKind.Submenu;
}

/// <summary>A per-profile mouse trigger: off, Back (3), Forward (4) or another button 5–31.</summary>
public readonly record struct RadialMouseTrigger(int Button)
{
    public const int Back = 3;
    public const int Forward = 4;

    public static RadialMouseTrigger Off => default;

    public bool IsOff => Button == 0;

    /// <summary>X1/X2 in low-level hook terms (1 = Back, 2 = Forward), or 0 when Windows hooks cannot see the button.</summary>
    public int XButton => Button switch
    {
        Back => 1,
        Forward => 2,
        _ => 0,
    };

    /// <summary>Only the side buttons reach a low-level hook; buttons 5+ need vendor software.</summary>
    public bool IsSupportedOnWindows => Button is Back or Forward;

    /// <summary>"off", "back", "forward" or "button:N" (N 3–31); anything else is off.</summary>
    public static RadialMouseTrigger Parse(string? value)
    {
        switch (value)
        {
            case "back":
                return new RadialMouseTrigger(Back);
            case "forward":
                return new RadialMouseTrigger(Forward);
        }

        if (value is not null && value.StartsWith("button:", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 3 and <= 31)
        {
            return new RadialMouseTrigger(n);
        }

        return Off;
    }

    public string ToStorage() => Button switch
    {
        0 => "off",
        Back => "back",
        Forward => "forward",
        _ => $"button:{Button.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>One wheel with its own colour, items and triggers.</summary>
public sealed record RadialProfile
{
    public const int MaxNameLength = 60;

    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Empty displays as "General".</summary>
    public string Name { get; init; } = string.Empty;

    public RadialColor Color { get; init; } = RadialColor.Accent;

    /// <summary>Shortcut storage string (<c>ctrl+alt+win:0x20</c>), empty for none.</summary>
    public string Shortcut { get; init; } = string.Empty;

    public RadialMouseTrigger MouseButton { get; init; }

    public IReadOnlyList<RadialItem> Items { get; init; } = [];

    public RadialPreset? Preset { get; init; }

    /// <summary>Four-finger tap (macOS only; kept for backups, never offered on Windows).</summary>
    public bool TrackpadTap { get; init; }

    public bool HasTrigger => Shortcut.Length > 0 || !MouseButton.IsOff;
}

/// <summary>Media slice ids.</summary>
public static class RadialMediaIds
{
    public const string PlayPause = "playPause";
    public const string PreviousTrack = "previousTrack";
    public const string NextTrack = "nextTrack";
    public const string NowPlaying = "nowPlaying";

    public static IReadOnlyList<string> All { get; } = [PlayPause, PreviousTrack, NextTrack, NowPlaying];
}

/// <summary>Quick toggle ids (resolved to the Quick toggles feature's actions on Windows).</summary>
public static class RadialQuickToggleIds
{
    public const string DarkMode = "darkMode";
    public const string EmptyTrash = "emptyTrash";
    public const string EjectDisks = "ejectDisks";
    public const string HiddenFiles = "hiddenFiles";
    public const string DesktopIcons = "desktopIcons";
    public const string LockScreen = "lockScreen";
    public const string DisplayOff = "displayOff";
    public const string ScreenSaver = "screenSaver";

    public static IReadOnlyList<string> All { get; } = [DarkMode, EmptyTrash, EjectDisks, HiddenFiles, DesktopIcons, LockScreen, DisplayOff, ScreenSaver];
}

/// <summary>The macOS tool ids the radial menu knows, mapped to Windows feature ids.</summary>
public static class RadialToolIds
{
    public static IReadOnlyDictionary<string, string> MacToolToFeature { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["screenRecording"] = "screenRecorder",
        ["screenRecorder"] = "screenRecorder",
        ["clipboard"] = "clipboardHistory",
        ["quickPanel"] = "quickLauncher",
        ["ocr"] = "screenOCR",
        ["screenOcr"] = "screenOCR",
    };

    /// <summary>The feature id a tool payload refers to when it is not an action id.</summary>
    public static string FeatureFor(string payload) => MacToolToFeature.TryGetValue(payload, out var feature) ? feature : payload;
}
