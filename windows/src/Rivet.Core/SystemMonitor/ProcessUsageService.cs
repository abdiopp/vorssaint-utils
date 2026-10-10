// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.SystemMonitor;

public enum ProcessListKind
{
    Cpu,
    Gpu,
    Memory,
    Energy,
}

/// <summary>One app in a per-app list (helpers rolled up into their app).</summary>
public sealed record ProcessUsageRow
{
    /// <summary>Group key (the executable name, lower case).</summary>
    public required string Key { get; init; }

    /// <summary>The app's own process (root of its group): target for activation and force kill.</summary>
    public required int Pid { get; init; }

    public required string Name { get; init; }

    /// <summary>Percent (CPU, GPU, energy) or bytes (memory).</summary>
    public required double Value { get; init; }

    /// <summary>Creation time of <see cref="Pid"/>; set only when the process existed before the sample started.</summary>
    public long? StartTime { get; init; }

    public string ImageName { get; init; } = string.Empty;

    public string? ExecutablePath { get; init; }

    public bool HasWindow { get; init; }
}

/// <summary>
/// Per-app breakdowns (spec §3.13), computed off the UI thread while a list
/// is on screen. Windows has no "responsible process": processes are grouped
/// by executable name (all chrome.exe processes are one Chrome row) and the
/// group's root process represents the app. Values are summed per group;
/// CPU and GPU rows are reconciled so they never add up to more than the
/// machine-wide figure.
/// </summary>
public sealed class ProcessUsageService : IDisposable
{
    public const int PanelLimit = 15;

    private readonly SystemMonitorService _monitor;
    private readonly ISettingsStore _settings;
    private readonly IProcessSampler _sampler;
    private readonly object _gate = new();
    private readonly Dictionary<ProcessListKind, int> _leases = [];
    private readonly Dictionary<ProcessListKind, IReadOnlyList<ProcessUsageRow>> _rows = [];
    private readonly Dictionary<ProcessListKind, double> _refreshedAt = [];
    private Dictionary<int, (long Cpu, long Create)>? _previousCpu;
    private double _previousCpuAt;
    private IReadOnlyList<ProcessUsageRow> _cpuAll = [];
    private bool _inFlight;

    public ProcessUsageService(SystemMonitorService monitor, ISettingsStore settings)
    {
        _monitor = monitor;
        _settings = settings;
        _sampler = monitor.Sensors.Processes;
        _monitor.SnapshotPublished += OnSnapshot;
    }

    /// <summary>Raised on the UI thread after rows changed.</summary>
    public event EventHandler? RowsChanged;

    /// <summary>Keeps <paramref name="kind"/> refreshing while the list is visible.</summary>
    public IDisposable Acquire(ProcessListKind kind)
    {
        lock (_gate)
        {
            _leases[kind] = _leases.GetValueOrDefault(kind) + 1;
            _refreshedAt.Remove(kind);
        }

        Schedule(force: true);
        return new Token(() =>
        {
            lock (_gate)
            {
                var count = _leases.GetValueOrDefault(kind) - 1;
                if (count <= 0)
                {
                    _leases.Remove(kind);
                    _rows.Remove(kind);
                }
                else
                {
                    _leases[kind] = count;
                }

                if (_leases.Count == 0)
                {
                    // Nothing visible: drop caches and the CPU baseline.
                    _previousCpu = null;
                    _cpuAll = [];
                }
            }
        });
    }

    /// <summary>Current rows, or null while the first measurement is running ("Measuring…").</summary>
    public IReadOnlyList<ProcessUsageRow>? Rows(ProcessListKind kind)
    {
        lock (_gate)
        {
            return _rows.GetValueOrDefault(kind);
        }
    }

    private double RefreshInterval(ProcessListKind kind) =>
        kind == ProcessListKind.Memory ? 4 : _settings.Get(MonitorSettings.IntervalSeconds);

    private void OnSnapshot(object? sender, MonitorSnapshot e) => Schedule(force: false);

    private void Schedule(bool force)
    {
        List<ProcessListKind> due;
        var now = _monitor.Sensors.Clock.Now;
        lock (_gate)
        {
            if (_inFlight || _leases.Count == 0)
            {
                return;
            }

            due = _leases.Keys
                .Where(k => force || !_refreshedAt.TryGetValue(k, out var at) || now - at >= 0.8 * RefreshInterval(k))
                .ToList();
            if (due.Count == 0)
            {
                return;
            }

            _inFlight = true;
        }

        var aggregateCpu = _monitor.Latest.CpuUsage;
        var aggregateGpu = _monitor.Latest.GpuUsage;
        var gpu = _monitor.LatestGpuSample;
        var logical = Math.Max(1, _monitor.Sensors.Cpu.LogicalProcessorCount);
        var interval = _settings.Get(MonitorSettings.IntervalSeconds);
        Task.Run(() =>
        {
            Dictionary<ProcessListKind, IReadOnlyList<ProcessUsageRow>?> results = [];
            try
            {
                var processes = _sampler.Snapshot();
                if (processes is not null)
                {
                    var at = _monitor.Sensors.Clock.Now;
                    foreach (var kind in due)
                    {
                        results[kind] = kind switch
                        {
                            ProcessListKind.Cpu => ComputeCpu(processes, at, aggregateCpu, logical, interval, PanelLimit),
                            ProcessListKind.Memory => ComputeMemory(processes, PanelLimit),
                            ProcessListKind.Gpu => ComputeGpu(processes, gpu, aggregateGpu, PanelLimit),
                            ProcessListKind.Energy => ComputeEnergy(processes, at, aggregateCpu, aggregateGpu, gpu, logical, interval, PanelLimit),
                            _ => null,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("monitor", "Per-app lists failed.", ex);
            }

            UiThread.Post(() =>
            {
                var changed = false;
                lock (_gate)
                {
                    _inFlight = false;
                    var stamp = _monitor.Sensors.Clock.Now;
                    foreach (var (kind, rows) in results)
                    {
                        if (!_leases.ContainsKey(kind))
                        {
                            continue;
                        }

                        _refreshedAt[kind] = stamp;
                        if (rows is not null)
                        {
                            _rows[kind] = rows;
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    RowsChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        });
    }

    private IReadOnlyList<ProcessUsageRow>? ComputeCpu(IReadOnlyList<ProcessSample> processes, double now, double? aggregate, int logical, int interval, int limit)
    {
        var rows = CpuRows(processes, now, aggregate, logical, interval);
        return rows?.Take(limit).ToList();
    }

    /// <summary>All CPU rows (unlimited), or null while priming the baseline.</summary>
    private IReadOnlyList<ProcessUsageRow>? CpuRows(IReadOnlyList<ProcessSample> processes, double now, double? aggregate, int logical, int interval)
    {
        Dictionary<int, (long Cpu, long Create)>? previous;
        double previousAt;
        lock (_gate)
        {
            previous = _previousCpu;
            previousAt = _previousCpuAt;
        }

        var elapsed = now - previousAt;
        if (previous is not null && elapsed < Math.Max(0.5, 0.8 * interval))
        {
            // Too soon for a meaningful delta: keep the current rows.
            lock (_gate)
            {
                return _cpuAll.Count > 0 ? _cpuAll : null;
            }
        }

        var current = processes.ToDictionary(p => p.Pid, p => (p.CpuTime, p.CreateTime));
        lock (_gate)
        {
            _previousCpu = current;
            _previousCpuAt = now;
        }

        if (previous is null || elapsed > Math.Max(30, 4.0 * interval))
        {
            return null;
        }

        var deltas = new Dictionary<int, double>();
        foreach (var p in processes)
        {
            if (p.Pid == 0 || !previous.TryGetValue(p.Pid, out var before) || before.Create != p.CreateTime)
            {
                continue;
            }

            var delta = Math.Max(0, p.CpuTime - before.Cpu);
            var percent = ProcessCpuMath.Percent(delta, elapsed, logical);
            if (percent >= 0.01)
            {
                deltas[p.Pid] = percent;
            }
        }

        var grouped = Group(processes, deltas, existedBefore: pid => previous.ContainsKey(pid));
        var rows = ProcessCpuMath.Reconcile(grouped, aggregate is { } a ? a * 100 : null);
        lock (_gate)
        {
            _cpuAll = rows;
        }

        return rows;
    }

    private IReadOnlyList<ProcessUsageRow> ComputeMemory(IReadOnlyList<ProcessSample> processes, int limit)
    {
        var values = processes.Where(p => p.Pid != 0 && p.PrivateWorkingSet > 0).ToDictionary(p => p.Pid, p => (double)p.PrivateWorkingSet);
        return Group(processes, values, existedBefore: _ => true).OrderByDescending(r => r.Value).Take(limit).ToList();
    }

    private IReadOnlyList<ProcessUsageRow>? ComputeGpu(IReadOnlyList<ProcessSample> processes, GpuSample? gpu, double? aggregate, int limit)
    {
        if (gpu is null)
        {
            return null;
        }

        var values = gpu.ProcessUsage.Where(kv => kv.Value >= 0.05).ToDictionary(kv => kv.Key, kv => Math.Min(100, kv.Value));
        var grouped = Group(processes, values, existedBefore: _ => true);
        return ProcessCpuMath.Reconcile(grouped, aggregate is { } a ? a * 100 : null).Take(limit).ToList();
    }

    private IReadOnlyList<ProcessUsageRow>? ComputeEnergy(IReadOnlyList<ProcessSample> processes, double now, double? aggregateCpu, double? aggregateGpu, GpuSample? gpu, int logical, int interval, int limit)
    {
        var candidates = Math.Max(3 * limit, 12);
        var cpu = CpuRows(processes, now, aggregateCpu, logical, interval);
        if (cpu is null)
        {
            return null;
        }

        var gpuRows = ComputeGpu(processes, gpu, aggregateGpu, candidates) ?? [];
        return ProcessCpuMath.Energy(cpu.Take(candidates), gpuRows).Take(limit).ToList();
    }

    /// <summary>Groups per-pid values by executable name; the group's root process is the owner.</summary>
    private List<ProcessUsageRow> Group(IReadOnlyList<ProcessSample> processes, IReadOnlyDictionary<int, double> values, Func<int, bool> existedBefore)
    {
        var byPid = processes.ToDictionary(p => p.Pid);
        var rows = new List<ProcessUsageRow>();
        foreach (var group in values.Where(kv => byPid.ContainsKey(kv.Key)).GroupBy(kv => byPid[kv.Key].ImageName.ToLowerInvariant()))
        {
            var members = group.Select(kv => byPid[kv.Key]).ToList();
            var allInGroup = processes.Where(p => string.Equals(p.ImageName, members[0].ImageName, StringComparison.OrdinalIgnoreCase)).ToList();
            var owner = allInGroup
                .Where(p => !allInGroup.Any(o => o.Pid == p.ParentPid && o.Pid != p.Pid))
                .OrderByDescending(p => values.GetValueOrDefault(p.Pid))
                .ThenBy(p => p.CreateTime)
                .FirstOrDefault() ?? members[0];
            var description = SafeDescribe(owner);
            rows.Add(new ProcessUsageRow
            {
                Key = group.Key,
                Pid = owner.Pid,
                Name = description?.DisplayName ?? DisplayFallback(owner),
                Value = group.Sum(kv => kv.Value),
                StartTime = existedBefore(owner.Pid) && owner.CreateTime != 0 ? owner.CreateTime : null,
                ImageName = owner.ImageName,
                ExecutablePath = description?.ExecutablePath,
                HasWindow = description?.HasWindow ?? false,
            });
        }

        return rows;
    }

    private AppDescription? SafeDescribe(ProcessSample process)
    {
        try
        {
            return _sampler.Describe(process);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Process name, then executable file name, then "pid N".</summary>
    public static string DisplayFallback(ProcessSample process)
    {
        var name = Path.GetFileNameWithoutExtension(process.ImageName);
        return string.IsNullOrWhiteSpace(name) ? $"pid {process.Pid}" : name;
    }

    public void Dispose() => _monitor.SnapshotPublished -= OnSnapshot;

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>Pure per-process math (spec §6.2), separated for tests.</summary>
public static class ProcessCpuMath
{
    /// <summary>Share of the whole machine: Δ(100 ns) / (elapsed × 1e7 × logical CPUs) × 100.</summary>
    public static double Percent(long delta100ns, double elapsedSeconds, int logicalCpus) =>
        elapsedSeconds <= 0 || logicalCpus <= 0 ? 0 : delta100ns / (elapsedSeconds * 1e7 * logicalCpus) * 100;

    /// <summary>Scales rows so their sum never exceeds the aggregate, clamps 0…100 and sorts descending.</summary>
    public static List<ProcessUsageRow> Reconcile(IEnumerable<ProcessUsageRow> rows, double? aggregatePercent)
    {
        var list = rows.ToList();
        var sum = list.Sum(r => r.Value);
        var scale = aggregatePercent is { } a && sum > 0 ? Math.Min(1, a / sum) : 1;
        return list
            .Select(r => r with { Value = Math.Clamp(r.Value * scale, 0, 100) })
            .OrderByDescending(r => r.Value)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Energy ≈ CPU + GPU per app; keep ≥ 2 %, sorted, clamped.</summary>
    public static List<ProcessUsageRow> Energy(IEnumerable<ProcessUsageRow> cpu, IEnumerable<ProcessUsageRow> gpu)
    {
        var merged = new Dictionary<string, ProcessUsageRow>(StringComparer.Ordinal);
        foreach (var row in cpu.Concat(gpu))
        {
            merged[row.Key] = merged.TryGetValue(row.Key, out var existing) ? existing with { Value = existing.Value + row.Value } : row;
        }

        return merged.Values
            .Where(r => r.Value >= 2)
            .Select(r => r with { Value = Math.Clamp(r.Value, 0, 100) })
            .OrderByDescending(r => r.Value)
            .ToList();
    }
}
