// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>One temperature sensor of a hardware monitoring source, by its name there.</summary>
public readonly record struct NamedTemperature(string Name, double? Celsius);

/// <summary>
/// Picks the figure to show from the sensors a source reports (spec §3.5:
/// valid readings only, the CPU's headline sensor, otherwise the hottest).
/// Names are the ones LibreHardwareMonitor uses.
/// </summary>
public static class TemperatureSelection
{
    // Headline CPU sensors in order of preference: Intel's package sensor,
    // AMD's control temperature, then the hottest core.
    private static readonly string[] CpuPreferred =
    [
        "CPU Package",
        "Core (Tctl/Tdie)",
        "Core (Tdie)",
        "Core Max",
        "CCDs Max (Tdie)",
        "CPU Cores",
        "Core (Tctl)",
    ];

    public static bool IsValid(double? celsius) => celsius is { } v && double.IsFinite(v) && v > 1 && v < 125;

    /// <summary>The CPU temperature from one processor's sensors, or null without a valid reading.</summary>
    public static double? Cpu(IEnumerable<NamedTemperature> sensors)
    {
        // "Distance to TjMax" counts down to the limit and averages hide a hot core.
        var valid = sensors
            .Where(s => IsValid(s.Celsius)
                && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
                && !s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var name in CpuPreferred)
        {
            foreach (var sensor in valid)
            {
                if (string.Equals(sensor.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return sensor.Celsius;
                }
            }
        }

        return valid.Count == 0 ? null : valid.Max(s => s.Celsius);
    }

    /// <summary>The GPU temperature from one adapter's sensors: its core sensor, otherwise the hottest.</summary>
    public static double? Gpu(IEnumerable<NamedTemperature> sensors)
    {
        var valid = sensors.Where(s => IsValid(s.Celsius)).ToList();
        foreach (var sensor in valid)
        {
            if (string.Equals(sensor.Name, "GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                return sensor.Celsius;
            }
        }

        return valid.Count == 0 ? null : valid.Max(s => s.Celsius);
    }

    /// <summary>
    /// The CPU temperature from ACPI thermal zones given in kelvin: the
    /// hottest zone named after the CPU, otherwise the hottest zone.
    /// </summary>
    public static double? ThermalZone(IEnumerable<(string Zone, double Kelvin)> zones)
    {
        double? cpu = null;
        double? any = null;
        foreach (var (zone, kelvin) in zones)
        {
            var celsius = kelvin - 273.15;
            if (!IsValid(celsius))
            {
                continue;
            }

            any = any is { } a ? Math.Max(a, celsius) : celsius;
            if (zone.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            {
                cpu = cpu is { } c ? Math.Max(c, celsius) : celsius;
            }
        }

        return cpu ?? any;
    }
}
