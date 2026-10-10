// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Rivet.Core.Maintenance.Processes;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace Rivet.Platform.Windows.Processes;

/// <summary>
/// Listening sockets from <c>GetExtendedTcpTable</c> (TCP_TABLE_OWNER_PID_LISTENER)
/// and <c>GetExtendedUdpTable</c> (UDP_TABLE_OWNER_PID), IPv4 and IPv6. No
/// administrator rights are needed, and every user's and service's sockets
/// are included. The buffers are parsed by <see cref="PortTableParser"/>.
/// </summary>
public sealed class WindowsPortTable : IPortTablePlatform
{
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;

    public IReadOnlyList<PortEntry> ReadListeners(bool includeUdp)
    {
        var entries = new List<PortEntry>();
        entries.AddRange(PortTableParser.ParseTcp4(Tcp(AfInet)));
        entries.AddRange(PortTableParser.ParseTcp6(Tcp(AfInet6)));
        if (includeUdp)
        {
            entries.AddRange(PortTableParser.ParseUdp4(Udp(AfInet)));
            entries.AddRange(PortTableParser.ParseUdp6(Udp(AfInet6)));
        }

        return entries;
    }

    private static byte[] Tcp(uint family)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[Math.Max(size, 4)];
            var result = PInvoke.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (result == 0)
            {
                return buffer;
            }

            if (result != ErrorInsufficientBuffer)
            {
                throw new Win32Exception((int)result);
            }

            // The table can grow between calls.
            size += 4096;
        }

        throw new InvalidOperationException("The TCP table kept growing.");
    }

    private static byte[] Udp(uint family)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new byte[Math.Max(size, 4)];
            var result = PInvoke.GetExtendedUdpTable(buffer, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            if (result == 0)
            {
                return buffer;
            }

            if (result != ErrorInsufficientBuffer)
            {
                throw new Win32Exception((int)result);
            }

            size += 4096;
        }

        throw new InvalidOperationException("The UDP table kept growing.");
    }
}
