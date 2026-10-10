// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.SystemMonitor;
using Xunit;

namespace Rivet.Core.Tests.SystemMonitor;

/// <summary>Formatting vectors of spec §6.10 (MetricsFeatureTests on macOS).</summary>
public class MetricFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10240, "10 KB")]
    [InlineData(1_048_064, "1.0 MB")]
    [InlineData(1e9, "954 MB")]
    [InlineData(1023, "1023 B")]
    public void Bytes(double value, string expected) => Assert.Equal(expected, MetricFormat.Bytes(value, En));

    [Fact]
    public void Bytes_per_second_promotes_on_the_printed_value() =>
        Assert.Equal("1.0 KB/s", MetricFormat.BytesPerSec(1023.6, En));

    [Theory]
    [InlineData(320 * 1024, "320K")]
    [InlineData(1.2 * 1024 * 1024, "1.2M")]
    [InlineData(1023.4, "1023B")]
    [InlineData(9.96 * 1024, "10K")]
    [InlineData(0, "0B")]
    public void Compact_bytes(double value, string expected) => Assert.Equal(expected, MetricFormat.BytesPerSecCompact(value, En));

    [Theory]
    [InlineData(1500, "12 Kbps")]
    [InlineData(1_200_000, "9.6 Mbps")]
    [InlineData(124.95, "1.0 Kbps")]
    public void Bits(double bytesPerSecond, string expected) => Assert.Equal(expected, MetricFormat.BitsPerSec(bytesPerSecond, En));

    [Theory]
    [InlineData(40_000, "320Kb")]
    [InlineData(1_245_000, "10Mb")]
    [InlineData(1e9, "8.0Gb")]
    public void Compact_bits(double bytesPerSecond, string expected) => Assert.Equal(expected, MetricFormat.BitsPerSecCompact(bytesPerSecond, En));

    [Fact]
    public void Compact_rates_are_never_wider_than_five_characters()
    {
        for (var exponent = 0.0; exponent < 13; exponent += 0.013)
        {
            var value = Math.Pow(10, exponent);
            Assert.True(MetricFormat.BytesPerSecCompact(value, En).Length <= 5, MetricFormat.BytesPerSecCompact(value, En));
            Assert.True(MetricFormat.BitsPerSecCompact(value, En).Length <= 5, MetricFormat.BitsPerSecCompact(value, En));
        }
    }

    [Theory]
    [InlineData(245_107_195_904, "245 GB")]
    [InlineData(1_000_204_845_056, "1.0 TB")]
    public void Disk_bytes_use_decimal_units(double value, string expected) => Assert.Equal(expected, MetricFormat.DiskBytes(value, En));

    [Fact]
    public void Precise_disk_bytes_keep_two_decimals_from_terabytes() =>
        Assert.Equal("14.88 TB", MetricFormat.DiskBytesPrecise(14_878_047_232_000, En));

    [Theory]
    [InlineData(8.5, "8.5 W")]
    [InlineData(23.4, "23 W")]
    [InlineData(9.96, "10 W")]
    public void Watts(double value, string expected) => Assert.Equal(expected, MetricFormat.Watts(value, En));

    [Fact]
    public void Compact_watts_round() => Assert.Equal("9W", MetricFormat.WattsCompact(8.6, En));

    [Theory]
    [InlineData(0.125, "13%")]
    [InlineData(1.4, "100%")]
    [InlineData(-0.2, "0%")]
    public void Percent(double value, string expected) => Assert.Equal(expected, MetricFormat.Percent(value, En));

    [Fact]
    public void Temperatures()
    {
        Assert.Equal("106 °F", MetricFormat.Temperature(41, TemperatureUnit.Fahrenheit, En));
        Assert.Equal("41 °C", MetricFormat.Temperature(41, TemperatureUnit.Celsius, En));
        Assert.Equal("50°", MetricFormat.TemperatureCompact(49.6, TemperatureUnit.Celsius, En));
    }

    [Theory]
    [InlineData(13_320, "3h 42m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(30, "0h 1m")]
    public void Battery_time(double seconds, string expected) => Assert.Equal(expected, MetricFormat.BatteryTime(seconds));

    [Theory]
    [InlineData(0, "0min")]
    [InlineData(3600, "1h 0min")]
    [InlineData(93_600, "1d 2h")]
    public void Uptime(double seconds, string expected) => Assert.Equal(expected, MetricFormat.Uptime(seconds));

    [Fact]
    public void Memory_sizes_use_binary_gigabytes()
    {
        Assert.Equal("16 GB", MetricFormat.MemoryBytes(16.0 * 1024 * 1024 * 1024, En));
        Assert.Equal("12.3 GB", MetricFormat.MemoryBytes(12.3 * 1024 * 1024 * 1024, En));
        Assert.Equal("512 MB", MetricFormat.MemoryBytes(512.0 * 1024 * 1024, En));
    }

    [Fact]
    public void Decimal_separator_follows_the_region()
    {
        Assert.Equal("1,5 KB", MetricFormat.Bytes(1536, De));
        Assert.Equal("9,6 Mbps", MetricFormat.BitsPerSec(1_200_000, De));
        Assert.Equal("8,5 W", MetricFormat.Watts(8.5, De));
    }

    [Theory]
    [InlineData(123.4, "123")]
    [InlineData(45.64, "45.6")]
    public void Speed_test_values(double mbps, string expected) => Assert.Equal(expected, MetricFormat.Mbps(mbps, En));

    [Theory]
    [InlineData(1.3 * 1024 * 1024, 1024, 2.0 * 1024 * 1024)]
    [InlineData(600 * 1024, 1024, 1024 * 1024)]
    [InlineData(12.4, 1000, 20)]
    [InlineData(20, 1000, 20)]
    public void Graph_ceiling(double peak, double step, double expected) => Assert.Equal(expected, GraphScale.Ceiling(peak, step));

    [Fact]
    public void Network_ceiling_in_bits_and_bytes()
    {
        var bits = GraphScale.NetworkCeiling(2000, bits: true);
        Assert.Equal(2500, bits);
        Assert.Equal("20 Kbps", GraphScale.NetworkLabel(bits, bits: true, En));
        var bytes = GraphScale.NetworkCeiling(1500, bits: false);
        Assert.Equal(2048, bytes);
        Assert.Equal("2.0 KB/s", GraphScale.NetworkLabel(bytes, bits: false, En));
    }
}
