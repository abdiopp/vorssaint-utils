// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Snippets;

public enum DateTokenKind
{
    Date,
    Time,
    DateTime,
}

public enum DateStyle
{
    Short,
    Medium,
    Long,
    Full,
    Iso8601,
    Custom,
}

/// <summary>
/// The named date/time styles of the region in use, as .NET patterns (for
/// <c>{{date}}</c> and <c>{{time}}</c>) and as ICU/TR35 patterns frozen into
/// tokens by the date/time builder (spec 06 §3.6.4). ICU's medium date has no
/// .NET twin; it is the long date without the weekday and with abbreviated
/// month names.
/// </summary>
public static partial class DateStyles
{
    public const string IsoDate = "yyyy-MM-dd";
    public const string IsoTime = "HH:mm:ssXXX";
    public const string IsoDateTime = "yyyy-MM-dd'T'HH:mm:ssXXX";

    /// <summary>.NET pattern of the medium date ("MMM d, yyyy" in en-US).</summary>
    public static string MediumDateNet(CultureInfo culture) =>
        LongDateNet(culture).Replace("MMMM", "MMM", StringComparison.Ordinal);

    /// <summary>.NET pattern of the long date without the weekday ("MMMM d, yyyy").</summary>
    public static string LongDateNet(CultureInfo culture) => WithoutWeekday(culture.DateTimeFormat.LongDatePattern);

    /// <summary>The medium date text <c>{{date}}</c> uses.</summary>
    public static string MediumDate(DateTimeOffset date, CultureInfo culture) => date.ToString(MediumDateNet(culture), culture);

    /// <summary>The short time text <c>{{time}}</c> uses.</summary>
    public static string ShortTime(DateTimeOffset date, CultureInfo culture) => date.ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    /// <summary>The long date the Command Bar shows ("August 18, 2026").</summary>
    public static string LongDate(DateTimeOffset date, CultureInfo culture) => date.ToString(LongDateNet(culture), culture);

    /// <summary>The full date ("Tuesday, August 18, 2026").</summary>
    public static string FullDate(DateTimeOffset date, CultureInfo culture) => date.ToString(culture.DateTimeFormat.LongDatePattern, culture);

    /// <summary>The TR35 pattern a style resolves to right now (frozen into the token).</summary>
    public static string Pattern(DateTokenKind kind, DateStyle style, CultureInfo culture)
    {
        if (style == DateStyle.Iso8601)
        {
            return kind switch
            {
                DateTokenKind.Date => IsoDate,
                DateTokenKind.Time => IsoTime,
                _ => IsoDateTime,
            };
        }

        var info = culture.DateTimeFormat;
        var date = style switch
        {
            DateStyle.Short => info.ShortDatePattern,
            DateStyle.Medium => MediumDateNet(culture),
            DateStyle.Long => LongDateNet(culture),
            _ => info.LongDatePattern,
        };
        var time = style switch
        {
            DateStyle.Short => info.ShortTimePattern,
            DateStyle.Medium => info.LongTimePattern,
            DateStyle.Long => info.LongTimePattern + " z",
            _ => info.LongTimePattern + " zzzz",
        };
        return kind switch
        {
            DateTokenKind.Date => ToIcu(date),
            DateTokenKind.Time => ToIcu(time),
            _ => ToIcu(date) + " " + ToIcu(time),
        };
    }

    /// <summary>The first named style whose pattern equals <paramref name="pattern"/> right now, else Custom.</summary>
    public static DateStyle StyleOf(DateTokenKind kind, string pattern, CultureInfo culture)
    {
        foreach (var style in new[] { DateStyle.Iso8601, DateStyle.Short, DateStyle.Medium, DateStyle.Long, DateStyle.Full })
        {
            if (Pattern(kind, style, culture) == pattern)
            {
                return style;
            }
        }

        return DateStyle.Custom;
    }

    /// <summary>Converts a .NET custom date pattern to ICU/TR35 syntax.</summary>
    public static string ToIcu(string net)
    {
        var builder = new StringBuilder(net.Length + 4);
        var i = 0;
        while (i < net.Length)
        {
            var c = net[i];
            if (c is '\'' or '"')
            {
                var end = net.IndexOf(c, i + 1);
                if (end < 0)
                {
                    end = net.Length;
                }

                AppendQuoted(builder, net[(i + 1)..end]);
                i = end + 1;
                continue;
            }

            if (c == '\\' && i + 1 < net.Length)
            {
                AppendQuoted(builder, net[i + 1].ToString());
                i += 2;
                continue;
            }

            var count = 1;
            while (i + count < net.Length && net[i + count] == c)
            {
                count++;
            }

            builder.Append(c switch
            {
                'y' => count == 2 ? "yy" : "y",
                'M' => new string('M', Math.Min(count, 4)),
                'd' => count switch
                {
                    1 => "d",
                    2 => "dd",
                    3 => "EEE",
                    _ => "EEEE",
                },
                't' => "a",
                'H' or 'h' or 'm' or 's' => new string(c, Math.Min(count, 2)),
                'f' or 'F' => new string('S', count),
                'z' => count switch
                {
                    1 => "x",
                    2 => "xx",
                    _ => "xxx",
                },
                'K' => "XXX",
                'g' => "G",
                _ when char.IsAsciiLetter(c) => "'" + new string(c, count) + "'",
                _ => new string(c, count),
            });
            i += count;
        }

        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string literal)
    {
        if (literal.Length == 0)
        {
            return;
        }

        if (literal.Any(char.IsAsciiLetter) || literal.Contains('\''))
        {
            builder.Append('\'').Append(literal.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        }
        else
        {
            builder.Append(literal);
        }
    }

    /// <summary>Removes the weekday ("dddd") and the separator next to it from a .NET date pattern.</summary>
    public static string WithoutWeekday(string pattern)
    {
        var result = WeekdayLeading().Replace(pattern, string.Empty);
        result = WeekdayTrailing().Replace(result, string.Empty);
        result = result.Replace("dddd", string.Empty, StringComparison.Ordinal);
        return result.Trim(' ', ',', '،');
    }

    [GeneratedRegex(@"^\s*dddd[\s,.،]*")]
    private static partial Regex WeekdayLeading();

    [GeneratedRegex(@"[\s,.،]*dddd\s*$")]
    private static partial Regex WeekdayTrailing();
}
