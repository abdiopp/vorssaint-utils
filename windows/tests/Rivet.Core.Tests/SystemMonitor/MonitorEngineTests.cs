// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

public class MonitorEngineTests
{
    private static RefreshRequest All(SamplingPlan plan, bool foreground = true) =>
        new() { Plan = plan, Due = SamplingCadence.NeededKinds(plan), Foreground = foreground, IntervalSeconds = 2 };

    [Fact]
    public void Cpu_holds_through_three_failed_reads_then_clears()
    {
        var sensors = new ScriptedSensors();
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { Cpu = true };
        sensors.Total = new CpuTicks(0, 0);
        engine.Refresh(All(plan));
        sensors.Now = 2;
        sensors.Total = new CpuTicks(50, 100);
        Assert.Equal(0.5, engine.Refresh(All(plan)).Snapshot.CpuUsage);
        sensors.Total = null;
        for (var i = 0; i < 3; i++)
        {
            sensors.Now += 2;
            Assert.Equal(0.5, engine.Refresh(All(plan)).Snapshot.CpuUsage);
        }

        sensors.Now += 2;
        Assert.Null(engine.Refresh(All(plan)).Snapshot.CpuUsage);
    }

    [Fact]
    public void A_long_gap_drops_the_held_cpu_value_and_its_read_time()
    {
        var sensors = new ScriptedSensors { Total = new CpuTicks(0, 0) };
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { Cpu = true };
        engine.Refresh(All(plan));
        sensors.Now = 2;
        sensors.Total = new CpuTicks(50, 100);
        var first = engine.Refresh(All(plan)).Snapshot;
        Assert.Equal(2, first.CpuUsageReadAt);
        sensors.Now = 62;
        sensors.Total = new CpuTicks(80, 200);
        var afterGap = engine.Refresh(All(plan)).Snapshot;
        Assert.Null(afterGap.CpuUsage);
        Assert.Null(afterGap.CpuUsageReadAt);
    }

    [Fact]
    public void Memory_holds_four_misses_and_unknown_pressure_never_overwrites_a_known_level()
    {
        const ulong g = 1024UL * 1024 * 1024;
        var sensors = new ScriptedSensors { MemorySample = new MemorySample { Total = 16 * g, Available = (ulong)(0.5 * g) } };
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { Memory = true };
        var first = engine.Refresh(All(plan)).Snapshot;
        Assert.Equal(MemoryPressure.Critical, first.Memory!.Pressure);
        Assert.Equal(15.5 * g, first.Memory.Used, 0);
        sensors.MemorySample = null;
        sensors.Now = 2;
        Assert.NotNull(engine.Refresh(All(plan)).Snapshot.Memory);
        sensors.Now = 13;
        Assert.Null(engine.Refresh(All(plan)).Snapshot.Memory);
    }

    [Fact]
    public void Histories_are_kept_in_background_but_published_only_in_foreground()
    {
        var sensors = new ScriptedSensors { Total = new CpuTicks(0, 0) };
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { Cpu = true };
        for (var i = 1; i <= 5; i++)
        {
            sensors.Now = i;
            sensors.Total = new CpuTicks((ulong)(i * 30), (ulong)(i * 100));
            var background = engine.Refresh(All(plan, foreground: false)).Snapshot;
            Assert.Empty(background.Histories.Cpu);
        }

        var foreground = engine.Refresh(All(plan with { }, foreground: true)).Snapshot;
        Assert.Equal(4, foreground.Histories.Cpu.Length);
    }

    [Fact]
    public void Gpu_is_smoothed_and_re_served_while_suppressed()
    {
        var sensors = new ScriptedSensors { GpuRaw = 0.03 };
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { GpuUsage = true };
        Assert.Equal(0.03, engine.Refresh(All(plan)).Snapshot.GpuUsage!.Value, 6);
        sensors.GpuRaw = 0.80;
        var suppressed = engine.Refresh(All(plan) with { SkipGpu = true }).Snapshot;
        Assert.Equal(0.03, suppressed.GpuUsage!.Value, 6);
        Assert.Equal(0.23, engine.Refresh(All(plan)).Snapshot.GpuUsage!.Value, 6);
    }

    [Fact]
    public void Disk_rates_are_per_physical_disk_and_volumes_are_sorted()
    {
        var sensors = new ScriptedSensors
        {
            Volumes =
            [
                new DiskVolumeInfo { Id = "e", Name = "Stick", MountPath = "E:\\", IsInternal = false, Total = 64_000_000_000, Free = 1, DiskId = "PhysicalDrive2" },
                new DiskVolumeInfo { Id = "d", Name = "Data", MountPath = "D:\\", Total = 1_000_000_000_000, Free = 1, DiskId = "PhysicalDrive0" },
                new DiskVolumeInfo { Id = "c", Name = "Windows", MountPath = "C:\\", IsSystem = true, Total = 1_000_000_000_000, Free = 1, DiskId = "PhysicalDrive0" },
                new DiskVolumeInfo { Id = "z", Name = "Empty", MountPath = "Z:\\", Total = 0, Free = 0 },
            ],
            DiskCounters = new() { ["PhysicalDrive0"] = new DiskCounters(1000, 2000), ["PhysicalDrive2"] = new DiskCounters(0, 0) },
        };
        var engine = new MonitorEngine(sensors.Bundle);
        var plan = new SamplingPlan { Disk = true };
        var first = engine.Refresh(All(plan)).Snapshot;
        Assert.Equal(["Windows", "Data", "Stick"], first.Disks.Select(d => d.Info.Name));
        Assert.Null(first.DiskReadBytesPerSec);
        sensors.Now = 2;
        sensors.DiskCounters = new() { ["PhysicalDrive0"] = new DiskCounters(5000, 4000), ["PhysicalDrive2"] = new DiskCounters(1000, 0) };
        var second = engine.Refresh(All(plan)).Snapshot;
        // C: and D: share PhysicalDrive0 and are counted once.
        Assert.Equal(2500, second.DiskReadBytesPerSec);
        Assert.Equal(1000, second.DiskWriteBytesPerSec);
        Assert.Equal(4000UL, second.Disks[0].SessionRead);
    }

    [Fact]
    public void Families_outside_the_plan_are_not_published()
    {
        var sensors = new ScriptedSensors { Total = new CpuTicks(1, 2), Counters = new NetworkCounters(1, 1) };
        var engine = new MonitorEngine(sensors.Bundle);
        var snapshot = engine.Refresh(All(new SamplingPlan { Network = true })).Snapshot;
        Assert.Null(snapshot.CpuUsage);
        Assert.Equal(0, sensors.Reads);
    }

    [Fact]
    public void Service_is_idle_at_rest_and_samples_while_a_surface_is_leased()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        var sensors = new ScriptedSensors { Total = new CpuTicks(0, 0) };
        using var service = new SystemMonitorService(sensors.Bundle, store, runtime);
        Assert.False(service.CurrentPlan.Any);
        Assert.False(service.IsRunning);
        using (service.AcquireSurface(MonitorSurface.System))
        {
            var snapshot = service.RefreshNow();
            Assert.True(snapshot.Plan.Cpu);
            Assert.True(snapshot.Foreground);
        }

        var after = service.RefreshNow();
        Assert.False(after.Plan.Any);
    }

    [Fact]
    public void Hub_removal_stops_the_family()
    {
        var store = SettingsStore.InMemory();
        var runtime = new FeatureRuntime(store);
        store.Set(MonitorSettings.AlertCpu, true);
        var sensors = new ScriptedSensors { Total = new CpuTicks(0, 0) };
        using var service = new SystemMonitorService(sensors.Bundle, store, runtime);
        Assert.True(service.RefreshNow().Plan.Cpu);
        runtime.SetAvailable(FeatureIds.MonitorCpu, false);
        Assert.False(service.RefreshNow().Plan.Cpu);
    }
}
