// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

// Platform interfaces of the system monitor. Every read is called from the
// sampler's background thread, never from the UI thread, and may be slow.
// A read returns null when it failed this time (the sampler then holds the
// last value for a few ticks); it never throws for expected failures.

/// <summary>Monotonic clocks.</summary>
public interface IMonitorClock
{
    /// <summary>
    /// Seconds on a monotonic clock that stops during sleep
    /// (QueryUnbiasedInterruptTime on Windows), so a sleep never passes for
    /// a "sustained" stretch or a short counter gap.
    /// </summary>
    double Now { get; }

    /// <summary>Time since boot, sleep included (what "Up for" shows).</summary>
    TimeSpan Uptime { get; }
}

/// <summary>Cumulative CPU time counters (100 ns units are fine; only deltas matter).</summary>
public readonly record struct CpuTicks(ulong Busy, ulong Total);

public interface ICpuSensor
{
    /// <summary>Whole-machine busy/total counters.</summary>
    CpuTicks? ReadTotal();

    /// <summary>Per logical processor counters, in logical processor order.</summary>
    IReadOnlyList<CpuTicks>? ReadPerCore();

    int LogicalProcessorCount { get; }

    /// <summary>Logical processors grouped by efficiency class (computed once).</summary>
    IReadOnlyList<CoreGroup> Topology { get; }

    /// <summary>Processor brand, e.g. "AMD Ryzen 7 7840U".</summary>
    string? ProcessorName { get; }
}

public interface IMemorySensor
{
    MemorySample? Read();
}

public interface IGpuSensor
{
    /// <summary>Usage of every adapter (PDH "GPU Engine"), the max over adapters and per-process usage.</summary>
    GpuSample? ReadUsage();

    /// <summary>Hottest adapter temperature in °C where the vendor interface or the driver reports it, else null.</summary>
    double? ReadTemperature();
}

public interface INetworkSensor
{
    /// <summary>64-bit counters summed over physical interfaces (loopback, tunnels, filters and virtual adapters excluded).</summary>
    NetworkCounters? ReadCounters();

    /// <summary>IPv4 addresses of the counted interfaces that are up, with their friendly names.</summary>
    IReadOnlyList<LocalAddress> ReadLocalAddresses();
}

public interface IDiskSensor
{
    IReadOnlyList<DiskVolumeInfo>? ReadVolumes();

    /// <summary>Cumulative bytes per physical disk id.</summary>
    IReadOnlyDictionary<string, DiskCounters>? ReadCounters(IReadOnlyCollection<string> diskIds);
}

public enum EjectResult
{
    Ejected,
    Failed,
    NotEjectable,
}

/// <summary>Safely removing external drives, opening them and the storage settings.</summary>
public interface IDiskActions
{
    /// <summary>Flushes, dismounts and asks Windows to eject the drive. Slow; call off the UI thread.</summary>
    (EjectResult Result, string? Detail) Eject(DiskVolumeInfo volume);
}

public interface IPowerSensor
{
    PowerReading? Read();

    /// <summary>Battery temperature in °C (rarely reported by ACPI batteries).</summary>
    double? ReadBatteryTemperature();

    /// <summary>An internal battery is installed (resolved once per boot).</summary>
    bool HasBattery { get; }

    /// <summary>Plugged in/unplugged or a new estimate (raised on any thread).</summary>
    event EventHandler? PowerStatusChanged;
}

public interface ITemperatureSensor
{
    /// <summary>CPU temperature in °C (its own sensors or an ACPI thermal zone), when this PC reports one to this user.</summary>
    double? ReadCpuTemperature();

    /// <summary>Why <see cref="ReadCpuTemperature"/> returns nothing (after the first read).</summary>
    SensorAvailability CpuAvailability { get; }
}

public interface IUsbSensor
{
    /// <summary>External USB peripherals (hubs, root hubs, billboard and built-in devices excluded).</summary>
    IReadOnlyList<UsbDevice>? Read();
}

public interface IPeripheralBatterySensor
{
    /// <summary>Accessory batteries Windows knows about (Bluetooth battery property, BLE Battery Service).</summary>
    IReadOnlyList<PeripheralBattery>? Read(double now);
}

public interface IProcessSampler
{
    /// <summary>Every process with CPU time, private working set and creation time.</summary>
    IReadOnlyList<ProcessSample>? Snapshot();

    /// <summary>The app's friendly name (file description) and path; cached by the implementation.</summary>
    AppDescription Describe(ProcessSample process);
}

public interface IProcessControl
{
    /// <summary>Brings the process's main window to the front. False when it has none.</summary>
    bool Activate(int pid);

    /// <summary>Force-terminates the process if it still has <paramref name="createTime"/>.</summary>
    KillResult Kill(int pid, long createTime);

    /// <summary>Opens the system's task manager.</summary>
    void OpenTaskManager();
}

/// <summary>Whether something full-screen (a game, a video, a presentation) is in front.</summary>
public interface IFullScreenDetector
{
    bool IsFullScreenAppInFront();
}

/// <summary>All sensors, resolved together.</summary>
public sealed record MonitorSensors(
    IMonitorClock Clock,
    ICpuSensor Cpu,
    IMemorySensor Memory,
    IGpuSensor Gpu,
    INetworkSensor Network,
    IDiskSensor Disk,
    IPowerSensor Power,
    ITemperatureSensor Temperature,
    IUsbSensor Usb,
    IPeripheralBatterySensor Peripherals,
    IProcessSampler Processes);
