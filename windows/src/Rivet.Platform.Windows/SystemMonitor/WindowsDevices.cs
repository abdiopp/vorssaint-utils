// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rivet.Core.Diagnostics;
using Rivet.Core.SystemMonitor;
using Windows.Devices.Enumeration.Pnp;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>SetupAPI device property readers.</summary>
internal static unsafe class DeviceProperties
{
    public static string? String(SafeHandle set, in SP_DEVINFO_DATA info, in DEVPROPKEY key)
    {
        var buffer = Raw(set, info, key, out var type);
        return buffer is null || type != DEVPROPTYPE.DEVPROP_TYPE_STRING ? null : DecodeString(buffer);
    }

    public static List<string> StringList(SafeHandle set, in SP_DEVINFO_DATA info, in DEVPROPKEY key)
    {
        var buffer = Raw(set, info, key, out var type);
        if (buffer is null || type != DEVPROPTYPE.DEVPROP_TYPE_STRING_LIST)
        {
            return [];
        }

        return DecodeString(buffer, keepNulls: true).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static uint? UInt32(SafeHandle set, in SP_DEVINFO_DATA info, in DEVPROPKEY key)
    {
        var buffer = Raw(set, info, key, out var type);
        return buffer is { Length: >= 4 } && type == DEVPROPTYPE.DEVPROP_TYPE_UINT32 ? BitConverter.ToUInt32(buffer) : null;
    }

    public static bool? Boolean(SafeHandle set, in SP_DEVINFO_DATA info, in DEVPROPKEY key)
    {
        var buffer = Raw(set, info, key, out var type);
        return buffer is { Length: >= 1 } && type == DEVPROPTYPE.DEVPROP_TYPE_BOOLEAN ? buffer[0] != 0 : null;
    }

    private static byte[]? Raw(SafeHandle set, in SP_DEVINFO_DATA info, in DEVPROPKEY key, out DEVPROPTYPE type)
    {
        PInvoke.SetupDiGetDeviceProperty(set, info, key, out type, default, out var required, 0);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        return PInvoke.SetupDiGetDeviceProperty(set, info, key, out type, buffer, out _, 0) ? buffer : null;
    }

    private static string DecodeString(byte[] buffer, bool keepNulls = false)
    {
        var text = System.Text.Encoding.Unicode.GetString(buffer);
        return keepNulls ? text : text.TrimEnd('\0');
    }
}

/// <summary>
/// External USB peripherals: every present device on the USB device
/// interface, minus root hubs, hubs (class 9), billboard devices (class 17),
/// zero ids and built-in devices (in the PC's own device container, or with a
/// "never removed" removal policy).
/// </summary>
public sealed unsafe partial class WindowsUsbSensor : IUsbSensor
{
    private const uint RemovalPolicyExpectNoRemoval = 1;

    public IReadOnlyList<UsbDevice>? Read()
    {
        var usbInterface = PInvoke.GUID_DEVINTERFACE_USB_DEVICE;
        using var set = PInvoke.SetupDiGetClassDevs(usbInterface, null, default, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT | SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_DEVICEINTERFACE);
        if (set.IsInvalid)
        {
            return null;
        }

        var nodes = new List<UsbNode>();
        for (uint i = 0; i < 256; i++)
        {
            var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            if (!PInvoke.SetupDiEnumDeviceInfo(set, i, ref info))
            {
                break;
            }

            var instance = DeviceProperties.String(set, info, PInvoke.DEVPKEY_Device_InstanceId) ?? string.Empty;
            var hardwareIds = DeviceProperties.StringList(set, info, PInvoke.DEVPKEY_Device_HardwareIds);
            var compatible = DeviceProperties.StringList(set, info, PInvoke.DEVPKEY_Device_CompatibleIds);
            var (vendor, product) = ParseIds(hardwareIds.FirstOrDefault() ?? instance);
            var manufacturer = DeviceProperties.String(set, info, PInvoke.DEVPKEY_Device_Manufacturer);
            nodes.Add(new UsbNode
            {
                InstanceId = instance,
                VendorId = vendor,
                ProductId = product,
                DeviceClass = ParseClass(compatible),
                IsRootHub = instance.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase),
                IsBuiltIn = DeviceProperties.Boolean(set, info, PInvoke.DEVPKEY_Device_InLocalMachineContainer) == true,
                IsRemovable = DeviceProperties.UInt32(set, info, PInvoke.DEVPKEY_Device_RemovalPolicy) != RemovalPolicyExpectNoRemoval,
                Product = DeviceProperties.String(set, info, PInvoke.DEVPKEY_Device_BusReportedDeviceDesc)
                          ?? DeviceProperties.String(set, info, PInvoke.DEVPKEY_Device_FriendlyName)
                          ?? DeviceProperties.String(set, info, PInvoke.DEVPKEY_Device_DeviceDesc),
                Vendor = IsGenericManufacturer(manufacturer) ? null : manufacturer,
            });
        }

        return UsbDeviceFilter.Filter(nodes);
    }

    /// <summary>"USB\VID_046D&amp;PID_C548&amp;REV_1203" → (0x046D, 0xC548).</summary>
    internal static (int Vendor, int Product) ParseIds(string id)
    {
        var match = IdPattern().Match(id);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>"USB\Class_09&amp;SubClass_00" → 9.</summary>
    internal static int? ParseClass(IEnumerable<string> compatibleIds)
    {
        foreach (var id in compatibleIds)
        {
            var match = ClassPattern().Match(id);
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>Driver package providers ("(Standard USB Host Controller)", "Microsoft") say nothing about the device.</summary>
    internal static bool IsGenericManufacturer(string? manufacturer) =>
        string.IsNullOrWhiteSpace(manufacturer) || manufacturer.StartsWith('(')
        || manufacturer.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Compatible", StringComparison.OrdinalIgnoreCase)
        || manufacturer.Contains("Generic", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Standard", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4}).*PID_([0-9A-Fa-f]{4})")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"Class_([0-9A-Fa-f]{2})")]
    private static partial Regex ClassPattern();
}

/// <summary>
/// Accessory batteries Windows already knows: the Bluetooth battery level
/// device property (what Settings › Bluetooth &amp; devices shows) on paired
/// devices. Vendor protocols (e.g. Logitech) are not readable.
/// </summary>
public sealed class WindowsPeripheralBatterySensor : IPeripheralBatterySensor
{
    private const string BatteryProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string NameProperty = "System.ItemNameDisplay";

    public IReadOnlyList<PeripheralBattery>? Read(double now)
    {
        try
        {
            var task = PnpObject.FindAllAsync(PnpObjectType.Device, [BatteryProperty, NameProperty], string.Empty).AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                return null;
            }

            var devices = new List<PeripheralBattery>();
            foreach (var device in task.Result)
            {
                if (!device.Properties.TryGetValue(BatteryProperty, out var raw) || PeripheralBatteries.ParsePercent(raw) is not { } percent)
                {
                    continue;
                }

                var name = device.Properties.TryGetValue(NameProperty, out var n) && n is string s && s.Length > 0 ? s : device.Id;
                devices.Add(new PeripheralBattery(device.Id, name, PeripheralBatteries.KindFromName(name), percent, now));
            }

            return PeripheralBatteries.Merge(devices, []);
        }
        catch (Exception ex) when (ex is COMException or AggregateException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn("monitor", "Reading accessory batteries failed.", ex);
            return null;
        }
    }
}
