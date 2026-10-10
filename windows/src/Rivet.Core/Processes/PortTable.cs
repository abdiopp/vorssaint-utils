// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace Rivet.Core.Maintenance.Processes;

public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>One listening socket from the system tables.</summary>
public sealed record PortEntry(PortProtocol Protocol, string Address, int Port, int Pid, bool IsIPv6);

/// <summary>Reads the listening TCP (and optionally UDP) sockets of every process.</summary>
public interface IPortTablePlatform
{
    /// <summary>Throws when the tables cannot be read (shown as "Could not read listening ports").</summary>
    IReadOnlyList<PortEntry> ReadListeners(bool includeUdp);
}

/// <summary>
/// Parses the buffers <c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c>
/// fill (OWNER_PID classes). Kept platform-neutral so the layouts are unit
/// tested: little-endian DWORDs, ports in network byte order in the low 16 bits.
/// </summary>
public static class PortTableParser
{
    /// <summary>MIB_TCP_STATE_LISTEN.</summary>
    public const uint TcpStateListen = 2;

    private const int Tcp4RowSize = 24;  // state, localAddr, localPort, remoteAddr, remotePort, pid
    private const int Tcp6RowSize = 56;  // localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
    private const int Udp4RowSize = 12;  // localAddr, localPort, pid
    private const int Udp6RowSize = 28;  // localAddr[16], scope, localPort, pid

    public static IReadOnlyList<PortEntry> ParseTcp4(ReadOnlySpan<byte> table)
    {
        var rows = new List<PortEntry>();
        var count = RowCount(table, Tcp4RowSize);
        for (var i = 0; i < count; i++)
        {
            var row = table.Slice(4 + (i * Tcp4RowSize), Tcp4RowSize);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row);
            if (state != TcpStateListen)
            {
                continue;
            }

            var address = new IPAddress(row.Slice(4, 4));
            rows.Add(new PortEntry(PortProtocol.Tcp, address.ToString(), Port(row.Slice(8, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4)), false));
        }

        return rows;
    }

    public static IReadOnlyList<PortEntry> ParseTcp6(ReadOnlySpan<byte> table)
    {
        var rows = new List<PortEntry>();
        var count = RowCount(table, Tcp6RowSize);
        for (var i = 0; i < count; i++)
        {
            var row = table.Slice(4 + (i * Tcp6RowSize), Tcp6RowSize);
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(48, 4));
            if (state != TcpStateListen)
            {
                continue;
            }

            var scope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            var address = new IPAddress(row[..16], scope);
            rows.Add(new PortEntry(PortProtocol.Tcp, address.ToString(), Port(row.Slice(20, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4)), true));
        }

        return rows;
    }

    public static IReadOnlyList<PortEntry> ParseUdp4(ReadOnlySpan<byte> table)
    {
        var rows = new List<PortEntry>();
        var count = RowCount(table, Udp4RowSize);
        for (var i = 0; i < count; i++)
        {
            var row = table.Slice(4 + (i * Udp4RowSize), Udp4RowSize);
            var address = new IPAddress(row[..4]);
            rows.Add(new PortEntry(PortProtocol.Udp, address.ToString(), Port(row.Slice(4, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(8, 4)), false));
        }

        return rows;
    }

    public static IReadOnlyList<PortEntry> ParseUdp6(ReadOnlySpan<byte> table)
    {
        var rows = new List<PortEntry>();
        var count = RowCount(table, Udp6RowSize);
        for (var i = 0; i < count; i++)
        {
            var row = table.Slice(4 + (i * Udp6RowSize), Udp6RowSize);
            var scope = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, 4));
            var address = new IPAddress(row[..16], scope);
            rows.Add(new PortEntry(PortProtocol.Udp, address.ToString(), Port(row.Slice(20, 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(24, 4)), true));
        }

        return rows;
    }

    /// <summary>The DWORD holds the port in network byte order in its low two bytes.</summary>
    public static int Port(ReadOnlySpan<byte> dword) => (dword[0] << 8) | dword[1];

    /// <summary>Rows actually present: the declared count, capped by the buffer length.</summary>
    private static int RowCount(ReadOnlySpan<byte> table, int rowSize)
    {
        if (table.Length < 4)
        {
            return 0;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var available = (table.Length - 4) / rowSize;
        return (int)Math.Min(declared, (uint)available);
    }
}

/// <summary>A row of the Port Manager list.</summary>
public sealed record PortRow
{
    public required PortProtocol Protocol { get; init; }

    public required string Address { get; init; }

    public required int Port { get; init; }

    public required int Pid { get; init; }

    public required string ProcessName { get; init; }

    public string? ProcessPath { get; init; }

    public string? User { get; init; }

    /// <summary>Creation time read before and after the table matched: kill actions are allowed.</summary>
    public bool IsStable { get; init; }

    public bool IsProtected { get; init; }

    public long CreationTime { get; init; }

    public bool IsAllInterfaces => PortRules.IsAllInterfaces(Address);

    public ProcessIdentity Identity => new(Pid, CreationTime);

    public bool CanKill => IsStable && !IsProtected && Identity.IsKnown;
}

/// <summary>Port Manager rules ported from the macOS parser tests.</summary>
public static class PortRules
{
    /// <summary>Wildcard binds: every interface, so other devices may connect.</summary>
    public static bool IsAllInterfaces(string address) =>
        address is "0.0.0.0" or "::" or "[::]" or "*";

    /// <summary>One row per distinct (protocol, port, address, pid), sorted by port then process name.</summary>
    public static IReadOnlyList<PortRow> Normalize(IEnumerable<PortRow> rows)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        return rows
            .Where(r => r.Port is > 0 and <= 65535)
            .GroupBy(r => (r.Protocol, r.Port, r.Address.ToLowerInvariant(), r.Pid))
            .Select(g => g.First())
            .OrderBy(r => r.Port)
            .ThenBy(r => r.ProcessName, comparer)
            .ThenBy(r => r.Protocol)
            .ThenBy(r => r.Pid)
            .ToList();
    }

    /// <summary>Case-insensitive substring of "port process pid address".</summary>
    public static bool Matches(PortRow row, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var haystack = string.Create(CultureInfo.InvariantCulture, $"{row.Port} {row.ProcessName} {row.Pid} {row.Address}");
        return haystack.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// "Open in browser" target for TCP rows: wildcard IPv4 → 127.0.0.1,
    /// wildcard IPv6 → [::1], other IPv6 hosts in brackets, others as bound.
    /// </summary>
    public static string? BrowserUrl(PortRow row)
    {
        if (row.Protocol != PortProtocol.Tcp || row.Port is <= 0 or > 65535)
        {
            return null;
        }

        var host = row.Address switch
        {
            "*" => "localhost",
            "0.0.0.0" => "127.0.0.1",
            "::" or "[::]" => "[::1]",
            var a when a.Contains(':') && !a.StartsWith('[') => "[" + a.Replace("%", "%25", StringComparison.Ordinal) + "]",
            var a => a,
        };
        return string.Create(CultureInfo.InvariantCulture, $"http://{host}:{row.Port}");
    }

    /// <summary>"address:port" for the copy action (IPv6 hosts in brackets).</summary>
    public static string AddressAndPort(PortRow row) =>
        row.Address.Contains(':') && !row.Address.StartsWith('[')
            ? string.Create(CultureInfo.InvariantCulture, $"[{row.Address}]:{row.Port}")
            : string.Create(CultureInfo.InvariantCulture, $"{row.Address}:{row.Port}");
}
