// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Shortcuts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Input.SmoothScroll;

/// <summary>
/// Raw Input side channel for the input fixes, on its own thread with a
/// message-only window (RIDEV_INPUTSINK, so input arrives while other apps are
/// in front). It provides two facts a low-level hook cannot:
/// <list type="bullet">
/// <item>the physical state of keys, including keys a hook swallowed (Raw
/// Input is produced before low-level hooks run), used by the Super key
/// watchdog;</item>
/// <item>Precision Touchpad activity (HID digitizer reports, usage page 0x0D,
/// usage 0x05), used to tell touchpad scrolling from a mouse wheel.</item>
/// </list>
/// Registrations exist only while some feature tracks them. Caveat: Windows
/// allows one Raw Input target window per device class per process, so a
/// second module registering the keyboard class would take it over.
/// </summary>
public sealed unsafe class WindowsRawInput : IDisposable
{
    private const uint MsgUpdate = PInvoke.WM_APP + 10;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;
    private const ushort UsagePageGeneric = 0x01;
    private const ushort UsageKeyboard = 0x06;
    private const ushort UsagePageDigitizer = 0x0D;
    private const ushort UsageTouchPad = 0x05;
    private const ushort KeyBreak = 0x01;
    private const ushort KeyE0 = 0x02;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceInfo = 0x2000000b;

    private readonly object _gate = new();
    private readonly bool[] _keyDown = new bool[256];
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WNDPROC _wndProc;
    private Thread? _thread;
    private uint _threadId;
    private HWND _hwnd;
    private int _keyboardUsers;
    private int _touchpadUsers;
    private volatile bool _keyboardRegistered;
    private volatile bool _touchpadRegistered;
    private long _lastTouchpadReport;
    private bool _disposed;

    public WindowsRawInput()
    {
        _wndProc = WndProc;
    }

    /// <summary>Stopwatch timestamp of the last touchpad report (0 = never).</summary>
    public long LastTouchpadReport => Interlocked.Read(ref _lastTouchpadReport);

    public bool TouchpadTracking => _touchpadRegistered;

    /// <summary>The physical state of a key while keyboard tracking runs, else null.</summary>
    public bool? IsKeyDown(int virtualKey) => _keyboardRegistered ? Volatile.Read(ref _keyDown[virtualKey & 0xFF]) : null;

    public IDisposable TrackKeyboard() => Track(ref _keyboardUsers, () => Interlocked.Decrement(ref _keyboardUsers));

    public IDisposable TrackTouchpad() => Track(ref _touchpadUsers, () => Interlocked.Decrement(ref _touchpadUsers));

    /// <summary>Whether a Precision Touchpad is connected (a HID device with the digitizer touch pad usage).</summary>
    public static bool HasPrecisionTouchpad()
    {
        uint count = 0;
        var itemSize = (uint)sizeof(RAWINPUTDEVICELIST);
        if (PInvoke.GetRawInputDeviceList(null, &count, itemSize) != 0 || count == 0)
        {
            return false;
        }

        var devices = new RAWINPUTDEVICELIST[count];
        fixed (RAWINPUTDEVICELIST* list = devices)
        {
            var got = PInvoke.GetRawInputDeviceList(list, &count, itemSize);
            if (got == uint.MaxValue)
            {
                return false;
            }

            for (var i = 0; i < got; i++)
            {
                if ((uint)list[i].dwType != RimTypeHid)
                {
                    continue;
                }

                var info = new RID_DEVICE_INFO { cbSize = (uint)sizeof(RID_DEVICE_INFO) };
                var size = info.cbSize;
                if (PInvoke.GetRawInputDeviceInfo(list[i].hDevice, (RAW_INPUT_DEVICE_INFO_COMMAND)RidiDeviceInfo, &info, &size) == uint.MaxValue)
                {
                    continue;
                }

                if (info.Anonymous.hid.usUsagePage == UsagePageDigitizer && info.Anonymous.hid.usUsage == UsageTouchPad)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _keyboardUsers = 0;
            _touchpadUsers = 0;
        }

        if (_thread is not null)
        {
            PInvoke.PostThreadMessage(_threadId, MsgUpdate, default, default);
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
        }
    }

    private IDisposable Track(ref int users, Action release)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new Releaser(() => { });
            }

            Interlocked.Increment(ref users);
            EnsureThread();
        }

        PInvoke.PostThreadMessage(_threadId, MsgUpdate, default, default);
        return new Releaser(() =>
        {
            release();
            PInvoke.PostThreadMessage(_threadId, MsgUpdate, default, default);
        });
    }

    private void EnsureThread()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "RawInput", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        var className = $"{Rivet.Core.App.AppIdentity.Id}.RawInput.{Environment.ProcessId}";
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = new HINSTANCE(PInvoke.GetModuleHandle((PCWSTR)null).Value),
                lpszClassName = name,
            };
            PInvoke.RegisterClassEx(in wc);
            _hwnd = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, new HWND((void*)-3) /* HWND_MESSAGE */, HMENU.Null, wc.hInstance, null);
        }

        PInvoke.PostThreadMessage(_threadId, PInvoke.WM_NULL, default, default);
        _ready.Set();
        if (_hwnd.IsNull)
        {
            Log.Warn("input", $"Raw Input window could not be created: {Marshal.GetLastWin32Error()}");
            return;
        }

        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0) > 0)
        {
            if (msg.hwnd.IsNull && msg.message == MsgUpdate)
            {
                UpdateRegistrations();
                continue;
            }

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        _keyboardRegistered = false;
        _touchpadRegistered = false;
        PInvoke.DestroyWindow(_hwnd);
    }

    private void UpdateRegistrations()
    {
        var wantKeyboard = Volatile.Read(ref _keyboardUsers) > 0;
        var wantTouchpad = Volatile.Read(ref _touchpadUsers) > 0;
        if (wantKeyboard != _keyboardRegistered)
        {
            if (Register(UsagePageGeneric, UsageKeyboard, wantKeyboard))
            {
                if (wantKeyboard)
                {
                    Array.Clear(_keyDown);
                }

                _keyboardRegistered = wantKeyboard;
            }
        }

        if (wantTouchpad != _touchpadRegistered && Register(UsagePageDigitizer, UsageTouchPad, wantTouchpad))
        {
            _touchpadRegistered = wantTouchpad;
        }
    }

    private bool Register(ushort usagePage, ushort usage, bool add)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = usagePage,
            usUsage = usage,
            dwFlags = add ? RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK : RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
            hwndTarget = add ? _hwnd : HWND.Null,
        };
        if (PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            return true;
        }

        Log.Warn("input", $"RegisterRawInputDevices({usagePage:X2}/{usage:X2}, {(add ? "add" : "remove")}) failed: {Marshal.GetLastWin32Error()}");
        return false;
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == PInvoke.WM_INPUT)
        {
            try
            {
                ReadInput(lParam);
            }
            catch (Exception ex)
            {
                Log.Warn("input", "Raw Input read failed.", ex);
            }
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ReadInput(LPARAM lParam)
    {
        var headerSize = (uint)sizeof(RAWINPUTHEADER);
        uint size = 0;
        var handle = new HRAWINPUT((void*)lParam.Value);
        if (PInvoke.GetRawInputData(handle, (RAW_INPUT_DATA_COMMAND_FLAGS)RidInput, null, &size, headerSize) != 0 || size == 0 || size > 4096)
        {
            return;
        }

        var buffer = stackalloc byte[(int)size];
        if (PInvoke.GetRawInputData(handle, (RAW_INPUT_DATA_COMMAND_FLAGS)RidInput, buffer, &size, headerSize) == uint.MaxValue)
        {
            return;
        }

        var input = (RAWINPUT*)buffer;
        switch (input->header.dwType)
        {
            case RimTypeKeyboard:
            {
                var keyboard = input->data.keyboard;
                var vk = Normalize(keyboard.VKey, keyboard.MakeCode, keyboard.Flags);
                if (vk is > 0 and < 256)
                {
                    Volatile.Write(ref _keyDown[vk], (keyboard.Flags & KeyBreak) == 0);
                }

                break;
            }

            case RimTypeHid:
                Interlocked.Exchange(ref _lastTouchpadReport, Stopwatch.GetTimestamp());
                break;
        }
    }

    /// <summary>Raw Input reports generic Shift/Ctrl/Alt codes; split them into left and right.</summary>
    private static int Normalize(ushort vk, ushort makeCode, ushort flags) => vk switch
    {
        VirtualKeys.Shift => makeCode == 0x36 ? VirtualKeys.RShift : VirtualKeys.LShift,
        VirtualKeys.Control => (flags & KeyE0) != 0 ? VirtualKeys.RControl : VirtualKeys.LControl,
        VirtualKeys.Menu => (flags & KeyE0) != 0 ? VirtualKeys.RMenu : VirtualKeys.LMenu,
        0xFF => 0,
        _ => vk,
    };

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
