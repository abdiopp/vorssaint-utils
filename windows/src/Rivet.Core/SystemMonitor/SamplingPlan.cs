// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>Panel sections of the monitor that can be on screen.</summary>
[Flags]
public enum MonitorSurface
{
    None = 0,
    System = 1,
    Network = 2,
    Disk = 4,
    Power = 8,
}

/// <summary>The metric detail views (spec §3.17.6).</summary>
public enum MetricKind
{
    Cpu,
    Gpu,
    Memory,
    Network,
    Disk,
    Battery,
    Power,
    ConnectedDevices,
}

/// <summary>Which metric families the sampler reads (spec §3.1.2).</summary>
public sealed record SamplingPlan
{
    public bool Cpu { get; init; }

    public bool CpuCores { get; init; }

    public bool Memory { get; init; }

    public bool Network { get; init; }

    public bool Disk { get; init; }

    public bool Power { get; init; }

    /// <summary>Watts pinned: power is read at the base interval even in background.</summary>
    public bool PowerDraw { get; init; }

    public bool PeripheralBattery { get; init; }

    public bool GpuUsage { get; init; }

    public bool CpuTemperature { get; init; }

    public bool GpuTemperature { get; init; }

    public bool BatteryTemperature { get; init; }

    public bool ConnectedDevices { get; init; }

    public bool AnyTemperature => CpuTemperature || GpuTemperature || BatteryTemperature;

    public bool Any => Cpu || CpuCores || Memory || Network || Disk || Power || PowerDraw || PeripheralBattery
                       || GpuUsage || AnyTemperature || ConnectedDevices;

    public static SamplingPlan None { get; } = new();
}

/// <summary>Everything the plan depends on, gathered by the service from its consumers and settings.</summary>
public sealed record SamplingInputs
{
    /// <summary>Panel sections on screen.</summary>
    public MonitorSurface Visible { get; init; }

    /// <summary>The "full monitor surface" depth counter is above zero (counts as every section visible).</summary>
    public bool FullSurface { get; init; }

    public IReadOnlySet<MetricKind> Details { get; init; } = new HashSet<MetricKind>();

    /// <summary>Pinned readouts have a consumer (tray tooltip summary or mini monitor).</summary>
    public bool ReadoutsActive { get; init; }

    public bool HasBattery { get; init; }

    // Panel item switches
    public bool SysCpu { get; init; } = true;
    public bool SysCpuCores { get; init; } = true;
    public bool SysMemory { get; init; } = true;
    public bool SysGpu { get; init; } = true;
    public bool SysTemps { get; init; } = true;
    public bool SysConnectedDevices { get; init; } = true;
    public bool SysBattery { get; init; } = true;
    public bool PwrTemperature { get; init; } = true;

    // Readout tokens
    public bool ReadoutCpu { get; init; }
    public bool ReadoutCpuTemperature { get; init; }
    public bool ReadoutGpu { get; init; }
    public bool ReadoutGpuTemperature { get; init; }
    public bool ReadoutMemory { get; init; }
    public bool ReadoutNetwork { get; init; }
    public bool ReadoutDiskUsage { get; init; }
    public bool ReadoutDiskActivity { get; init; }
    public bool ReadoutBattery { get; init; }
    public bool ReadoutBatteryTime { get; init; }
    public bool ReadoutBatteryTemperature { get; init; }
    public bool ReadoutPeripheralBattery { get; init; }
    public bool ReadoutConnectedDevices { get; init; }
    public bool ReadoutPower { get; init; }

    // Alerts
    public bool AlertCpu { get; init; }
    public bool AlertCpuTemperature { get; init; }
    public bool AlertMemory { get; init; }
    public bool AlertDisk { get; init; }
    public bool AlertBattery { get; init; }
    public bool AlertBatteryTemperature { get; init; }

    // Hub availability
    public bool HasCpuFamily { get; init; } = true;
    public bool HasGpuFamily { get; init; } = true;
    public bool HasMemoryFamily { get; init; } = true;
    public bool HasNetworkFamily { get; init; } = true;
    public bool HasDiskFamily { get; init; } = true;
    public bool HasPowerFamily { get; init; } = true;
    public bool HasConnectedDevicesFamily { get; init; } = true;

    /// <summary>A foreground consumer exists (a section or a detail view is on screen).</summary>
    public bool Foreground => Visible != MonitorSurface.None || FullSurface || Details.Count > 0;
}

/// <summary>The pure plan rules of spec §3.1.2, hub overrides applied last.</summary>
public static class SamplingPlanner
{
    public static SamplingPlan Compute(SamplingInputs i)
    {
        bool Visible(MonitorSurface s) => i.FullSurface || i.Visible.HasFlag(s);
        bool Detail(MetricKind k) => i.Details.Contains(k);
        var r = i.ReadoutsActive;
        var system = Visible(MonitorSurface.System);
        var power = Visible(MonitorSurface.Power);

        var plan = new SamplingPlan
        {
            Cpu = (system && i.SysCpu) || Detail(MetricKind.Cpu) || (r && i.ReadoutCpu) || i.AlertCpu,
            // Read whenever the System section itself is on screen (never for readouts, alerts or details).
            CpuCores = i.Visible.HasFlag(MonitorSurface.System) && i.SysCpu && i.SysCpuCores,
            Memory = (system && i.SysMemory) || Detail(MetricKind.Memory) || (r && i.ReadoutMemory) || i.AlertMemory,
            Network = Visible(MonitorSurface.Network) || Detail(MetricKind.Network) || (r && i.ReadoutNetwork),
            Disk = Visible(MonitorSurface.Disk) || Detail(MetricKind.Disk) || (r && (i.ReadoutDiskUsage || i.ReadoutDiskActivity)) || i.AlertDisk,
            Power = power || Detail(MetricKind.Power) || (r && i.ReadoutPower)
                    || (i.HasBattery && ((power && i.SysBattery) || Detail(MetricKind.Battery) || (r && (i.ReadoutBattery || i.ReadoutBatteryTime)) || i.AlertBattery)),
            PowerDraw = r && i.ReadoutPower,
            PeripheralBattery = Detail(MetricKind.Battery) || (r && i.ReadoutPeripheralBattery),
            GpuUsage = (system && i.SysGpu) || Detail(MetricKind.Gpu) || (r && i.ReadoutGpu),
            CpuTemperature = (system && i.SysTemps) || Detail(MetricKind.Cpu) || (r && i.ReadoutCpuTemperature) || i.AlertCpuTemperature,
            GpuTemperature = (system && i.SysTemps) || Detail(MetricKind.Gpu) || (r && i.ReadoutGpuTemperature),
            BatteryTemperature = i.HasBattery && ((power && i.PwrTemperature) || Detail(MetricKind.Battery) || (r && i.ReadoutBatteryTemperature) || i.AlertBatteryTemperature),
            ConnectedDevices = i.FullSurface || (system && i.SysConnectedDevices) || Detail(MetricKind.ConnectedDevices) || (r && i.ReadoutConnectedDevices),
        };

        return plan with
        {
            Cpu = plan.Cpu && i.HasCpuFamily,
            CpuCores = plan.CpuCores && i.HasCpuFamily,
            CpuTemperature = plan.CpuTemperature && i.HasCpuFamily,
            GpuUsage = plan.GpuUsage && i.HasGpuFamily,
            GpuTemperature = plan.GpuTemperature && i.HasGpuFamily,
            Memory = plan.Memory && i.HasMemoryFamily,
            Network = plan.Network && i.HasNetworkFamily,
            Disk = plan.Disk && i.HasDiskFamily,
            Power = plan.Power && i.HasPowerFamily,
            PowerDraw = plan.PowerDraw && i.HasPowerFamily,
            PeripheralBattery = plan.PeripheralBattery && i.HasPowerFamily,
            BatteryTemperature = plan.BatteryTemperature && i.HasPowerFamily,
            ConnectedDevices = plan.ConnectedDevices && i.HasConnectedDevicesFamily,
        };
    }
}

/// <summary>What the sampler reads on a tick; each has its own target period.</summary>
public enum SampleKind
{
    Cpu,
    Memory,
    Network,
    PowerDraw,
    GpuUsage,
    ConnectedDevices,
    Power,
    Temperature,
    Disk,
    PeripheralBattery,
}

/// <summary>
/// Strides and wake period (spec §3.1.3): stride = max(1, ⌈T / I⌉), a kind is
/// read when tick % stride == 0, the timer period is I × gcd(strides) and the
/// tick counter advances by that gcd, re-aligned when the gcd changes.
/// </summary>
public sealed class SamplingCadence
{
    private int _wake = 1;

    /// <summary>The current tick (a multiple of <see cref="WakeTicks"/>).</summary>
    public long Tick { get; private set; }

    public int WakeTicks => _wake;

    public static (double Foreground, double Background) Target(SampleKind kind) => kind switch
    {
        SampleKind.Cpu or SampleKind.Memory or SampleKind.Network or SampleKind.PowerDraw => (1, 1),
        SampleKind.GpuUsage => (1, 10),
        SampleKind.ConnectedDevices => (2, 10),
        SampleKind.Power or SampleKind.Temperature => (1, 15),
        SampleKind.Disk => (1, 10),
        SampleKind.PeripheralBattery => (15, 60),
        _ => (1, 1),
    };

    public static int Stride(SampleKind kind, int intervalSeconds, bool foreground)
    {
        var (fg, bg) = Target(kind);
        var target = foreground ? fg : bg;
        return Math.Max(1, (int)Math.Ceiling(target / Math.Max(1, intervalSeconds)));
    }

    public static IReadOnlyList<SampleKind> NeededKinds(SamplingPlan plan)
    {
        var kinds = new List<SampleKind>();
        if (plan.Cpu || plan.CpuCores) kinds.Add(SampleKind.Cpu);
        if (plan.Memory) kinds.Add(SampleKind.Memory);
        if (plan.Network) kinds.Add(SampleKind.Network);
        if (plan.PowerDraw) kinds.Add(SampleKind.PowerDraw);
        if (plan.GpuUsage) kinds.Add(SampleKind.GpuUsage);
        if (plan.ConnectedDevices) kinds.Add(SampleKind.ConnectedDevices);
        if (plan.Power) kinds.Add(SampleKind.Power);
        if (plan.AnyTemperature) kinds.Add(SampleKind.Temperature);
        if (plan.Disk) kinds.Add(SampleKind.Disk);
        if (plan.PeripheralBattery) kinds.Add(SampleKind.PeripheralBattery);
        return kinds;
    }

    /// <summary>gcd of the needed kinds' strides (1 when nothing is needed).</summary>
    public static int WakeFor(SamplingPlan plan, int intervalSeconds, bool foreground)
    {
        var wake = 0;
        foreach (var kind in NeededKinds(plan))
        {
            wake = Gcd(wake, Stride(kind, intervalSeconds, foreground));
        }

        return Math.Max(1, wake);
    }

    /// <summary>Applies a new plan: recomputes the wake and rounds the tick up onto the new grid.</summary>
    public void Configure(SamplingPlan plan, int intervalSeconds, bool foreground)
    {
        _wake = WakeFor(plan, intervalSeconds, foreground);
        if (Tick % _wake != 0)
        {
            Tick += _wake - (Tick % _wake);
        }
    }

    /// <summary>Kinds due at the current tick.</summary>
    public IReadOnlyList<SampleKind> Due(SamplingPlan plan, int intervalSeconds, bool foreground) =>
        NeededKinds(plan).Where(k => Tick % Stride(k, intervalSeconds, foreground) == 0).ToList();

    public void Advance() => Tick += _wake;

    /// <summary>Seconds between wakes.</summary>
    public double PeriodSeconds(int intervalSeconds) => intervalSeconds * (double)_wake;

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return Math.Abs(a);
    }
}
