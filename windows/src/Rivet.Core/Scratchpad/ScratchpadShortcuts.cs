// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Scratchpad;

public enum ScratchpadCommand
{
    Hide,
    NewTab,
    CloseTab,
    Find,
    FindNext,
    FindPrevious,
    MoveLinesUp,
    MoveLinesDown,
}

public enum ScratchpadNamedKey
{
    None,
    Escape,
    F3,
    Up,
    Down,
}

/// <summary>
/// The pad's own keyboard shortcuts (spec 07 §3.3.7), matched on the
/// character the keyboard layout produces (lower-cased) so Caps Lock still
/// works and an AZERTY "Z" key never closes a tab. Undo/redo stay with the editor.
/// </summary>
public static class ScratchpadShortcuts
{
    public static ScratchpadCommand? Resolve(char? character, ScratchpadNamedKey key, bool control, bool alt, bool shift)
    {
        switch (key)
        {
            case ScratchpadNamedKey.Escape when !control && !alt && !shift:
                return ScratchpadCommand.Hide;
            case ScratchpadNamedKey.F3 when !control && !alt:
                return shift ? ScratchpadCommand.FindPrevious : ScratchpadCommand.FindNext;
            case ScratchpadNamedKey.Up when alt && !control && !shift:
                return ScratchpadCommand.MoveLinesUp;
            case ScratchpadNamedKey.Down when alt && !control && !shift:
                return ScratchpadCommand.MoveLinesDown;
        }

        if (!control || alt || character is not { } c)
        {
            return null;
        }

        // Ctrl+letter often arrives as a control code (Ctrl+T = 0x14).
        if (c is >= '\u0001' and <= '\u001A')
        {
            c = (char)(c + 0x60);
        }

        return char.ToLowerInvariant(c) switch
        {
            't' when !shift => ScratchpadCommand.NewTab,
            'w' when !shift => ScratchpadCommand.CloseTab,
            'f' when !shift => ScratchpadCommand.Find,
            'g' => shift ? ScratchpadCommand.FindPrevious : ScratchpadCommand.FindNext,
            _ => null,
        };
    }
}
