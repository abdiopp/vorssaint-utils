// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

public class MonitorMathTests
{
    [Fact]
    public void Cpu_reader_baselines_then_measures_and_resets_after_a_long_gap()
    {
        var cpu = new CpuUsageCalculator();
        // user, system, idle = (100, 100, 800) then (200, 200, 1400): busy share 200/800.
        Assert.Null(cpu.Update(new CpuTicks(200, 1000), 0));
        Assert.Equal(0.25, cpu.Update(new CpuTicks(400, 1800), 2)!.Value, 6);
        Assert.Null(cpu.Update(new CpuTicks(800, 2800), 62));
        Assert.True(cpu.GapReset);
        Assert.Equal(0.5, cpu.Update(new CpuTicks(900, 3000), 64)!.Value, 6);
        Assert.False(cpu.GapReset);
    }

    [Fact]
    public void Cpu_reader_returns_nothing_when_total_did_not_grow()
    {
        var cpu = new CpuUsageCalculator();
        cpu.Update(new CpuTicks(10, 100), 0);
        Assert.Null(cpu.Update(new CpuTicks(10, 100), 1));
    }

    [Fact]
    public void Per_core_handles_wrap_reset_and_core_count_changes()
    {
        var cores = new PerCoreUsageCalculator(thirtyTwoBitCounters: true);
        Assert.All(cores.Update([new CpuTicks(0, 0), new CpuTicks(0, 0)], 0), v => Assert.Null(v));
        var second = cores.Update([new CpuTicks(50, 100), new CpuTicks(25, 100)], 1);
        Assert.Equal(0.5, second[0]!.Value, 6);
        Assert.Equal(0.25, second[1]!.Value, 6);

        // Core 0 wraps around 2^32 (large backwards step); core 1 resets (small step back).
        var wrapped = cores.Update([new CpuTicks(uint.MaxValue - 10UL + 0, uint.MaxValue - 50UL), new CpuTicks(20, 90)], 2);
        Assert.Null(wrapped[1]);
        var afterWrap = cores.Update([new CpuTicks(39, 49), new CpuTicks(30, 100)], 3);
        Assert.Equal(0.5, afterWrap[0]!.Value, 2);

        Assert.All(cores.Update([new CpuTicks(1, 1)], 4), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(0.03, 0.80, 0.23)]
    [InlineData(0.23, 0.80, 0.43)]
    [InlineData(0.60, 0.10, 0.275)]
    public void Gpu_smoothing_vectors(double previous, double raw, double expected) =>
        Assert.Equal(expected, GpuSmoothing.Apply(previous, raw), 6);

    [Fact]
    public void Gpu_smoothing_clamps_the_first_value() => Assert.Equal(1.0, GpuSmoothing.Apply(null, 1.4));

    [Fact]
    public void Sustained_gate_ignores_single_spikes()
    {
        var gate = new SustainedGate();
        Assert.False(gate.ShouldAlert(0.99, 0.9, 100));
        Assert.False(gate.ShouldAlert(0.5, 0.9, 102));
        Assert.False(gate.ShouldAlert(0.99, 0.9, 104));
    }

    [Fact]
    public void Sustained_gate_fires_after_twelve_seconds_of_distinct_reads()
    {
        var gate = new SustainedGate();
        Assert.False(gate.ShouldAlert(99, 90, 100));
        Assert.False(gate.ShouldAlert(99, 90, 108));
        Assert.True(gate.ShouldAlert(99, 90, 112));
    }

    [Fact]
    public void Sustained_gate_never_ages_a_re_served_reading()
    {
        var gate = new SustainedGate();
        for (var i = 0; i < 60; i++)
        {
            Assert.False(gate.ShouldAlert(99, 90, 100));
        }
    }

    [Fact]
    public void Sustained_gate_restarts_below_threshold_and_ignores_missing_values()
    {
        var gate = new SustainedGate();
        Assert.False(gate.ShouldAlert(99, 90, 100));
        Assert.False(gate.ShouldAlert(50, 90, 106));
        Assert.False(gate.ShouldAlert(99, 90, 110));
        Assert.False(gate.ShouldAlert(99, 90, 120));
        Assert.True(gate.ShouldAlert(99, 90, 122));
        Assert.False(gate.ShouldAlert(null, 90, 130));
        Assert.False(gate.ShouldAlert(99, 90, null));
    }

    [Fact]
    public void Network_failures_keep_the_previous_sample_for_short_gap_averaging()
    {
        var sampler = new RateSampler(MonitorEngine.NetworkMaxGap);
        Assert.Equal((null, null), sampler.Update(1000, 500, 0));
        Assert.Equal((500.0, 250.0), sampler.Update(2000, 1000, 2));
        // Reads at t = 3 and 4 failed: the caller skips them; t = 5 averages over 3 s.
        Assert.Equal((1000.0, 500.0), sampler.Update(5000, 2500, 5));
        Assert.Equal(4000UL, sampler.TotalA);
    }

    [Fact]
    public void Network_long_gaps_start_a_new_baseline_without_adding_to_totals()
    {
        var sampler = new RateSampler(MonitorEngine.NetworkMaxGap);
        sampler.Update(0, 0, 0);
        sampler.Update(1000, 1000, 1);
        Assert.Equal((null, null), sampler.Update(900_000, 900_000, 30));
        Assert.Equal(1000UL, sampler.TotalA);
        Assert.Equal((100.0, 100.0), sampler.Update(900_100, 900_100, 31));
    }

    [Fact]
    public void Counter_resets_count_as_zero_and_keep_totals()
    {
        var sampler = new RateSampler(MonitorEngine.NetworkMaxGap);
        sampler.Update(5000, 5000, 0);
        sampler.Update(6000, 6000, 1);
        Assert.Equal((0.0, 0.0), sampler.Update(100, 100, 2));
        Assert.Equal(1000UL, sampler.TotalA);
        Assert.Equal((50.0, 50.0), sampler.Update(150, 150, 3));
    }

    [Fact]
    public void History_keeps_order_and_capacity()
    {
        var ring = new HistoryRing(3);
        ring.Push(1);
        ring.Push(2);
        Assert.Equal([1.0, 2.0], ring.ToArray());
        ring.Push(3);
        ring.Push(4);
        Assert.Equal([2.0, 3.0, 4.0], ring.ToArray());
        for (var i = 0; i < 100; i++)
        {
            ring.Push(i);
        }

        Assert.Equal(3, ring.Count);
        ring.Push(double.NaN);
        Assert.Equal(3, ring.Count);
    }

    [Fact]
    public void Temperature_bridge_serves_the_cache_for_a_few_misses()
    {
        var bridge = new TemperatureBridge(10);
        Assert.Equal(55, bridge.Update(55, 0, 12));
        Assert.Equal(55, bridge.Update(null, 2, 12));
        Assert.Equal(55, bridge.Update(200, 4, 12));
        Assert.Equal(0, bridge.ReadAt);
        Assert.Null(bridge.Update(null, 13, 12));
        Assert.Null(bridge.Update(5, 14, 12));
        Assert.Equal(35.2, TemperatureBridge.BridgeSeconds(8, 2), 3);
    }

    [Fact]
    public void Held_values_survive_three_misses()
    {
        var held = new HeldValue<double>(3);
        held.Fresh(0.5, 1);
        held.Miss();
        held.Miss();
        held.Miss();
        Assert.Equal(0.5, held.Value);
        held.Miss();
        Assert.Null(held.Value);
        Assert.Null(held.ReadAt);
    }

    [Theory]
    [InlineData(16, 8, 10, 24, false, MemoryPressure.Normal)]
    [InlineData(16, 2, 10, 24, false, MemoryPressure.Warning)]
    [InlineData(16, 8, 21, 24, false, MemoryPressure.Warning)]
    [InlineData(16, 0.5, 10, 24, false, MemoryPressure.Critical)]
    [InlineData(16, 8, 23, 24, false, MemoryPressure.Critical)]
    [InlineData(16, 8, 10, 24, true, MemoryPressure.Critical)]
    public void Memory_pressure_heuristic(double total, double available, double commit, double limit, bool low, MemoryPressure expected)
    {
        const double g = 1024d * 1024 * 1024;
        var sample = new MemorySample { Total = (ulong)(total * g), Available = (ulong)(available * g), CommitTotal = (ulong)(commit * g), CommitLimit = (ulong)(limit * g), LowMemorySignaled = low };
        Assert.Equal(expected, MemoryPressureHeuristic.Evaluate(sample));
    }

    [Theory]
    [InlineData(-1d, false, false, null)]
    [InlineData(65_535d * 60, false, false, null)]
    [InlineData(13_320d, true, false, null)]
    [InlineData(13_320d, false, true, null)]
    [InlineData(30d, false, false, null)]
    [InlineData(13_320d, false, false, 13_320d)]
    public void Battery_time_validity(double seconds, bool external, bool charging, double? expected) =>
        Assert.Equal(expected, BatteryMath.ValidTimeRemaining(seconds, external, charging));

    [Fact]
    public void Battery_health_and_fallbacks()
    {
        Assert.Equal(93, BatteryMath.Health(46_500, 50_000));
        Assert.Equal(100, BatteryMath.Health(52_000, 50_000));
        Assert.Null(BatteryMath.Health(0, 50_000));
        Assert.Equal(9.4, BatteryMath.SystemWattsFallback(-9.4, externalConnected: false));
        Assert.Null(BatteryMath.SystemWattsFallback(-9.4, externalConnected: true));
        Assert.Null(BatteryMath.SystemWattsFallback(5, externalConnected: false));
        Assert.Equal([100, 75, 50, 25, 0], new[] { 90, 70, 40, 15, 5 }.Select(BatteryMath.GlyphLevel));
    }

    [Fact]
    public void Process_cpu_share_of_the_whole_machine()
    {
        // 1.8 s of CPU over 2 s on 18 logical CPUs = 5 %.
        Assert.Equal(5, ProcessCpuMath.Percent(18_000_000, 2, 18), 6);
    }

    [Fact]
    public void Process_rows_are_reconciled_with_the_aggregate()
    {
        var rows = new[]
        {
            new ProcessUsageRow { Key = "a", Pid = 1, Name = "A", Value = 60 },
            new ProcessUsageRow { Key = "b", Pid = 2, Name = "B", Value = 40 },
        };
        var reconciled = ProcessCpuMath.Reconcile(rows, 50);
        Assert.Equal(30, reconciled[0].Value, 6);
        Assert.Equal(20, reconciled[1].Value, 6);
        var unchanged = ProcessCpuMath.Reconcile(rows, 150);
        Assert.Equal(60, unchanged[0].Value, 6);
    }

    [Fact]
    public void Energy_sums_cpu_and_gpu_and_drops_small_rows()
    {
        var cpu = new[] { new ProcessUsageRow { Key = "a", Pid = 1, Name = "A", Value = 5 }, new ProcessUsageRow { Key = "b", Pid = 2, Name = "B", Value = 1 } };
        var gpu = new[] { new ProcessUsageRow { Key = "a", Pid = 1, Name = "A", Value = 3 }, new ProcessUsageRow { Key = "c", Pid = 3, Name = "C", Value = 2.5 } };
        var energy = ProcessCpuMath.Energy(cpu, gpu);
        Assert.Equal(["a", "c"], energy.Select(r => r.Key));
        Assert.Equal(8, energy[0].Value, 6);
    }

    [Fact]
    public void Natural_order_sorts_numbers_by_value()
    {
        var names = new[] { "Disk 10", "Disk 2", "Data", "disk 1" };
        Assert.Equal(["Data", "disk 1", "Disk 2", "Disk 10"], names.OrderBy(n => n, NaturalComparer.Instance));
    }
}
