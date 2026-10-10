// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Rivet.Core.Util;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// RegisterHotKey on the host window, one disjoint id per registration.
/// Combinations Windows owns (Alt+Space, Print Screen) can be taken over with
/// the shared low-level keyboard hook when the caller asks for it.
/// </summary>
public sealed unsafe class WindowsHotkeyService : IHotkeyService, IDisposable
{
    private readonly NativeWindowHost _host;
    private readonly IInputHooks _hooks;
    private readonly WindowMessageHandler _handler;
    private readonly Dictionary<int, Action> _callbacks = [];
    private int _nextId = 0x100;

    public WindowsHotkeyService(NativeWindowHost host, IInputHooks hooks)
    {
        _host = host;
        _hooks = hooks;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public IDisposable? Register(KeyChord chord, Action onPressed, HotkeyOptions options = HotkeyOptions.None)
    {
        if (chord.IsEmpty)
        {
            return null;
        }

        var id = _nextId++;
        var modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (options.HasFlag(HotkeyOptions.AllowRepeat)) modifiers = 0;
        if (chord.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Win)) modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;

        if (PInvoke.RegisterHotKey(new HWND((void*)_host.Handle), id, modifiers, (uint)chord.VirtualKey))
        {
            _callbacks[id] = onPressed;
            return new Registration(() =>
            {
                PInvoke.UnregisterHotKey(new HWND((void*)_host.Handle), id);
                _callbacks.Remove(id);
            });
        }

        if (!options.HasFlag(HotkeyOptions.OverrideSystem))
        {
            return null;
        }

        // Take the combination over with the hook: swallow it and fire the callback.
        var subscription = _hooks.SubscribeKeyboard((ref KeyboardHookEvent e) =>
        {
            if (e.Action != KeyAction.Down || e.VirtualKey != chord.VirtualKey || e.Modifiers != chord.Modifiers)
            {
                return false;
            }

            UiThread.Post(onPressed);
            return true;
        }, priority: 100);
        return subscription;
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message != PInvoke.WM_HOTKEY)
        {
            return null;
        }

        if (_callbacks.TryGetValue((int)wParam, out var callback))
        {
            callback();
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys.ToArray())
        {
            PInvoke.UnregisterHotKey(new HWND((void*)_host.Handle), id);
        }

        _callbacks.Clear();
        _host.RemoveHandler(_handler);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
