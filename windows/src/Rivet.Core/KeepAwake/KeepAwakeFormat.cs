// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Localization;

namespace Rivet.Core.Awake;

/// <summary>Keep Awake time formats (spec §6.10).</summary>
public static class KeepAwakeFormat
{
    /// <summary>Status line remaining time: "1 h 05 min", "4 min 09 s", "12 s".</summary>
    public static string Remaining(TimeSpan remaining)
    {
        var total = (long)Math.Max(0, Math.Floor(remaining.TotalSeconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        if (hours > 0)
        {
            return $"{hours} h {minutes:00} min";
        }

        return minutes > 0 ? $"{minutes} min {seconds:00} s" : $"{seconds} s";
    }

    /// <summary>Highlighted chip countdown: "1:05:12", "58:12".</summary>
    public static string Chip(TimeSpan remaining)
    {
        var total = (long)Math.Max(0, Math.Floor(remaining.TotalSeconds));
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    /// <summary>Compact countdown next to the readouts: "1:05" from an hour, "12 min" (at least 1), "∞" indefinitely.</summary>
    public static string Compact(TimeSpan? remaining)
    {
        if (remaining is not { } r)
        {
            return "∞";
        }

        var minutes = (long)Math.Max(0, Math.Floor(r.TotalMinutes));
        return minutes >= 60 ? $"{minutes / 60}:{minutes % 60:00}" : $"{Math.Max(1, minutes)} min";
    }

    /// <summary>Duration chip label: "15m", "1h", "∞".</summary>
    public static string ChipLabel(int minutes) => minutes switch
    {
        0 => "∞",
        < 60 => L.Format("win.keepAwake.minutesShortFormat", minutes),
        _ => L.Format("win.keepAwake.hoursShortFormat", minutes / 60),
    };

    /// <summary>Long duration label for menus and Settings: "15 minutes", "1 hour", "Indefinitely".</summary>
    public static string DurationTitle(int minutes) => minutes switch
    {
        15 => L.Get("Strings.minutes15"),
        30 => L.Get("Strings.minutes30"),
        60 => L.Get("Strings.hour1"),
        120 => L.Get("Strings.hours2"),
        240 => L.Get("Strings.hours4"),
        480 => L.Get("Strings.hours8"),
        _ => L.Get("Strings.indefinitely"),
    };

    /// <summary>"Today · 2h 5m" / "Tomorrow · 9h 30m" for the Until picker.</summary>
    public static string UntilSummary(DateTimeOffset until, DateTimeOffset now)
    {
        var day = until.ToLocalTime().Date == now.ToLocalTime().Date ? L.Get("win.keepAwake.today") : L.Get("win.keepAwake.tomorrow");
        var minutes = (long)Math.Max(0, Math.Ceiling((until - now).TotalMinutes));
        return $"{day} · {minutes / 60}h {minutes % 60}m";
    }

    /// <summary>The end time in the short local time style ("14:30" or "2:30 PM").</summary>
    public static string Time(DateTimeOffset time, CultureInfo? culture = null) =>
        time.ToLocalTime().ToString("t", culture ?? CultureInfo.CurrentCulture);
}
