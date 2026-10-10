// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Localization;

/// <summary>
/// Formats Apple/C printf-style strings, as stored in the extracted catalogs:
/// <c>%@ %d %i %u %ld %lld %f %.2f %s %x %c %%</c>, positional <c>%1$@</c>,
/// flags (<c>-0+ #</c>), width and precision. Only call it for strings that
/// are format strings: some captions contain literal percent signs.
/// </summary>
public static class PrintfFormatter
{
    public static string Format(string format, CultureInfo culture, params object?[] args)
    {
        if (string.IsNullOrEmpty(format) || format.IndexOf('%') < 0)
        {
            return format;
        }

        var output = new StringBuilder(format.Length + 16);
        var sequential = 0;
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c != '%')
            {
                output.Append(c);
                i++;
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '%')
            {
                output.Append('%');
                i += 2;
                continue;
            }

            var start = i;
            i++;

            // Positional argument: %<n>$
            int? position = null;
            var digitsStart = i;
            while (i < format.Length && char.IsAsciiDigit(format[i])) i++;
            if (i > digitsStart && i < format.Length && format[i] == '$')
            {
                position = int.Parse(format.AsSpan(digitsStart, i - digitsStart), CultureInfo.InvariantCulture);
                i++;
            }
            else
            {
                i = digitsStart;
            }

            var leftAlign = false;
            var zeroPad = false;
            var plus = false;
            var space = false;
            while (i < format.Length && format[i] is '-' or '0' or '+' or ' ' or '#' or '\'')
            {
                switch (format[i])
                {
                    case '-': leftAlign = true; break;
                    case '0': zeroPad = true; break;
                    case '+': plus = true; break;
                    case ' ': space = true; break;
                }

                i++;
            }

            var width = 0;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                width = (width * 10) + (format[i] - '0');
                i++;
            }

            int? precision = null;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                var p = 0;
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                {
                    p = (p * 10) + (format[i] - '0');
                    i++;
                }

                precision = p;
            }

            while (i < format.Length && format[i] is 'h' or 'l' or 'q' or 'L' or 'z' or 't' or 'j')
            {
                i++;
            }

            if (i >= format.Length)
            {
                output.Append(format, start, format.Length - start);
                break;
            }

            var conversion = format[i];
            i++;
            var argIndex = position.HasValue ? position.Value - 1 : sequential++;
            var arg = argIndex >= 0 && argIndex < args.Length ? args[argIndex] : null;

            string text;
            switch (conversion)
            {
                case '@':
                case 's':
                case 'S':
                    text = Convert.ToString(arg, culture) ?? string.Empty;
                    if (precision is { } maxChars && text.Length > maxChars)
                    {
                        text = text[..maxChars];
                    }

                    break;
                case 'd':
                case 'i':
                    text = FormatInteger(arg, culture, plus, space);
                    break;
                case 'u':
                    text = FormatInteger(arg, culture, false, false);
                    break;
                case 'f':
                case 'F':
                    text = ToDouble(arg).ToString("F" + (precision ?? 6), culture);
                    if (plus && !text.StartsWith('-')) text = "+" + text;
                    break;
                case 'e':
                case 'E':
                    text = ToDouble(arg).ToString((conversion == 'e' ? "e" : "E") + (precision ?? 6), culture);
                    break;
                case 'g':
                case 'G':
                    text = ToDouble(arg).ToString("G" + (precision ?? 6), culture);
                    break;
                case 'x':
                case 'X':
                    text = Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture).ToString(conversion == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    break;
                case 'o':
                    text = Convert.ToString(Convert.ToInt64(arg ?? 0, CultureInfo.InvariantCulture), 8);
                    break;
                case 'c':
                    text = arg is char ch ? ch.ToString() : Convert.ToString(arg, culture) ?? string.Empty;
                    break;
                default:
                    // Unknown conversion: keep the original text.
                    output.Append(format, start, i - start);
                    continue;
            }

            if (text.Length < width)
            {
                if (leftAlign)
                {
                    text = text.PadRight(width);
                }
                else if (zeroPad && conversion is 'd' or 'i' or 'u' or 'f' or 'F' or 'x' or 'X')
                {
                    var negative = text.StartsWith('-') || text.StartsWith('+');
                    text = negative ? text[0] + text[1..].PadLeft(width - 1, '0') : text.PadLeft(width, '0');
                }
                else
                {
                    text = text.PadLeft(width);
                }
            }

            output.Append(text);
        }

        return output.ToString();
    }

    /// <summary>Counts the placeholders and their kinds, e.g. <c>["int","object"]</c>, for catalog checks.</summary>
    public static IReadOnlyList<string> ArgumentSignature(string format)
    {
        var kinds = new SortedDictionary<int, string>();
        var sequential = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%') continue;
            if (i + 1 < format.Length && format[i + 1] == '%') { i++; continue; }
            var j = i + 1;
            int? position = null;
            var ds = j;
            while (j < format.Length && char.IsAsciiDigit(format[j])) j++;
            if (j > ds && j < format.Length && format[j] == '$') { position = int.Parse(format.AsSpan(ds, j - ds), CultureInfo.InvariantCulture); j++; }
            else j = ds;
            while (j < format.Length && "-0+ #'.0123456789hlqLztj".Contains(format[j])) j++;
            if (j >= format.Length) break;
            var kind = format[j] switch
            {
                'd' or 'i' or 'u' or 'x' or 'X' or 'o' or 'c' => "int",
                'f' or 'F' or 'e' or 'E' or 'g' or 'G' => "double",
                '@' or 's' or 'S' => "object",
                _ => null,
            };
            if (kind is null) continue;
            kinds[position ?? ++sequential] = kind;
            i = j;
        }

        return kinds.Values.ToList();
    }

    private static string FormatInteger(object? arg, CultureInfo culture, bool plus, bool space)
    {
        long value = arg switch
        {
            null => 0,
            double d => (long)Math.Truncate(d),
            float f => (long)Math.Truncate(f),
            decimal m => (long)Math.Truncate(m),
            _ => Convert.ToInt64(arg, CultureInfo.InvariantCulture),
        };
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (value >= 0)
        {
            if (plus) text = "+" + text;
            else if (space) text = " " + text;
        }

        return text;
    }

    private static double ToDouble(object? arg) => arg switch
    {
        null => 0,
        double d => d,
        float f => f,
        _ => Convert.ToDouble(arg, CultureInfo.InvariantCulture),
    };
}
