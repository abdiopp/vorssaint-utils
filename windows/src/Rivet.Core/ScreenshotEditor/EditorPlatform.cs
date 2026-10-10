// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>The current desktop picture of every monitor (backdrop swatches).</summary>
public interface IDesktopWallpaperProvider
{
    /// <summary>Existing local image files, deduplicated, primary monitor first. Empty when unknown.</summary>
    IReadOnlyList<string> GetWallpaperPaths();
}

/// <summary>What a key types on the current keyboard layout (tool digits are judged by the typed character).</summary>
public interface IKeyboardLayoutInfo
{
    /// <summary>The digit 0–9 the chord types with the current layout and Caps Lock state, or null.</summary>
    int? DigitTypedBy(KeyChord chord);
}

/// <summary>The Windows share sheet for a file.</summary>
public interface IShareSheet
{
    bool IsAvailable { get; }

    /// <summary>
    /// Opens the share UI over <paramref name="windowHandle"/> (UI thread).
    /// <paramref name="targetChosen"/> runs when the user picks an app; the
    /// share UI reports no cancellation. False when the UI could not open.
    /// </summary>
    Task<bool> ShareFileAsync(nint windowHandle, string filePath, string title, Action targetChosen);
}

/// <summary>US-layout fallback: the digit row and the number pad (with Num Lock) type digits without Shift.</summary>
public sealed class UsKeyboardLayoutInfo : IKeyboardLayoutInfo
{
    public int? DigitTypedBy(KeyChord chord)
    {
        if ((chord.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0)
        {
            return null;
        }

        var vk = chord.VirtualKey;
        if (vk is >= VirtualKeys.D0 and <= VirtualKeys.D9 && !chord.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            return vk - VirtualKeys.D0;
        }

        return vk is >= VirtualKeys.NumPad0 and <= VirtualKeys.NumPad9 ? vk - VirtualKeys.NumPad0 : null;
    }
}

/// <summary>A wallpaper provider with nothing to offer (development builds).</summary>
public sealed class NoWallpapers : IDesktopWallpaperProvider
{
    public IReadOnlyList<string> GetWallpaperPaths() => [];
}
