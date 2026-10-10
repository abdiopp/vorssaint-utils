// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// The notification-area icon (Shell_NotifyIcon, NOTIFYICON_VERSION_4).
/// Re-adds itself when Explorer restarts and reports taskbar theme changes so
/// the owner can re-render a monochrome glyph for the taskbar's theme.
/// </summary>
public sealed unsafe class WindowsTrayIcon : ITrayIcon
{
    private const uint IconId = 1;
    private const uint CallbackMessage = PInvoke.WM_APP + 0x51;
    private const uint NinKeySelect = PInvoke.NIN_SELECT | 0x1; // NIN_SELECT | NINF_KEY

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private IReadOnlyList<PixelBuffer> _sizes = [];
    private HICON _icon;
    private string _tooltip = string.Empty;
    private bool _visible;

    public WindowsTrayIcon(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public event EventHandler<TrayClickEventArgs>? Clicked;

    public event EventHandler? TaskbarThemeChanged;

    public PixelRect? Bounds
    {
        get
        {
            var id = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER),
                hWnd = new HWND((void*)_host.Handle),
                uID = IconId,
            };
            if (PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Failed)
            {
                return null;
            }

            var r = new PixelRect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
            return r.IsEmpty ? null : r;
        }
    }

    public void Show()
    {
        _visible = true;
        Add();
    }

    public void Hide()
    {
        _visible = false;
        var data = NewData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
    }

    public void SetIcon(IReadOnlyList<PixelBuffer> sizes)
    {
        _sizes = sizes;
        RebuildIcon();
        Modify();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        Modify();
    }

    private void RebuildIcon()
    {
        if (_sizes.Count == 0)
        {
            return;
        }

        var dpi = PInvoke.GetDpiForSystem();
        var wanted = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
        var best = _sizes.OrderBy(s => s.Width >= wanted ? s.Width - wanted : 1000 + wanted - s.Width).First();
        var next = IconFactory.Create(best);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
        }

        _icon = next;
    }

    private NOTIFYICONDATAW NewData()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = new HWND((void*)_host.Handle),
            uID = IconId,
        };
        return data;
    }

    private void Add()
    {
        if (!_visible)
        {
            return;
        }

        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            // Already present (e.g. after a theme change): update instead.
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
        }

        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    private void Modify()
    {
        if (!_visible)
        {
            return;
        }

        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data))
        {
            Add();
        }
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            // Version 4: LOWORD(lParam) = event, wParam = anchor x/y.
            var evt = (uint)(lParam & 0xFFFF);
            var x = (short)(wParam & 0xFFFF);
            var y = (short)((wParam >> 16) & 0xFFFF);
            var point = new PixelPoint(x, y);
            switch (evt)
            {
                case PInvoke.NIN_SELECT:
                case NinKeySelect:
                    Clicked?.Invoke(this, new TrayClickEventArgs(TrayMouseButton.Left, point));
                    break;
                case PInvoke.WM_CONTEXTMENU:
                    Clicked?.Invoke(this, new TrayClickEventArgs(TrayMouseButton.Right, point));
                    break;
                case PInvoke.WM_MBUTTONUP:
                    Clicked?.Invoke(this, new TrayClickEventArgs(TrayMouseButton.Middle, point));
                    break;
            }

            return 0;
        }

        if (message == _host.TaskbarCreatedMessage)
        {
            Log.Info("tray", "Explorer restarted; re-adding the tray icon.");
            RebuildIcon();
            Add();
            return null;
        }

        if (message == PInvoke.WM_SETTINGCHANGE && lParam != 0)
        {
            var area = Marshal.PtrToStringUni(lParam);
            if (area == "ImmersiveColorSet")
            {
                TaskbarThemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (message == PInvoke.WM_DPICHANGED || message == PInvoke.WM_DISPLAYCHANGE)
        {
            RebuildIcon();
            Modify();
        }

        return null;
    }

    public void Dispose()
    {
        Hide();
        _host.RemoveHandler(_handler);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }
}
