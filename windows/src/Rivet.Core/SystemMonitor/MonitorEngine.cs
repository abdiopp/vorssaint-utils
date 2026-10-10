// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;

namespace Rivet.Core.SystemMonitor;

/// <summary>One refresh request: the plan, which kinds are due on this tick and the foreground state.</summary>
public sealed record RefreshRequest
{
    public required SamplingPlan Plan { get; init; }

    public required IReadOnlyCollection<SampleKind> Due { get; init; }

    public bool Foreground { get; init; }

    /// <summary>Re-serve the previous GPU value (panel animations raise GPU load).</summary>
    public bool SkipGpu { get; init; }

    public int IntervalSeconds { get; init; } = 2;
}

/// <summary>
/// The synchronous core of the sampler: reads the due sensors, applies the
/// hold and bridge rules, keeps the history rings and builds the snapshot.
/// Not thread-safe; the service calls it from its worker thread only.
/// </summary>
public sealed class MonitorEngine
{
    public const double NetworkMaxGap = 10;
    public const double DiskMaxGap = 15;
    public const double MemoryHoldSeconds = 12;

    private readonly MonitorSensors _sensors;
    private readonly CpuUsageCalculator _cpu = new();
    private readonly PerCoreUsageCalculator _cores = new();
    private readonly HeldValue<double> _cpuHold = new(3);
    private readonly HeldValue<double> _gpuHold = new(3);
    private readonly TemperatureBridge _cpuTemp = new(10);
    private readonly TemperatureBridge _gpuTemp = new(10);
    private readonly TemperatureBridge _batteryTemp = new(1);
    private readonly RateSampler _network = new(NetworkMaxGap);
    private readonly Dictionary<string, RateSampler> _diskSamplers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double? Read, double? Write)> _diskRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HistoryRing _cpuHistory = new();
    private readonly HistoryRing _gpuHistory = new();
    private readonly HistoryRing _memoryHistory = new();
    private readonly HistoryRing _memoryAppHistory = new();
    private readonly HistoryRing _netDownHistory = new();
    private readonly HistoryRing _netUpHistory = new();
    private readonly HistoryRing _diskReadHistory = new();
    private readonly HistoryRing _diskWriteHistory = new();
    private readonly HistoryRing _powerHistory = new();
    private readonly HistoryRing _batteryHistory = new();

    private IReadOnlyList<double?> _coreUsage = [];
    private IReadOnlyList<GpuAdapterReading> _gpuAdapters = [];
    private MemoryReading? _memory;
    private double _memoryAt;
    private int _memoryMisses;
    private double? _netDown;
    private double? _netUp;
    private PowerReading? _power;
    private IReadOnlyList<PeripheralBattery> _peripherals = [];
    private IReadOnlyList<DiskVolumeInfo> _volumes = [];
    private IReadOnlyList<UsbDevice>? _usb;
    private bool? _hasBattery;

    public MonitorEngine(MonitorSensors sensors) => _sensors = sensors;

    /// <summary>The last GPU read (per-process usage for the app lists). Set on the worker thread.</summary>
    public GpuSample? LatestGpuSample { get; private set; }

    public bool HasBattery => _hasBattery ??= SafeHasBattery();

    public (MonitorSnapshot Snapshot, bool AnyRead) Refresh(RefreshRequest request)
    {
        var plan = request.Plan;
        var due = request.Due;
        var now = _sensors.Clock.Now;
        var anyRead = false;

        ResetFamiliesOutsidePlan(plan);

        if (due.Contains(SampleKind.Cpu))
        {
            anyRead = true;
            ReadCpu(plan, now);
        }

        if (due.Contains(SampleKind.Memory) && plan.Memory)
        {
            anyRead = true;
            ReadMemory(now);
        }

        if (due.Contains(SampleKind.Network) && plan.Network)
        {
            anyRead = true;
            ReadNetwork(now);
        }

        if (due.Contains(SampleKind.GpuUsage) && plan.GpuUsage && !request.SkipGpu)
        {
            anyRead = true;
            ReadGpu(now);
        }

        if (due.Contains(SampleKind.ConnectedDevices) && plan.ConnectedDevices)
        {
            anyRead = true;
            _usb = Safe(() => _sensors.Usb.Read(), "usb") ?? _usb;
        }

        if ((due.Contains(SampleKind.Power) && plan.Power) || (due.Contains(SampleKind.PowerDraw) && plan.PowerDraw))
        {
            anyRead = true;
            ReadPower();
        }

        if (due.Contains(SampleKind.Temperature) && plan.AnyTemperature)
        {
            anyRead = true;
            ReadTemperatures(plan, now, request);
        }

        if (due.Contains(SampleKind.Disk) && plan.Disk)
        {
            anyRead = true;
            ReadDisks(now);
        }

        if (due.Contains(SampleKind.PeripheralBattery) && plan.PeripheralBattery)
        {
            anyRead = true;
            _peripherals = Safe(() => _sensors.Peripherals.Read(now), "peripherals") ?? _peripherals;
        }

        return (Build(plan, request.Foreground, now), anyRead);
    }

    private void ResetFamiliesOutsidePlan(SamplingPlan plan)
    {
        if (!plan.Cpu)
        {
            _cpu.Reset();
            _cpuHold.Clear();
        }

        if (!plan.CpuCores)
        {
            _cores.Reset();
            _coreUsage = [];
        }

        if (!plan.Memory)
        {
            _memory = null;
            _memoryMisses = 0;
        }

        if (!plan.Network)
        {
            _network.Reset();
            _netDown = _netUp = null;
        }

        if (!plan.GpuUsage)
        {
            _gpuHold.Clear();
            _gpuAdapters = [];
            LatestGpuSample = null;
        }

        if (!plan.Power && !plan.PowerDraw)
        {
            _power = null;
        }

        if (!plan.CpuTemperature)
        {
            _cpuTemp.Clear();
        }

        if (!plan.GpuTemperature)
        {
            _gpuTemp.Clear();
        }

        if (!plan.BatteryTemperature)
        {
            _batteryTemp.Clear();
        }

        if (!plan.Disk)
        {
            foreach (var sampler in _diskSamplers.Values)
            {
                sampler.Reset();
            }

            _diskRates.Clear();
            _volumes = [];
        }

        if (!plan.PeripheralBattery)
        {
            _peripherals = [];
        }

        if (!plan.ConnectedDevices)
        {
            _usb = null;
        }
    }

    private void ReadCpu(SamplingPlan plan, double now)
    {
        if (plan.Cpu)
        {
            var ticks = Safe(() => _sensors.Cpu.ReadTotal(), "cpu");
            now = _sensors.Clock.Now;
            if (ticks is not { } t)
            {
                _cpuHold.Miss();
            }
            else
            {
                var usage = _cpu.Update(t, now);
                if (_cpu.GapReset)
                {
                    _cpuHold.Clear();
                }
                else if (usage is { } u)
                {
                    _cpuHold.Fresh(u, now);
                    _cpuHistory.Push(u);
                }
                else
                {
                    _cpuHold.Miss();
                }
            }
        }

        if (plan.CpuCores)
        {
            var cores = Safe(() => _sensors.Cpu.ReadPerCore(), "cpu cores");
            if (cores is not null)
            {
                _coreUsage = _cores.Update(cores, _sensors.Clock.Now);
            }
        }
    }

    private void ReadMemory(double now)
    {
        var sample = Safe(() => _sensors.Memory.Read(), "memory");
        if (sample is null || sample.Total == 0)
        {
            _memoryMisses++;
            if (_memoryMisses > 4 || now - _memoryAt > MemoryHoldSeconds)
            {
                _memory = null;
            }

            return;
        }

        ulong Clamp(ulong v) => Math.Min(v, sample.Total);
        var pressure = MemoryPressureHeuristic.Evaluate(sample);
        if (pressure == MemoryPressure.Unknown && _memory is { Pressure: not MemoryPressure.Unknown } previous)
        {
            pressure = previous.Pressure;
        }

        var reading = new MemoryReading
        {
            Total = sample.Total,
            Used = Clamp(sample.Total > sample.Available ? sample.Total - sample.Available : 0),
            AppUsed = sample.AppUsed is { } app ? Clamp(app) : null,
            Compressed = sample.Compressed is { } c ? Clamp(c) : null,
            Cached = sample.Cached is { } cached ? Clamp(cached) : null,
            SwapUsed = sample.SwapUsed,
            Pressure = pressure,
        };
        _memory = reading;
        _memoryAt = now;
        _memoryMisses = 0;
        _memoryHistory.Push((double)reading.Used / reading.Total);
        if (reading.AppUsed is { } appUsed)
        {
            _memoryAppHistory.Push((double)appUsed / reading.Total);
        }
    }

    private void ReadNetwork(double now)
    {
        // Timestamp each counter read itself: a slow sensor must not skew the rate.
        var counters = Safe(() => _sensors.Network.ReadCounters(), "network");
        now = _sensors.Clock.Now;
        if (counters is not { } c)
        {
            // Unavailable this tick; the previous sample stays so the next read averages the gap.
            _netDown = _netUp = null;
            return;
        }

        var (down, up) = _network.Update(c.BytesIn, c.BytesOut, now);
        _netDown = down;
        _netUp = up;
        if (down is { } d && up is { } u)
        {
            _netDownHistory.Push(d);
            _netUpHistory.Push(u);
        }
    }

    private void ReadGpu(double now)
    {
        var sample = Safe(() => _sensors.Gpu.ReadUsage(), "gpu");
        if (sample?.Usage is not { } raw)
        {
            _gpuHold.Miss();
            if (sample is not null)
            {
                _gpuAdapters = sample.Adapters;
            }

            return;
        }

        var smoothed = GpuSmoothing.Apply(_gpuHold.Value, raw);
        _gpuHold.Fresh(smoothed, now);
        _gpuHistory.Push(smoothed);
        _gpuAdapters = sample.Adapters;
        LatestGpuSample = sample;
    }

    private void ReadPower()
    {
        var reading = Safe(() => _sensors.Power.Read(), "power");
        if (reading is null)
        {
            return;
        }

        _power = reading;
        _hasBattery = reading.HasBattery || (_hasBattery ?? false);
        if (reading.SystemWatts is { } watts)
        {
            _powerHistory.Push(watts);
        }

        if (reading.HasBattery && reading.ChargePercent is { } charge)
        {
            _batteryHistory.Push(charge / 100.0);
        }
    }

    private void ReadTemperatures(SamplingPlan plan, double now, RefreshRequest request)
    {
        var stride = SamplingCadence.Stride(SampleKind.Temperature, request.IntervalSeconds, request.Foreground);
        var bridge = TemperatureBridge.BridgeSeconds(stride, request.IntervalSeconds);
        if (plan.CpuTemperature)
        {
            _cpuTemp.Update(Safe(() => _sensors.Temperature.ReadCpuTemperature(), "cpu temperature"), now, bridge);
        }

        if (plan.GpuTemperature)
        {
            _gpuTemp.Update(Safe(() => _sensors.Gpu.ReadTemperature(), "gpu temperature"), now, bridge);
        }

        if (plan.BatteryTemperature)
        {
            _batteryTemp.Update(Safe(() => _sensors.Power.ReadBatteryTemperature(), "battery temperature"), now, bridge);
        }
    }

    private void ReadDisks(double now)
    {
        var volumes = Safe(() => _sensors.Disk.ReadVolumes(), "disk volumes");
        if (volumes is not null)
        {
            _volumes = SortVolumes(volumes.Where(v => v.Total > 0));
        }

        var diskIds = _volumes.Select(v => v.DiskId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (diskIds.Count == 0)
        {
            _diskRates.Clear();
            return;
        }

        var counters = Safe(() => _sensors.Disk.ReadCounters(diskIds), "disk counters");
        now = _sensors.Clock.Now;
        if (counters is null)
        {
            _diskRates.Clear();
            return;
        }

        double? readSum = null;
        double? writeSum = null;
        foreach (var id in diskIds)
        {
            if (!counters.TryGetValue(id, out var c))
            {
                _diskRates.Remove(id);
                continue;
            }

            if (!_diskSamplers.TryGetValue(id, out var sampler))
            {
                _diskSamplers[id] = sampler = new RateSampler(DiskMaxGap);
            }

            var (read, write) = sampler.Update(c.BytesRead, c.BytesWritten, now);
            _diskRates[id] = (read, write);
            if (read is { } r)
            {
                readSum = (readSum ?? 0) + r;
            }

            if (write is { } w)
            {
                writeSum = (writeSum ?? 0) + w;
            }
        }

        if (readSum is { } rs && writeSum is { } ws)
        {
            _diskReadHistory.Push(rs);
            _diskWriteHistory.Push(ws);
        }
    }

    private MonitorSnapshot Build(SamplingPlan plan, bool foreground, double now)
    {
        var disks = _volumes.Select(v =>
        {
            var rates = v.DiskId is { } id && _diskRates.TryGetValue(id, out var r) ? r : (null, null);
            var sampler = v.DiskId is { } did && _diskSamplers.TryGetValue(did, out var s) ? s : null;
            return new DiskVolume
            {
                Info = v,
                ReadRate = rates.Read,
                WriteRate = rates.Write,
                SessionRead = sampler?.TotalA ?? 0,
                SessionWritten = sampler?.TotalB ?? 0,
            };
        }).ToList();

        var unique = disks.Where(d => d.Info.DiskId is not null).GroupBy(d => d.Info.DiskId!, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        double? readSum = unique.Any(d => d.ReadRate is not null) ? unique.Sum(d => d.ReadRate ?? 0) : null;
        double? writeSum = unique.Any(d => d.WriteRate is not null) ? unique.Sum(d => d.WriteRate ?? 0) : null;

        return new MonitorSnapshot
        {
            Plan = plan,
            Foreground = foreground,
            Timestamp = now,
            CpuUsage = plan.Cpu ? _cpuHold.Value : null,
            CpuUsageReadAt = plan.Cpu ? _cpuHold.ReadAt : null,
            CpuCoreUsage = plan.CpuCores ? _coreUsage : [],
            CoreGroups = plan.CpuCores ? SafeTopology() : [],
            GpuUsage = plan.GpuUsage ? _gpuHold.Value : null,
            GpuAdapters = plan.GpuUsage ? _gpuAdapters : [],
            Memory = plan.Memory ? _memory : null,
            CpuTemperature = plan.CpuTemperature ? _cpuTemp.Value : null,
            CpuTemperatureReadAt = plan.CpuTemperature ? _cpuTemp.ReadAt : null,
            CpuTemperatureAvailability = plan.CpuTemperature ? SafeCpuAvailability() : SensorAvailability.Available,
            GpuTemperature = plan.GpuTemperature ? _gpuTemp.Value : null,
            BatteryTemperature = plan.BatteryTemperature ? _batteryTemp.Value : null,
            BatteryTemperatureReadAt = plan.BatteryTemperature ? _batteryTemp.ReadAt : null,
            NetDownBytesPerSec = plan.Network ? _netDown : null,
            NetUpBytesPerSec = plan.Network ? _netUp : null,
            NetTotalDown = _network.TotalA,
            NetTotalUp = _network.TotalB,
            Power = plan.Power || plan.PowerDraw ? _power : null,
            PeripheralBatteries = plan.PeripheralBattery ? _peripherals : [],
            Disks = plan.Disk ? disks : [],
            DiskReadBytesPerSec = plan.Disk ? readSum : null,
            DiskWriteBytesPerSec = plan.Disk ? writeSum : null,
            ConnectedDevices = plan.ConnectedDevices ? _usb : null,
            Uptime = SafeUptime(),
            HasBattery = HasBattery,
            Histories = foreground ? Histories(plan) : MonitorHistories.Empty,
        };
    }

    private MonitorHistories Histories(SamplingPlan plan) => new()
    {
        Cpu = plan.Cpu ? _cpuHistory.ToArray() : [],
        Gpu = plan.GpuUsage ? _gpuHistory.ToArray() : [],
        Memory = plan.Memory ? _memoryHistory.ToArray() : [],
        MemoryApp = plan.Memory ? _memoryAppHistory.ToArray() : [],
        NetDown = plan.Network ? _netDownHistory.ToArray() : [],
        NetUp = plan.Network ? _netUpHistory.ToArray() : [],
        DiskRead = plan.Disk ? _diskReadHistory.ToArray() : [],
        DiskWrite = plan.Disk ? _diskWriteHistory.ToArray() : [],
        SystemPower = plan.Power || plan.PowerDraw ? _powerHistory.ToArray() : [],
        Battery = plan.Power ? _batteryHistory.ToArray() : [],
    };

    /// <summary>Internal volumes first, then the system volume, then natural name order.</summary>
    public static List<DiskVolumeInfo> SortVolumes(IEnumerable<DiskVolumeInfo> volumes) =>
        volumes
            .OrderByDescending(v => v.IsInternal)
            .ThenByDescending(v => v.IsSystem)
            .ThenBy(v => v.Name, NaturalComparer.Instance)
            .ThenBy(v => v.MountPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<CoreGroup> SafeTopology()
    {
        try
        {
            return _sensors.Cpu.Topology;
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", "CPU topology failed.", ex);
            return [];
        }
    }

    private SensorAvailability SafeCpuAvailability()
    {
        try
        {
            return _sensors.Temperature.CpuAvailability;
        }
        catch (Exception)
        {
            return SensorAvailability.NotReported;
        }
    }

    private TimeSpan SafeUptime()
    {
        try
        {
            return _sensors.Clock.Uptime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    private bool SafeHasBattery()
    {
        try
        {
            return _sensors.Power.HasBattery;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sensor reads never take the sampler down: an exception counts as a failed read.</summary>
    private static T? Safe<T>(Func<T?> read, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", $"Reading {what} failed.", ex);
            return default;
        }
    }
}

/// <summary>Orders "Disk 2" before "Disk 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0) return byLength;
                var byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0) return byDigits;
                continue;
            }

            var c = string.Compare(x[i].ToString(), y[j].ToString(), StringComparison.CurrentCultureIgnoreCase);
            if (c != 0) return c;
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
