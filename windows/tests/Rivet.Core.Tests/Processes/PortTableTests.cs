// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net;
using Rivet.Core.Maintenance.Processes;
using Xunit;

namespace Rivet.Core.Tests.Processes;

public class PortTableTests
{
    [Fact]
    public void Tcp4_table_keeps_listeners_and_converts_network_byte_order_ports()
    {
        var table = Table(24,
            Tcp4Row(state: 2, address: "0.0.0.0", port: 135, pid: 1088),
            Tcp4Row(state: 2, address: "127.0.0.1", port: 5173, pid: 9120),
            Tcp4Row(state: 5, address: "10.0.0.5", port: 50122, pid: 9120)); // ESTABLISHED is ignored
        var rows = PortTableParser.ParseTcp4(table);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new PortEntry(PortProtocol.Tcp, "0.0.0.0", 135, 1088, false), rows[0]);
        Assert.Equal(5173, rows[1].Port);
        Assert.Equal("127.0.0.1", rows[1].Address);
    }

    [Fact]
    public void Tcp6_table_reads_addresses_scope_and_state()
    {
        var table = Table(56,
            Tcp6Row(state: 2, address: IPAddress.IPv6Any, scope: 0, port: 445, pid: 4),
            Tcp6Row(state: 2, address: IPAddress.IPv6Loopback, scope: 0, port: 3000, pid: 7744),
            Tcp6Row(state: 1, address: IPAddress.IPv6Loopback, scope: 0, port: 3001, pid: 7744));
        var rows = PortTableParser.ParseTcp6(table);
        Assert.Equal(2, rows.Count);
        Assert.Equal("::", rows[0].Address);
        Assert.True(rows[0].IsIPv6);
        Assert.Equal(4, rows[0].Pid);
        Assert.Equal("::1", rows[1].Address);
        Assert.Equal(3000, rows[1].Port);
    }

    [Fact]
    public void Udp_tables_parse_both_families()
    {
        var udp4 = Table(12, Udp4Row("0.0.0.0", 5353, 2208));
        var udp6 = Table(28, Udp6Row(IPAddress.Parse("fe80::1"), scope: 12, port: 546, pid: 1500));
        Assert.Equal(new PortEntry(PortProtocol.Udp, "0.0.0.0", 5353, 2208, false), Assert.Single(PortTableParser.ParseUdp4(udp4)));
        var v6 = Assert.Single(PortTableParser.ParseUdp6(udp6));
        Assert.Equal("fe80::1%12", v6.Address);
        Assert.Equal(546, v6.Port);
    }

    [Fact]
    public void Declared_count_is_capped_by_the_buffer()
    {
        var table = Table(24, Tcp4Row(2, "0.0.0.0", 80, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(table, 50);
        Assert.Single(PortTableParser.ParseTcp4(table));
        Assert.Empty(PortTableParser.ParseTcp4([1, 0]));
    }

    [Fact]
    public void Rows_are_deduplicated_and_sorted_by_port_then_process()
    {
        var rows = PortRules.Normalize(
        [
            Row(8080, "node.exe", 10),
            Row(443, "svchost.exe", 20),
            Row(8080, "node.exe", 10),
            Row(8080, "Code.exe", 11),
            Row(0, "bad.exe", 12),
        ]);
        Assert.Equal([(443, "svchost.exe"), (8080, "Code.exe"), (8080, "node.exe")], rows.Select(r => (r.Port, r.ProcessName)));
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("::", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    public void Wildcard_addresses_listen_on_every_interface(string address, bool expected) =>
        Assert.Equal(expected, PortRules.IsAllInterfaces(address));

    [Theory]
    [InlineData("0.0.0.0", 3000, "http://127.0.0.1:3000")]
    [InlineData("::", 8080, "http://[::1]:8080")]
    [InlineData("127.0.0.1", 5173, "http://127.0.0.1:5173")]
    [InlineData("::1", 5173, "http://[::1]:5173")]
    [InlineData("fe80::1%12", 80, "http://[fe80::1%2512]:80")]
    public void Browser_urls_map_wildcards_to_loopback(string address, int port, string expected) =>
        Assert.Equal(expected, PortRules.BrowserUrl(Row(port, "x.exe", 1) with { Address = address }));

    [Fact]
    public void Udp_rows_have_no_browser_url_and_copy_keeps_ipv6_brackets()
    {
        Assert.Null(PortRules.BrowserUrl(Row(53, "dns.exe", 1) with { Protocol = PortProtocol.Udp }));
        Assert.Equal("[::1]:3000", PortRules.AddressAndPort(Row(3000, "x.exe", 1) with { Address = "::1" }));
        Assert.Equal("127.0.0.1:3000", PortRules.AddressAndPort(Row(3000, "x.exe", 1)));
    }

    [Fact]
    public void Filter_matches_port_process_pid_and_address()
    {
        var row = Row(5432, "postgres.exe", 6060);
        Assert.True(PortRules.Matches(row, "543"));
        Assert.True(PortRules.Matches(row, "POSTGRES"));
        Assert.True(PortRules.Matches(row, "6060"));
        Assert.True(PortRules.Matches(row, "127.0"));
        Assert.False(PortRules.Matches(row, "mysql"));
        Assert.True(PortRules.Matches(row, " "));
    }

    [Fact]
    public void Only_stable_unprotected_rows_can_be_killed()
    {
        var row = Row(80, "httpd.exe", 300) with { IsStable = true, CreationTime = 133_000_000_000_000_000 };
        Assert.True(row.CanKill);
        Assert.False((row with { IsStable = false }).CanKill);
        Assert.False((row with { IsProtected = true }).CanKill);
        Assert.False((row with { CreationTime = 0 }).CanKill);
    }

    private static PortRow Row(int port, string name, int pid) => new()
    {
        Protocol = PortProtocol.Tcp,
        Address = "127.0.0.1",
        Port = port,
        Pid = pid,
        ProcessName = name,
    };

    private static byte[] Table(int rowSize, params byte[][] rows)
    {
        var buffer = new byte[4 + (rowSize * rows.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].CopyTo(buffer, 4 + (i * rowSize));
        }

        return buffer;
    }

    private static byte[] PortDword(int port)
    {
        // Network byte order in the low 16 bits of the DWORD, as Windows fills it.
        return [(byte)(port >> 8), (byte)(port & 0xFF), 0, 0];
    }

    private static byte[] Tcp4Row(uint state, string address, int port, int pid)
    {
        var row = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(row, state);
        IPAddress.Parse(address).GetAddressBytes().CopyTo(row, 4);
        PortDword(port).CopyTo(row, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), (uint)pid);
        return row;
    }

    private static byte[] Tcp6Row(uint state, IPAddress address, uint scope, int port, int pid)
    {
        var row = new byte[56];
        address.GetAddressBytes().CopyTo(row, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), scope);
        PortDword(port).CopyTo(row, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(48), state);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(52), (uint)pid);
        return row;
    }

    private static byte[] Udp4Row(string address, int port, int pid)
    {
        var row = new byte[12];
        IPAddress.Parse(address).GetAddressBytes().CopyTo(row, 0);
        PortDword(port).CopyTo(row, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), (uint)pid);
        return row;
    }

    private static byte[] Udp6Row(IPAddress address, uint scope, int port, int pid)
    {
        var row = new byte[28];
        address.GetAddressBytes().CopyTo(row, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(16), scope);
        PortDword(port).CopyTo(row, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(24), (uint)pid);
        return row;
    }
}
