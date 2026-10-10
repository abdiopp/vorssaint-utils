// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Input;

/// <summary>
/// The app's single WH_KEYBOARD_LL / WH_MOUSE_LL pair, on a dedicated thread
/// with its own message loop so UI work can never make Windows drop the hooks
/// (LowLevelHooksTimeout). Hooks exist only while someone subscribes. Input
/// this app injects carries <see cref="InjectSignature"/> and is skipped.
/// </summary>
public sealed unsafe class WindowsInputHooks : IInputHooks, IDisposable
{
    /// <summary>dwExtraInfo stamped on every event we synthesize ('RIVT').</summary>
    public const nuint InjectSignature = 0x52495654;

    private const uint MsgInstallKeyboard = PInvoke.WM_APP + 1;
    private const uint MsgRemoveKeyboard = PInvoke.WM_APP + 2;
    private const uint MsgInstallMouse = PInvoke.WM_APP + 3;
    private const uint MsgRemoveMouse = PInvoke.WM_APP + 4;

    private readonly object _gate = new();
    private readonly HOOKPROC _keyboardProc;
    private readonly HOOKPROC _mouseProc;
    private ImmutableArray<(int Priority, KeyboardHookHandler Handler)> _keyboardHandlers = [];
    private ImmutableArray<(int Priority, MouseHookHandler Handler)> _mouseHandlers = [];
    private Thread? _thread;
    private uint _threadId;
    private readonly ManualResetEventSlim _threadReady = new(false);
    private nint _keyboardHook;
    private nint _mouseHook;

    // Watchdog state (hook thread only). Windows silently removes a low-level
    // hook whose callback once took longer than LowLevelHooksTimeout; when the
    // system keeps seeing input that a hook no longer receives, reinstall it.
    private const uint WatchdogIntervalMs = 5000;
    private const uint DeafThresholdMs = 10_000;
    private const uint ReinstallCooldownMs = 30_000;
    private uint _lastKeyboardEventTime;
    private uint _lastMouseEventTime;
    private uint _lastKeyboardReinstall;
    private uint _lastMouseReinstall;

    public WindowsInputHooks()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        lock (_gate)
        {
            var wasEmpty = _keyboardHandlers.IsEmpty;
            _keyboardHandlers = _keyboardHandlers.Add((priority, handler)).Sort((a, b) => b.Priority.CompareTo(a.Priority));
            if (wasEmpty)
            {
                Post(MsgInstallKeyboard);
            }
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _keyboardHandlers = _keyboardHandlers.RemoveAll(h => h.Handler == handler);
                if (_keyboardHandlers.IsEmpty)
                {
                    Post(MsgRemoveKeyboard);
                }
            }
        });
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0)
    {
        lock (_gate)
        {
            var wasEmpty = _mouseHandlers.IsEmpty;
            _mouseHandlers = _mouseHandlers.Add((priority, handler)).Sort((a, b) => b.Priority.CompareTo(a.Priority));
            if (wasEmpty)
            {
                Post(MsgInstallMouse);
            }
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _mouseHandlers = _mouseHandlers.RemoveAll(h => h.Handler == handler);
                if (_mouseHandlers.IsEmpty)
                {
                    Post(MsgRemoveMouse);
                }
            }
        });
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes)
    {
        if (strokes.Count == 0)
        {
            return;
        }

        var inputs = new INPUT[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            var (vk, action) = strokes[i];
            var flags = action == KeyAction.Up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
            if (IsExtendedKey(vk))
            {
                flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY;
            }

            inputs[i] = new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = new INPUT._Anonymous_e__Union
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (VIRTUAL_KEY)vk,
                        wScan = (ushort)PInvoke.MapVirtualKeyEx((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, default),
                        dwFlags = flags,
                        dwExtraInfo = InjectSignature,
                    },
                },
            };
        }

        Send(inputs);
    }

    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var scan = text[i] == '\n' ? '\r' : text[i];
            inputs[2 * i] = UnicodeInput(scan, up: false);
            inputs[(2 * i) + 1] = UnicodeInput(scan, up: true);
        }

        Send(inputs);
    }

    public void SendWheel(int delta, bool horizontal)
    {
        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = horizontal ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL : MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL,
                    dwExtraInfo = InjectSignature,
                },
            },
        };
        Send([input]);
    }

    public void SendMouse(MouseHookKind kind, int xButton = 0)
    {
        MOUSE_EVENT_FLAGS flags = kind switch
        {
            MouseHookKind.LeftDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
            MouseHookKind.LeftUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
            MouseHookKind.RightDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN,
            MouseHookKind.RightUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP,
            MouseHookKind.MiddleDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN,
            MouseHookKind.MiddleUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP,
            MouseHookKind.XDown => MOUSE_EVENT_FLAGS.MOUSEEVENTF_XDOWN,
            MouseHookKind.XUp => MOUSE_EVENT_FLAGS.MOUSEEVENTF_XUP,
            _ => 0,
        };
        if (flags == 0)
        {
            return;
        }

        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                mi = new MOUSEINPUT
                {
                    mouseData = kind is MouseHookKind.XDown or MouseHookKind.XUp ? (uint)xButton : 0,
                    dwFlags = flags,
                    dwExtraInfo = InjectSignature,
                },
            },
        };
        Send([input]);
    }

    public bool IsKeyDown(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static INPUT UnicodeInput(char c, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wScan = c,
                dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = InjectSignature,
            },
        },
    };

    private static void Send(INPUT[] inputs)
    {
        var sent = PInvoke.SendInput(inputs.AsSpan(), sizeof(INPUT));
        if (sent != inputs.Length)
        {
            Log.Warn("input", $"SendInput delivered {sent} of {inputs.Length} events (UIPI or secure desktop?).");
        }
    }

    private static bool IsExtendedKey(int vk) => vk is
        VirtualKeys.Insert or VirtualKeys.Delete or VirtualKeys.Home or VirtualKeys.End or
        VirtualKeys.Prior or VirtualKeys.Next or VirtualKeys.Left or VirtualKeys.Right or
        VirtualKeys.Up or VirtualKeys.Down or VirtualKeys.RControl or VirtualKeys.RMenu or
        VirtualKeys.LWin or VirtualKeys.RWin or VirtualKeys.Apps or VirtualKeys.Divide or VirtualKeys.Snapshot;

    private static KeyModifiers CurrentModifiers()
    {
        var m = KeyModifiers.None;
        if ((PInvoke.GetAsyncKeyState(VirtualKeys.Control) & 0x8000) != 0) m |= KeyModifiers.Control;
        if ((PInvoke.GetAsyncKeyState(VirtualKeys.Menu) & 0x8000) != 0) m |= KeyModifiers.Alt;
        if ((PInvoke.GetAsyncKeyState(VirtualKeys.Shift) & 0x8000) != 0) m |= KeyModifiers.Shift;
        if ((PInvoke.GetAsyncKeyState(VirtualKeys.LWin) & 0x8000) != 0 || (PInvoke.GetAsyncKeyState(VirtualKeys.RWin) & 0x8000) != 0) m |= KeyModifiers.Win;
        return m;
    }

    private void Post(uint message)
    {
        EnsureThread();
        PInvoke.PostThreadMessage(_threadId, message, default, default);
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(HookThread) { IsBackground = true, Name = "InputHooks", Priority = ThreadPriority.Highest };
        _thread.Start();
        _threadReady.Wait();
    }

    private void HookThread()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        // Force the thread's message queue to exist before anyone posts to it.
        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_NULL, default, default);
        var watchdog = PInvoke.SetTimer(HWND.Null, 0, WatchdogIntervalMs, null);
        _threadReady.Set();

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case MsgInstallKeyboard when _keyboardHook == 0:
                    _keyboardHook = Install(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc);
                    _lastKeyboardEventTime = (uint)Environment.TickCount;
                    break;
                case MsgRemoveKeyboard when _keyboardHook != 0 && _keyboardHandlers.IsEmpty:
                    PInvoke.UnhookWindowsHookEx(new HHOOK(_keyboardHook));
                    _keyboardHook = 0;
                    break;
                case MsgInstallMouse when _mouseHook == 0:
                    _mouseHook = Install(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc);
                    _lastMouseEventTime = (uint)Environment.TickCount;
                    break;
                case MsgRemoveMouse when _mouseHook != 0 && _mouseHandlers.IsEmpty:
                    PInvoke.UnhookWindowsHookEx(new HHOOK(_mouseHook));
                    _mouseHook = 0;
                    break;
                case PInvoke.WM_TIMER when msg.wParam.Value == watchdog:
                    CheckHooksAlive();
                    break;
                default:
                    PInvoke.TranslateMessage(in msg);
                    PInvoke.DispatchMessage(in msg);
                    break;
            }
        }
    }

    /// <summary>
    /// Reinstalls a hook that has stopped receiving events although the system
    /// still sees input (Windows dropped it after a slow callback). A keyboard
    /// hook during mouse-only use looks "deaf" too; the cooldown keeps those
    /// harmless reinstalls rare.
    /// </summary>
    private void CheckHooksAlive()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        if (!PInvoke.GetLastInputInfo(ref info))
        {
            return;
        }

        var now = (uint)Environment.TickCount;
        var lastInput = info.dwTime;
        if (_keyboardHook != 0 && unchecked(lastInput - _lastKeyboardEventTime) > DeafThresholdMs
            && unchecked(now - _lastKeyboardReinstall) > ReinstallCooldownMs)
        {
            PInvoke.UnhookWindowsHookEx(new HHOOK(_keyboardHook));
            _keyboardHook = Install(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _keyboardProc);
            _lastKeyboardReinstall = now;
            _lastKeyboardEventTime = lastInput;
            Log.Debug("input", "Keyboard hook reinstalled by the watchdog.");
        }

        if (_mouseHook != 0 && unchecked(lastInput - _lastMouseEventTime) > DeafThresholdMs
            && unchecked(now - _lastMouseReinstall) > ReinstallCooldownMs)
        {
            PInvoke.UnhookWindowsHookEx(new HHOOK(_mouseHook));
            _mouseHook = Install(WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseProc);
            _lastMouseReinstall = now;
            _lastMouseEventTime = lastInput;
            Log.Info("input", "Mouse hook reinstalled by the watchdog (Windows had removed it).");
        }
    }

    private static nint Install(WINDOWS_HOOK_ID id, HOOKPROC proc)
    {
        var module = PInvoke.GetModuleHandle((string?)null);
        var hook = PInvoke.SetWindowsHookEx(id, proc, module, 0);
        var handle = hook.DangerousGetHandle();
        hook.SetHandleAsInvalid();
        if (handle == 0)
        {
            Log.Error("input", $"SetWindowsHookEx({id}) failed: {Marshal.GetLastWin32Error()}");
        }

        return handle;
    }

    private LRESULT KeyboardProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var data = (KBDLLHOOKSTRUCT*)lParam.Value;
            _lastKeyboardEventTime = data->time;
            if (data->dwExtraInfo != InjectSignature)
            {
                var message = (uint)wParam.Value;
                var e = new KeyboardHookEvent
                {
                    VirtualKey = (int)data->vkCode,
                    ScanCode = (int)data->scanCode,
                    Action = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN ? KeyAction.Down : KeyAction.Up,
                    IsSystemKey = message is PInvoke.WM_SYSKEYDOWN or PInvoke.WM_SYSKEYUP,
                    IsInjected = (data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0,
                    IsExtended = (data->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0,
                    Modifiers = CurrentModifiers(),
                    Time = data->time,
                };

                foreach (var (_, handler) in _keyboardHandlers)
                {
                    try
                    {
                        if (handler(ref e))
                        {
                            return new LRESULT(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("input", "Keyboard hook handler failed.", ex);
                    }
                }
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private LRESULT MouseProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var data = (MSLLHOOKSTRUCT*)lParam.Value;
            _lastMouseEventTime = data->time;
            if (data->dwExtraInfo != InjectSignature)
            {
                var message = (uint)wParam.Value;
                var high = (short)((data->mouseData >> 16) & 0xFFFF);
                MouseHookKind? kind = message switch
                {
                    PInvoke.WM_MOUSEMOVE => MouseHookKind.Move,
                    PInvoke.WM_LBUTTONDOWN => MouseHookKind.LeftDown,
                    PInvoke.WM_LBUTTONUP => MouseHookKind.LeftUp,
                    PInvoke.WM_RBUTTONDOWN => MouseHookKind.RightDown,
                    PInvoke.WM_RBUTTONUP => MouseHookKind.RightUp,
                    PInvoke.WM_MBUTTONDOWN => MouseHookKind.MiddleDown,
                    PInvoke.WM_MBUTTONUP => MouseHookKind.MiddleUp,
                    PInvoke.WM_XBUTTONDOWN => MouseHookKind.XDown,
                    PInvoke.WM_XBUTTONUP => MouseHookKind.XUp,
                    PInvoke.WM_MOUSEWHEEL => MouseHookKind.Wheel,
                    PInvoke.WM_MOUSEHWHEEL => MouseHookKind.HorizontalWheel,
                    _ => null,
                };

                if (kind is { } k)
                {
                    var e = new MouseHookEvent
                    {
                        Kind = k,
                        Position = new PixelPoint(data->pt.X, data->pt.Y),
                        WheelDelta = k is MouseHookKind.Wheel or MouseHookKind.HorizontalWheel ? high : 0,
                        XButton = k is MouseHookKind.XDown or MouseHookKind.XUp ? high : 0,
                        IsInjected = (data->flags & PInvoke.LLMHF_INJECTED) != 0,
                        Modifiers = k == MouseHookKind.Move ? KeyModifiers.None : CurrentModifiers(),
                        Time = data->time,
                    };

                    foreach (var (_, handler) in _mouseHandlers)
                    {
                        try
                        {
                            if (handler(ref e))
                            {
                                return new LRESULT(1);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error("input", "Mouse hook handler failed.", ex);
                        }
                    }
                }
            }
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _keyboardHandlers = [];
            _mouseHandlers = [];
        }

        if (_thread is not null)
        {
            PInvoke.PostThreadMessage(_threadId, MsgRemoveKeyboard, default, default);
            PInvoke.PostThreadMessage(_threadId, MsgRemoveMouse, default, default);
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
