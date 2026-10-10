// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.App.Modules;

/// <summary>How a feature wants the tray icon to look while it is active.</summary>
public sealed record TrayIndicator
{
    /// <summary>Tint the glyph (0xAARRGGBB), e.g. orange while Keep Awake runs.</summary>
    public uint? Tint { get; init; }

    /// <summary>Draw a small red badge (e.g. microphone muted).</summary>
    public bool Badge { get; init; }

    /// <summary>Extra tooltip line ("Awake until 18:30").</summary>
    public string? TooltipLine { get; init; }

    /// <summary>Higher wins when several features ask for a tint.</summary>
    public int Priority { get; init; }
}

/// <summary>Lets features change the tray icon's tint, badge and tooltip.</summary>
public interface ITrayPresence
{
    /// <summary>Sets (or with null clears) the indicator owned by <paramref name="source"/>.</summary>
    void SetIndicator(string source, TrayIndicator? indicator);
}
