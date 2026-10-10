// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// CPU and GPU temperatures through LibreHardwareMonitorLib.
/// <list type="bullet">
/// <item>GPU: the vendor interfaces (NVIDIA NVAPI, AMD ADL, Intel IGCL), which
/// answer a standard user.</item>
/// <item>CPU: the processor's own sensors (Intel digital thermal sensors, AMD
/// Tctl/Tdie). Those are model-specific registers, so they need the PawnIO
/// driver, which the user installs, and an elevated process: this one when
/// the app runs as administrator, otherwise the sensor helper
/// (<see cref="SensorHelperClient"/>). This app does not install a driver.</item>
/// </list>
/// The library runs x64 machine code for CPUID, so it is used on x64 only.
/// It opens on the first read, on the sampler's thread, and stops asking a
/// source that never answered.
/// </summary>
public sealed class HardwareSensors : IDisposable
{
    private const int AttemptsBeforeGivingUp = 3;

    private readonly object _gate = new();
    private readonly Source _cpu = new();
    private readonly Source _gpu = new();
    private Computer? _computer;
    private bool _failed;
    private bool _disposed;
    private bool? _driverInstalled;

    /// <summary>A source that is dropped for the session when it never produced a reading.</summary>
    private sealed class Source
    {
        private int _misses;
        private bool _worked;

        public bool GivenUp { get; private set; }

        public void Record(bool success)
        {
            if (success)
            {
                _worked = true;
                _misses = 0;
            }
            else if (!_worked && ++_misses >= AttemptsBeforeGivingUp)
            {
                GivenUp = true;
            }
        }
    }

    /// <summary>The library can run in this process.</summary>
    public static bool IsSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>The PawnIO driver, which the CPU's own sensors are read through, is installed.</summary>
    public bool SensorDriverInstalled
    {
        get
        {
            lock (_gate)
            {
                return _driverInstalled ??= QueryDriverInstalled();
            }
        }
    }

    /// <summary>The hottest processor's temperature in °C, or null when its sensors cannot be read.</summary>
    public double? ReadCpuTemperature()
    {
        lock (_gate)
        {
            // Without the driver every register reads as zero; do not walk the cores for nothing.
            if (_cpu.GivenUp || !IsSupported || !(_driverInstalled ??= QueryDriverInstalled()))
            {
                return null;
            }

            var value = Hottest(static type => type == HardwareType.Cpu, TemperatureSelection.Cpu);
            _cpu.Record(value is not null);
            if (_cpu.GivenUp)
            {
                Log.Info("monitor", Environment.IsPrivilegedProcess
                    ? "The processor's sensors gave no temperature."
                    : "The processor's sensors need an elevated process.");
            }

            return value;
        }
    }

    /// <summary>The hottest graphics adapter's temperature in °C, or null when no vendor interface reports one.</summary>
    public double? ReadGpuTemperature()
    {
        lock (_gate)
        {
            if (_gpu.GivenUp || !IsSupported)
            {
                return null;
            }

            var value = Hottest(
                static type => type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel,
                TemperatureSelection.Gpu);
            _gpu.Record(value is not null);
            return value;
        }
    }

    private double? Hottest(Func<HardwareType, bool> wanted, Func<IEnumerable<NamedTemperature>, double?> pick)
    {
        if (Open() is not { } computer)
        {
            return null;
        }

        double? hottest = null;
        foreach (var hardware in computer.Hardware)
        {
            if (!wanted(hardware.HardwareType))
            {
                continue;
            }

            try
            {
                hardware.Update();
                var sensors = hardware.Sensors
                    .Where(s => s.SensorType == SensorType.Temperature)
                    .Select(s => new NamedTemperature(s.Name, s.Value));
                if (pick(sensors) is { } value && (hottest is null || value > hottest))
                {
                    hottest = value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("monitor", $"Reading the sensors of {hardware.Name} failed.", ex);
            }
        }

        return hottest;
    }

    private Computer? Open()
    {
        if (_computer is not null || _failed || _disposed)
        {
            return _computer;
        }

        try
        {
            // The Intel graphics group hangs off the processor group, so both are enabled.
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            computer.Open();
            _computer = computer;
            Log.Info("monitor", $"Hardware sensors opened: {string.Join(", ", computer.Hardware.Select(h => h.Name))}.");
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Warn("monitor", "The hardware sensor library could not start.", ex);
        }

        return _computer;
    }

    private static bool QueryDriverInstalled()
    {
        try
        {
            return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled;
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", "Could not tell whether the PawnIO driver is installed.", ex);
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                Log.Warn("monitor", "Closing the hardware sensor library failed.", ex);
            }

            _computer = null;
        }
    }
}
