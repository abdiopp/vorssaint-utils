// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Recording.Engine;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Rivet.Platform.Windows.Recording;

/// <summary>A waitable timer with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+): ~0.5 ms accuracy without raising the global timer rate.</summary>
internal sealed unsafe class HighResolutionTimer : IPrecisionTimer
{
    private SafeFileHandle? _timer;

    public HighResolutionTimer()
    {
        var timer = PInvoke.CreateWaitableTimerEx(null, (string?)null, PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, (uint)SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_ALL_ACCESS);
        if (timer.IsInvalid)
        {
            timer.Dispose();
        }
        else
        {
            _timer = timer;
        }
    }

    public void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        var timer = _timer;
        if (timer is null)
        {
            Thread.Sleep(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
            return;
        }

        var due = -Math.Max(1, duration.Ticks); // relative, 100 ns units
        if (!PInvoke.SetWaitableTimer(timer, due, 0, null, null, false))
        {
            Thread.Sleep(Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds)));
            return;
        }

        PInvoke.WaitForSingleObject(timer, (uint)Math.Ceiling(duration.TotalMilliseconds) + 50);
    }

    public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();
}
