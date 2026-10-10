// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Rivet.Core.Recording.Engine;

/// <summary>
/// The host clock every source is timestamped with: seconds of the
/// performance counter (QueryPerformanceCounter on Windows). Windows Graphics
/// Capture frame times and WASAPI packet positions are QPC-based too, so
/// picture, sound, pointer and keys share one timeline.
/// </summary>
public interface IHostClock
{
    /// <summary>Seconds since an arbitrary fixed point (boot on Windows).</summary>
    double Now { get; }
}

public sealed class QpcClock : IHostClock
{
    public static QpcClock Instance { get; } = new();

    public double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>QPC values that APIs report in 100-nanosecond units (frame SystemRelativeTime, WASAPI QPC position).</summary>
    public static double FromHundredNanoseconds(long value) => value / 10_000_000.0;
}

/// <summary>
/// One recording's timeline (spec 02 §3.10, §6.2): an origin set just before
/// capture starts, closed pause intervals and an optional open pause.
/// <c>Elapsed(t)</c> is host time minus the origin minus paused time; every
/// sample and event is mapped through it, and anything that starts before the
/// origin or overlaps a pause is dropped (null), so pauses vanish from every
/// track at once without stopping the capture streams. Thread-safe.
/// </summary>
public sealed class PauseClock
{
    private readonly object _gate = new();
    private readonly List<(double Start, double End)> _pauses = [];
    private double? _origin;
    private double? _openPause;

    public bool HasOrigin
    {
        get
        {
            lock (_gate)
            {
                return _origin.HasValue;
            }
        }
    }

    public double? Origin
    {
        get
        {
            lock (_gate)
            {
                return _origin;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _openPause.HasValue;
            }
        }
    }

    /// <summary>Sets the origin once; a later call (or a non-finite value) cannot move it.</summary>
    public bool Begin(double origin)
    {
        lock (_gate)
        {
            if (_origin.HasValue || !double.IsFinite(origin))
            {
                return false;
            }

            _origin = origin;
            return true;
        }
    }

    /// <summary>Accepted only after the origin is set and while not paused.</summary>
    public bool Pause(double time)
    {
        lock (_gate)
        {
            if (!_origin.HasValue || _openPause.HasValue || !double.IsFinite(time))
            {
                return false;
            }

            _openPause = Math.Max(time, _origin.Value);
            return true;
        }
    }

    /// <summary>Closes the open pause as <c>[p0, max(p0, t))</c>.</summary>
    public bool Resume(double time)
    {
        lock (_gate)
        {
            if (_openPause is not { } start)
            {
                return false;
            }

            _pauses.Add((start, double.IsFinite(time) ? Math.Max(start, time) : start));
            _openPause = null;
            return true;
        }
    }

    /// <summary>Recording time at host time <paramref name="time"/>; frozen while paused, 0 before the origin.</summary>
    public double Elapsed(double time)
    {
        lock (_gate)
        {
            return ElapsedCore(time);
        }
    }

    /// <summary>A media sample's time on the recording timeline, or null to drop it.</summary>
    public double? SampleTime(double start, double duration)
    {
        lock (_gate)
        {
            if (!_origin.HasValue || !double.IsFinite(start) || start < _origin.Value || OverlapsPause(start, start + Math.Max(0, duration)))
            {
                return null;
            }

            return ElapsedCore(start);
        }
    }

    /// <summary>An instantaneous event's time (pointer sample, click, key), or null to drop it.</summary>
    public double? EventTime(double time)
    {
        lock (_gate)
        {
            if (!_origin.HasValue || !double.IsFinite(time) || time < _origin.Value || OverlapsPause(time, time))
            {
                return null;
            }

            return ElapsedCore(time);
        }
    }

    private double ElapsedCore(double time)
    {
        if (_origin is not { } origin || !double.IsFinite(time) || time <= origin)
        {
            return 0;
        }

        return Math.Max(0, time - origin - Excluded(origin, time));
    }

    private double Excluded(double low, double high)
    {
        var total = 0.0;
        foreach (var (start, end) in _pauses)
        {
            total += Math.Max(0, Math.Min(high, end) - Math.Max(low, start));
        }

        if (_openPause is { } open)
        {
            total += Math.Max(0, high - Math.Max(low, open));
        }

        return total;
    }

    private bool OverlapsPause(double start, double end)
    {
        if (end <= start)
        {
            foreach (var (ps, pe) in _pauses)
            {
                if (start >= ps && start < pe)
                {
                    return true;
                }
            }

            return _openPause is { } open && start >= open;
        }

        foreach (var (ps, pe) in _pauses)
        {
            if (start < pe && end > ps)
            {
                return true;
            }
        }

        return _openPause is { } p0 && end > p0;
    }
}
