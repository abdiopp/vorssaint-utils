// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Snippets;

/// <summary>
/// Formats dates with ICU/TR35 patterns (<c>yyyy-MM-dd'T'HH:mm:ssXXX</c>),
/// the pattern language snippet tokens store on macOS, so tokens move between
/// platforms byte for byte (spec 06 §6.12). Month and weekday names come from
/// .NET's culture data (ICU-backed on Windows 10 1903+). Unknown letters are
/// kept literally.
/// </summary>
public static class IcuDateFormat
{
    public static string Format(DateTimeOffset instant, string pattern, TimeZoneInfo zone, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var builder = new StringBuilder(pattern.Length + 16);
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i += 2;
                    continue;
                }

                var end = i + 1;
                while (end < pattern.Length)
                {
                    if (pattern[end] == '\'')
                    {
                        if (end + 1 < pattern.Length && pattern[end + 1] == '\'')
                        {
                            builder.Append('\'');
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    builder.Append(pattern[end]);
                    end++;
                }

                i = end + 1;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                var count = 1;
                while (i + count < pattern.Length && pattern[i + count] == c)
                {
                    count++;
                }

                builder.Append(Field(c, count, local, zone, culture));
                i += count;
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static string Field(char letter, int count, DateTimeOffset date, TimeZoneInfo zone, CultureInfo culture)
    {
        var info = culture.DateTimeFormat;
        var invariant = CultureInfo.InvariantCulture;
        string Pad(long value, int width) => value.ToString(new string('0', Math.Max(1, width)), invariant);
        switch (letter)
        {
            case 'G':
                return count == 4 ? info.GetEraName(1) : count == 5 ? info.GetAbbreviatedEraName(1)[..1] : info.GetAbbreviatedEraName(1);
            case 'y' or 'u':
                return count == 2 ? Pad(date.Year % 100, 2) : Pad(date.Year, count);
            case 'Y':
                var weekYear = ISOWeek.GetYear(date.DateTime);
                return count == 2 ? Pad(weekYear % 100, 2) : Pad(weekYear, count);
            case 'Q' or 'q':
                var quarter = ((date.Month - 1) / 3) + 1;
                return count switch
                {
                    1 or 2 => Pad(quarter, count),
                    3 => "Q" + quarter,
                    5 => quarter.ToString(invariant),
                    _ => Ordinal(quarter) + " quarter",
                };
            case 'M':
                return Month(date.Month, count, info, genitive: true);
            case 'L':
                return Month(date.Month, count, info, genitive: false);
            case 'w':
                return Pad(info.Calendar.GetWeekOfYear(date.DateTime, info.CalendarWeekRule, info.FirstDayOfWeek), count);
            case 'W':
                return WeekOfMonth(date, info).ToString(invariant);
            case 'd':
                return Pad(date.Day, count);
            case 'D':
                return Pad(date.DayOfYear, count);
            case 'F':
                return (((date.Day - 1) / 7) + 1).ToString(invariant);
            case 'E':
                return Weekday(date.DayOfWeek, Math.Max(3, count), info);
            case 'e' or 'c':
                if (count <= 2)
                {
                    var local = (((int)date.DayOfWeek - (int)info.FirstDayOfWeek + 7) % 7) + 1;
                    return Pad(local, count);
                }

                return Weekday(date.DayOfWeek, count, info);
            case 'a' or 'b' or 'B':
                var marker = date.Hour < 12 ? info.AMDesignator : info.PMDesignator;
                if (marker.Length == 0)
                {
                    marker = date.Hour < 12 ? "AM" : "PM";
                }

                return count == 5 ? marker[..1].ToLowerInvariant() : marker;
            case 'h':
                return Pad(date.Hour % 12 == 0 ? 12 : date.Hour % 12, count);
            case 'H':
                return Pad(date.Hour, count);
            case 'K':
                return Pad(date.Hour % 12, count);
            case 'k':
                return Pad(date.Hour == 0 ? 24 : date.Hour, count);
            case 'm':
                return Pad(date.Minute, count);
            case 's':
                return Pad(date.Second, count);
            case 'S':
                var fraction = (date.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", invariant);
                return count <= 7 ? fraction[..count] : fraction + new string('0', count - 7);
            case 'A':
                return Pad((long)date.TimeOfDay.TotalMilliseconds, count);
            case 'z':
                return count >= 4 ? LongZoneName(zone, date) : ShortZoneName(zone, date);
            case 'Z':
                return count switch
                {
                    <= 3 => Offset(date.Offset, colon: false, zulu: false, forceMinutes: true),
                    4 => "GMT" + Offset(date.Offset, colon: true, zulu: false, forceMinutes: true),
                    _ => Offset(date.Offset, colon: true, zulu: true, forceMinutes: true),
                };
            case 'O':
                return count >= 4 ? "GMT" + Offset(date.Offset, colon: true, zulu: false, forceMinutes: true) : ShortGmt(date.Offset);
            case 'v':
                return count >= 4 ? GenericZoneName(zone) : ShortZoneName(zone, date);
            case 'V':
                var iana = IanaId(zone);
                return count switch
                {
                    1 or 2 => iana,
                    3 => TimeZones.CityName(iana),
                    _ => TimeZones.CityName(iana) + " Time",
                };
            case 'X':
                return IsoOffset(date.Offset, count, zulu: true);
            case 'x':
                return IsoOffset(date.Offset, count, zulu: false);
            default:
                return new string(letter, count);
        }
    }

    private static string Month(int month, int count, DateTimeFormatInfo info, bool genitive)
    {
        var index = month - 1;
        switch (count)
        {
            case 1:
                return month.ToString(CultureInfo.InvariantCulture);
            case 2:
                return month.ToString("00", CultureInfo.InvariantCulture);
            case 3:
                var abbreviated = genitive ? info.AbbreviatedMonthGenitiveNames[index] : info.AbbreviatedMonthNames[index];
                return abbreviated.Length > 0 ? abbreviated : info.AbbreviatedMonthNames[index];
            case 4:
                var full = genitive ? info.MonthGenitiveNames[index] : info.MonthNames[index];
                return full.Length > 0 ? full : info.MonthNames[index];
            default:
                var name = info.MonthNames[index];
                return name.Length > 0 ? name[..1].ToUpperInvariant() : month.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string Weekday(DayOfWeek day, int count, DateTimeFormatInfo info) => count switch
    {
        3 => info.GetAbbreviatedDayName(day),
        4 => info.GetDayName(day),
        5 => info.GetDayName(day) is { Length: > 0 } n ? n[..1].ToUpperInvariant() : string.Empty,
        _ => info.GetShortestDayName(day),
    };

    private static int WeekOfMonth(DateTimeOffset date, DateTimeFormatInfo info)
    {
        var first = new DateTime(date.Year, date.Month, 1);
        var offset = ((int)first.DayOfWeek - (int)info.FirstDayOfWeek + 7) % 7;
        return ((date.Day + offset - 1) / 7) + 1;
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => n.ToString(CultureInfo.InvariantCulture) + "th",
    };

    private static string Offset(TimeSpan offset, bool colon, bool zulu, bool forceMinutes)
    {
        if (zulu && offset == TimeSpan.Zero)
        {
            return "Z";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var hours = ((int)abs.TotalHours).ToString("00", CultureInfo.InvariantCulture);
        var minutes = abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
        if (!forceMinutes && abs.Minutes == 0)
        {
            return sign + hours;
        }

        return colon ? $"{sign}{hours}:{minutes}" : $"{sign}{hours}{minutes}";
    }

    private static string IsoOffset(TimeSpan offset, int count, bool zulu)
    {
        if (zulu && offset == TimeSpan.Zero)
        {
            return "Z";
        }

        return count switch
        {
            1 => Offset(offset, colon: false, zulu: false, forceMinutes: false),
            2 or 4 => Offset(offset, colon: false, zulu: false, forceMinutes: true),
            _ => Offset(offset, colon: true, zulu: false, forceMinutes: true),
        };
    }

    private static string ShortGmt(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
        {
            return "GMT";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"GMT{sign}{(int)abs.TotalHours}"
            : $"GMT{sign}{(int)abs.TotalHours}:{abs.Minutes:00}";
    }

    /// <summary>"PDT"/"JST" from the abbreviation table when this zone has one, else "GMT+9".</summary>
    private static string ShortZoneName(TimeZoneInfo zone, DateTimeOffset date)
    {
        if (zone == TimeZoneInfo.Utc || zone.Id is "UTC" or "Etc/UTC")
        {
            return "UTC";
        }

        var daylight = zone.IsDaylightSavingTime(date);
        var id = IanaId(zone);
        foreach (var (abbreviation, target) in TimeZones.Abbreviations)
        {
            if (string.Equals(target, id, StringComparison.Ordinal) && MatchesDaylight(abbreviation, daylight, zone))
            {
                return abbreviation;
            }
        }

        return ShortGmt(date.Offset);
    }

    /// <summary>The IANA id of a zone (Windows ids of the local zone are converted).</summary>
    public static string IanaId(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    private static bool MatchesDaylight(string abbreviation, bool daylight, TimeZoneInfo zone)
    {
        if (!zone.SupportsDaylightSavingTime)
        {
            return true;
        }

        var summer = abbreviation is "ADT" or "AKDT" or "BRST" or "BST" or "CDT" or "CEST" or "CLST" or "EDT" or "EEST" or "MDT" or "MSD" or "NDT" or "NZDT" or "PDT" or "WEST";
        return summer == daylight;
    }

    private static string LongZoneName(TimeZoneInfo zone, DateTimeOffset date)
    {
        if (zone == TimeZoneInfo.Utc)
        {
            return "Coordinated Universal Time";
        }

        var name = zone.IsDaylightSavingTime(date) ? zone.DaylightName : zone.StandardName;
        return string.IsNullOrWhiteSpace(name) ? "GMT" + Offset(date.Offset, colon: true, zulu: false, forceMinutes: true) : name;
    }

    private static string GenericZoneName(TimeZoneInfo zone)
    {
        var name = zone.StandardName;
        return name.Replace(" Standard Time", " Time", StringComparison.Ordinal);
    }
}
