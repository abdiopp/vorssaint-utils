// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.SystemMonitor;

namespace Rivet.Core.Tests.SystemMonitor;

/// <summary>Sensors whose next readings a test sets by hand.</summary>
internal sealed class ScriptedSensors :
    IMonitorClock, ICpuSensor, IMemorySensor, IGpuSensor, INetworkSensor, IDiskSensor,
    IPowerSensor, ITemperatureSensor, IUsbSensor, IPeripheralBatterySensor, IProcessSampler
{
    public double Now { get; set; }

    public TimeSpan Uptime { get; set; } = TimeSpan.FromHours(5);

    public CpuTicks? Total { get; set; }

    public IReadOnlyList<CpuTicks>? Cores { get; set; }

    public MemorySample? MemorySample { get; set; }

    public double? GpuRaw { get; set; }

    public double? GpuTemp { get; set; }

    public NetworkCounters? Counters { get; set; }

    public List<DiskVolumeInfo>? Volumes { get; set; }

    public Dictionary<string, DiskCounters>? DiskCounters { get; set; }

    public PowerReading? PowerReading { get; set; }

    public double? BatteryTemp { get; set; }

    public double? CpuTemp { get; set; }

    public List<UsbDevice>? Usb { get; set; }

    public int Reads { get; private set; }

    public int LogicalProcessorCount => 4;

    public IReadOnlyList<CoreGroup> Topology { get; } = CoreTopologyBuilder.Build([0, 0, 0, 0]);

    public string? ProcessorName => "Scripted";

    public bool HasBattery { get; set; }

    public SensorAvailability CpuAvailability => SensorAvailability.Available;

    public event EventHandler? PowerStatusChanged;

    public MonitorSensors Bundle => new(this, this, this, this, this, this, this, this, this, this, this);

    public CpuTicks? ReadTotal()
    {
        Reads++;
        return Total;
    }

    public IReadOnlyList<CpuTicks>? ReadPerCore() => Cores;

    public MemorySample? Read() => MemorySample;

    public GpuSample? ReadUsage() => GpuRaw is { } raw ? new GpuSample { Usage = raw } : null;

    public double? ReadTemperature() => GpuTemp;

    public NetworkCounters? ReadCounters() => Counters;

    public IReadOnlyList<LocalAddress> ReadLocalAddresses() => [];

    public IReadOnlyList<DiskVolumeInfo>? ReadVolumes() => Volumes;

    public IReadOnlyDictionary<string, DiskCounters>? ReadCounters(IReadOnlyCollection<string> diskIds) => DiskCounters;

    PowerReading? IPowerSensor.Read() => PowerReading;

    public double? ReadBatteryTemperature() => BatteryTemp;

    public double? ReadCpuTemperature() => CpuTemp;

    IReadOnlyList<UsbDevice>? IUsbSensor.Read() => Usb;

    public IReadOnlyList<PeripheralBattery>? Read(double now) => [];

    public IReadOnlyList<ProcessSample>? Snapshot() => [];

    public AppDescription Describe(ProcessSample process) => new(process.ImageName, null, false);

    public void RaisePowerChanged() => PowerStatusChanged?.Invoke(this, EventArgs.Empty);
}
