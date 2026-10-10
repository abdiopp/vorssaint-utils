// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Shortcuts;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination: Windows virtual-key code plus modifiers. Stored as
/// <c>"ctrl+alt+win:0x4B"</c> (modifier tokens in the fixed order ctrl, alt,
/// shift, win; reading accepts any order). An empty string means "none".
/// </summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, int VirtualKey)
{
    public static KeyChord None => default;

    public bool IsEmpty => VirtualKey == 0;

    public static KeyChord Of(KeyModifiers modifiers, int virtualKey) => new(modifiers, virtualKey);

    /// <summary>
    /// A usable global shortcut: Ctrl, Alt or Win held with a key, or a
    /// function key / Print Screen / Pause with any modifiers.
    /// </summary>
    public bool IsValidGlobalShortcut =>
        !IsEmpty && !VirtualKeys.IsModifier(VirtualKey) &&
        ((Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0
         || VirtualKeys.IsFunctionKey(VirtualKey)
         || VirtualKey is VirtualKeys.Snapshot or VirtualKeys.Pause);

    public string ToStorageString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var tokens = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) tokens.Add("ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) tokens.Add("alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) tokens.Add("shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) tokens.Add("win");
        return $"{string.Join('+', tokens)}:0x{VirtualKey:X2}";
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var token in text[..colon].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= KeyModifiers.Control; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "win": case "meta": modifiers |= KeyModifiers.Win; break;
                default: return false;
            }
        }

        var keyText = text[(colon + 1)..].Trim();
        int vk;
        if (keyText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(keyText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk)) return false;
        }
        else if (!int.TryParse(keyText, NumberStyles.None, CultureInfo.InvariantCulture, out vk))
        {
            return false;
        }

        if (vk is <= 0 or > 0xFE)
        {
            return false;
        }

        chord = new KeyChord(modifiers, vk);
        return true;
    }

    /// <summary>"Ctrl+Alt+Win+K" using <paramref name="names"/> for the key label.</summary>
    public string ToDisplayString(IKeyNameProvider? names = null)
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(names?.NameOf(VirtualKey) ?? VirtualKeys.DefaultName(VirtualKey));
        return string.Join('+', parts);
    }

    public override string ToString() => ToDisplayString();
}

/// <summary>Turns a virtual-key code into the label the current keyboard layout prints.</summary>
public interface IKeyNameProvider
{
    string NameOf(int virtualKey);
}
