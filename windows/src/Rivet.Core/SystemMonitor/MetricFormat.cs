// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.SystemMonitor;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

/// <summary>
/// Metric formatting ported from the macOS <c>MetricFormat</c> (spec §6.10).
/// The decimal separator follows the user's region (<see cref="CultureInfo.CurrentCulture"/>)
/// unless a culture is passed. Halves round away from zero, like Swift's <c>rounded()</c>.
/// </summary>
public static class MetricFormat
{
    private static readonly string[] BinaryUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] CompactUnits = ["B", "K", "M", "G", "T", "P"];
    private static readonly string[] BitUnits = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
    private static readonly string[] CompactBitUnits = ["b", "Kb", "Mb", "Gb", "Tb"];
    private static readonly string[] DecimalUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Session totals: base 1024, one decimal under 10 ("0 B", "1.5 KB", "954 MB").</summary>
    public static string Bytes(double bytes, CultureInfo? culture = null) =>
        Scaled(bytes, 1024, BinaryUnits, " ", wholeFirstUnit: true, culture);

    /// <summary>"1.0 KB/s".</summary>
    public static string BytesPerSec(double bytesPerSecond, CultureInfo? culture = null) =>
        Bytes(bytesPerSecond, culture) + "/s";

    /// <summary>Menu bar / list rates: "320K", "1.2M", "1023B"; never wider than 5 characters.</summary>
    public static string BytesPerSecCompact(double bytesPerSecond, CultureInfo? culture = null) =>
        Scaled(bytesPerSecond, 1024, CompactUnits, string.Empty, wholeFirstUnit: true, culture);

    /// <summary>"12 Kbps", "9.6 Mbps" (×8, base 1000).</summary>
    public static string BitsPerSec(double bytesPerSecond, CultureInfo? culture = null) =>
        Scaled(bytesPerSecond * 8, 1000, BitUnits, " ", wholeFirstUnit: true, culture);

    /// <summary>"320Kb", "10Mb", "8.0Gb"; never wider than 5 characters.</summary>
    public static string BitsPerSecCompact(double bytesPerSecond, CultureInfo? culture = null) =>
        Scaled(bytesPerSecond * 8, 1000, CompactBitUnits, string.Empty, wholeFirstUnit: true, culture);

    /// <summary>A live network rate in the chosen unit.</summary>
    public static string NetworkRate(double bytesPerSecond, bool bits, CultureInfo? culture = null) =>
        bits ? BitsPerSec(bytesPerSecond, culture) : BytesPerSec(bytesPerSecond, culture);

    public static string NetworkRateCompact(double bytesPerSecond, bool bits, CultureInfo? culture = null) =>
        bits ? BitsPerSecCompact(bytesPerSecond, culture) : BytesPerSecCompact(bytesPerSecond, culture);

    /// <summary>Disk capacities, base 1000 like File Explorer's decimal sizes ("245 GB", "1.0 TB").</summary>
    public static string DiskBytes(double bytes, CultureInfo? culture = null) =>
        Scaled(bytes, 1000, DecimalUnits, " ", wholeFirstUnit: true, culture);

    /// <summary>Base 1000 with two decimals from TB up ("14.88 TB").</summary>
    public static string DiskBytesPrecise(double bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var tb = Math.Pow(1000, 4);
        if (bytes >= tb * 1000)
        {
            return Round(bytes / (tb * 1000), 2).ToString("0.00", culture) + " PB";
        }

        if (bytes >= tb)
        {
            var value = Round(bytes / tb, 2);
            return value >= 1000
                ? Round(bytes / (tb * 1000), 2).ToString("0.00", culture) + " PB"
                : value.ToString("0.00", culture) + " TB";
        }

        return DiskBytes(bytes, culture);
    }

    /// <summary>Memory sizes in binary units labelled GB/MB ("12.3 GB", "16 GB", "512 MB").</summary>
    public static string MemoryBytes(double bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;
        if (bytes >= gib * 1024)
        {
            return TrimmedOneDecimal(bytes / (gib * 1024), culture) + " TB";
        }

        if (bytes >= gib || Round(bytes / mib, 0) >= 1024)
        {
            return TrimmedOneDecimal(bytes / gib, culture) + " GB";
        }

        if (bytes >= mib)
        {
            return Round(bytes / mib, 0).ToString("0", culture) + " MB";
        }

        return bytes >= 1024 ? Round(bytes / 1024, 0).ToString("0", culture) + " KB" : Math.Max(0, Round(bytes, 0)).ToString("0", culture) + " B";
    }

    /// <summary>"8.5 W" under 10, "23 W" otherwise.</summary>
    public static string Watts(double watts, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var abs = Math.Abs(watts);
        return abs < 10 && Round(abs, 1) < 10
            ? Round(watts, 1).ToString("0.0", culture) + " W"
            : Round(watts, 0).ToString("0", culture) + " W";
    }

    /// <summary>"9W".</summary>
    public static string WattsCompact(double watts, CultureInfo? culture = null) =>
        Round(watts, 0).ToString("0", culture ?? CultureInfo.CurrentCulture) + "W";

    /// <summary>Fraction 0…1 clamped and rounded: 0.125 → "13%", 1.4 → "100%".</summary>
    public static string Percent(double fraction, CultureInfo? culture = null) =>
        PercentValue(fraction).ToString("0", culture ?? CultureInfo.CurrentCulture) + "%";

    /// <summary>The integer behind <see cref="Percent"/>.</summary>
    public static int PercentValue(double fraction) =>
        double.IsFinite(fraction) ? (int)Round(Math.Clamp(fraction, 0, 1) * 100, 0) : 0;

    public static double ToUnit(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? (celsius * 9 / 5) + 32 : celsius;

    /// <summary>"41 °C" or "106 °F".</summary>
    public static string Temperature(double celsius, TemperatureUnit unit, CultureInfo? culture = null) =>
        Round(ToUnit(celsius, unit), 0).ToString("0", culture ?? CultureInfo.CurrentCulture)
        + (unit == TemperatureUnit.Fahrenheit ? " °F" : " °C");

    /// <summary>"50°".</summary>
    public static string TemperatureCompact(double celsius, TemperatureUnit unit, CultureInfo? culture = null) =>
        Round(ToUnit(celsius, unit), 0).ToString("0", culture ?? CultureInfo.CurrentCulture) + "°";

    public static TemperatureUnit ParseTemperatureUnit(string value) =>
        value == "fahrenheit" ? TemperatureUnit.Fahrenheit : TemperatureUnit.Celsius;

    /// <summary>Battery time remaining: max(1, ⌊s/60⌋) minutes as "3h 42m".</summary>
    public static string BatteryTime(double seconds)
    {
        var minutes = Math.Max(1, (long)Math.Floor(Math.Max(0, seconds) / 60));
        return $"{minutes / 60}h {minutes % 60}m";
    }

    /// <summary>Uptime: "1d 2h", "1h 0min", "0min".</summary>
    public static string Uptime(double seconds)
    {
        var total = (long)Math.Max(0, Math.Floor(seconds));
        var days = total / 86_400;
        var hours = total % 86_400 / 3_600;
        var minutes = total % 3_600 / 60;
        if (days >= 1)
        {
            return $"{days}d {hours}h";
        }

        return hours >= 1 ? $"{hours}h {minutes}min" : $"{minutes}min";
    }

    /// <summary>Speed test results: "%.0f" from 100 Mbps, else "%.1f".</summary>
    public static string Mbps(double mbps, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return mbps >= 100 ? Round(mbps, 0).ToString("0", culture) : Round(mbps, 1).ToString("0.0", culture);
    }

    /// <summary>Half away from zero, like Swift's <c>rounded()</c>.</summary>
    public static double Round(double value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static string TrimmedOneDecimal(double value, CultureInfo culture)
    {
        var rounded = Round(value, 1);
        return rounded >= 100 || rounded == Math.Floor(rounded)
            ? Round(value, 0).ToString("0", culture)
            : rounded.ToString("0.0", culture);
    }

    /// <summary>
    /// Shared scaling rule: divide by <paramref name="step"/> while the value
    /// reaches a step; one decimal under 10 (none in the first unit when
    /// <paramref name="wholeFirstUnit"/>); a value whose printed form rounds
    /// up to a full step is promoted to the next unit.
    /// </summary>
    private static string Scaled(double value, double step, string[] units, string space, bool wholeFirstUnit, CultureInfo? culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (!double.IsFinite(value) || value < 0)
        {
            value = 0;
        }

        var index = 0;
        while (value >= step && index < units.Length - 1)
        {
            value /= step;
            index++;
        }

        while (true)
        {
            var decimals = (index == 0 && wholeFirstUnit) || value >= 10 ? 0 : 1;
            var rounded = Round(value, decimals);
            if (decimals == 1 && rounded >= 10)
            {
                decimals = 0;
                rounded = Round(value, 0);
            }

            if (rounded >= step && index < units.Length - 1)
            {
                value /= step;
                index++;
                continue;
            }

            return rounded.ToString(decimals == 1 ? "0.0" : "0", culture) + space + units[index];
        }
    }
}

/// <summary>Graph ceilings (spec §6.8): the next 1-2-5 step at or above the peak.</summary>
public static class GraphScale
{
    private static readonly double[] Steps = [1, 2, 5, 10, 20, 50, 100, 200, 500];

    public static double Ceiling(double peak, double step)
    {
        for (var p = 0; p <= 5; p++)
        {
            foreach (var s in Steps)
            {
                var candidate = s * Math.Pow(step, p);
                if (candidate >= peak)
                {
                    return candidate;
                }
            }
        }

        return peak;
    }

    /// <summary>Network ceiling in bytes/s: byte steps of 1024, or 1000-steps in bits then back to bytes.</summary>
    public static double NetworkCeiling(double peakBytesPerSecond, bool bits) =>
        bits ? Ceiling(peakBytesPerSecond * 8, 1000) / 8 : Ceiling(peakBytesPerSecond, 1024);

    /// <summary>The ceiling label of a network graph ("2.0 KB/s", "20 Kbps").</summary>
    public static string NetworkLabel(double ceilingBytesPerSecond, bool bits, CultureInfo? culture = null) =>
        MetricFormat.NetworkRate(ceilingBytesPerSecond, bits, culture);
}
