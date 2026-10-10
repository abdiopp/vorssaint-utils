// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Features;

/// <summary>
/// Each feature's own on/off switches ("enable keys"). The raw keys match the
/// macOS app wherever the feature exists on both. Features without a switch
/// (on-demand tools) have none: being installed already means "engaged".
/// </summary>
public static class FeatureKeys
{
    // Mouse and keyboard
    public static readonly Setting<bool> ScrollInverterEnabled = new("scrollInverterEnabled", false);
    public static readonly Setting<bool> SmoothScrollEnabled = new("smoothScrollEnabled", false);
    public static readonly Setting<bool> MouseButtonShortcutsEnabled = new("mouseButtonShortcutsEnabled", false);
    public static readonly Setting<bool> MouseClickDebounceEnabled = new("mouseClickDebounceEnabled", false);
    public static readonly Setting<bool> KeyboardDebounceEnabled = new("keyboardDebounceEnabled", false);
    public static readonly Setting<bool> TextSnippetsEnabled = new("textSnippetsEnabled", false);
    public static readonly Setting<bool> SnippetLibraryEnabled = new("snippetLibraryEnabled", false);
    public static readonly Setting<bool> SuperKeyEnabled = new("superKeyEnabled", false);
    public static readonly Setting<bool> QuitProtectionQuitEnabled = new("quitProtectionQuitEnabled", false);
    public static readonly Setting<bool> QuitProtectionCloseEnabled = new("quitProtectionCloseEnabled", false);

    // Clipboard and files
    public static readonly Setting<bool> ClipboardHistoryEnabled = new("clipboardHistoryEnabled", false);
    public static readonly Setting<bool> PastePlainEnabled = new("pastePlainEnabled", false);
    public static readonly Setting<bool> ShelfEnabled = new("shelfEnabled", false);
    public static readonly Setting<bool> UrlCleanerEnabled = new("urlCleanerEnabled", false);

    // Sound
    public static readonly Setting<bool> SoundOutputSwitcherEnabled = new("soundOutputSwitcherEnabled", false);
    public static readonly Setting<bool> AudioPriorityOutputEnabled = new("audioPriorityOutputEnabled", true);
    public static readonly Setting<bool> AudioPriorityInputEnabled = new("audioPriorityInputEnabled", true);

    // Energy and display
    public static readonly Setting<bool> BrightnessControlEnabled = new("brightnessControlEnabled", false);

    // Tools
    public static readonly Setting<bool> RadialMenuEnabled = new("radialMenuEnabled", false);
}
