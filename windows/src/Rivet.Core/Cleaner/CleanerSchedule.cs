// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.Cleaner;

public enum ScheduleFrequency
{
    Off,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>The automatic cleanup schedule as set by the person.</summary>
public sealed record CleanerScheduleSpec(ScheduleFrequency Frequency, int Hour, int Minute, int Weekday, int MonthDay)
{
    public static ScheduleFrequency ParseFrequency(string value) => value switch
    {
        "daily" => ScheduleFrequency.Daily,
        "weekly" => ScheduleFrequency.Weekly,
        "monthly" => ScheduleFrequency.Monthly,
        _ => ScheduleFrequency.Off,
    };

    public static string FrequencyKey(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Daily => "daily",
        ScheduleFrequency.Weekly => "weekly",
        ScheduleFrequency.Monthly => "monthly",
        _ => "off",
    };
}

/// <summary>
/// Calendar math of the automatic cleanup (spec §3.1.7, §6.3): the next
/// matching wall-clock time strictly after a moment, "next time" policy for
/// daylight-saving gaps (a time that does not exist moves to the first one
/// that does), the first occurrence for repeated hours, and a catch-up two
/// minutes after launch when a run was missed while the PC was off.
/// </summary>
public static class CleanerSchedule
{
    public static readonly TimeSpan MissedRunDelay = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(600);

    public static DateTime? NextFireUtc(CleanerScheduleSpec spec, DateTime afterUtc, TimeZoneInfo zone)
    {
        if (spec.Frequency == ScheduleFrequency.Off)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);
        var hour = Math.Clamp(spec.Hour, 0, 23);
        var minute = Math.Clamp(spec.Minute, 0, 59);
        for (var day = 0; day <= 400; day++)
        {
            var date = local.Date.AddDays(day);
            if (!Matches(spec, date))
            {
                continue;
            }

            var candidate = ToUtc(DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified), zone);
            if (candidate > afterUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// When the timer should fire: two minutes from now when a run was missed
    /// (a last run exists and the run after it is already due), otherwise the
    /// next calendar match. A schedule that never ran does not catch up.
    /// </summary>
    public static DateTime? DueUtc(CleanerScheduleSpec spec, DateTime? lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (spec.Frequency == ScheduleFrequency.Off)
        {
            return null;
        }

        if (lastRunUtc is { } last && NextFireUtc(spec, last, zone) is { } afterLast && afterLast <= nowUtc)
        {
            return nowUtc + MissedRunDelay;
        }

        return NextFireUtc(spec, nowUtc, zone);
    }

    /// <summary>12-hour picker → 24-hour value.</summary>
    public static int To24Hour(int hour12, bool pm) => (Math.Clamp(hour12, 1, 12) % 12) + (pm ? 12 : 0);

    /// <summary>24-hour value → 12-hour picker.</summary>
    public static (int Hour12, bool Pm) To12Hour(int hour24) => (hour24 % 12 == 0 ? 12 : hour24 % 12, hour24 >= 12);

    private static bool Matches(CleanerScheduleSpec spec, DateTime date) => spec.Frequency switch
    {
        ScheduleFrequency.Weekly => (int)date.DayOfWeek == Math.Clamp(spec.Weekday, 1, 7) - 1,
        ScheduleFrequency.Monthly => date.Day == Math.Min(Math.Clamp(spec.MonthDay, 1, 28), DateTime.DaysInMonth(date.Year, date.Month)),
        _ => true,
    };

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var guard = 0;
        while (zone.IsInvalidTime(local) && guard++ < 240)
        {
            local = local.AddMinutes(1);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The first occurrence of a repeated hour is the one with the larger offset.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
