// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

public class TemperatureSelectionTests
{
    private static NamedTemperature T(string name, double? celsius) => new(name, celsius);

    [Fact]
    public void Intel_shows_the_package_sensor_and_ignores_the_distance_to_the_limit()
    {
        var sensors = new[]
        {
            T("Core Max", 71), T("Core Average", 58), T("CPU Core #1", 71), T("CPU Core #2", 49),
            T("CPU Package", 73), T("CPU Core #1 Distance to TjMax", 29), T("CPU Core #2 Distance to TjMax", 51),
        };
        Assert.Equal(73, TemperatureSelection.Cpu(sensors));
    }

    [Fact]
    public void Amd_shows_the_control_temperature()
    {
        Assert.Equal(64.5, TemperatureSelection.Cpu([T("Core (Tctl/Tdie)", 64.5), T("CCD1 (Tdie)", 70)]));
        Assert.Equal(55, TemperatureSelection.Cpu([T("Core (Tctl)", 82), T("Core (Tdie)", 55)]));
    }

    [Fact]
    public void Cpu_falls_back_to_the_hottest_core()
    {
        Assert.Equal(66, TemperatureSelection.Cpu([T("CPU Core #1", 61), T("CPU Core #2", 66), T("Core Average", 90)]));
    }

    [Fact]
    public void Readings_without_the_driver_are_not_a_temperature()
    {
        // The registers read as zero (or nothing) when the driver did not load.
        Assert.Null(TemperatureSelection.Cpu([T("CPU Package", null), T("Core Max", null)]));
        Assert.Null(TemperatureSelection.Cpu([T("Core (Tctl/Tdie)", 0), T("CCD1 (Tdie)", -49)]));
        Assert.Null(TemperatureSelection.Cpu([T("CPU Package", 255), T("CPU Core #1", double.NaN)]));
        Assert.Null(TemperatureSelection.Cpu([]));
    }

    [Fact]
    public void An_unreadable_headline_sensor_does_not_hide_the_cores()
    {
        Assert.Equal(52, TemperatureSelection.Cpu([T("CPU Package", null), T("CPU Core #1", 52)]));
    }

    [Fact]
    public void Gpu_shows_the_core_sensor_over_the_hot_spot()
    {
        Assert.Equal(63, TemperatureSelection.Gpu([T("GPU Hot Spot", 78), T("GPU Core", 63), T("GPU Memory Junction", 84)]));
        Assert.Equal(78, TemperatureSelection.Gpu([T("GPU Hot Spot", 78), T("GPU Core", 0)]));
        Assert.Null(TemperatureSelection.Gpu([T("GPU Core", null)]));
    }

    [Fact]
    public void Thermal_zones_prefer_the_cpu_zone_and_skip_unusable_ones()
    {
        // 3232 dK = 50.05 °C, 3182 dK = 45.05 °C; 0 K and 2732 dK (0.05 °C) are placeholders.
        var zones = new[] { (@"\_TZ.BATZ", 333.15), (@"\_TZ.CPUZ", 323.2), (@"\_TZ.TZ01", 0.0), (@"\_TZ.TZ02", 273.2) };
        Assert.Equal(50.05, TemperatureSelection.ThermalZone(zones)!.Value, 2);
        Assert.Equal(60, TemperatureSelection.ThermalZone([(@"\_TZ.THM0", 318.2), (@"\_TZ.THM1", 333.15)])!.Value, 2);
        Assert.Null(TemperatureSelection.ThermalZone([(@"\_TZ.TZ00", 0.0)]));
        Assert.Null(TemperatureSelection.ThermalZone([]));
    }
}
