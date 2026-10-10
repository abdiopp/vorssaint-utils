// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Recording;

/// <summary>Power requests, window geometry, a high-resolution timer and the error beep.</summary>
public sealed unsafe class WindowsRecorderSystem : IRecorderSystem
{
    public IDisposable PreventSleep(string reason) => PowerRequest.Create(reason);

    public WindowRects? GetWindowRects(nint window)
    {
        var hwnd = new HWND((void*)window);
        if (window == 0 || !PInvoke.IsWindow(hwnd) || !PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        RECT frame;
        var hasFrame = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Succeeded;
        return new WindowRects(ToPixelRect(rect), hasFrame ? ToPixelRect(frame) : default);
    }

    public (string? Title, string? Process) DescribeWindow(nint window)
    {
        var hwnd = new HWND((void*)window);
        if (window == 0 || !PInvoke.IsWindow(hwnd))
        {
            return (null, null);
        }

        string? title = null;
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length > 0)
        {
            var buffer = new char[Math.Min(length + 1, 1024)];
            fixed (char* p = buffer)
            {
                var copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
                title = copied > 0 ? new string(p, 0, copied) : null;
            }
        }

        string? process = null;
        uint pid;
        if (PInvoke.GetWindowThreadProcessId(hwnd, &pid) != 0 && pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                process = p.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return (title, process);
    }

    public IPrecisionTimer CreateTimer() => new HighResolutionTimer();

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_ICONHAND);

    internal static PixelRect ToPixelRect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    /// <summary>
    /// A power request ("Recording the screen" in <c>powercfg /requests</c>)
    /// keeping the system and the display on. Unlike SetThreadExecutionState it
    /// is not tied to a thread, so it can be released from any thread, including
    /// during shutdown.
    /// </summary>
    private sealed class PowerRequest : IDisposable
    {
        private SafeFileHandle? _handle;
        private readonly bool _usedExecutionState;

        private PowerRequest(SafeFileHandle? handle, bool usedExecutionState)
        {
            _handle = handle;
            _usedExecutionState = usedExecutionState;
        }

        public static PowerRequest Create(string reason)
        {
            var text = Marshal.StringToHGlobalUni(reason);
            try
            {
                var context = new REASON_CONTEXT
                {
                    Version = 0, // POWER_REQUEST_CONTEXT_VERSION
                    Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                };
                context.Reason.SimpleReasonString = new PWSTR((char*)text);
                var handle = PInvoke.PowerCreateRequest(context);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new InvalidOperationException($"PowerCreateRequest failed ({Marshal.GetLastWin32Error()}).");
                }

                PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
                PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
                return new PowerRequest(handle, usedExecutionState: false);
            }
            catch (Exception ex)
            {
                Log.Warn("recorder", "Power request unavailable; using the thread execution state.", ex);
                PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
                return new PowerRequest(null, usedExecutionState: true);
            }
            finally
            {
                Marshal.FreeHGlobal(text);
            }
        }

        public void Dispose()
        {
            if (_usedExecutionState)
            {
                PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                return;
            }

            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is not null)
            {
                PInvoke.PowerClearRequest(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
                PInvoke.PowerClearRequest(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
                handle.Dispose();
            }
        }
    }
}
