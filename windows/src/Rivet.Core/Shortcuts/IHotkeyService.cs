// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Shortcuts;

[Flags]
public enum HotkeyOptions
{
    None = 0,

    /// <summary>Keep firing while the keys are held.</summary>
    AllowRepeat = 1,

    /// <summary>
    /// The combination belongs to Windows (Alt+Space, Print Screen); take it
    /// over with a low-level keyboard hook while registered. Use sparingly.
    /// </summary>
    OverrideSystem = 2,
}

/// <summary>System-wide hotkeys (RegisterHotKey on Windows).</summary>
public interface IHotkeyService
{
    /// <summary>
    /// Registers <paramref name="chord"/>. Returns a handle that unregisters on
    /// dispose, or null when Windows or another app already owns the combination.
    /// The callback runs on the UI thread.
    /// </summary>
    IDisposable? Register(KeyChord chord, Action onPressed, HotkeyOptions options = HotkeyOptions.None);
}
