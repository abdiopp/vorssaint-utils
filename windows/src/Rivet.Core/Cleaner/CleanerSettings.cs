// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Settings;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>Cleaner preferences (keys match the macOS app; "monthly" and the month day are Windows additions).</summary>
public static class CleanerSettings
{
    public static readonly Setting<string> ScheduleFrequency =
        new("cleanerScheduleFrequency", "off", Sanitize.OneOfStrings("off", "off", "daily", "weekly", "monthly"));

    public static readonly Setting<int> ScheduleHour = new("cleanerScheduleHour", 9, Sanitize.Clamp(0, 23));

    public static readonly Setting<int> ScheduleMinute = new("cleanerScheduleMinute", 0, Sanitize.Clamp(0, 59));

    /// <summary>1 = Sunday … 7 = Saturday (macOS calendar numbering).</summary>
    public static readonly Setting<int> ScheduleWeekday = new("cleanerScheduleWeekday", 2, Sanitize.Clamp(1, 7));

    /// <summary>Day of the month for monthly runs (1–28, so every month has it).</summary>
    public static readonly Setting<int> ScheduleMonthDay = new("cleanerScheduleMonthDay", 1, Sanitize.Clamp(1, 28));

    public static readonly Setting<bool> ScheduleNotify = new("cleanerScheduleNotify", true);

    /// <summary>0 = off; offered 7/14/30/60/90; stored values clamp to ≤ 3650.</summary>
    public static readonly Setting<int> ScreenshotAgeDays = new("cleanerScreenshotAgeDays", 30, Sanitize.Clamp(0, 3650));

    public static readonly Setting<double> LastAutoRun = new("cleanerLastAutoRun", 0d, machineState: true);

    public static readonly Setting<long> LastAutoFreed = new("cleanerLastAutoFreed", 0L, machineState: true);

    public static readonly Setting<int> LastAutoFailed = new("cleanerLastAutoFailed", 0, machineState: true);

    public static readonly IReadOnlyList<int> ScreenshotAgeOptions = [0, 7, 14, 30, 60, 90];
}

/// <summary>
/// File sizes the way Explorer shows them: 1024-based units, three
/// significant digits, extra digits truncated (StrFormatByteSize style).
/// One formatter for the whole area keeps rounding consistent.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return string.Format(culture, "{0} bytes", bytes);
        }

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Explorer truncates rather than rounds: 1.999 KB shows as "1.99 KB".
        var decimals = value < 10 ? 2 : value < 100 ? 1 : 0;
        var factor = Math.Pow(10, decimals);
        var truncated = Math.Floor(value * factor) / factor;
        return truncated.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), culture) + " " + Units[unit];
    }
}
