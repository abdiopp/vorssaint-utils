// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Input;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Controls;

/// <summary>Avalonia keys → Windows virtual-key codes (used when no low-level hook is available).</summary>
public static class AvaloniaKeyMap
{
    public static int ToVirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => VirtualKeys.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => VirtualKeys.D0 + (key - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => VirtualKeys.NumPad0 + (key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => VirtualKeys.F1 + (key - Key.F1),
        Key.Back => VirtualKeys.Back,
        Key.Tab => VirtualKeys.Tab,
        Key.Enter => VirtualKeys.Return,
        Key.Pause => VirtualKeys.Pause,
        Key.CapsLock => VirtualKeys.Capital,
        Key.Escape => VirtualKeys.Escape,
        Key.Space => VirtualKeys.Space,
        Key.PageUp => VirtualKeys.Prior,
        Key.PageDown => VirtualKeys.Next,
        Key.End => VirtualKeys.End,
        Key.Home => VirtualKeys.Home,
        Key.Left => VirtualKeys.Left,
        Key.Up => VirtualKeys.Up,
        Key.Right => VirtualKeys.Right,
        Key.Down => VirtualKeys.Down,
        Key.PrintScreen => VirtualKeys.Snapshot,
        Key.Insert => VirtualKeys.Insert,
        Key.Delete => VirtualKeys.Delete,
        Key.Multiply => VirtualKeys.Multiply,
        Key.Add => VirtualKeys.Add,
        Key.Subtract => VirtualKeys.Subtract,
        Key.Decimal => VirtualKeys.Decimal,
        Key.Divide => VirtualKeys.Divide,
        Key.OemSemicolon => VirtualKeys.OemSemicolon,
        Key.OemPlus => VirtualKeys.OemPlus,
        Key.OemComma => VirtualKeys.OemComma,
        Key.OemMinus => VirtualKeys.OemMinus,
        Key.OemPeriod => VirtualKeys.OemPeriod,
        Key.OemQuestion => VirtualKeys.OemQuestion,
        Key.OemTilde => VirtualKeys.OemTilde,
        Key.OemOpenBrackets => VirtualKeys.OemOpenBrackets,
        Key.OemPipe => VirtualKeys.OemPipe,
        Key.OemCloseBrackets => VirtualKeys.OemCloseBrackets,
        Key.OemQuotes => VirtualKeys.OemQuotes,
        Key.LeftShift or Key.RightShift => VirtualKeys.Shift,
        Key.LeftCtrl or Key.RightCtrl => VirtualKeys.Control,
        Key.LeftAlt or Key.RightAlt => VirtualKeys.Menu,
        Key.LWin => VirtualKeys.LWin,
        Key.RWin => VirtualKeys.RWin,
        _ => 0,
    };

    public static Rivet.Core.Shortcuts.KeyModifiers ToModifiers(Avalonia.Input.KeyModifiers modifiers)
    {
        var result = Rivet.Core.Shortcuts.KeyModifiers.None;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) result |= Rivet.Core.Shortcuts.KeyModifiers.Control;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) result |= Rivet.Core.Shortcuts.KeyModifiers.Alt;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) result |= Rivet.Core.Shortcuts.KeyModifiers.Shift;
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) result |= Rivet.Core.Shortcuts.KeyModifiers.Win;
        return result;
    }
}
