// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Power;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>Raw battery figures summed over the internal batteries.</summary>
internal sealed record BatteryFacts(
    uint PowerState,
    long? RateMilliwatts,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    uint RemainingCapacity,
    int CycleCount,
    double? TemperatureCelsius);

/// <summary>
/// Power and battery: GetSystemPowerStatus for the AC line, charge and the
/// OS time estimate; the battery class driver (IOCTL_BATTERY_QUERY_*) for
/// charging state, flow in mW, design and full-charge capacity (health) and
/// cycle count. UPS units on the battery class are ignored. Windows has no
/// whole-system or adapter meter, so system draw is only known on battery.
/// </summary>
public sealed unsafe class WindowsPowerSensor : IPowerSensor, IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const byte NoSystemBattery = 128;
    private const byte UnknownStatus = 255;
    private const uint GenericReadWrite = 0x80000000 | 0x40000000;

    private readonly NativeWindowHost _host;
    private readonly WindowMessageHandler _handler;
    private readonly object _gate = new();
    private List<string>? _devicePaths;
    private DateTime _pathsAt = DateTime.MinValue;
    private bool? _hasBattery;

    public WindowsPowerSensor(NativeWindowHost host)
    {
        _host = host;
        _handler = OnMessage;
        _host.AddHandler(_handler);
    }

    public event EventHandler? PowerStatusChanged;

    public bool HasBattery
    {
        get
        {
            lock (_gate)
            {
                if (_hasBattery is { } known)
                {
                    return known;
                }

                var status = PInvoke.GetSystemPowerStatus(out var s) ? s : default;
                _hasBattery = status.BatteryFlag != NoSystemBattery && status.BatteryFlag != UnknownStatus && ReadBatteries() is not null;
                return _hasBattery.Value;
            }
        }
    }

    public PowerReading? Read()
    {
        if (!PInvoke.GetSystemPowerStatus(out var status))
        {
            return null;
        }

        var external = status.ACLineStatus == 1;
        if (!HasBattery)
        {
            return new PowerReading { ExternalConnected = external || status.ACLineStatus == UnknownStatus, HasBattery = false };
        }

        BatteryFacts? facts;
        lock (_gate)
        {
            facts = ReadBatteries();
        }

        var charging = facts is not null && (facts.PowerState & PInvoke.BATTERY_CHARGING) != 0;
        external |= facts is not null && (facts.PowerState & PInvoke.BATTERY_POWER_ON_LINE) != 0;
        int? charge = status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : facts is { FullChargedCapacity: > 0 } f ? (int)Math.Clamp(Math.Round(100.0 * f.RemainingCapacity / f.FullChargedCapacity), 0, 100) : null;
        double? batteryWatts = facts?.RateMilliwatts is { } rate && rate != 0 ? rate / 1000.0 : null;
        double? lifetime = status.BatteryLifeTime == uint.MaxValue ? null : status.BatteryLifeTime;
        return new PowerReading
        {
            HasBattery = true,
            ExternalConnected = external,
            IsCharging = charging,
            ChargePercent = charge,
            BatteryWatts = batteryWatts,
            SystemWatts = BatteryMath.SystemWattsFallback(batteryWatts, external),
            TimeRemainingSeconds = BatteryMath.ValidTimeRemaining(lifetime, external, charging),
            HealthPercent = facts is null ? null : BatteryMath.Health(facts.FullChargedCapacity, facts.DesignedCapacity),
            CycleCount = facts is { CycleCount: > 0 } ? facts.CycleCount : null,
        };
    }

    public double? ReadBatteryTemperature()
    {
        lock (_gate)
        {
            return HasBattery ? ReadBatteries()?.TemperatureCelsius : null;
        }
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast && (uint)wParam == PInvoke.PBT_APMPOWERSTATUSCHANGE)
        {
            PowerStatusChanged?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }

    /// <summary>Sums the internal (system) batteries; null when none answers.</summary>
    private BatteryFacts? ReadBatteries()
    {
        if (_devicePaths is null || DateTime.UtcNow - _pathsAt > TimeSpan.FromMinutes(1))
        {
            _devicePaths = BatteryDevicePaths();
            _pathsAt = DateTime.UtcNow;
        }

        uint state = 0;
        long rate = 0;
        var rateKnown = false;
        uint design = 0, full = 0, remaining = 0;
        var cycles = 0;
        double? temperature = null;
        var any = false;
        foreach (var path in _devicePaths)
        {
            using var handle = PInvoke.CreateFile(path, GenericReadWrite,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
            if (handle.IsInvalid)
            {
                continue;
            }

            uint wait = 0;
            uint tag = 0;
            if (!WindowsDiskSensor.Ioctl(handle, PInvoke.IOCTL_BATTERY_QUERY_TAG, &wait, sizeof(uint), &tag, sizeof(uint), out _) || tag == 0)
            {
                continue;
            }

            var query = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = BATTERY_QUERY_INFORMATION_LEVEL.BatteryInformation };
            BATTERY_INFORMATION info;
            if (!WindowsDiskSensor.Ioctl(handle, PInvoke.IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(BATTERY_QUERY_INFORMATION), &info, sizeof(BATTERY_INFORMATION), out _)
                || (info.Capabilities & PInvoke.BATTERY_SYSTEM_BATTERY) == 0)
            {
                continue; // A UPS, or no answer.
            }

            var waitStatus = new BATTERY_WAIT_STATUS { BatteryTag = tag };
            BATTERY_STATUS status;
            if (!WindowsDiskSensor.Ioctl(handle, PInvoke.IOCTL_BATTERY_QUERY_STATUS, &waitStatus, sizeof(BATTERY_WAIT_STATUS), &status, sizeof(BATTERY_STATUS), out _))
            {
                continue;
            }

            any = true;
            state |= status.PowerState;
            var relative = (info.Capabilities & PInvoke.BATTERY_CAPACITY_RELATIVE) != 0;
            if (!relative && unchecked((uint)status.Rate) != PInvoke.BATTERY_UNKNOWN_RATE)
            {
                rate += status.Rate;
                rateKnown = true;
            }

            if (!relative)
            {
                design += info.DesignedCapacity;
                full += info.FullChargedCapacity;
                remaining += status.Capacity == uint.MaxValue ? 0 : status.Capacity;
            }

            cycles = Math.Max(cycles, (int)info.CycleCount);
            query.InformationLevel = BATTERY_QUERY_INFORMATION_LEVEL.BatteryTemperature;
            uint tenthsKelvin = 0;
            if (WindowsDiskSensor.Ioctl(handle, PInvoke.IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(BATTERY_QUERY_INFORMATION), &tenthsKelvin, sizeof(uint), out _)
                && tenthsKelvin > 0)
            {
                var celsius = (tenthsKelvin / 10.0) - 273.15;
                temperature = temperature is { } t ? Math.Max(t, celsius) : celsius;
            }
        }

        return any ? new BatteryFacts(state, rateKnown ? rate : null, design, full, remaining, cycles, temperature) : null;
    }

    private static List<string> BatteryDevicePaths()
    {
        var paths = new List<string>();
        try
        {
            var batteryClass = PInvoke.GUID_DEVCLASS_BATTERY;
            using var set = PInvoke.SetupDiGetClassDevs(batteryClass, null, default, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);
            if (set.IsInvalid)
            {
                return paths;
            }

            for (uint i = 0; i < 8; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!PInvoke.SetupDiEnumDeviceInterfaces(set, null, batteryClass, i, ref data))
                {
                    break;
                }

                var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                if (WindowsDiskSensor.DevicePath(set, data, ref info) is { } path)
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", "Could not list batteries.", ex);
        }

        return paths;
    }

    public void Dispose() => _host.RemoveHandler(_handler);
}
