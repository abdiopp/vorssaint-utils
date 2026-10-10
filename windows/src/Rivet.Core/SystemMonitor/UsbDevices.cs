// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>A USB device node as the platform enumerates it, before filtering.</summary>
public sealed record UsbNode
{
    /// <summary>Plug and Play instance id (stable while the device stays on the same port).</summary>
    public string? InstanceId { get; init; }

    public int VendorId { get; init; }

    public int ProductId { get; init; }

    /// <summary>USB device class (9 = hub, 17 = billboard), when known.</summary>
    public int? DeviceClass { get; init; }

    public bool IsRootHub { get; init; }

    /// <summary>Part of the PC itself (webcam, fingerprint reader, internal Bluetooth radio).</summary>
    public bool IsBuiltIn { get; init; }

    public bool IsRemovable { get; init; } = true;

    public string? Product { get; init; }

    public string? Vendor { get; init; }

    public string? Serial { get; init; }

    public string? Location { get; init; }
}

/// <summary>The connected-devices rules of spec §3.11.</summary>
public static class UsbDeviceFilter
{
    public const int HubClass = 9;
    public const int BillboardClass = 17;

    public static bool IsExternalPeripheral(UsbNode node) =>
        !node.IsBuiltIn
        && node.IsRemovable
        && !node.IsRootHub
        && !(node.VendorId == 0 && node.ProductId == 0)
        && node.DeviceClass is not HubClass and not BillboardClass;

    /// <summary>Stable id: vendor-product-instance, else serial, else location, else name.</summary>
    public static string StableId(UsbNode node)
    {
        var name = Clean(node.Product);
        if (!string.IsNullOrEmpty(node.InstanceId))
        {
            return $"{node.VendorId:x4}-{node.ProductId:x4}-{node.InstanceId}";
        }

        if (!string.IsNullOrWhiteSpace(node.Serial))
        {
            return node.Serial!;
        }

        if (!string.IsNullOrWhiteSpace(node.Location))
        {
            return "loc" + node.Location;
        }

        return name ?? $"{node.VendorId:x4}-{node.ProductId:x4}";
    }

    /// <summary>External peripherals, one per id, sorted by name (case-insensitive).</summary>
    public static List<UsbDevice> Filter(IEnumerable<UsbNode> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UsbDevice>();
        foreach (var node in nodes.Where(IsExternalPeripheral))
        {
            var id = StableId(node);
            if (seen.Add(id))
            {
                result.Add(new UsbDevice(id, Clean(node.Product), Clean(node.Vendor)));
            }
        }

        return result.OrderBy(d => d.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
