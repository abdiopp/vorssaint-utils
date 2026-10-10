// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

/// <summary>Plan rules (§3.1.2) and cadence (§3.1.3).</summary>
public class SamplingTests
{
    [Fact]
    public void Nothing_needs_data_at_rest() =>
        Assert.False(SamplingPlanner.Compute(new SamplingInputs()).Any);

    [Fact]
    public void System_section_reads_cpu_cores_gpu_memory_temperatures_and_devices()
    {
        var plan = SamplingPlanner.Compute(new SamplingInputs { Visible = MonitorSurface.System });
        Assert.True(plan.Cpu && plan.CpuCores && plan.GpuUsage && plan.Memory && plan.CpuTemperature && plan.GpuTemperature && plan.ConnectedDevices);
        Assert.False(plan.Network || plan.Disk || plan.Power);
    }

    [Fact]
    public void Per_core_is_never_read_for_readouts_alerts_details_or_the_full_surface()
    {
        Assert.False(SamplingPlanner.Compute(new SamplingInputs { ReadoutsActive = true, ReadoutCpu = true, AlertCpu = true }).CpuCores);
        Assert.False(SamplingPlanner.Compute(new SamplingInputs { Details = new HashSet<MetricKind> { MetricKind.Cpu } }).CpuCores);
        Assert.False(SamplingPlanner.Compute(new SamplingInputs { FullSurface = true }).CpuCores);
        Assert.False(SamplingPlanner.Compute(new SamplingInputs { Visible = MonitorSurface.System, SysCpuCores = false }).CpuCores);
    }

    [Fact]
    public void Readouts_count_only_while_they_have_a_consumer()
    {
        Assert.False(SamplingPlanner.Compute(new SamplingInputs { ReadoutNetwork = true }).Network);
        Assert.True(SamplingPlanner.Compute(new SamplingInputs { ReadoutsActive = true, ReadoutNetwork = true }).Network);
    }

    [Fact]
    public void Battery_needs_follow_the_hardware()
    {
        var desktop = SamplingPlanner.Compute(new SamplingInputs { ReadoutsActive = true, ReadoutBattery = true, AlertBattery = true, AlertBatteryTemperature = true });
        Assert.False(desktop.Power);
        Assert.False(desktop.BatteryTemperature);
        var laptop = SamplingPlanner.Compute(new SamplingInputs { HasBattery = true, ReadoutsActive = true, ReadoutBattery = true });
        Assert.True(laptop.Power);
        var watts = SamplingPlanner.Compute(new SamplingInputs { ReadoutsActive = true, ReadoutPower = true });
        Assert.True(watts.Power && watts.PowerDraw);
    }

    [Fact]
    public void Uninstalled_families_never_sample_even_when_pinned_or_alerting()
    {
        var plan = SamplingPlanner.Compute(new SamplingInputs
        {
            Visible = MonitorSurface.System | MonitorSurface.Power,
            HasBattery = true,
            ReadoutsActive = true,
            ReadoutCpu = true,
            AlertCpu = true,
            AlertCpuTemperature = true,
            ReadoutPeripheralBattery = true,
            HasCpuFamily = false,
            HasPowerFamily = false,
            HasConnectedDevicesFamily = false,
        });
        Assert.False(plan.Cpu || plan.CpuCores || plan.CpuTemperature);
        Assert.False(plan.Power || plan.PowerDraw || plan.PeripheralBattery || plan.BatteryTemperature);
        Assert.False(plan.ConnectedDevices);
        Assert.True(plan.Memory && plan.GpuUsage);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 16)]
    [InlineData(5, 15)]
    public void Only_temperature_pinned_wakes_every_fifteen_seconds(int interval, double period)
    {
        var plan = new SamplingPlan { CpuTemperature = true };
        var cadence = new SamplingCadence();
        cadence.Configure(plan, interval, foreground: false);
        Assert.Equal(period, cadence.PeriodSeconds(interval));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Only_peripheral_battery_wakes_once_a_minute(int interval)
    {
        var cadence = new SamplingCadence();
        cadence.Configure(new SamplingPlan { PeripheralBattery = true }, interval, foreground: false);
        Assert.Equal(60, cadence.PeriodSeconds(interval));
    }

    [Fact]
    public void Cpu_and_temperature_wake_every_tick_and_read_temperature_on_its_stride()
    {
        var plan = new SamplingPlan { Cpu = true, CpuTemperature = true };
        var cadence = new SamplingCadence();
        cadence.Configure(plan, 1, foreground: false);
        Assert.Equal(1, cadence.WakeTicks);
        var temperatureTicks = new List<long>();
        for (var i = 0; i < 31; i++)
        {
            var due = cadence.Due(plan, 1, foreground: false);
            Assert.Contains(SampleKind.Cpu, due);
            if (due.Contains(SampleKind.Temperature))
            {
                temperatureTicks.Add(cadence.Tick);
            }

            cadence.Advance();
        }

        Assert.Equal([0L, 15L, 30L], temperatureTicks);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 5)]
    [InlineData(5, 2)]
    public void Background_disk_stays_under_its_fifteen_second_gap(int interval, int stride)
    {
        Assert.Equal(stride, SamplingCadence.Stride(SampleKind.Disk, interval, foreground: false));
        Assert.True(stride * interval < MonitorEngine.DiskMaxGap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Pinned_watts_honour_the_base_interval_while_charge_alone_stays_slow(int interval)
    {
        Assert.Equal(1, SamplingCadence.Stride(SampleKind.PowerDraw, interval, foreground: false));
        Assert.True(SamplingCadence.Stride(SampleKind.Power, interval, foreground: false) * interval >= 15);
    }

    [Fact]
    public void Foreground_reads_connected_devices_every_two_seconds_and_peripherals_every_fifteen()
    {
        Assert.Equal(2, SamplingCadence.Stride(SampleKind.ConnectedDevices, 1, foreground: true));
        Assert.Equal(1, SamplingCadence.Stride(SampleKind.ConnectedDevices, 2, foreground: true));
        Assert.Equal(15, SamplingCadence.Stride(SampleKind.PeripheralBattery, 1, foreground: true));
        Assert.Equal(8, SamplingCadence.Stride(SampleKind.PeripheralBattery, 2, foreground: true));
    }

    [Fact]
    public void Tick_is_rounded_up_onto_the_new_grid_when_the_wake_changes()
    {
        var cadence = new SamplingCadence();
        cadence.Configure(new SamplingPlan { Cpu = true }, 1, foreground: false);
        for (var i = 0; i < 7; i++)
        {
            cadence.Advance();
        }

        Assert.Equal(7, cadence.Tick);
        cadence.Configure(new SamplingPlan { GpuUsage = true }, 1, foreground: false);
        Assert.Equal(10, cadence.WakeTicks);
        Assert.Equal(10, cadence.Tick);
        Assert.Contains(SampleKind.GpuUsage, cadence.Due(new SamplingPlan { GpuUsage = true }, 1, false));
        cadence.Advance();
        Assert.Equal(20, cadence.Tick);
    }

    [Fact]
    public void Gcd_of_strides_sets_the_wake()
    {
        // Background, I = 1: GPU 10 and connected devices 10 → wake every 10 ticks; adding power (15) → gcd 5.
        Assert.Equal(10, SamplingCadence.WakeFor(new SamplingPlan { GpuUsage = true, ConnectedDevices = true }, 1, false));
        Assert.Equal(5, SamplingCadence.WakeFor(new SamplingPlan { GpuUsage = true, Power = true }, 1, false));
        Assert.Equal(1, SamplingCadence.WakeFor(SamplingPlan.None, 1, false));
    }
}
