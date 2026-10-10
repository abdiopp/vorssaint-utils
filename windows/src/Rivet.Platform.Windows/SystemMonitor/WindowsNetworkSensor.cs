// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Rivet.Core.SystemMonitor;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.NetworkManagement.Ndis;
using Windows.Win32.Networking.WinSock;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// Network throughput from GetIfTable2's 64-bit octet counters. Counted
/// interfaces (documented rule, see docs/modules/systemMonitor.md): up,
/// hardware, not a filter (QoS/WFP lightweight filters would double count),
/// not a tunnel, of type Ethernet, Wi-Fi or mobile broadband, and not a
/// virtual adapter by description (Hyper-V, VMware, VirtualBox, VPN TAP/TUN,
/// WireGuard, loopback, packet capture). Local IPv4 addresses use the same rule.
/// </summary>
public sealed unsafe class WindowsNetworkSensor : INetworkSensor
{
    private const uint TypeEthernet = 6;
    private const uint TypeWifi = 71;
    private const uint TypeWwan = 243;
    private const uint TypeWwan2 = 244;
    private const ushort AfInet = 2;

    private static readonly string[] VirtualMarkers =
    [
        "Hyper-V", "vEthernet", "Virtual", "VMware", "VirtualBox", "TAP-", "TAP Adapter", "TUN", "WireGuard", "Wintun",
        "Loopback", "Npcap", "WAN Miniport", "OpenVPN", "ZeroTier", "Tailscale", "NordLynx", "Docker",
    ];

    public NetworkCounters? ReadCounters()
    {
        if (PInvoke.GetIfTable2(out var table) != 0 || table is null)
        {
            return null;
        }

        try
        {
            ulong received = 0;
            ulong sent = 0;
            var rows = (MIB_IF_ROW2*)&table->Table;
            for (var i = 0; i < table->NumEntries; i++)
            {
                var row = &rows[i];
                if (!IsCounted(row))
                {
                    continue;
                }

                received += row->InOctets;
                sent += row->OutOctets;
            }

            return new NetworkCounters(received, sent);
        }
        finally
        {
            PInvoke.FreeMibTable(table);
        }
    }

    private static bool IsCounted(MIB_IF_ROW2* row)
    {
        var flags = row->InterfaceAndOperStatusFlags;
        if (row->OperStatus != IF_OPER_STATUS.IfOperStatusUp || !flags.HardwareInterface || flags.FilterInterface)
        {
            return false;
        }

        if (row->TunnelType != TUNNEL_TYPE.TUNNEL_TYPE_NONE || !IsCountedType(row->Type))
        {
            return false;
        }

        return !IsVirtual(row->Description.ToString());
    }

    internal static bool IsCountedType(uint type) => type is TypeEthernet or TypeWifi or TypeWwan or TypeWwan2;

    internal static bool IsVirtual(string description) =>
        VirtualMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<LocalAddress> ReadLocalAddresses()
    {
        const GET_ADAPTERS_ADDRESSES_FLAGS flags = GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_ANYCAST
                                                   | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_MULTICAST
                                                   | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_DNS_SERVER;
        uint size = 16 * 1024;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                var first = (IP_ADAPTER_ADDRESSES_LH*)p;
                var result = PInvoke.GetAdaptersAddresses(AfInet, flags, null, first, &size);
                if (result == 111) // ERROR_BUFFER_OVERFLOW
                {
                    continue;
                }

                if (result != 0)
                {
                    return [];
                }

                var addresses = new List<LocalAddress>();
                for (var adapter = first; adapter is not null; adapter = adapter->Next)
                {
                    if (adapter->OperStatus != IF_OPER_STATUS.IfOperStatusUp || !IsCountedType(adapter->IfType)
                        || adapter->TunnelType != TUNNEL_TYPE.TUNNEL_TYPE_NONE || IsVirtual(adapter->Description.ToString()))
                    {
                        continue;
                    }

                    var name = adapter->FriendlyName.ToString();
                    for (var unicast = adapter->FirstUnicastAddress; unicast is not null; unicast = unicast->Next)
                    {
                        var socket = unicast->Address.lpSockaddr;
                        if (socket is null || (ushort)socket->sa_family != AfInet)
                        {
                            continue;
                        }

                        var sin = (SOCKADDR_IN*)socket;
                        var address = new IPAddress(sin->sin_addr.S_un.S_addr).ToString();
                        addresses.Add(new LocalAddress(address, string.IsNullOrWhiteSpace(name) ? null : name));
                    }
                }

                return addresses
                    .DistinctBy(a => (a.Address, a.InterfaceName))
                    .OrderBy(a => a.Address, StringComparer.Ordinal)
                    .ToList();
            }
        }

        return [];
    }
}
