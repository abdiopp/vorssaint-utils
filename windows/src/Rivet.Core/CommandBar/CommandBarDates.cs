// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;
using Rivet.Core.Snippets;

namespace Rivet.Core.Launcher;

/// <summary>A date answer: the main text and a detail line.</summary>
public sealed record DateAnswer(string Text, string Detail);

/// <summary>
/// Date questions answered locally (spec 06 §6.9): "in 3 weeks", "3 days
/// ago", "days until 12/25", "time in Tokyo". Never uses the network.
/// </summary>
public static class CommandBarDates
{
    private static readonly HashSet<string> TodayWords = Words("today hoje heute hoy oggi aujourdhui aujourd'hui segodnya сегодня bugun 今日 오늘 今天");
    private static readonly HashSet<string> FutureWords = Words("in em daqui dentro nach tra fra dans через sonra 後 후 后 from");
    private static readonly HashSet<string> PastWords = Words("ago atras ha hace vor fa назад once 前 전");
    private static readonly HashSet<string> UntilWords = Words("until till ate hasta bis fino jusqu до kadar 까지 까지는");
    private static readonly HashSet<string> TimeWords = Words("time hora horas hour uhrzeit uhr ora heure время saat 時刻 時間 시간 时间 现在");
    private static readonly HashSet<string> DayWords = Words("day days dia dias tag tage giorno giorni jour jours den dnya dney дня дней день gun gunler 日 일 天");
    private static readonly HashSet<string> WeekWords = Words("week weeks semana semanas woche wochen settimana settimane semaine semaines nedelya недели недель неделя hafta 週 주 周 星期");
    private static readonly HashSet<string> MonthWords = Words("month months mes meses monat monate mese mesi mois месяц месяца месяцев ay 月 월 个月");
    private static readonly HashSet<string> YearWords = Words("year years ano anos jahr jahre anno anni an ans annee annees god года лет год yil 年 년");
    private static readonly HashSet<string> PlaceFillers = Words("a at de");

    public static DateAnswer? Answer(string input, DateTimeOffset now, CultureInfo? culture = null, TimeZoneInfo? localZone = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        localZone ??= TimeZoneInfo.Local;
        var trimmed = input.Trim();
        if (trimmed.Length is 0 or > 80)
        {
            return null;
        }

        var tokens = TextFold.ForCommand(trimmed).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(now, localZone);
        return Relative(tokens, local, culture) ?? DaysUntil(tokens, local, culture) ?? TimeElsewhere(tokens, now, culture);
    }

    private static DateAnswer? Relative(string[] tokens, DateTimeOffset now, CultureInfo culture)
    {
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > 10000)
            {
                continue;
            }

            var unit = tokens[i + 1];
            Func<DateTimeOffset, int, DateTimeOffset>? add =
                DayWords.Contains(unit) ? (d, k) => d.AddDays(k)
                : WeekWords.Contains(unit) ? (d, k) => d.AddDays(7 * k)
                : MonthWords.Contains(unit) ? (d, k) => d.AddMonths(k)
                : YearWords.Contains(unit) ? (d, k) => d.AddYears(k)
                : null;
            if (add is null)
            {
                continue;
            }

            var hasDirection = tokens.Any(t => TodayWords.Contains(t) || FutureWords.Contains(t) || PastWords.Contains(t));
            if (!hasDirection)
            {
                return null;
            }

            var backwards = tokens.Any(PastWords.Contains) || tokens.Contains("-");
            var target = add(now, backwards ? -Math.Abs(n) : n);
            return new DateAnswer(DateStyles.LongDate(target, culture), culture.DateTimeFormat.GetDayName(target.DayOfWeek));
        }

        return null;
    }

    private static DateAnswer? DaysUntil(string[] tokens, DateTimeOffset now, CultureInfo culture)
    {
        var index = Array.FindIndex(tokens, UntilWords.Contains);
        if (index < 0 || index == tokens.Length - 1)
        {
            return null;
        }

        var dateText = string.Join(' ', tokens[(index + 1)..]);
        var today = now.Date;
        DateTime target;
        if (DateTime.TryParseExact(dateText, culture.DateTimeFormat.ShortDatePattern, culture, DateTimeStyles.None, out var exact))
        {
            target = exact.Date;
        }
        else
        {
            var numbers = dateText.Split((char[])['/', '.', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 2 || !int.TryParse(numbers[0], CultureInfo.InvariantCulture, out var a) || !int.TryParse(numbers[1], CultureInfo.InvariantCulture, out var b))
            {
                return null;
            }

            var monthFirst = MonthBeforeDay(culture);
            var (month, day) = monthFirst ? (a, b) : (b, a);
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(today.Year, month) && !(month == 2 && day == 29))
            {
                return null;
            }

            target = NextOccurrence(today, month, day);
        }

        var days = Math.Abs((target - today).Days);
        var count = days == 1 ? Localization.L.Get("win.commandBar.dayCountOne") : Localization.L.Format("win.commandBar.dayCountFormat", days);
        return new DateAnswer(count, DateStyles.LongDate(new DateTimeOffset(target), culture));
    }

    private static DateTime NextOccurrence(DateTime today, int month, int day)
    {
        for (var year = today.Year; year < today.Year + 9; year++)
        {
            if (day <= DateTime.DaysInMonth(year, month))
            {
                var candidate = new DateTime(year, month, day);
                if (candidate >= today)
                {
                    return candidate;
                }
            }
        }

        return new DateTime(today.Year + 1, month, Math.Min(day, DateTime.DaysInMonth(today.Year + 1, month)));
    }

    /// <summary>Day/month order from the locale's short date pattern (y-M-d order).</summary>
    public static bool MonthBeforeDay(CultureInfo culture)
    {
        var pattern = culture.DateTimeFormat.ShortDatePattern;
        var month = pattern.IndexOf('M');
        var day = pattern.IndexOf('d');
        return month >= 0 && day >= 0 && month < day;
    }

    private static DateAnswer? TimeElsewhere(string[] tokens, DateTimeOffset now, CultureInfo culture)
    {
        if (!tokens.Any(TimeWords.Contains))
        {
            return null;
        }

        var place = tokens.Where(t => !TimeWords.Contains(t) && !FutureWords.Contains(t) && !PlaceFillers.Contains(t)).ToArray();
        if (place.Length == 0)
        {
            return null;
        }

        var cities = TimeZones.Cities;
        var name = string.Join(' ', place);
        if (!cities.TryGetValue(name, out var id) && !cities.TryGetValue(place[^1], out id))
        {
            return null;
        }

        if (TimeZones.Find(id) is not { } zone)
        {
            return null;
        }

        var there = TimeZoneInfo.ConvertTime(now, zone);
        var time = there.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        return new DateAnswer(time, $"{TimeZones.CityName(id)} · {DateStyles.FullDate(there, culture)}");
    }

    private static HashSet<string> Words(string list) =>
        list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TextFold.ForCommand).ToHashSet(StringComparer.Ordinal);
}
