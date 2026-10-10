// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Fake.SystemMonitor;

/// <summary>
/// Real time plus an offset tests can advance, so the fakes (which derive
/// their counters from this clock) can be fast-forwarded to fill graphs.
/// </summary>
public sealed class FakeMonitorClock : IMonitorClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _offset;

    public double Now => _stopwatch.Elapsed.TotalSeconds + Volatile.Read(ref _offset);

    public TimeSpan Uptime => TimeSpan.FromHours(76) + TimeSpan.FromSeconds(Now);

    public void Advance(double seconds) => Interlocked.Exchange(ref _offset, _offset + seconds);
}

/// <summary>Smooth, deterministic waves for plausible changing values.</summary>
internal static class Wave
{
    public static double At(double t, double baseline, double amplitude, double period, double phase = 0) =>
        baseline + (amplitude * Math.Sin((2 * Math.PI * t / period) + phase));

    /// <summary>Integral of <see cref="At"/> from 0 to <paramref name="t"/>.</summary>
    public static double Integral(double t, double baseline, double amplitude, double period, double phase = 0)
    {
        var w = 2 * Math.PI / period;
        return (baseline * t) - (amplitude / w * (Math.Cos((w * t) + phase) - Math.Cos(phase)));
    }
}

/// <summary>12 logical processors: 8 performance threads and 4 efficiency cores.</summary>
public sealed class FakeCpuSensor(FakeMonitorClock clock) : ICpuSensor
{
    private const int Cores = 12;
    private const double TickRate = 1e7;

    public int LogicalProcessorCount => Cores;

    public IReadOnlyList<CoreGroup> Topology { get; } =
        CoreTopologyBuilder.Build([1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0]);

    public string? ProcessorName => "Fake Processor 12-Core";

    public CpuTicks? ReadTotal()
    {
        var t = clock.Now;
        ulong busy = 0;
        for (var i = 0; i < Cores; i++)
        {
            busy += Busy(i, t);
        }

        return new CpuTicks(busy, (ulong)(t * TickRate * Cores));
    }

    public IReadOnlyList<CpuTicks>? ReadPerCore()
    {
        var t = clock.Now;
        return Enumerable.Range(0, Cores).Select(i => new CpuTicks(Busy(i, t), (ulong)(t * TickRate))).ToList();
    }

    private static ulong Busy(int core, double t)
    {
        var baseline = core < 8 ? 0.24 - (core * 0.015) : 0.12;
        // Keep the busy rate positive (baseline - both amplitudes > 0) so counters never run backwards.
        var amplitude = Math.Min(baseline - 0.07, 0.13);
        var integral = Wave.Integral(t, baseline, amplitude, 23 + (core * 3.1), core * 0.9)
                       + Wave.Integral(t, 0, 0.06, 7.3 + core, core * 1.7);
        return (ulong)Math.Max(0, integral * TickRate);
    }
}

public sealed class FakeMemorySensor(FakeMonitorClock clock) : IMemorySensor
{
    private const ulong GiB = 1024UL * 1024 * 1024;

    /// <summary>Tests can force a pressure state.</summary>
    public bool SimulateLowMemory { get; set; }

    public MemorySample? Read()
    {
        var t = clock.Now;
        var total = 16 * GiB;
        var available = SimulateLowMemory ? (ulong)(0.4 * GiB) : (ulong)(Wave.At(t, 6.2, 0.5, 47) * GiB);
        return new MemorySample
        {
            Total = total,
            Available = available,
            CommitTotal = (ulong)(Wave.At(t, 14.5, 0.6, 61) * GiB),
            CommitLimit = 24 * GiB,
            AppUsed = (ulong)(Wave.At(t, 7.1, 0.4, 53) * GiB),
            Compressed = (ulong)(0.42 * GiB),
            Cached = (ulong)(Wave.At(t, 4.3, 0.2, 71) * GiB),
            SwapUsed = (ulong)(1.2 * GiB),
            LowMemorySignaled = SimulateLowMemory,
        };
    }
}

public sealed class FakeGpuSensor(FakeMonitorClock clock) : IGpuSensor
{
    public GpuSample? ReadUsage()
    {
        var t = clock.Now;
        var discrete = Math.Clamp(Wave.At(t, 0.14, 0.09, 19) + Wave.At(t, 0, 0.05, 5.1), 0, 1);
        var integrated = Math.Clamp(Wave.At(t, 0.05, 0.03, 13), 0, 1);
        return new GpuSample
        {
            Usage = Math.Max(discrete, integrated),
            Adapters =
            [
                new GpuAdapterReading { Id = "luid_0x0001", Name = "Fake Graphics RTX", Usage = discrete, DedicatedUsed = 1_400_000_000, DedicatedTotal = 8_589_934_592, SharedUsed = 210_000_000 },
                new GpuAdapterReading { Id = "luid_0x0002", Name = "Fake Integrated Graphics", Usage = integrated, DedicatedUsed = 128_000_000, DedicatedTotal = 536_870_912, SharedUsed = 640_000_000 },
            ],
            ProcessUsage = new Dictionary<int, double>
            {
                [FakeProcessSampler.ChromePid] = discrete * 55,
                [FakeProcessSampler.DwmPid] = discrete * 30,
                [FakeProcessSampler.CodePid] = discrete * 10,
            },
        };
    }

    public double? ReadTemperature() => Wave.At(clock.Now, 52, 4, 37);
}

public sealed class FakeNetworkSensor(FakeMonitorClock clock) : INetworkSensor
{
    public NetworkCounters? ReadCounters()
    {
        var t = clock.Now;
        var down = Wave.Integral(t, 1_250_000, 900_000, 17) + Wave.Integral(t, 0, 300_000, 4.3);
        var up = Wave.Integral(t, 330_000, 220_000, 23, 1.3);
        return new NetworkCounters(5_000_000_000UL + (ulong)Math.Max(0, down), 900_000_000UL + (ulong)Math.Max(0, up));
    }

    public IReadOnlyList<LocalAddress> ReadLocalAddresses() =>
        [new LocalAddress("10.0.0.5", "Ethernet"), new LocalAddress("192.168.1.23", "Wi-Fi")];
}

public sealed class FakeDiskSensor(FakeMonitorClock clock) : IDiskSensor, IDiskActions
{
    private readonly HashSet<string> _ejected = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DiskVolumeInfo>? ReadVolumes()
    {
        var volumes = new List<DiskVolumeInfo>
        {
            new() { Id = "\\\\?\\Volume{c}", Name = "Windows", MountPath = "C:\\", FileSystem = "NTFS", IsInternal = true, IsSystem = true, Total = 1_000_204_845_056, Free = 371_000_000_000, DiskId = "PhysicalDrive0" },
            new() { Id = "\\\\?\\Volume{d}", Name = "Data", MountPath = "D:\\", FileSystem = "NTFS", IsInternal = true, Total = 2_000_398_934_016, Free = 1_310_000_000_000, DiskId = "PhysicalDrive1" },
            new() { Id = "\\\\?\\Volume{e}", Name = "USB Stick", MountPath = "E:\\", FileSystem = "exFAT", IsInternal = false, IsEjectable = true, Total = 64_000_000_000, Free = 41_500_000_000, DiskId = "PhysicalDrive2" },
        };
        return volumes.Where(v => !_ejected.Contains(v.Id)).ToList();
    }

    public IReadOnlyDictionary<string, DiskCounters>? ReadCounters(IReadOnlyCollection<string> diskIds)
    {
        var t = clock.Now;
        var result = new Dictionary<string, DiskCounters>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var id in diskIds)
        {
            var factor = id.EndsWith('0') ? 1.0 : 0.25;
            result[id] = new DiskCounters(
                (ulong)Math.Max(0, Wave.Integral(t, 2_400_000 * factor, 2_000_000 * factor, 11 + i, i)),
                (ulong)Math.Max(0, Wave.Integral(t, 900_000 * factor, 800_000 * factor, 7 + i, i + 1)));
            i++;
        }

        return result;
    }

    public (EjectResult Result, string? Detail) Eject(DiskVolumeInfo volume)
    {
        if (!volume.IsEjectable)
        {
            return (EjectResult.NotEjectable, null);
        }

        Thread.Sleep(600);
        _ejected.Add(volume.Id);
        Log.Info("monitor", $"[fake] ejected {volume.MountPath}");
        return (EjectResult.Ejected, null);
    }
}

public sealed class FakePowerSensor(FakeMonitorClock clock) : IPowerSensor
{
    /// <summary>Tests can turn the fake into a desktop.</summary>
    public bool Battery { get; set; } = true;

    public bool PluggedIn { get; set; }

    public bool HasBattery => Battery;

    public event EventHandler? PowerStatusChanged;

    public PowerReading? Read()
    {
        if (!Battery)
        {
            return new PowerReading { ExternalConnected = true };
        }

        var t = clock.Now;
        var rate = PluggedIn ? 18.0 : -Wave.At(t, 9.4, 2.1, 29);
        var charge = (int)Math.Clamp(Math.Round(78 - (t / 600)), 5, 100);
        return new PowerReading
        {
            ChargePercent = charge,
            BatteryWatts = rate,
            SystemWatts = BatteryMath.SystemWattsFallback(rate, PluggedIn),
            IsCharging = PluggedIn,
            ExternalConnected = PluggedIn,
            HasBattery = true,
            TimeRemainingSeconds = BatteryMath.ValidTimeRemaining(13_320, PluggedIn, PluggedIn),
            HealthPercent = 93,
            CycleCount = 212,
        };
    }

    public double? ReadBatteryTemperature() => Battery ? Wave.At(clock.Now, 31, 1.5, 83) : null;

    public void RaiseChanged() => PowerStatusChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class FakeTemperatureSensor(FakeMonitorClock clock) : ITemperatureSensor
{
    /// <summary>Tests can switch to the "needs administrator" state real PCs usually show.</summary>
    public SensorAvailability Availability { get; set; } = SensorAvailability.Available;

    public SensorAvailability CpuAvailability => Availability;

    public double? ReadCpuTemperature() =>
        Availability == SensorAvailability.Available ? Wave.At(clock.Now, 48, 6, 41) + Wave.At(clock.Now, 0, 2, 6) : null;
}

/// <summary>The sensor helper's setup without Windows: installing and removing just flip the state.</summary>
public sealed class FakeSensorHelper : ISensorHelper
{
    public SensorHelperState State { get; set; } = SensorHelperState.NotInstalled;

    public bool DriverInstalled { get; set; } = true;

    public SensorHelperResult NextResult { get; set; } = SensorHelperResult.Done;

    public event EventHandler? StateChanged;

    public Task<SensorHelperResult> InstallAsync() => Finish(SensorHelperState.Installed);

    public Task<SensorHelperResult> RemoveAsync() => Finish(SensorHelperState.NotInstalled);

    private Task<SensorHelperResult> Finish(SensorHelperState state)
    {
        if (NextResult == SensorHelperResult.Done)
        {
            State = state;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(NextResult);
    }
}

public sealed class FakeUsbSensor : IUsbSensor
{
    public IReadOnlyList<UsbDevice>? Read() => UsbDeviceFilter.Filter(
    [
        new UsbNode { InstanceId = "USB\\VID_046D&PID_C548\\5&1", VendorId = 0x046D, ProductId = 0xC548, Product = "USB Receiver", Vendor = "Logitech" },
        new UsbNode { InstanceId = "USB\\VID_04E8&PID_4001\\S3", VendorId = 0x04E8, ProductId = 0x4001, Product = "Portable SSD T7", Vendor = "Samsung" },
        new UsbNode { InstanceId = "USB\\VID_1050&PID_0407\\6&2", VendorId = 0x1050, ProductId = 0x0407, Product = "YubiKey 5 NFC", Vendor = "Yubico" },
        new UsbNode { InstanceId = "USB\\ROOT_HUB30\\4&1", IsRootHub = true, Product = "USB Root Hub (USB 3.0)" },
        new UsbNode { InstanceId = "USB\\VID_05E3&PID_0610\\7&3", VendorId = 0x05E3, ProductId = 0x0610, DeviceClass = UsbDeviceFilter.HubClass, Product = "Generic USB Hub" },
        new UsbNode { InstanceId = "USB\\VID_0BDA&PID_58FD\\200901010001", VendorId = 0x0BDA, ProductId = 0x58FD, IsBuiltIn = true, Product = "Integrated Camera" },
    ]);
}

public sealed class FakePeripheralBatterySensor : IPeripheralBatterySensor
{
    public IReadOnlyList<PeripheralBattery>? Read(double now) => PeripheralBatteries.Sort(
    [
        new PeripheralBattery("bt:keys", "MX Keys", PeripheralKind.Keyboard, 64, now),
        new PeripheralBattery("bt:mouse", "MX Master 3S", PeripheralKind.Mouse, 24, now),
        new PeripheralBattery("bt:audio", "WH-1000XM5", PeripheralKind.Audio, 80, now),
    ]);
}

/// <summary>A handful of apps with helpers, CPU time growing at different rates.</summary>
public sealed class FakeProcessSampler(FakeMonitorClock clock) : IProcessSampler, IProcessControl
{
    public const int ChromePid = 4120;
    public const int CodePid = 5210;
    public const int DwmPid = 1388;

    private static readonly (int Pid, int Parent, string Image, double Cpu, double MemoryMb, string Name)[] Table =
    [
        (4, 0, "System", 0.4, 0.2, "System"),
        (DwmPid, 900, "dwm.exe", 1.9, 140, "Desktop Window Manager"),
        (2210, 2100, "explorer.exe", 0.6, 180, "Windows Explorer"),
        (ChromePid, 2210, "chrome.exe", 3.2, 420, "Google Chrome"),
        (4188, ChromePid, "chrome.exe", 4.1, 610, "Google Chrome"),
        (4190, ChromePid, "chrome.exe", 1.7, 330, "Google Chrome"),
        (4202, ChromePid, "chrome.exe", 0.9, 210, "Google Chrome"),
        (CodePid, 2210, "Code.exe", 2.4, 380, "Visual Studio Code"),
        (5230, CodePid, "Code.exe", 1.1, 290, "Visual Studio Code"),
        (6001, 2210, "ms-teams.exe", 1.4, 520, "Microsoft Teams"),
        (6630, 2210, "Spotify.exe", 0.8, 240, "Spotify"),
        (3010, 900, "MsMpEng.exe", 0.7, 260, "Antimalware Service Executable"),
        (1200, 900, "svchost.exe", 0.3, 60, "Host Process for Windows Services"),
        (1240, 900, "svchost.exe", 0.2, 45, "Host Process for Windows Services"),
        (7720, 2210, "OneDrive.exe", 0.2, 95, "Microsoft OneDrive"),
    ];

    public List<string> Actions { get; } = [];

    public IReadOnlyList<ProcessSample>? Snapshot()
    {
        var t = clock.Now;
        return Table.Select((p, i) => new ProcessSample
        {
            Pid = p.Pid,
            ParentPid = p.Parent,
            ImageName = p.Image,
            CpuTime = (long)(Math.Max(0, Wave.Integral(t, p.Cpu / 100 * 12, p.Cpu / 100 * 6, 9 + i, i)) * 1e7),
            PrivateWorkingSet = (ulong)(Wave.At(t, p.MemoryMb, p.MemoryMb * 0.05, 30 + i) * 1024 * 1024),
            CreateTime = 133_000_000_000_000_000 + p.Pid,
        }).ToList();
    }

    public AppDescription Describe(ProcessSample process)
    {
        var entry = Table.FirstOrDefault(p => p.Pid == process.Pid);
        return new AppDescription(entry.Name ?? ProcessUsageService.DisplayFallback(process), $"C:\\Program Files\\{process.ImageName}", process.Pid is ChromePid or CodePid);
    }

    public bool Activate(int pid)
    {
        Actions.Add($"activate:{pid}");
        return true;
    }

    public KillResult Kill(int pid, long createTime)
    {
        Actions.Add($"kill:{pid}");
        return pid is 4 or DwmPid ? KillResult.Protected : KillResult.Killed;
    }

    public void OpenTaskManager() => Actions.Add("taskmgr");
}

public sealed class FakeFullScreenDetector : IFullScreenDetector
{
    public bool FullScreen { get; set; }

    public bool IsFullScreenAppInFront() => FullScreen;
}
