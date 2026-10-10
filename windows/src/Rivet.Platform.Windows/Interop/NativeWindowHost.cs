// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Interop;

/// <summary>Handles a window message; return a result to stop further handling.</summary>
public delegate nint? WindowMessageHandler(uint message, nuint wParam, nint lParam);

/// <summary>
/// A hidden top-level window owned by the UI thread. It receives tray icon
/// callbacks, hotkeys, clipboard updates and broadcasts (TaskbarCreated,
/// WM_SETTINGCHANGE, WM_DISPLAYCHANGE). Create it on the UI thread after the
/// UI framework started its message loop; Avalonia's loop dispatches to it.
/// A top-level window (not HWND_MESSAGE) is needed to receive broadcasts.
/// </summary>
public sealed unsafe class NativeWindowHost : IDisposable
{
    private readonly WNDPROC _wndProc;
    private readonly List<WindowMessageHandler> _handlers = [];
    private readonly string _className;
    private HWND _hwnd;

    public NativeWindowHost()
    {
        _wndProc = WndProc;
        _className = $"{Rivet.Core.App.AppIdentity.Id}.Host.{Environment.ProcessId}";
        fixed (char* className = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = new HINSTANCE(PInvoke.GetModuleHandle((PCWSTR)null).Value),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            }

            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                className,
                className,
                WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0,
                HWND.Null,
                HMENU.Null,
                wc.hInstance,
                null);
        }

        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }

        TaskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
    }

    public nint Handle => (nint)_hwnd.Value;

    /// <summary>Broadcast after Explorer (re)starts; tray icons must be re-added.</summary>
    public uint TaskbarCreatedMessage { get; }

    public void AddHandler(WindowMessageHandler handler)
    {
        lock (_handlers)
        {
            _handlers.Add(handler);
        }
    }

    public void RemoveHandler(WindowMessageHandler handler)
    {
        lock (_handlers)
        {
            _handlers.Remove(handler);
        }
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        WindowMessageHandler[] handlers;
        lock (_handlers)
        {
            handlers = _handlers.ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                var result = handler(msg, wParam.Value, lParam.Value);
                if (result.HasValue)
                {
                    return new LRESULT(result.Value);
                }
            }
            catch (Exception ex)
            {
                Log.Error("win32", $"Message handler failed for 0x{msg:X4}.", ex);
            }
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = HWND.Null;
            fixed (char* className = _className)
            {
                PInvoke.UnregisterClass(className, new HINSTANCE(PInvoke.GetModuleHandle((PCWSTR)null).Value));
            }
        }
    }
}
