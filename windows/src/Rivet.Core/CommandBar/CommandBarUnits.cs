// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Rivet.Core.Clipboard;

namespace Rivet.Core.Launcher;

public enum UnitFamily
{
    Temperature,
    Length,
    Mass,
    Data,
    Duration,
    Volume,
}

/// <summary>One unit of the lexicon: names (folded), display symbol and the factor to the family's base.</summary>
public sealed record UnitDefinition(UnitFamily Family, string Symbol, double Factor, double Offset, IReadOnlyList<string> Names)
{
    public double ToBase(double value) => (value * Factor) + Offset;

    public double FromBase(double value) => (value - Offset) / Factor;
}

/// <summary>
/// "100 km to mi" style conversions (spec 06 §6.7). Matched exactly after
/// folding, never fuzzy; anything that does not parse stays a search.
/// Imperial factors are Foundation's (truncated to 6 significant digits) so
/// results match the macOS app.
/// </summary>
public static class CommandBarUnits
{
    /// <summary>Folded conversion words, shared with colour conversions.</summary>
    public static readonly IReadOnlySet<string> ConversionWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "to", "in", "into", "as", "em", "para", "pra", "en", "a", "nach", "zu", "su", "->", ">", "→",
    };

    public static readonly IReadOnlyList<UnitDefinition> Units =
    [
        new(UnitFamily.Temperature, "°C", 1, 273.15, ["c", "celsius", "°c", "centigrade"]),
        new(UnitFamily.Temperature, "°F", 0.55555555555556, 255.37222222222428, ["f", "fahrenheit", "°f"]),
        new(UnitFamily.Temperature, "K", 1, 0, ["k", "kelvin"]),
        new(UnitFamily.Length, "mm", 0.001, 0, ["mm", "millimeter", "millimeters", "milimetro", "milimetros"]),
        new(UnitFamily.Length, "cm", 0.01, 0, ["cm", "centimeter", "centimeters", "centimetro", "centimetros"]),
        new(UnitFamily.Length, "m", 1, 0, ["m", "meter", "meters", "metro", "metros", "metre", "metres"]),
        new(UnitFamily.Length, "km", 1000, 0, ["km", "kilometer", "kilometers", "quilometro", "quilometros"]),
        new(UnitFamily.Length, "in", 0.0254, 0, ["in", "inch", "inches", "polegada", "polegadas", "\""]),
        new(UnitFamily.Length, "ft", 0.3048, 0, ["ft", "foot", "feet", "pe", "pes"]),
        new(UnitFamily.Length, "yd", 0.9144, 0, ["yd", "yard", "yards", "jarda", "jardas"]),
        new(UnitFamily.Length, "mi", 1609.344, 0, ["mi", "mile", "miles", "milha", "milhas"]),
        new(UnitFamily.Mass, "mg", 0.000001, 0, ["mg", "milligram", "milligrams"]),
        new(UnitFamily.Mass, "g", 0.001, 0, ["g", "gram", "grams", "grama", "gramas"]),
        new(UnitFamily.Mass, "kg", 1, 0, ["kg", "kilogram", "kilograms", "quilo", "quilos"]),
        new(UnitFamily.Mass, "t", 1000, 0, ["t", "ton", "tons", "tonne", "tonelada", "toneladas"]),
        new(UnitFamily.Mass, "oz", 0.0283495, 0, ["oz", "ounce", "ounces", "onca", "oncas"]),
        new(UnitFamily.Mass, "lb", 0.453592, 0, ["lb", "lbs", "pound", "pounds", "libra", "libras"]),
        new(UnitFamily.Data, "B", 1, 0, ["b", "byte", "bytes"]),
        new(UnitFamily.Data, "kB", 1e3, 0, ["kb", "kilobyte", "kilobytes"]),
        new(UnitFamily.Data, "MB", 1e6, 0, ["mb", "megabyte", "megabytes"]),
        new(UnitFamily.Data, "GB", 1e9, 0, ["gb", "gigabyte", "gigabytes"]),
        new(UnitFamily.Data, "TB", 1e12, 0, ["tb", "terabyte", "terabytes"]),
        new(UnitFamily.Data, "KiB", 1024, 0, ["kib", "kibibyte", "kibibytes"]),
        new(UnitFamily.Data, "MiB", 1048576, 0, ["mib", "mebibyte", "mebibytes"]),
        new(UnitFamily.Data, "GiB", 1073741824, 0, ["gib", "gibibyte", "gibibytes"]),
        new(UnitFamily.Data, "TiB", 1099511627776, 0, ["tib", "tebibyte", "tebibytes"]),
        new(UnitFamily.Data, "bit", 0.125, 0, ["bit", "bits"]),
        new(UnitFamily.Duration, "ms", 0.001, 0, ["ms", "millisecond", "milliseconds"]),
        new(UnitFamily.Duration, "s", 1, 0, ["s", "sec", "secs", "second", "seconds", "segundo", "segundos"]),
        new(UnitFamily.Duration, "min", 60, 0, ["min", "mins", "minute", "minutes", "minuto", "minutos"]),
        new(UnitFamily.Duration, "hr", 3600, 0, ["h", "hr", "hrs", "hour", "hours", "hora", "horas"]),
        new(UnitFamily.Volume, "mL", 0.001, 0, ["ml", "milliliter", "milliliters", "mililitro", "mililitros"]),
        new(UnitFamily.Volume, "L", 1, 0, ["l", "liter", "liters", "litre", "litres", "litro", "litros"]),
        new(UnitFamily.Volume, "gal", 3.78541, 0, ["gal", "gallon", "gallons", "galao", "galoes"]),
        new(UnitFamily.Volume, "qt", 0.946353, 0, ["qt", "quart", "quarts"]),
        new(UnitFamily.Volume, "fl oz", 0.0295735, 0, ["floz", "fluidounce", "fluidounces"]),
        new(UnitFamily.Volume, "c", 0.24, 0, ["cup", "cups", "xicara", "xicaras"]),
    ];

    private static readonly Dictionary<string, UnitDefinition> ByName = Units
        .SelectMany(u => u.Names.Select(n => (Name: n, Unit: u)))
        .GroupBy(x => x.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Unit, StringComparer.Ordinal);

    public static UnitDefinition? Find(string foldedName) => ByName.GetValueOrDefault(foldedName);

    /// <summary>"100 km to mi" → "62.14 mi"; null when the text is not a conversion.</summary>
    public static string? Convert(string input, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var tokens = Tokenize(input);
        if (tokens.Count is < 3 or > 8)
        {
            return null;
        }

        for (var position = tokens.Count - 2; position >= 1; position--)
        {
            if (!ConversionWords.Contains(tokens[position]))
            {
                continue;
            }

            var left = tokens.Take(position).ToList();
            var right = tokens.Skip(position + 1).ToList();
            if (right.Count != 1 || left.Count != 2 || Find(right[0]) is not { } to || Find(left[1]) is not { } from || from.Family != to.Family)
            {
                continue;
            }

            if (ParseNumber(left[0], culture) is not { } value)
            {
                continue;
            }

            var result = to.FromBase(from.ToBase(value));
            if (!double.IsFinite(result))
            {
                continue;
            }

            return Format(result, to, culture);
        }

        return null;
    }

    /// <summary>Folded words; a token starting with a digit, '-', '.' or ',' splits off its numeric run ("100km").</summary>
    public static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        foreach (var word in TextFold.ForCommand(input).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length > 0 && (char.IsAsciiDigit(word[0]) || word[0] is '-' or '.' or ','))
            {
                var end = 0;
                while (end < word.Length && (char.IsAsciiDigit(word[end]) || word[end] is '.' or ',' or '-'))
                {
                    end++;
                }

                tokens.Add(word[..end]);
                if (end < word.Length)
                {
                    tokens.Add(word[end..]);
                }
            }
            else
            {
                tokens.Add(word);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Locale-aware number: the locale grouping mark (when it is . or , and not
    /// the decimal mark) or else the other of . and , is the alternate; with
    /// both present the last one is the decimal point; alone, the alternate is
    /// grouping only when the groups look like thousands.
    /// </summary>
    public static double? ParseNumber(string text, CultureInfo culture)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var dec = culture.NumberFormat.NumberDecimalSeparator is { Length: 1 } d && d[0] is '.' or ',' ? d[0] : '.';
        var grouping = culture.NumberFormat.NumberGroupSeparator;
        var alternate = grouping is { Length: 1 } g && g[0] is '.' or ',' && g[0] != dec ? g[0] : (dec == '.' ? ',' : '.');
        var hasDec = text.Contains(dec);
        var hasAlt = text.Contains(alternate);
        string normalized;
        if (hasDec && hasAlt)
        {
            var lastDec = text.LastIndexOf(dec);
            var lastAlt = text.LastIndexOf(alternate);
            var point = lastDec > lastAlt ? dec : alternate;
            var group = point == dec ? alternate : dec;
            normalized = text.Replace(group.ToString(), string.Empty).Replace(point, '.');
        }
        else if (hasAlt)
        {
            normalized = LooksLikeThousands(text, alternate) ? text.Replace(alternate.ToString(), string.Empty) : text.Replace(alternate, '.');
        }
        else
        {
            normalized = text.Replace(dec, '.');
        }

        if (normalized.Count(c => c == '.') > 1 || normalized.Any(c => !(char.IsAsciiDigit(c) || c is '.' or '-')))
        {
            return null;
        }

        if (normalized.LastIndexOf('-') > 0)
        {
            return null;
        }

        return double.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
    }

    private static bool LooksLikeThousands(string text, char separator)
    {
        var body = text.StartsWith('-') ? text[1..] : text;
        var groups = body.Split(separator);
        if (groups.Length < 2 || groups[0].Length is < 1 or > 3 || groups[0].StartsWith('0') || !groups[0].All(char.IsAsciiDigit))
        {
            return false;
        }

        return groups.Skip(1).All(group => group.Length == 3 && group.All(char.IsAsciiDigit));
    }

    /// <summary>Fraction digits by magnitude: 0 → 0, below 1 → 4, below 100 → 2, else 1.</summary>
    public static int FractionDigits(double value)
    {
        var magnitude = Math.Abs(value);
        return magnitude == 0 ? 0 : magnitude < 1 ? 4 : magnitude < 100 ? 2 : 1;
    }

    public static string FormatNumber(double value, int maxFractionDigits, CultureInfo culture)
    {
        var rounded = Math.Round(value, maxFractionDigits, MidpointRounding.AwayFromZero);
        if (rounded == 0)
        {
            rounded = 0; // no "-0"
        }

        var pattern = maxFractionDigits == 0 ? "#,##0" : "#,##0." + new string('#', maxFractionDigits);
        return rounded.ToString(pattern, culture);
    }

    public static string Format(double value, UnitDefinition unit, CultureInfo culture)
    {
        if (unit.Symbol == "ft" && value >= 1)
        {
            var feet = Math.Floor(value);
            var inches = (value - feet) * 12;
            var digits = FractionDigits(inches);
            inches = Math.Round(inches, digits, MidpointRounding.AwayFromZero);
            if (inches >= 12)
            {
                feet += 1;
                inches -= 12;
            }

            var builder = new StringBuilder();
            builder.Append(FormatNumber(feet, 0, culture)).Append(" ft");
            if (inches > 0)
            {
                builder.Append(' ').Append(FormatNumber(inches, digits, culture)).Append(" in");
            }

            return builder.ToString();
        }

        var number = FormatNumber(value, FractionDigits(value), culture);
        return unit.Family == UnitFamily.Temperature && unit.Symbol != "K" ? number + unit.Symbol : number + " " + unit.Symbol;
    }
}
