// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Shortcuts;

/// <summary>Windows virtual-key codes used across the app, with US-layout fallback names.</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int Pause = 0x13;
    public const int Capital = 0x14;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int Prior = 0x21;
    public const int Next = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Snapshot = 0x2C;
    public const int Insert = 0x2D;
    public const int Delete = 0x2E;
    public const int D0 = 0x30;
    public const int D9 = 0x39;
    public const int A = 0x41;
    public const int Z = 0x5A;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int Apps = 0x5D;
    public const int NumPad0 = 0x60;
    public const int NumPad9 = 0x69;
    public const int Multiply = 0x6A;
    public const int Add = 0x6B;
    public const int Subtract = 0x6D;
    public const int Decimal = 0x6E;
    public const int Divide = 0x6F;
    public const int F1 = 0x70;
    public const int F24 = 0x87;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
    public const int OemSemicolon = 0xBA;
    public const int OemPlus = 0xBB;
    public const int OemComma = 0xBC;
    public const int OemMinus = 0xBD;
    public const int OemPeriod = 0xBE;
    public const int OemQuestion = 0xBF;
    public const int OemTilde = 0xC0;
    public const int OemOpenBrackets = 0xDB;
    public const int OemPipe = 0xDC;
    public const int OemCloseBrackets = 0xDD;
    public const int OemQuotes = 0xDE;

    public static int Letter(char c) => char.ToUpperInvariant(c) is >= 'A' and <= 'Z' and var u ? u : throw new ArgumentOutOfRangeException(nameof(c));

    public static int Digit(int d) => d is >= 0 and <= 9 ? D0 + d : throw new ArgumentOutOfRangeException(nameof(d));

    public static int Function(int n) => n is >= 1 and <= 24 ? F1 + n - 1 : throw new ArgumentOutOfRangeException(nameof(n));

    public static bool IsFunctionKey(int vk) => vk is >= F1 and <= F24;

    public static bool IsModifier(int vk) =>
        vk is Shift or Control or Menu or LWin or RWin or LShift or RShift or LControl or RControl or LMenu or RMenu;

    /// <summary>US-layout label; the platform provider replaces letters/punctuation with the active layout's.</summary>
    public static string DefaultName(int vk) => vk switch
    {
        >= A and <= Z => ((char)vk).ToString(),
        >= D0 and <= D9 => ((char)vk).ToString(),
        >= NumPad0 and <= NumPad9 => $"Num {vk - NumPad0}",
        >= F1 and <= F24 => $"F{vk - F1 + 1}",
        Back => "Backspace",
        Tab => "Tab",
        Return => "Enter",
        Pause => "Pause",
        Capital => "Caps Lock",
        Escape => "Esc",
        Space => "Space",
        Prior => "Page Up",
        Next => "Page Down",
        End => "End",
        Home => "Home",
        Left => "←",
        Up => "↑",
        Right => "→",
        Down => "↓",
        Snapshot => "Print Screen",
        Insert => "Insert",
        Delete => "Delete",
        Apps => "Menu",
        Multiply => "Num *",
        Add => "Num +",
        Subtract => "Num -",
        Decimal => "Num .",
        Divide => "Num /",
        OemSemicolon => ";",
        OemPlus => "=",
        OemComma => ",",
        OemMinus => "-",
        OemPeriod => ".",
        OemQuestion => "/",
        OemTilde => "`",
        OemOpenBrackets => "[",
        OemPipe => "\\",
        OemCloseBrackets => "]",
        OemQuotes => "'",
        _ => $"Key {vk}",
    };
}
