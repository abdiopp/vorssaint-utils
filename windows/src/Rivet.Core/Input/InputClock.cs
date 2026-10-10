// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Rivet.Core.Input;

/// <summary>
/// Monotonic time for the input pipeline, in nanoseconds. Handlers read it at
/// hook entry: <c>KBDLLHOOKSTRUCT.time</c> only has ~15.6 ms resolution, which
/// is coarser than the debounce windows (spec 07 §3.7.0 "Timestamps").
/// </summary>
public interface IInputClock
{
    long NowNs { get; }
}

/// <summary>QueryPerformanceCounter-backed clock (Stopwatch).</summary>
public sealed class SystemInputClock : IInputClock
{
    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public static SystemInputClock Instance { get; } = new();

    public long NowNs => (long)(Stopwatch.GetTimestamp() * NsPerTick);
}

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualInputClock : IInputClock
{
    private long _now = 1_000_000_000;

    public long NowNs => Interlocked.Read(ref _now);

    public void Set(long ns) => Interlocked.Exchange(ref _now, ns);

    public void AdvanceMs(double ms) => Interlocked.Add(ref _now, (long)(ms * 1_000_000));
}

public static class InputTime
{
    public const long NsPerMs = 1_000_000;

    public const long NsPerSecond = 1_000_000_000;

    public static long FromMs(double ms) => (long)(ms * NsPerMs);
}
