// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

public class SensorHelperProtocolTests
{
    [Fact]
    public void Names_derive_from_the_product_id()
    {
        Assert.Equal("Acme.SensorHelper", SensorHelperProtocol.PipeName("Acme"));
        Assert.Equal(@"\Acme\SensorHelper", SensorHelperProtocol.TaskName("Acme"));
        Assert.Equal("Acme.SensorHelper.exe", SensorHelperProtocol.ExecutableName("Acme"));
        Assert.Equal(Path.Combine("pf", "Acme", "SensorHelper"), SensorHelperProtocol.InstallFolder("Acme", "pf"));
    }

    [Fact]
    public void The_header_carries_the_version()
    {
        Assert.True(SensorHelperProtocol.TryParseHeader(SensorHelperProtocol.Header, out var version));
        Assert.Equal(SensorHelperProtocol.Version, version);
        Assert.True(SensorHelperProtocol.TryParseHeader("ok 7", out version));
        Assert.Equal(7, version);
        Assert.False(SensorHelperProtocol.TryParseHeader("ok", out _));
        Assert.False(SensorHelperProtocol.TryParseHeader("ok -1", out _));
        Assert.False(SensorHelperProtocol.TryParseHeader("err 1", out _));
        Assert.False(SensorHelperProtocol.TryParseHeader(null, out _));
    }

    [Fact]
    public void A_sensor_line_round_trips_in_any_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var line = SensorHelperProtocol.FormatSensor(1, "Core (Tctl/Tdie)", 64.375);
            Assert.Equal("t\t1\t64.375\tCore (Tctl/Tdie)", line);
            Assert.True(SensorHelperProtocol.TryParseSensor(line, out var hardware, out var name, out var celsius));
            Assert.Equal(1, hardware);
            Assert.Equal("Core (Tctl/Tdie)", name);
            Assert.Equal(64.375, celsius);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void A_sensor_name_cannot_break_the_line_format()
    {
        var line = SensorHelperProtocol.FormatSensor(0, "CPU\tPackage\nend", 50);
        Assert.DoesNotContain('\n', line);
        Assert.True(SensorHelperProtocol.TryParseSensor(line, out _, out var name, out _));
        Assert.Equal("CPU Package end", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("end")]
    [InlineData("t\t0\t50")]
    [InlineData("x\t0\t50\tCPU Package")]
    [InlineData("t\tzero\t50\tCPU Package")]
    [InlineData("t\t0\thot\tCPU Package")]
    [InlineData("t\t0\tNaN\tCPU Package")]
    [InlineData("t\t0\tInfinity\tCPU Package")]
    public void Malformed_sensor_lines_are_rejected(string line)
    {
        Assert.False(SensorHelperProtocol.TryParseSensor(line, out _, out _, out _));
    }

    [Fact]
    public async Task The_line_reader_splits_lines_across_reads()
    {
        var text = "ok 1\nt\t0\t61.5\tCPU Package\r\nend\n";
        var reader = new BoundedLineReader(new TrickleStream(Encoding.UTF8.GetBytes(text), 3), 64);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("ok 1", await reader.ReadLineAsync(ct));
        Assert.Equal("t\t0\t61.5\tCPU Package", await reader.ReadLineAsync(ct));
        Assert.Equal("end", await reader.ReadLineAsync(ct));
        Assert.Null(await reader.ReadLineAsync(ct));
    }

    [Fact]
    public async Task The_line_reader_gives_up_on_a_line_that_is_too_long()
    {
        var ct = TestContext.Current.CancellationToken;
        var exact = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 32) + "\n")), 32);
        Assert.Equal(new string('a', 32), await exact.ReadLineAsync(ct));
        var tooLong = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 33) + "\n")), 32);
        Assert.Null(await tooLong.ReadLineAsync(ct));
        var unterminated = new BoundedLineReader(new MemoryStream(Encoding.UTF8.GetBytes("cpu")), 32);
        Assert.Null(await unterminated.ReadLineAsync(ct));
    }

    /// <summary>Hands out a few bytes per read, like a pipe.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }
}
