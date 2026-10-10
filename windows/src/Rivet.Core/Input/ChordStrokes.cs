// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Input;

/// <summary>
/// Builds key strokes that make the foreground app see exactly one chord.
/// Windows key messages carry no modifier flags (apps read the key state), so
/// modifiers the user holds that are not part of the chord are released
/// first, and missing ones are pressed and released around the key. A mask
/// key goes first whenever a held modifier is released, so the release cannot
/// open Start (Win), activate a menu bar (Alt), switch the input language
/// (Alt+Shift) or toggle an IME (Shift). Released modifiers are not pressed
/// again: restoring them would cause exactly those side effects on the
/// user's own release.
/// </summary>
public static class ChordStrokes
{
    /// <summary>An unassigned virtual key (0xE8) used to "mask" lone modifier releases.</summary>
    public const int MaskKey = 0xE8;

    private static readonly (KeyModifiers Modifier, int Left, int Right)[] Modifiers =
    [
        (KeyModifiers.Control, VirtualKeys.LControl, VirtualKeys.RControl),
        (KeyModifiers.Alt, VirtualKeys.LMenu, VirtualKeys.RMenu),
        (KeyModifiers.Shift, VirtualKeys.LShift, VirtualKeys.RShift),
        (KeyModifiers.Win, VirtualKeys.LWin, VirtualKeys.RWin),
    ];

    public static IReadOnlyList<(int VirtualKey, KeyAction Action)> Mask { get; } =
        [(MaskKey, KeyAction.Down), (MaskKey, KeyAction.Up)];

    /// <summary>Strokes for <paramref name="chord"/>, given which keys are down right now.</summary>
    public static List<(int VirtualKey, KeyAction Action)> Exact(KeyChord chord, Func<int, bool> isDown)
    {
        var strokes = new List<(int, KeyAction)>(12);
        if (chord.IsEmpty)
        {
            return strokes;
        }

        var release = new List<int>(4);
        var press = new List<int>(4);
        foreach (var (modifier, left, right) in Modifiers)
        {
            var leftDown = isDown(left);
            var rightDown = isDown(right);
            if (chord.Modifiers.HasFlag(modifier))
            {
                if (!leftDown && !rightDown)
                {
                    press.Add(left);
                }
            }
            else
            {
                if (leftDown) release.Add(left);
                if (rightDown) release.Add(right);
            }
        }

        if (release.Count > 0)
        {
            strokes.AddRange(Mask);
            foreach (var vk in release)
            {
                strokes.Add((vk, KeyAction.Up));
            }
        }

        foreach (var vk in press)
        {
            strokes.Add((vk, KeyAction.Down));
        }

        strokes.Add((chord.VirtualKey, KeyAction.Down));
        strokes.Add((chord.VirtualKey, KeyAction.Up));
        for (var i = press.Count - 1; i >= 0; i--)
        {
            strokes.Add((press[i], KeyAction.Up));
        }

        return strokes;
    }

    /// <summary>A plain tap of one key with no modifier handling.</summary>
    public static IReadOnlyList<(int VirtualKey, KeyAction Action)> Tap(int vk) => [(vk, KeyAction.Down), (vk, KeyAction.Up)];

    /// <summary>The left-hand virtual keys for a modifier set, in Ctrl, Alt, Shift, Win order.</summary>
    public static IEnumerable<int> LeftKeys(KeyModifiers set)
    {
        foreach (var (modifier, left, _) in Modifiers)
        {
            if (set.HasFlag(modifier))
            {
                yield return left;
            }
        }
    }

    /// <summary>The modifier a virtual key belongs to (either side, or the generic code).</summary>
    public static KeyModifiers ModifierOf(int vk) => vk switch
    {
        VirtualKeys.LControl or VirtualKeys.RControl or VirtualKeys.Control => KeyModifiers.Control,
        VirtualKeys.LMenu or VirtualKeys.RMenu or VirtualKeys.Menu => KeyModifiers.Alt,
        VirtualKeys.LShift or VirtualKeys.RShift or VirtualKeys.Shift => KeyModifiers.Shift,
        VirtualKeys.LWin or VirtualKeys.RWin => KeyModifiers.Win,
        _ => KeyModifiers.None,
    };
}
