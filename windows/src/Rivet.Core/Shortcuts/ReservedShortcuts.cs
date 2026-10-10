// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Shortcuts;

/// <summary>
/// Combinations Windows (or the keyboard layout) already uses. The recorder
/// refuses them up front with "Windows uses this combination." Ctrl+Alt
/// alone is AltGr on many layouts, so Ctrl+Alt+character keys are refused too.
/// </summary>
public static class ReservedShortcuts
{
    private const KeyModifiers C = KeyModifiers.Control;
    private const KeyModifiers A = KeyModifiers.Alt;
    private const KeyModifiers S = KeyModifiers.Shift;
    private const KeyModifiers W = KeyModifiers.Win;

    private static readonly HashSet<KeyChord> Exact =
    [
        new(C | A, VirtualKeys.Delete),
        new(C | S, VirtualKeys.Escape),
        new(C, VirtualKeys.Escape),
        new(A, VirtualKeys.Tab),
        new(A | S, VirtualKeys.Tab),
        new(A, VirtualKeys.Escape),
        new(A, VirtualKeys.Function(4)),
        new(A, VirtualKeys.Space),
        new(W | S, VirtualKeys.Letter('S')),
        new(W | C, VirtualKeys.Letter('D')),
        new(W | C, VirtualKeys.Function(4)),
        new(W | C, VirtualKeys.Left),
        new(W | C, VirtualKeys.Right),
        new(W | C, VirtualKeys.Letter('V')),
        new(W | C, VirtualKeys.Letter('C')),
        new(W | C, VirtualKeys.Letter('O')),
        new(W | C, VirtualKeys.Letter('Q')),
        new(W | C, VirtualKeys.Return),
        new(W | A, VirtualKeys.Letter('R')),
        new(W | A, VirtualKeys.Letter('G')),
        new(W | A, VirtualKeys.Letter('B')),
        new(W | A, VirtualKeys.Letter('K')),
        new(W | A, VirtualKeys.Snapshot),
        new(W, VirtualKeys.OemPeriod),
        new(W, VirtualKeys.OemSemicolon),
    ];

    public static bool IsReserved(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return false;
        }

        if (Exact.Contains(chord))
        {
            return true;
        }

        // Win plus a single letter, digit, arrow or Tab: almost all are taken by the shell.
        if (chord.Modifiers is W or (W | S))
        {
            var vk = chord.VirtualKey;
            if (vk is >= VirtualKeys.A and <= VirtualKeys.Z or >= VirtualKeys.D0 and <= VirtualKeys.D9
                or VirtualKeys.Left or VirtualKeys.Right or VirtualKeys.Up or VirtualKeys.Down
                or VirtualKeys.Tab or VirtualKeys.Home or VirtualKeys.Space or VirtualKeys.Snapshot)
            {
                return true;
            }
        }

        // The Office key (Ctrl+Alt+Shift+Win) opens Microsoft 365 shortcuts.
        if (chord.Modifiers == (C | A | S | W))
        {
            return true;
        }

        // AltGr = Ctrl+Alt on European layouts: character keys would stop typing € @ ł ...
        if (chord.Modifiers is (C | A) or (C | A | S))
        {
            var vk = chord.VirtualKey;
            if (vk is >= VirtualKeys.A and <= VirtualKeys.Z or >= VirtualKeys.D0 and <= VirtualKeys.D9
                or >= VirtualKeys.OemSemicolon and <= VirtualKeys.OemTilde
                or >= VirtualKeys.OemOpenBrackets and <= VirtualKeys.OemQuotes)
            {
                return true;
            }
        }

        return false;
    }
}
