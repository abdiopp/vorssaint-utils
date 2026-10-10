// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>Memory pressure traffic light. Windows has no kernel level; see <see cref="MemoryPressureHeuristic"/>.</summary>
public enum MemoryPressure
{
    Unknown,
    Normal,
    Warning,
    Critical,
}

/// <summary>Raw memory figures as the platform reports them (bytes).</summary>
public sealed record MemorySample
{
    public required ulong Total { get; init; }

    public required ulong Available { get; init; }

    /// <summary>Committed bytes and the commit limit (RAM + page files), for the pressure heuristic.</summary>
    public ulong CommitTotal { get; init; }

    public ulong CommitLimit { get; init; }

    /// <summary>Sum of private working sets ("App Memory" approximation), when known.</summary>
    public ulong? AppUsed { get; init; }

    /// <summary>Working set of the Memory Compression process, when known.</summary>
    public ulong? Compressed { get; init; }

    /// <summary>Standby list plus system working set ("Cached files").</summary>
    public ulong? Cached { get; init; }

    /// <summary>Page file space in use; null hides the row.</summary>
    public ulong? SwapUsed { get; init; }

    /// <summary>Windows' own low-memory resource notification is signaled.</summary>
    public bool LowMemorySignaled { get; init; }
}

/// <summary>Memory as published in the snapshot (every figure clamped to <see cref="Total"/>).</summary>
public sealed record MemoryReading
{
    public required ulong Total { get; init; }

    /// <summary>"Memory Used" (Task Manager "In use": total minus available).</summary>
    public required ulong Used { get; init; }

    public ulong? AppUsed { get; init; }

    public ulong? Compressed { get; init; }

    public ulong? Cached { get; init; }

    public ulong? SwapUsed { get; init; }

    public MemoryPressure Pressure { get; init; }

    /// <summary>The figure chosen by "Measure memory as" (falls back to used).</summary>
    public ulong Chosen(bool appMetric) => appMetric && AppUsed is { } app ? app : Used;

    public double ChosenFraction(bool appMetric) => Total == 0 ? 0 : Math.Clamp((double)Chosen(appMetric) / Total, 0, 1);
}

/// <summary>One graphics adapter as read in one GPU tick.</summary>
public sealed record GpuAdapterReading
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Busiest engine, 0…1 (Task Manager semantics).</summary>
    public double Usage { get; init; }

    public ulong? DedicatedUsed { get; init; }

    public ulong? DedicatedTotal { get; init; }

    public ulong? SharedUsed { get; init; }
}

/// <summary>A GPU sensor read: overall usage, per adapter, and per process (percent of the busiest engine).</summary>
public sealed record GpuSample
{
    public double? Usage { get; init; }

    public IReadOnlyList<GpuAdapterReading> Adapters { get; init; } = [];

    /// <summary>pid → utilization percent (0…100) of that process on its busiest engine.</summary>
    public IReadOnlyDictionary<int, double> ProcessUsage { get; init; } = new Dictionary<int, double>();
}

/// <summary>Cumulative network byte counters summed over the counted interfaces.</summary>
public readonly record struct NetworkCounters(ulong BytesIn, ulong BytesOut);

public sealed record LocalAddress(string Address, string? InterfaceName);

/// <summary>A mounted volume as the platform reports it.</summary>
public sealed record DiskVolumeInfo
{
    /// <summary>Stable id (volume GUID path when known, else the mount path).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Mount point, e.g. <c>C:\</c>.</summary>
    public required string MountPath { get; init; }

    /// <summary>File system tag ("NTFS", "exFAT"), or null when unknown.</summary>
    public string? FileSystem { get; init; }

    public bool IsInternal { get; init; } = true;

    /// <summary>The Windows volume.</summary>
    public bool IsSystem { get; init; }

    public bool IsEjectable { get; init; }

    public required ulong Total { get; init; }

    /// <summary>Space available to the user.</summary>
    public required ulong Free { get; init; }

    /// <summary>Physical disk the volume lives on (I/O counters are per disk), e.g. "PhysicalDrive0".</summary>
    public string? DiskId { get; init; }

    public ulong Used => Total > Free ? Total - Free : 0;

    public double UsedFraction => Total == 0 ? 0 : Math.Clamp((double)Used / Total, 0, 1);
}

public readonly record struct DiskCounters(ulong BytesRead, ulong BytesWritten);

/// <summary>A volume with the live activity of its physical disk.</summary>
public sealed record DiskVolume
{
    public required DiskVolumeInfo Info { get; init; }

    public double? ReadRate { get; init; }

    public double? WriteRate { get; init; }

    public ulong SessionRead { get; init; }

    public ulong SessionWritten { get; init; }
}

/// <summary>Power and battery (spec §3.9.1). Every field is optional: rows without data are hidden.</summary>
public sealed record PowerReading
{
    /// <summary>Whole machine draw, watts. On Windows only known while discharging (from the battery rate).</summary>
    public double? SystemWatts { get; init; }

    /// <summary>Real-time adapter input; no Windows API, always null.</summary>
    public double? AdapterWatts { get; init; }

    public double? AdapterMaxWatts { get; init; }

    /// <summary>Signed battery flow: positive while charging, negative while discharging.</summary>
    public double? BatteryWatts { get; init; }

    public int? ChargePercent { get; init; }

    /// <summary>OS estimate; valid only on battery, not charging, 1 min ≤ t &lt; 7 days.</summary>
    public double? TimeRemainingSeconds { get; init; }

    public int? HealthPercent { get; init; }

    public int? CycleCount { get; init; }

    public bool IsCharging { get; init; }

    public bool ExternalConnected { get; init; }

    public bool HasBattery { get; init; }

    public static PowerReading None { get; } = new();
}

/// <summary>An external USB peripheral.</summary>
public sealed record UsbDevice(string Id, string? Name, string? Vendor);

public enum PeripheralKind
{
    Keyboard,
    Mouse,
    Trackpad,
    Audio,
    Device,
}

/// <summary>A Bluetooth or HID accessory battery.</summary>
public sealed record PeripheralBattery(string Id, string Name, PeripheralKind Kind, int Percent, double ObservedAt);

/// <summary>Why a temperature is missing (shown as an explanation instead of a number).</summary>
public enum SensorAvailability
{
    Available,

    /// <summary>The PC does not report this sensor through any interface the app can read.</summary>
    NotReported,

    /// <summary>A sensor exists but only administrators may read it.</summary>
    NeedsAdministrator,

    /// <summary>The CPU's own sensors need a sensor driver (PawnIO) that is not installed.</summary>
    NeedsSensorDriver,

    /// <summary>The sensor driver is there; the elevated sensor helper that reads through it is not installed.</summary>
    NeedsSensorHelper,
}

/// <summary>Logical cores grouped by performance class (spec §3.2 core topology).</summary>
public sealed record CoreGroup(CoreClass Class, IReadOnlyList<int> Cores)
{
    public double Weight => Class switch
    {
        CoreClass.Super => 1.5,
        CoreClass.Performance => 1.25,
        _ => 1.0,
    };
}

public enum CoreClass
{
    /// <summary>All cores alike: one group labelled "CPU".</summary>
    Uniform,
    Super,
    Performance,
    Efficiency,
}

/// <summary>One process as the platform reports it in a snapshot.</summary>
public sealed record ProcessSample
{
    public required int Pid { get; init; }

    public int ParentPid { get; init; }

    /// <summary>Executable file name, e.g. "chrome.exe".</summary>
    public required string ImageName { get; init; }

    /// <summary>Cumulative kernel + user CPU time, 100 ns units.</summary>
    public long CpuTime { get; init; }

    /// <summary>Private working set (Task Manager "Memory"), bytes.</summary>
    public ulong PrivateWorkingSet { get; init; }

    /// <summary>Process creation time (FILETIME ticks): identity for force kill (PID reuse guard).</summary>
    public long CreateTime { get; init; }
}

/// <summary>How an app shows up in the per-app lists.</summary>
public sealed record AppDescription(string DisplayName, string? ExecutablePath, bool HasWindow);

public enum KillResult
{
    Killed,
    NotFound,
    AccessDenied,
    Protected,

    /// <summary>The PID now belongs to another process (start time changed).</summary>
    IdentityMismatch,
}
