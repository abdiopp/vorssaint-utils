// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Clipboard;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Util;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Clipboard;

/// <summary>
/// Sleep, display-off and lock notifications for auto clear (spec 06 §7.7):
/// WM_POWERBROADCAST (PBT_APMSUSPEND, and GUID_CONSOLE_DISPLAY_STATE through
/// RegisterPowerSettingNotification) and WTSRegisterSessionNotification, all
/// on the app's top-level host window. Registered only while someone listens.
/// </summary>
public sealed unsafe class WindowsSessionEvents : ISessionEvents, IDisposable
{
    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private EventHandler<SessionEventKind>? _occurred;
    private HPOWERNOTIFY _displayNotification;
    private bool _registered;

    public WindowsSessionEvents(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
    }

    public event EventHandler<SessionEventKind>? Occurred
    {
        add
        {
            _occurred += value;
            UiThread.Run(UpdateRegistration);
        }

        remove
        {
            _occurred -= value;
            UiThread.Run(UpdateRegistration);
        }
    }

    private void UpdateRegistration()
    {
        var want = _occurred is not null;
        if (want == _registered)
        {
            return;
        }

        var hwnd = new HWND((void*)_host.Handle);
        if (want)
        {
            _host.AddHandler(_handler);
            if (!PInvoke.WTSRegisterSessionNotification(hwnd, PInvoke.NOTIFY_FOR_THIS_SESSION))
            {
                Log.Warn("clipboard", $"WTSRegisterSessionNotification failed: {Marshal.GetLastWin32Error()}");
            }

            var guid = PInvoke.GUID_CONSOLE_DISPLAY_STATE;
            _displayNotification = PInvoke.RegisterPowerSettingNotification(new HANDLE((void*)_host.Handle), &guid, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
            _registered = true;
        }
        else
        {
            PInvoke.WTSUnRegisterSessionNotification(hwnd);
            if (!_displayNotification.IsNull)
            {
                PInvoke.UnregisterPowerSettingNotification(_displayNotification);
                _displayNotification = default;
            }

            _host.RemoveHandler(_handler);
            _registered = false;
        }
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        SessionEventKind? kind = null;
        if (message == PInvoke.WM_WTSSESSION_CHANGE)
        {
            kind = (uint)wParam switch
            {
                PInvoke.WTS_SESSION_LOCK => SessionEventKind.Lock,
                PInvoke.WTS_SESSION_UNLOCK => SessionEventKind.Unlock,
                PInvoke.WTS_CONSOLE_DISCONNECT => SessionEventKind.ConsoleDisconnect,
                PInvoke.WTS_CONSOLE_CONNECT => SessionEventKind.ConsoleConnect,
                _ => null,
            };
        }
        else if (message == PInvoke.WM_POWERBROADCAST)
        {
            if ((uint)wParam == PInvoke.PBT_APMSUSPEND)
            {
                kind = SessionEventKind.Sleep;
            }
            else if ((uint)wParam == PInvoke.PBT_POWERSETTINGCHANGE && lParam != 0)
            {
                var setting = (POWERBROADCAST_SETTING*)lParam;
                if (setting->PowerSetting == PInvoke.GUID_CONSOLE_DISPLAY_STATE && setting->DataLength >= 4)
                {
                    var state = *(uint*)&setting->Data;
                    if (state == 0)
                    {
                        kind = SessionEventKind.DisplaySleep;
                    }
                }
            }
        }

        if (kind is { } k)
        {
            try
            {
                _occurred?.Invoke(this, k);
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", "A session event handler failed.", ex);
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _occurred = null;
            UpdateRegistration();
        }
    }
}

/// <summary>Registers the Windows clipboard lane, session events and foreground tracking.</summary>
public sealed class WindowsClipboardRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<WindowsClipboardPlatform>();
        services.AddSingleton<IClipboardPlatform>(sp => sp.GetRequiredService<WindowsClipboardPlatform>());
        services.AddSingleton<WindowsSessionEvents>();
        services.AddSingleton<ISessionEvents>(sp => sp.GetRequiredService<WindowsSessionEvents>());
        services.AddSingleton<WindowsForegroundService>();
        services.AddSingleton<IForegroundService>(sp => sp.GetRequiredService<WindowsForegroundService>());
    }
}
