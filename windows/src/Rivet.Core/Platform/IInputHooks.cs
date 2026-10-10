// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Platform;

public enum KeyAction
{
    Down,
    Up,
}

/// <summary>A key event seen by the low-level keyboard hook.</summary>
public struct KeyboardHookEvent
{
    public int VirtualKey;
    public int ScanCode;
    public KeyAction Action;

    /// <summary>The Alt key was held (WM_SYSKEYDOWN/UP).</summary>
    public bool IsSystemKey;

    /// <summary>Synthesized by some app (not by this one: our own injections are never reported).</summary>
    public bool IsInjected;

    public bool IsExtended;

    /// <summary>Modifiers held at the time of the event (from async key state).</summary>
    public KeyModifiers Modifiers;

    public uint Time;
}

public enum MouseHookKind
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    XDown,
    XUp,
    Wheel,
    HorizontalWheel,
}

/// <summary>A mouse event seen by the low-level mouse hook.</summary>
public struct MouseHookEvent
{
    public MouseHookKind Kind;

    /// <summary>Pointer position, physical pixels.</summary>
    public PixelPoint Position;

    /// <summary>Wheel delta (multiples of 120 for notched wheels) for wheel events.</summary>
    public int WheelDelta;

    /// <summary>1 or 2 for side buttons (X1/X2).</summary>
    public int XButton;

    public bool IsInjected;

    public KeyModifiers Modifiers;

    public uint Time;
}

/// <summary>Return true to swallow the event (other apps never see it).</summary>
public delegate bool KeyboardHookHandler(ref KeyboardHookEvent e);

/// <summary>Return true to swallow the event.</summary>
public delegate bool MouseHookHandler(ref MouseHookEvent e);

/// <summary>
/// One shared pair of low-level hooks for the whole app. Handlers run on the
/// hook thread and must return within a few milliseconds (Windows silently
/// removes slow hooks); post anything heavier to the UI thread. Hooks are
/// installed while at least one handler is subscribed. Higher priority runs first.
/// Events this app injects with <see cref="SendKeys"/>/<see cref="SendMouse"/> are never reported.
/// </summary>
public interface IInputHooks
{
    IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0);

    IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0);

    /// <summary>Types key strokes (virtual key, down/up) as if from the keyboard.</summary>
    void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes);

    /// <summary>Types Unicode text at the caret.</summary>
    void SendText(string text);

    /// <summary>Synthesizes a wheel scroll at the pointer (delta in 1/120 notches).</summary>
    void SendWheel(int delta, bool horizontal);

    /// <summary>Synthesizes a mouse button event at the pointer.</summary>
    void SendMouse(MouseHookKind kind, int xButton = 0);

    /// <summary>Whether a key is currently down (async key state).</summary>
    bool IsKeyDown(int virtualKey);
}
