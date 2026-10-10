// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>History series published with a snapshot (empty in background).</summary>
public sealed record MonitorHistories
{
    public double[] Cpu { get; init; } = [];

    public double[] Gpu { get; init; } = [];

    /// <summary>Memory used / total.</summary>
    public double[] Memory { get; init; } = [];

    /// <summary>App memory / total.</summary>
    public double[] MemoryApp { get; init; } = [];

    public double[] NetDown { get; init; } = [];

    public double[] NetUp { get; init; } = [];

    public double[] DiskRead { get; init; } = [];

    public double[] DiskWrite { get; init; } = [];

    /// <summary>System power, watts.</summary>
    public double[] SystemPower { get; init; } = [];

    /// <summary>Battery charge fraction.</summary>
    public double[] Battery { get; init; } = [];

    public static MonitorHistories Empty { get; } = new();
}

/// <summary>An immutable reading of everything the plan asked for, published on the UI thread.</summary>
public sealed record MonitorSnapshot
{
    public SamplingPlan Plan { get; init; } = SamplingPlan.None;

    public bool Foreground { get; init; }

    /// <summary>Monotonic time of the refresh that produced this snapshot.</summary>
    public double Timestamp { get; init; }

    public double? CpuUsage { get; init; }

    /// <summary>Monotonic time of the last real CPU read (alerts key on it).</summary>
    public double? CpuUsageReadAt { get; init; }

    /// <summary>One entry per logical core (null = no fresh interval yet).</summary>
    public IReadOnlyList<double?> CpuCoreUsage { get; init; } = [];

    public IReadOnlyList<CoreGroup> CoreGroups { get; init; } = [];

    public double? GpuUsage { get; init; }

    public IReadOnlyList<GpuAdapterReading> GpuAdapters { get; init; } = [];

    public MemoryReading? Memory { get; init; }

    public double? CpuTemperature { get; init; }

    public double? CpuTemperatureReadAt { get; init; }

    public SensorAvailability CpuTemperatureAvailability { get; init; } = SensorAvailability.Available;

    public double? GpuTemperature { get; init; }

    public double? BatteryTemperature { get; init; }

    public double? BatteryTemperatureReadAt { get; init; }

    /// <summary>Bytes per second; null until a previous sample exists ("Measuring…").</summary>
    public double? NetDownBytesPerSec { get; init; }

    public double? NetUpBytesPerSec { get; init; }

    /// <summary>Bytes seen while the network family was sampled, since launch.</summary>
    public ulong NetTotalDown { get; init; }

    public ulong NetTotalUp { get; init; }

    public PowerReading? Power { get; init; }

    public IReadOnlyList<PeripheralBattery> PeripheralBatteries { get; init; } = [];

    public IReadOnlyList<DiskVolume> Disks { get; init; } = [];

    /// <summary>Sum of read rates over unique physical disks.</summary>
    public double? DiskReadBytesPerSec { get; init; }

    public double? DiskWriteBytesPerSec { get; init; }

    /// <summary>Null when the family was never read.</summary>
    public IReadOnlyList<UsbDevice>? ConnectedDevices { get; init; }

    public TimeSpan Uptime { get; init; }

    public bool HasBattery { get; init; }

    public MonitorHistories Histories { get; init; } = MonitorHistories.Empty;

    public static MonitorSnapshot Empty { get; } = new();

    /// <summary>The primary disk: first internal, else first.</summary>
    public DiskVolume? PrimaryDisk => Disks.FirstOrDefault(d => d.Info.IsInternal) ?? Disks.FirstOrDefault();
}
