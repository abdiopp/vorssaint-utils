// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>
/// Whole-machine CPU usage from cumulative counters (spec §3.2):
/// usage = Δbusy / Δtotal. The first read is a baseline (null); a read
/// more than 12.5 s after the previous one starts a new baseline and tells
/// the caller to drop the held value, so a sleep never shows a stale spike.
/// </summary>
public sealed class CpuUsageCalculator
{
    public const double MaxGapSeconds = 12.5;

    private CpuTicks? _previous;
    private double _previousAt;

    /// <summary>The last call reset the baseline because of a long gap.</summary>
    public bool GapReset { get; private set; }

    public double? Update(CpuTicks ticks, double now)
    {
        GapReset = false;
        if (_previous is not { } previous)
        {
            _previous = ticks;
            _previousAt = now;
            return null;
        }

        if (now - _previousAt > MaxGapSeconds)
        {
            _previous = ticks;
            _previousAt = now;
            GapReset = true;
            return null;
        }

        _previous = ticks;
        _previousAt = now;
        if (ticks.Total <= previous.Total || ticks.Busy < previous.Busy)
        {
            return null;
        }

        var busy = (double)(ticks.Busy - previous.Busy);
        var total = (double)(ticks.Total - previous.Total);
        return Math.Clamp(busy / total, 0, 1);
    }

    public void Reset() => _previous = null;
}

/// <summary>
/// Per logical core usage (spec §3.2). Counters are treated as unsigned and
/// may be 32-bit: a large backwards step is a wrap (modular difference), a
/// small one a reset (null for that core). A gap over 12.5 s or a change in
/// core count starts a new baseline.
/// </summary>
public sealed class PerCoreUsageCalculator
{
    private const double MaxGapSeconds = 12.5;

    private readonly bool _thirtyTwoBit;
    private CpuTicks[]? _previous;
    private double _previousAt;

    public PerCoreUsageCalculator(bool thirtyTwoBitCounters = false) => _thirtyTwoBit = thirtyTwoBitCounters;

    public IReadOnlyList<double?> Update(IReadOnlyList<CpuTicks> cores, double now)
    {
        var previous = _previous;
        var gap = now - _previousAt;
        _previous = cores.ToArray();
        _previousAt = now;
        if (previous is null || previous.Length != cores.Count || gap > MaxGapSeconds)
        {
            return new double?[cores.Count];
        }

        var result = new double?[cores.Count];
        for (var i = 0; i < cores.Count; i++)
        {
            var total = Delta(previous[i].Total, cores[i].Total);
            var busy = Delta(previous[i].Busy, cores[i].Busy);
            if (total is not { } t || busy is not { } b || t == 0)
            {
                continue;
            }

            result[i] = Math.Clamp((double)b / t, 0, 1);
        }

        return result;
    }

    public void Reset() => _previous = null;

    private ulong? Delta(ulong previous, ulong current)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        if (!_thirtyTwoBit)
        {
            return null;
        }

        // 32-bit counters: a small backwards step is a reset, a large one a wrap-around.
        var back = previous - current;
        return back < 0x8000_0000UL ? null : (ulong)((uint)current - (uint)previous);
    }
}

/// <summary>GPU anti-spike smoothing (spec §3.3): cap upward jumps at +0.20, fall with a 0.35/0.65 blend.</summary>
public static class GpuSmoothing
{
    public static double Apply(double? previous, double raw)
    {
        var value = Math.Clamp(double.IsFinite(raw) ? raw : 0, 0, 1);
        if (previous is not { } prev)
        {
            return value;
        }

        return value > prev ? Math.Min(value, prev + 0.20) : (0.35 * prev) + (0.65 * value);
    }
}

/// <summary>
/// Rates and session totals from cumulative counters (network, disk; spec §6.7).
/// rate = Δ/elapsed when the counter grew (0 for a backwards step); a sample
/// older than <c>maxGap</c> is a new baseline (no rate, nothing added to the
/// totals). A failed read does not replace the previous sample, so the next
/// good read averages over the short gap.
/// </summary>
public sealed class RateSampler
{
    private readonly double _maxGap;
    private (ulong A, ulong B)? _previous;
    private double _previousAt;

    public RateSampler(double maxGapSeconds) => _maxGap = maxGapSeconds;

    public ulong TotalA { get; private set; }

    public ulong TotalB { get; private set; }

    /// <summary>Returns (rateA, rateB) or nulls while measuring.</summary>
    public (double? A, double? B) Update(ulong a, ulong b, double now)
    {
        if (_previous is not { } previous || now - _previousAt > _maxGap)
        {
            _previous = (a, b);
            _previousAt = now;
            return (null, null);
        }

        if (now <= _previousAt)
        {
            // No time elapsed: keep the baseline for the next read.
            return (null, null);
        }

        var elapsed = now - _previousAt;
        var deltaA = a >= previous.A ? a - previous.A : 0;
        var deltaB = b >= previous.B ? b - previous.B : 0;
        TotalA += deltaA;
        TotalB += deltaB;
        _previous = (a, b);
        _previousAt = now;
        return (deltaA / elapsed, deltaB / elapsed);
    }

    /// <summary>Forget the baseline (the family stopped being sampled). Totals are kept.</summary>
    public void Reset() => _previous = null;
}

/// <summary>
/// "Sustained for 12 s" gate for CPU usage and temperatures (spec §3.15).
/// Keys on the time of the last real read: a re-served value never ages
/// into an alert, and dropping below the threshold restarts the window.
/// </summary>
public sealed class SustainedGate
{
    public const double SustainSeconds = 12;

    private double? _lastReadingAt;
    private double? _heldSince;

    public bool ShouldAlert(double? reading, double threshold, double? readAt)
    {
        if (reading is not { } value || readAt is not { } at || value < threshold)
        {
            Reset();
            return false;
        }

        if (_lastReadingAt == at)
        {
            return false;
        }

        _lastReadingAt = at;
        if (_heldSince is not { } since)
        {
            _heldSince = at;
            return false;
        }

        return at - since >= SustainSeconds;
    }

    public void Reset()
    {
        _lastReadingAt = null;
        _heldSince = null;
    }
}

/// <summary>Holds the last good value through a few failed reads, then clears it.</summary>
public sealed class HeldValue<T>
    where T : struct
{
    private readonly int _maxMisses;
    private int _misses;

    public HeldValue(int maxMisses) => _maxMisses = maxMisses;

    public T? Value { get; private set; }

    /// <summary>Monotonic time of the last real read (what alerts key on).</summary>
    public double? ReadAt { get; private set; }

    public void Fresh(T? value, double now)
    {
        Value = value;
        ReadAt = value is null ? null : now;
        _misses = 0;
    }

    public void Miss()
    {
        if (++_misses > _maxMisses)
        {
            Value = null;
            ReadAt = null;
        }
    }

    public void Clear()
    {
        Value = null;
        ReadAt = null;
        _misses = 0;
    }
}

/// <summary>
/// Temperature stabilization (spec §3.5): a reading is accepted when
/// 1 &lt; v, v ≥ minimum and v &lt; 125; otherwise the cache is re-served for
/// up to 4 misses while younger than the bridge window. The cache time moves
/// only on real reads.
/// </summary>
public sealed class TemperatureBridge
{
    private readonly double _minimum;
    private int _misses;

    public TemperatureBridge(double minimum) => _minimum = minimum;

    public double? Value { get; private set; }

    public double? ReadAt { get; private set; }

    public static double BridgeSeconds(int temperatureStride, int intervalSeconds) =>
        Math.Max(12, 2.2 * temperatureStride * intervalSeconds);

    public double? Update(double? reading, double now, double bridgeSeconds)
    {
        if (reading is { } v && double.IsFinite(v) && v > 1 && v >= _minimum && v < 125)
        {
            Value = v;
            ReadAt = now;
            _misses = 0;
            return v;
        }

        _misses++;
        if (Value is not null && ReadAt is { } at && _misses <= 4 && now - at <= bridgeSeconds)
        {
            return Value;
        }

        Value = null;
        ReadAt = null;
        return null;
    }

    public void Clear()
    {
        Value = null;
        ReadAt = null;
        _misses = 0;
    }
}

/// <summary>
/// Windows has no memory pressure level. This heuristic stands in for it
/// (documented in docs/modules/systemMonitor.md):
/// <list type="bullet">
/// <item>Critical: Windows' low-memory resource notification is signaled, or
/// less than 5 % of RAM is available, or the commit charge reaches 95 % of
/// the commit limit (allocations are about to fail).</item>
/// <item>Warning: less than 15 % of RAM available, or commit at 85 % or more.</item>
/// <item>Normal otherwise; Unknown without figures.</item>
/// </list>
/// </summary>
public static class MemoryPressureHeuristic
{
    public const double CriticalAvailable = 0.05;
    public const double WarningAvailable = 0.15;
    public const double CriticalCommit = 0.95;
    public const double WarningCommit = 0.85;

    public static MemoryPressure Evaluate(MemorySample sample)
    {
        if (sample.Total == 0)
        {
            return MemoryPressure.Unknown;
        }

        var available = (double)sample.Available / sample.Total;
        var commit = sample.CommitLimit > 0 ? (double)sample.CommitTotal / sample.CommitLimit : 0;
        if (sample.LowMemorySignaled || available < CriticalAvailable || commit >= CriticalCommit)
        {
            return MemoryPressure.Critical;
        }

        return available < WarningAvailable || commit >= WarningCommit ? MemoryPressure.Warning : MemoryPressure.Normal;
    }
}

/// <summary>Battery rules (spec §6.11).</summary>
public static class BatteryMath
{
    /// <summary>Windows reports 0xFFFFFFFF (−1) for "unknown"; macOS used 65,535.</summary>
    public static double? ValidTimeRemaining(double? seconds, bool externalConnected, bool charging)
    {
        if (seconds is not { } s || externalConnected || charging || s < 0)
        {
            return null;
        }

        var minutes = s / 60;
        return minutes is >= 1 and < 10_080 ? s : null;
    }

    /// <summary>min(100, full / design × 100), null without both capacities.</summary>
    public static int? Health(double? fullChargeCapacity, double? designCapacity)
    {
        if (fullChargeCapacity is not { } full || designCapacity is not { } design || full <= 0 || design <= 0)
        {
            return null;
        }

        return (int)Math.Clamp(MetricFormat.Round(full / design * 100, 0), 1, 100);
    }

    /// <summary>The whole-machine draw on battery: the discharge rate when not on external power.</summary>
    public static double? SystemWattsFallback(double? batteryWatts, bool externalConnected) =>
        !externalConnected && batteryWatts is < 0 and var w ? -w : null;

    /// <summary>Menu bar glyph level: 100/75/50/25/0 for ≥85/60–84/35–59/10–34/&lt;10.</summary>
    public static int GlyphLevel(int percent) => percent switch
    {
        >= 85 => 100,
        >= 60 => 75,
        >= 35 => 50,
        >= 10 => 25,
        _ => 0,
    };

    /// <summary>Panel charge bar tint: red &lt; 20, yellow &lt; 40, else green.</summary>
    public static MetricTone ChargeTone(int percent) => percent < 20 ? MetricTone.Critical : percent < 40 ? MetricTone.Elevated : MetricTone.Good;
}

/// <summary>Semantic colour of a value; views map it to theme brushes.</summary>
public enum MetricTone
{
    Normal,
    Good,
    Elevated,
    Critical,
}

public static class UsageTone
{
    /// <summary>CPU/GPU usage bar: accent under 60 %, yellow under 85 %, red from 85 %.</summary>
    public static MetricTone ForUsage(double fraction) => fraction < 0.60 ? MetricTone.Normal : fraction < 0.85 ? MetricTone.Elevated : MetricTone.Critical;

    /// <summary>Disk usage bar: accent under 75 %, yellow under 90 %, red from 90 %.</summary>
    public static MetricTone ForDisk(double fraction) => fraction < 0.75 ? MetricTone.Normal : fraction < 0.90 ? MetricTone.Elevated : MetricTone.Critical;

    public static MetricTone ForPressure(MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Normal => MetricTone.Good,
        MemoryPressure.Warning => MetricTone.Elevated,
        MemoryPressure.Critical => MetricTone.Critical,
        _ => MetricTone.Normal,
    };
}

/// <summary>A fixed-capacity history of fresh reads (spec §3.14), oldest first.</summary>
public sealed class HistoryRing
{
    public const int DefaultCapacity = 120;

    private readonly double[] _items;
    private int _start;

    public HistoryRing(int capacity = DefaultCapacity) => _items = new double[capacity];

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public void Push(double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = value;
            Count++;
        }
        else
        {
            _items[_start] = value;
            _start = (_start + 1) % _items.Length;
        }
    }

    public double[] ToArray()
    {
        var result = new double[Count];
        for (var i = 0; i < Count; i++)
        {
            result[i] = _items[(_start + i) % _items.Length];
        }

        return result;
    }

    public void Clear()
    {
        Count = 0;
        _start = 0;
    }
}
