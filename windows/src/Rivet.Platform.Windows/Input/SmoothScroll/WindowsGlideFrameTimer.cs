// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Rivet.Platform.Windows.Input.SmoothScroll;

/// <summary>
/// Paces smooth-scroll frames on a dedicated thread with a high-resolution
/// waitable timer (Windows 10 1803+; a normal waitable timer before that) at
/// the refresh rate of the monitor under the pointer. The thread sleeps on an
/// event while no glide runs. Elapsed time comes from QueryPerformanceCounter.
/// </summary>
public sealed unsafe class WindowsGlideFrameTimer : IGlideFrameTimer, IDisposable
{
    private const uint HighResolutionTimer = 0x00000002; // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
    private const uint TimerAllAccess = 0x1F0003;

    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private Func<double, bool>? _callback;
    private Thread? _thread;
    private volatile bool _disposed;

    public void Start(Func<double, bool> onFrame)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _callback, onFrame);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "SmoothScroll", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }

        _wake.Set();
    }

    public void Stop() => Volatile.Write(ref _callback, null);

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _wake.Set();
    }

    private void Run()
    {
        var timer = PInvoke.CreateWaitableTimerEx(null, (string?)null, HighResolutionTimer, TimerAllAccess);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            timer = PInvoke.CreateWaitableTimerEx(null, (string?)null, 0, TimerAllAccess);
        }

        try
        {
            while (!_disposed)
            {
                if (Volatile.Read(ref _callback) is null)
                {
                    _wake.WaitOne();
                    continue;
                }

                var interval = 1.0 / RefreshRateUnderPointer();
                var last = Stopwatch.GetTimestamp();
                var first = true;
                while (!_disposed && Volatile.Read(ref _callback) is { } callback)
                {
                    WaitFor(timer, interval);
                    var now = Stopwatch.GetTimestamp();
                    var elapsed = first ? interval : (now - last) / (double)Stopwatch.Frequency;
                    first = false;
                    last = now;
                    bool more;
                    try
                    {
                        more = callback(elapsed);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("input", "Smooth-scroll frame failed.", ex);
                        more = false;
                    }

                    if (!more)
                    {
                        Interlocked.CompareExchange(ref _callback, null, callback);
                    }
                }
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static void WaitFor(Microsoft.Win32.SafeHandles.SafeFileHandle timer, double seconds)
    {
        if (timer.IsInvalid)
        {
            Thread.Sleep(Math.Max(1, (int)Math.Round(seconds * 1000)));
            return;
        }

        // Negative due time = relative, in 100 ns units.
        var due = -(long)(seconds * 10_000_000);
        if (!PInvoke.SetWaitableTimer(timer, due, 0, null, null, false))
        {
            Thread.Sleep(Math.Max(1, (int)Math.Round(seconds * 1000)));
            return;
        }

        PInvoke.WaitForSingleObject(timer, 100);
    }

    /// <summary>The refresh rate of the monitor under the pointer (24–500 Hz, 60 when unknown).</summary>
    private static double RefreshRateUnderPointer()
    {
        try
        {
            if (!PInvoke.GetCursorPos(out var point))
            {
                return 60;
            }

            var monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                return 60;
            }

            var mode = new DEVMODEW { dmSize = (ushort)sizeof(DEVMODEW) };
            if (!PInvoke.EnumDisplaySettings(info.szDevice.ToString(), ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode))
            {
                return 60;
            }

            var hz = (int)mode.dmDisplayFrequency;
            return hz is >= 24 and <= 500 ? hz : 60;
        }
        catch (Exception)
        {
            return 60;
        }
    }
}
