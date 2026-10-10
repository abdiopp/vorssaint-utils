// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Awake;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Displays;
using Rivet.Platform.Windows.Input;
using Rivet.Platform.Windows.Interop;
using Rivet.Platform.Windows.SystemMonitor;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Power;
using Windows.Win32.System.RemoteDesktop;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Awake;

/// <summary>
/// PowerCreateRequest + PowerSetRequest: "system required" always, "display
/// required" unless the display may sleep. Visible in <c>powercfg /requests</c>.
/// </summary>
public sealed unsafe class WindowsPowerRequests : IPowerRequests, IDisposable
{
    private readonly object _gate = new();
    private SafeFileHandle? _request;
    private bool _system;
    private bool _display;

    public void Apply(bool keepSystemAwake, bool keepDisplayOn, string reason)
    {
        lock (_gate)
        {
            if (_request is null)
            {
                fixed (char* text = reason)
                {
                    var context = new REASON_CONTEXT
                    {
                        Version = 0, // POWER_REQUEST_CONTEXT_VERSION
                        Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                    };
                    context.Reason.SimpleReasonString = new PWSTR(text);
                    var handle = PInvoke.PowerCreateRequest(in context);
                    if (handle.IsInvalid)
                    {
                        Log.Error("keep-awake", $"PowerCreateRequest failed ({Marshal.GetLastPInvokeError()}).");
                        handle.Dispose();
                        return;
                    }

                    _request = handle;
                }
            }

            Set(POWER_REQUEST_TYPE.PowerRequestSystemRequired, keepSystemAwake, ref _system);
            Set(POWER_REQUEST_TYPE.PowerRequestDisplayRequired, keepDisplayOn, ref _display);
        }
    }

    private void Set(POWER_REQUEST_TYPE type, bool want, ref bool current)
    {
        if (want == current || _request is null)
        {
            return;
        }

        var ok = want ? PInvoke.PowerSetRequest(_request, type) : PInvoke.PowerClearRequest(_request, type);
        if (ok)
        {
            current = want;
        }
        else
        {
            Log.Warn("keep-awake", $"Power request {type} -> {want} failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_request is null)
            {
                return;
            }

            Set(POWER_REQUEST_TYPE.PowerRequestDisplayRequired, false, ref _display);
            Set(POWER_REQUEST_TYPE.PowerRequestSystemRequired, false, ref _system);
            _request.Dispose();
            _request = null;
            _system = _display = false;
        }
    }

    public void Dispose() => Release();
}

/// <summary>Screen lock and unlock from WTS session notifications on the shared native window.</summary>
public sealed unsafe class WindowsSessionLockMonitor : ISessionLockMonitor, IDisposable
{
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint NotifyForThisSession = 0;

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private readonly bool _registered;

    public WindowsSessionLockMonitor(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
        _registered = PInvoke.WTSRegisterSessionNotification(new HWND((void*)host.Handle), NotifyForThisSession);
        IsLocked = QueryLocked();
    }

    public bool IsLocked { get; private set; }

    public event EventHandler<bool>? LockChanged;

    private static bool QueryLocked()
    {
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, PInvoke.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSSessionInfoEx, out var buffer, out var bytes))
        {
            return false;
        }

        try
        {
            if (bytes < sizeof(WTSINFOEXW))
            {
                return false;
            }

            var info = (WTSINFOEXW*)buffer.Value;
            // WTS_SESSIONSTATE_LOCK = 0, UNLOCK = 1 (correct from Windows 8 on).
            return info->Level == 1 && info->Data.WTSInfoExLevel1.SessionFlags == 0;
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message != WmWtsSessionChange)
        {
            return null;
        }

        var locked = (uint)wParam switch
        {
            PInvoke.WTS_SESSION_LOCK => true,
            PInvoke.WTS_SESSION_UNLOCK => false,
            _ => (bool?)null,
        };
        if (locked is { } state && state != IsLocked)
        {
            IsLocked = state;
            LockChanged?.Invoke(this, state);
        }

        return null;
    }

    public void Dispose()
    {
        if (_registered)
        {
            PInvoke.WTSUnRegisterSessionNotification(new HWND((void*)_host.Handle));
        }

        _host.RemoveHandler(_handler);
    }
}

/// <summary>AC line and charge from GetSystemPowerStatus; changes from WM_POWERBROADCAST.</summary>
public sealed class WindowsPowerSource : IPowerSource, IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;

    public WindowsPowerSource(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public event EventHandler? Changed;

    public bool HasBattery => PInvoke.GetSystemPowerStatus(out var s) && s.BatteryFlag is not (128 or 255);

    public bool OnExternalPower => PInvoke.GetSystemPowerStatus(out var s) && s.ACLineStatus == 1;

    public int? BatteryPercent => PInvoke.GetSystemPowerStatus(out var s) && s.BatteryFlag is not (128 or 255) && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast && (uint)wParam == PInvoke.PBT_APMPOWERSTATUSCHANGE)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    public void Dispose() => _host.RemoveHandler(_handler);
}

/// <summary>External displays: any active path that is not the built-in panel nor a virtual display.</summary>
public sealed class WindowsDisplayTopology : IDisplayTopology, IDisposable
{
    private readonly IScreenService _screens;

    public WindowsDisplayTopology(IScreenService screens)
    {
        _screens = screens;
        _screens.ScreensChanged += OnScreensChanged;
    }

    public event EventHandler? Changed;

    public bool? ExternalDisplayConnected()
    {
        var targets = DisplayConfig.ActiveTargets();
        return targets?.Any(t => !t.IsInternal && !t.IsVirtual);
    }

    private void OnScreensChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _screens.ScreensChanged -= OnScreensChanged;
}

/// <summary>Running executables from the kernel's process list (one call, no handles).</summary>
public sealed class WindowsRunningApps : IRunningApps
{
    public IReadOnlySet<string> RunningExecutables()
    {
        var processes = NtQuery.Processes();
        return processes is null
            ? new HashSet<string>()
            : processes.Select(p => p.ImageName.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Moves the pointer one pixel (right, else left, else down, else up, staying
/// two pixels inside its monitor) with SendInput, which also resets the idle
/// timer apps watch, and moves it back 80 ms later if the user did not move it.
/// </summary>
public sealed unsafe class WindowsPointerJiggler : IPointerJiggler
{
    public void Nudge() => Task.Run(NudgeNow);

    private static void NudgeNow()
    {
        if (!PInvoke.GetCursorPos(out var start))
        {
            return;
        }

        var monitor = PInvoke.MonitorFromPoint(start, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var bounds = info.rcMonitor;
        var target = start;
        if (start.X + 1 <= bounds.right - 3) target.X += 1;
        else if (start.X - 1 >= bounds.left + 2) target.X -= 1;
        else if (start.Y + 1 <= bounds.bottom - 3) target.Y += 1;
        else if (start.Y - 1 >= bounds.top + 2) target.Y -= 1;
        else return;

        MoveTo(target.X, target.Y);
        Thread.Sleep(80);
        if (PInvoke.GetCursorPos(out var now) && Math.Abs(now.X - target.X) <= 2 && Math.Abs(now.Y - target.Y) <= 2)
        {
            MoveTo(start.X, start.Y);
        }
    }

    private static void MoveTo(int x, int y)
    {
        var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var width = Math.Max(2, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN));
        var height = Math.Max(2, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                    dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
                    // Our own hooks skip input stamped with the app's signature.
                    dwExtraInfo = WindowsInputHooks.InjectSignature,
                },
            },
        };
        if (PInvoke.SendInput([input], sizeof(INPUT)) != 1)
        {
            Log.Warn("keep-awake", "SendInput did not move the pointer (secure desktop or elevated window in front?).");
        }
    }
}

/// <summary>
/// The power plan's lid action (GUID_SYSTEM_BUTTON_SUBGROUP / GUID_LIDCLOSE_ACTION:
/// 0 do nothing, 1 sleep, 2 hibernate, 3 shut down) on the active scheme, for
/// AC and battery. The lid state comes from GUID_LIDSWITCH_STATE_CHANGE
/// notifications; sleep requests use SetSuspendState, falling back to turning
/// the displays off on Modern Standby PCs.
/// </summary>
public sealed unsafe class WindowsLidActionController : ILidActionController, IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmSysCommand = 0x0112;
    private const nuint ScMonitorPower = 0xF170;

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private readonly global::Windows.Win32.UnregisterPowerSettingNotificationSafeHandle? _registration;

    public WindowsLidActionController(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
        HasLid = PInvoke.GetPwrCapabilities(out var capabilities) && capabilities.LidPresent;
        if (HasLid)
        {
            var hwndHandle = new SafeFileHandle(host.Handle, ownsHandle: false);
            var registration = PInvoke.RegisterPowerSettingNotification(hwndHandle, PInvoke.GUID_LIDSWITCH_STATE_CHANGE, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
            _registration = registration.IsInvalid ? null : registration;
        }
    }

    public bool HasLid { get; }

    public bool? IsLidClosed { get; private set; }

    public event EventHandler? LidChanged;

    public LidRecovery? ReadCurrent()
    {
        Guid* active = null;
        if (PInvoke.PowerGetActiveScheme(default, &active) != 0 || active is null)
        {
            return null;
        }

        var scheme = *active;
        PInvoke.LocalFree(new HLOCAL(active));
        var subgroup = PInvoke.GUID_SYSTEM_BUTTON_SUBGROUP;
        var setting = PInvoke.GUID_LIDCLOSE_ACTION;
        uint ac;
        uint dc;
        if (PInvoke.PowerReadACValueIndex(default, &scheme, &subgroup, &setting, &ac) != 0
            || PInvoke.PowerReadDCValueIndex(default, &scheme, &subgroup, &setting, &dc) != 0)
        {
            return null;
        }

        return new LidRecovery { Scheme = scheme, OriginalAc = ac, OriginalDc = dc };
    }

    public bool Write(Guid scheme, uint ac, uint dc)
    {
        var subgroup = PInvoke.GUID_SYSTEM_BUTTON_SUBGROUP;
        var setting = PInvoke.GUID_LIDCLOSE_ACTION;
        var acResult = PInvoke.PowerWriteACValueIndex(default, &scheme, &subgroup, &setting, ac);
        var dcResult = PInvoke.PowerWriteDCValueIndex(default, &scheme, &subgroup, &setting, dc);
        if (acResult != 0 || dcResult != 0)
        {
            Log.Warn("keep-awake", $"Writing the lid action failed (AC {acResult}, DC {dcResult}).");
            return false;
        }

        // Re-applying the active scheme makes the new values take effect now.
        var apply = PInvoke.PowerSetActiveScheme(default, &scheme);
        if (apply != 0)
        {
            Log.Warn("keep-awake", $"Applying the power scheme failed ({apply}).");
            return false;
        }

        return true;
    }

    public bool RequestSleep()
    {
        if (PInvoke.SetSuspendState(false, false, false))
        {
            return true;
        }

        // Modern Standby PCs refuse SetSuspendState; turning the displays off with the lid closed enters standby.
        return PInvoke.PostMessage(new HWND((void*)_host.Handle), WmSysCommand, ScMonitorPower, 2);
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message != WmPowerBroadcast || (uint)wParam != PInvoke.PBT_POWERSETTINGCHANGE || lParam == 0)
        {
            return null;
        }

        var setting = (POWERBROADCAST_SETTING*)lParam;
        if (setting->PowerSetting == PInvoke.GUID_LIDSWITCH_STATE_CHANGE && setting->DataLength >= 4)
        {
            // 0 = closed, 1 = open.
            var closed = *(uint*)&setting->Data == 0;
            if (IsLidClosed != closed)
            {
                IsLidClosed = closed;
                LidChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        return null;
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _host.RemoveHandler(_handler);
    }
}

public sealed class KeepAwakeRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IPowerRequests, WindowsPowerRequests>();
        services.AddSingleton<ISessionLockMonitor, WindowsSessionLockMonitor>();
        services.AddSingleton<IPowerSource, WindowsPowerSource>();
        services.AddSingleton<IDisplayTopology, WindowsDisplayTopology>();
        services.AddSingleton<IRunningApps, WindowsRunningApps>();
        services.AddSingleton<IPointerJiggler, WindowsPointerJiggler>();
        services.AddSingleton<ILidActionController, WindowsLidActionController>();
    }
}
