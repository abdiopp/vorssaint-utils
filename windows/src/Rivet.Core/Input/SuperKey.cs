// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Input;

/// <summary>
/// The key held as the Super key. Raw values match the macOS app (Command →
/// Win, Option → Alt); "apps" (the Menu key) is a Windows addition.
/// </summary>
public static class SuperKeySources
{
    public const string CapsLock = "capsLock";
    public const string RightCommand = "rightCommand";
    public const string RightOption = "rightOption";
    public const string RightControl = "rightControl";
    public const string RightShift = "rightShift";
    public const string Apps = "apps";

    public static IReadOnlyList<string> All { get; } = [CapsLock, RightCommand, RightOption, RightControl, RightShift, Apps];

    public static string Sanitize(string value) => All.Contains(value, StringComparer.Ordinal) ? value : CapsLock;

    public static int VirtualKey(string source) => source switch
    {
        RightCommand => VirtualKeys.RWin,
        RightOption => VirtualKeys.RMenu,
        RightControl => VirtualKeys.RControl,
        RightShift => VirtualKeys.RShift,
        Apps => VirtualKeys.Apps,
        _ => VirtualKeys.Capital,
    };

    /// <summary>Fluent icon for the source key (the macOS hub uses the key's own symbol).</summary>
    public static string Icon(string source) => source switch
    {
        CapsLock => "KeyboardShiftUppercase",
        RightShift => "KeyboardShift",
        _ => "Keyboard",
    };
}

/// <summary>
/// The modifier set the Super key stands for. Stored as tokens joined by "+"
/// in the fixed order control, option, shift, command (Ctrl, Alt, Shift, Win).
/// It must contain Ctrl, Alt or Win; Shift alone is never allowed.
/// </summary>
public static class SuperKeyModifierSet
{
    public const string DefaultStorage = "control+option+shift";

    public static KeyModifiers Default => KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;

    public static KeyModifiers Parse(string? storage)
    {
        var result = KeyModifiers.None;
        foreach (var token in (storage ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= token switch
            {
                "control" => KeyModifiers.Control,
                "option" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                "command" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
        }

        return IsValid(result) ? result : Default;
    }

    public static bool IsValid(KeyModifiers set) =>
        (set & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;

    public static string Format(KeyModifiers set)
    {
        if (!IsValid(set))
        {
            set = Default;
        }

        var tokens = new List<string>(4);
        if (set.HasFlag(KeyModifiers.Control)) tokens.Add("control");
        if (set.HasFlag(KeyModifiers.Alt)) tokens.Add("option");
        if (set.HasFlag(KeyModifiers.Shift)) tokens.Add("shift");
        if (set.HasFlag(KeyModifiers.Win)) tokens.Add("command");
        return string.Join('+', tokens);
    }

    public static string SanitizeStorage(string value) => Format(Parse(value));

    /// <summary>Whether a keycap may be switched off without leaving no Ctrl/Alt/Win.</summary>
    public static bool CanToggle(KeyModifiers set, KeyModifiers modifier) =>
        !set.HasFlag(modifier) || IsValid(set & ~modifier);

    /// <summary>All four together is the Office key on Windows 10/11.</summary>
    public static bool IsOfficeKey(KeyModifiers set) =>
        set == (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Win);

    /// <summary>Ctrl+Alt is AltGr on many European layouts: chords may type characters.</summary>
    public static bool IncludesAltGr(KeyModifiers set) =>
        set.HasFlag(KeyModifiers.Control) && set.HasFlag(KeyModifiers.Alt);

    /// <summary>"Ctrl+Alt+Shift" style label.</summary>
    public static string Display(KeyModifiers set)
    {
        var parts = new List<string>(4);
        if (set.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (set.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (set.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (set.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join('+', parts);
    }
}

public static class SuperKeySoloActions
{
    public const string None = "none";
    public const string CapsLock = "capsLock";
    public const string InputSource = "inputSource";
    public const string Escape = "escape";

    public static IReadOnlyList<string> All { get; } = [None, CapsLock, InputSource, Escape];

    public static string Sanitize(string value) => All.Contains(value, StringComparer.Ordinal) ? value : None;
}

public enum SuperKeyRelease
{
    /// <summary>Another key or click happened while held (a chord), or there was no hold.</summary>
    Chord,

    /// <summary>Released alone in under 500 ms.</summary>
    SoloTap,

    /// <summary>Released alone after 500 ms or more.</summary>
    SoloHold,
}

public enum SuperKeyEffect
{
    None,
    Escape,
    ToggleCapsLock,
    NextInputSource,
}

/// <summary>
/// The Super key state machine (spec 07 §3.7.6). Windows auto-repeats a held
/// source key; repeats are swallowed and ignored, so a solo press always runs
/// its action (on macOS the F18 trigger never repeats).
/// </summary>
public sealed class SuperKeyState
{
    public const long SoloHoldNs = 500 * InputTime.NsPerMs;

    public bool IsHeld { get; private set; }

    public bool IsAlone { get; private set; }

    public long DownTimestamp { get; private set; }

    public bool DidRepeat { get; private set; }

    /// <summary>
    /// The trigger went down. Returns true for a fresh press (the caller then
    /// presses the modifiers), false for an auto-repeat. Always swallowed.
    /// </summary>
    public bool OnTriggerDown(bool otherModifiersHeld, long t, bool countRepeats = false)
    {
        if (IsHeld)
        {
            if (countRepeats)
            {
                DidRepeat = true;
            }

            return false;
        }

        IsHeld = true;
        IsAlone = !otherModifiersHeld;
        DownTimestamp = t;
        DidRepeat = false;
        return true;
    }

    /// <summary>The trigger went up; the state resets.</summary>
    public SuperKeyRelease OnTriggerUp(long t)
    {
        var wasAlone = IsHeld && IsAlone;
        var wasLong = wasAlone && t - DownTimestamp >= SoloHoldNs;
        Reset();
        return wasLong ? SuperKeyRelease.SoloHold : wasAlone ? SuperKeyRelease.SoloTap : SuperKeyRelease.Chord;
    }

    /// <summary>Another key (down or up), a modifier change or a mouse button while held: no longer alone.</summary>
    public void OnOtherInput()
    {
        if (IsHeld)
        {
            IsAlone = false;
        }
    }

    public void Reset()
    {
        IsHeld = false;
        IsAlone = false;
        DidRepeat = false;
        DownTimestamp = 0;
    }

    /// <summary>The solo-effect table (spec 07 §3.7.6).</summary>
    public static SuperKeyEffect Effect(string soloAction, SuperKeyRelease release, bool didRepeat)
    {
        if (release == SuperKeyRelease.Chord)
        {
            return SuperKeyEffect.None;
        }

        return soloAction switch
        {
            SuperKeySoloActions.Escape when !didRepeat => SuperKeyEffect.Escape,
            SuperKeySoloActions.CapsLock when !didRepeat => SuperKeyEffect.ToggleCapsLock,
            SuperKeySoloActions.InputSource => release == SuperKeyRelease.SoloHold ? SuperKeyEffect.ToggleCapsLock : SuperKeyEffect.NextInputSource,
            _ => SuperKeyEffect.None,
        };
    }

    /// <summary>Held-key watchdog delay: clamp(2 × repeat delay, 3 s, 30 s).</summary>
    public static TimeSpan WatchdogDelay(int repeatDelayMs) =>
        TimeSpan.FromMilliseconds(repeatDelayMs > 0 ? Math.Clamp(2.0 * repeatDelayMs, 3000, 30000) : 3000);
}

/// <summary>
/// Picks the next keyboard layout for "switch input source": the one after
/// the current, wrapping; an unknown current gives the first; fewer than two
/// layouts gives nothing.
/// </summary>
public static class InputSourceCycle
{
    public static T? Next<T>(IReadOnlyList<T> sources, T? current)
        where T : struct
    {
        if (sources.Count < 2)
        {
            return null;
        }

        for (var i = 0; i < sources.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(sources[i], current.GetValueOrDefault()) && current.HasValue)
            {
                return sources[(i + 1) % sources.Count];
            }
        }

        return sources[0];
    }
}
