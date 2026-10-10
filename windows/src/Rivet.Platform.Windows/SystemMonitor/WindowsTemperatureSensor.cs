// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// CPU temperature, from the best source this PC offers:
/// <list type="number">
/// <item>the processor's own sensors, when the PawnIO driver is installed:
/// in this process if the app runs as administrator (<see cref="HardwareSensors"/>),
/// otherwise through the elevated sensor helper (<see cref="SensorHelperClient"/>);</item>
/// <item>the ACPI thermal zones from the "Thermal Zone Information"
/// performance counters, which a standard user may read;</item>
/// <item>the same zones from WMI (root\WMI MSAcpi_ThermalZoneTemperature),
/// which most PCs allow only to administrators.</item>
/// </list>
/// Thermal zones are what the firmware reports: usually close to the CPU on
/// laptops, absent or fixed on many desktops.
/// </summary>
public sealed class WindowsTemperatureSensor : ITemperatureSensor, IDisposable
{
    private readonly object _gate = new();
    private readonly HardwareSensors _hardware;
    private readonly SensorHelperClient _helper;
    private readonly ThermalZoneCounters _zones = new();
    private readonly AcpiThermalZones _acpi = new();
    private volatile SensorAvailability _availability = SensorAvailability.Available;

    public WindowsTemperatureSensor(HardwareSensors hardware, SensorHelperClient helper)
    {
        _hardware = hardware;
        _helper = helper;
    }

    public SensorAvailability CpuAvailability => _availability;

    public double? ReadCpuTemperature()
    {
        lock (_gate)
        {
            var value = _hardware.ReadCpuTemperature() ?? _helper.ReadCpuTemperature();
            if (value is null && _helper.WorkedRecently)
            {
                // A hiccup of the helper: the sampler bridges it. A thermal zone is another
                // sensor with another figure, and must not flash in between.
                return null;
            }

            value ??= _zones.Read() ?? _acpi.Read();
            _availability = value is null ? Explain() : SensorAvailability.Available;
            return value;
        }
    }

    private SensorAvailability Explain()
    {
        if (HardwareSensors.IsSupported)
        {
            if (!_hardware.SensorDriverInstalled)
            {
                return SensorAvailability.NeedsSensorDriver;
            }

            if (!Environment.IsPrivilegedProcess)
            {
                return _helper.State switch
                {
                    SensorHelperState.NotInstalled => SensorAvailability.NeedsSensorHelper,
                    SensorHelperState.Installed => SensorAvailability.NotReported,
                    _ => SensorAvailability.NeedsAdministrator,
                };
            }

            return SensorAvailability.NotReported;
        }

        return _acpi.Availability;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _zones.Dispose();
            _acpi.Dispose();
        }
    }
}

/// <summary>
/// ACPI thermal zones from the "Thermal Zone Information" performance
/// counters: "High Precision Temperature" in tenths of a kelvin, "Temperature"
/// in whole kelvin on older builds. No administrator rights needed.
/// </summary>
internal sealed class ThermalZoneCounters : IDisposable
{
    private const string PrecisePath = @"\Thermal Zone Information(*)\High Precision Temperature";
    private const string CoarsePath = @"\Thermal Zone Information(*)\Temperature";
    private const int AttemptsBeforeGivingUp = 3;

    private PdhCounterSet? _pdh;
    private bool _unavailable;
    private bool _worked;
    private int _misses;

    public double? Read()
    {
        if (_unavailable)
        {
            return null;
        }

        _pdh ??= PdhCounterSet.Open(PrecisePath, CoarsePath);
        if (_pdh is null)
        {
            _unavailable = true;
            Log.Info("monitor", "This PC has no thermal zone performance counters.");
            return null;
        }

        double? value = null;
        if (_pdh.Collect())
        {
            value = TemperatureSelection.ThermalZone(_pdh.Values(PrecisePath).Select(v => (v.Instance, v.Value / 10.0)))
                ?? TemperatureSelection.ThermalZone(_pdh.Values(CoarsePath));
        }

        if (value is not null)
        {
            _worked = true;
            _misses = 0;
        }
        else if (!_worked && ++_misses >= AttemptsBeforeGivingUp)
        {
            // No zone, or zones that report nothing usable: stop collecting.
            _unavailable = true;
            Dispose();
        }

        return value;
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        _pdh = null;
    }
}

/// <summary>
/// ACPI thermal zones from WMI. After an "access denied" or "not supported"
/// answer it stops asking for the rest of the session.
/// </summary>
internal sealed class AcpiThermalZones : IDisposable
{
    private readonly WmiClient _wmi = new(@"ROOT\WMI");
    private bool _givenUp;
    private DateTime _retryAfter = DateTime.MinValue;

    public SensorAvailability Availability { get; private set; } = SensorAvailability.NotReported;

    public double? Read()
    {
        if (_givenUp || DateTime.UtcNow < _retryAfter)
        {
            return null;
        }

        try
        {
            var rows = _wmi.Query("SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", "InstanceName", "CurrentTemperature");
            var zones = new List<(string Zone, double Kelvin)>();
            foreach (var row in rows)
            {
                if (row["CurrentTemperature"] is IConvertible raw)
                {
                    // Tenths of a kelvin.
                    zones.Add((row["InstanceName"] as string ?? string.Empty, raw.ToDouble(CultureInfo.InvariantCulture) / 10.0));
                }
            }

            var hottest = TemperatureSelection.ThermalZone(zones);
            if (hottest is null)
            {
                Availability = SensorAvailability.NotReported;
                _givenUp = rows.Count == 0;
                _retryAfter = DateTime.UtcNow.AddMinutes(5);
            }
            else
            {
                Availability = SensorAvailability.Available;
            }

            return hottest;
        }
        catch (Exception ex)
        {
            switch (WmiClient.Classify(ex))
            {
                case WmiFailure.AccessDenied:
                    Availability = SensorAvailability.NeedsAdministrator;
                    _givenUp = true;
                    break;
                case WmiFailure.NotSupported:
                    Availability = SensorAvailability.NotReported;
                    _givenUp = true;
                    break;
                default:
                    Availability = SensorAvailability.NotReported;
                    _retryAfter = DateTime.UtcNow.AddMinutes(5);
                    Log.Warn("monitor", "Reading the ACPI thermal zone failed.", ex);
                    break;
            }

            return null;
        }
    }

    public void Dispose() => _wmi.Dispose();
}
