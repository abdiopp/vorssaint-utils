// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Shell;

public sealed unsafe class WindowsWindowChrome : IWindowChrome
{
    public void Apply(nint hwnd, WindowChromeOptions options) => Change(hwnd, options, add: true);

    public void Remove(nint hwnd, WindowChromeOptions options) => Change(hwnd, options, add: false);

    private static void Change(nint handle, WindowChromeOptions options, bool add)
    {
        var hwnd = new HWND((void*)handle);
        var ex = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var mask = (WINDOW_EX_STYLE)0;
        if (options.HasFlag(WindowChromeOptions.ToolWindow)) mask |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        if (options.HasFlag(WindowChromeOptions.NoActivate)) mask |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        if (options.HasFlag(WindowChromeOptions.ClickThrough)) mask |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED;
        if (options.HasFlag(WindowChromeOptions.Topmost)) mask |= WINDOW_EX_STYLE.WS_EX_TOPMOST;
        var updated = add ? ex | mask : ex & ~mask;
        if (add && options.HasFlag(WindowChromeOptions.ToolWindow))
        {
            updated &= ~WINDOW_EX_STYLE.WS_EX_APPWINDOW;
        }

        if (updated != ex)
        {
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)updated);
        }

        // Click-through needs WS_EX_LAYERED, and a window made layered here stays invisible until
        // its attributes are set: keep it fully opaque. A window that was already layered is left
        // alone, since whoever made it layered manages its attributes.
        if ((ex & WINDOW_EX_STYLE.WS_EX_LAYERED) == 0 && (updated & WINDOW_EX_STYLE.WS_EX_LAYERED) != 0)
        {
            PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }

        if (options.HasFlag(WindowChromeOptions.ExcludeFromCapture))
        {
            var affinity = add ? WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE : WINDOW_DISPLAY_AFFINITY.WDA_NONE;
            if (!PInvoke.SetWindowDisplayAffinity(hwnd, affinity) && add)
            {
                // Before Windows 10 2004 only WDA_MONITOR exists (the window shows black in captures).
                PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_MONITOR);
            }
        }

        var flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED;
        var insertAfter = options.HasFlag(WindowChromeOptions.Topmost)
            ? (add ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST)
            : HWND.Null;
        if (insertAfter == HWND.Null)
        {
            flags |= SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
        }

        PInvoke.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);
    }

    public bool SetBackdrop(nint handle, WindowBackdrop backdrop, bool dark)
    {
        var hwnd = new HWND((void*)handle);
        SetDarkTitleBar(handle, dark);
        if (Environment.OSVersion.Version.Build < 22621)
        {
            return backdrop == WindowBackdrop.None;
        }

        var type = backdrop switch
        {
            WindowBackdrop.Mica => DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW,
            WindowBackdrop.Acrylic => DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW,
            _ => DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE,
        };
        return PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &type, (uint)sizeof(DWM_SYSTEMBACKDROP_TYPE)).Succeeded;
    }

    public void SetDarkTitleBar(nint handle, bool dark)
    {
        var hwnd = new HWND((void*)handle);
        BOOL value = dark;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &value, (uint)sizeof(BOOL));
    }

    public void SetRoundedCorners(nint handle, bool rounded)
    {
        var hwnd = new HWND((void*)handle);
        var preference = rounded ? DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND : DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    public nint GetForegroundWindow() => (nint)PInvoke.GetForegroundWindow().Value;

    public bool BringToFront(nint handle)
    {
        var hwnd = new HWND((void*)handle);
        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return true;
        }

        // Foreground lock: attach to the current foreground thread for the call.
        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var thisThread = PInvoke.GetCurrentThreadId();
        if (foregroundThread != thisThread && PInvoke.AttachThreadInput(thisThread, foregroundThread, true))
        {
            try
            {
                PInvoke.BringWindowToTop(hwnd);
                return PInvoke.SetForegroundWindow(hwnd);
            }
            finally
            {
                PInvoke.AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        return false;
    }
}

/// <summary>Remembers the foreground window before the app takes focus and gives it back.</summary>
public sealed class WindowsFocusHandoff(IWindowChrome chrome) : IFocusHandoff
{
    private nint _previous;

    public void Remember()
    {
        var current = chrome.GetForegroundWindow();
        if (current != 0)
        {
            _previous = current;
        }
    }

    public unsafe void Restore()
    {
        var previous = Interlocked.Exchange(ref _previous, 0);
        if (previous != 0 && PInvoke.IsWindow(new HWND((void*)previous)))
        {
            PInvoke.SetForegroundWindow(new HWND((void*)previous));
        }
    }
}
