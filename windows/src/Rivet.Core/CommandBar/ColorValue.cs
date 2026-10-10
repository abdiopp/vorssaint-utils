// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Launcher;

public enum ColorFormat
{
    Hex,
    Rgb,
    Rgba,
    Hsl,
    Hsla,
    SwiftUI,
}

/// <summary>
/// A colour typed or copied as text: <c>#RGB[A]</c>, <c>#RRGGBB[AA]</c>,
/// <c>rgb()/rgba()</c>, <c>hsl()/hsla()</c> or SwiftUI <c>Color(red:green:blue:[opacity:])</c>
/// (spec 06 §6.8). Components are 0–1.
/// </summary>
public readonly partial record struct ColorValue(double Red, double Green, double Blue, double Alpha)
{
    private static readonly CultureInfo Posix = CultureInfo.InvariantCulture;

    /// <summary>Parses a whole trimmed text as one colour (case-insensitive, at most 96 bytes).</summary>
    public static bool TryParse(string? text, out ColorValue color)
    {
        color = default;
        if (text is null)
        {
            return false;
        }

        var value = text.Trim();
        if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > 96)
        {
            return false;
        }

        var lower = value.ToLowerInvariant();
        if (lower.StartsWith('#'))
        {
            return TryParseHex(lower[1..], out color);
        }

        if (TryFunction(lower, "rgba", out var args) || TryFunction(lower, "rgb", out args))
        {
            return TryParseRgb(args, out color);
        }

        if (TryFunction(lower, "hsla", out args) || TryFunction(lower, "hsl", out args))
        {
            return TryParseHsl(args, out color);
        }

        return TryParseSwiftUI(lower, out color);
    }

    public static bool IsColor(string? text) => TryParse(text, out _);

    /// <summary>Packed 0xAARRGGBB, for swatches.</summary>
    public uint ToArgb()
    {
        static uint Byte(double c) => (uint)Math.Round(Math.Clamp(c, 0, 1) * 255, MidpointRounding.AwayFromZero);
        return (Byte(Alpha) << 24) | (Byte(Red) << 16) | (Byte(Green) << 8) | Byte(Blue);
    }

    /// <summary>Formats the colour; alpha is written for the "a" forms or when it is below 1.</summary>
    public string Format(ColorFormat format)
    {
        var r = Math.Clamp(Red, 0, 1);
        var g = Math.Clamp(Green, 0, 1);
        var b = Math.Clamp(Blue, 0, 1);
        var a = Math.Clamp(Alpha, 0, 1);
        var withAlpha = a < 1;
        static int Channel(double c) => (int)Math.Round(c * 255, MidpointRounding.AwayFromZero);
        switch (format)
        {
            case ColorFormat.Hex:
                var hex = $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";
                return withAlpha ? hex + $"{Channel(a):X2}" : hex;
            case ColorFormat.Rgb or ColorFormat.Rgba:
                return format == ColorFormat.Rgba || withAlpha
                    ? $"rgba({Channel(r)}, {Channel(g)}, {Channel(b)}, {AlphaText(a)})"
                    : $"rgb({Channel(r)}, {Channel(g)}, {Channel(b)})";
            case ColorFormat.Hsl or ColorFormat.Hsla:
                var (h, s, l) = ToHsl(r, g, b);
                var hi = (int)Math.Round(h, MidpointRounding.AwayFromZero) % 360;
                var si = (int)Math.Round(s * 100, MidpointRounding.AwayFromZero);
                var li = (int)Math.Round(l * 100, MidpointRounding.AwayFromZero);
                return format == ColorFormat.Hsla || withAlpha
                    ? $"hsla({hi}, {si}%, {li}%, {AlphaText(a)})"
                    : $"hsl({hi}, {si}%, {li}%)";
            default:
                var swift = string.Create(Posix, $"Color(red: {r:0.000}, green: {g:0.000}, blue: {b:0.000}");
                return withAlpha ? swift + string.Create(Posix, $", opacity: {a:0.000})") : swift + ")";
        }
    }

    /// <summary>The <c>%g</c> spelling of alpha rounded to 3 decimals (POSIX).</summary>
    private static string AlphaText(double alpha) =>
        Math.Round(alpha, 3, MidpointRounding.AwayFromZero).ToString("0.###", Posix);

    /// <summary>Standard RGB → HSL; hue in degrees, saturation and lightness 0–1.</summary>
    public static (double Hue, double Saturation, double Lightness) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var delta = max - min;
        if (delta < 1e-6)
        {
            return (0, 0, l);
        }

        var s = delta / (1 - Math.Abs((2 * l) - 1));
        double h;
        if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            h = 60 * (((r - g) / delta) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }

        return (h, Math.Clamp(s, 0, 1), l);
    }

    /// <summary>HSL → RGB with the CSS formula (hue in degrees, wrapped; s and l 0–1).</summary>
    public static (double R, double G, double B) FromHsl(double hue, double s, double l)
    {
        var h = (((hue % 360) + 360) % 360) / 360;
        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        double Channel(double n)
        {
            var k = (n + (12 * h)) % 12;
            return l - (c / 2 * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
        }

        return (Channel(0), Channel(8), Channel(4));
    }

    private static bool TryParseHex(string digits, out ColorValue color)
    {
        color = default;
        if (digits.Length is not (3 or 4 or 6 or 8) || !digits.All(Uri.IsHexDigit))
        {
            return false;
        }

        static double Expand(char c) => Convert.ToInt32(new string(c, 2), 16) / 255.0;
        static double Pair(string s, int index) => Convert.ToInt32(s.Substring(index, 2), 16) / 255.0;
        color = digits.Length switch
        {
            3 => new ColorValue(Expand(digits[0]), Expand(digits[1]), Expand(digits[2]), 1),
            4 => new ColorValue(Expand(digits[0]), Expand(digits[1]), Expand(digits[2]), Expand(digits[3])),
            6 => new ColorValue(Pair(digits, 0), Pair(digits, 2), Pair(digits, 4), 1),
            _ => new ColorValue(Pair(digits, 0), Pair(digits, 2), Pair(digits, 4), Pair(digits, 6)),
        };
        return true;
    }

    private static bool TryFunction(string text, string name, out string[] args)
    {
        args = [];
        if (!text.StartsWith(name, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = text[name.Length..].TrimStart();
        if (!rest.StartsWith('(') || !rest.EndsWith(')'))
        {
            return false;
        }

        var inner = rest[1..^1].Trim();
        if (inner.Length == 0 || !SeparatorShape().IsMatch(inner))
        {
            return false;
        }

        args = ArgumentSplit().Split(inner);
        return args.Length is 3 or 4 && args.All(a => a.Length > 0);
    }

    private static bool TryParseRgb(string[] args, out ColorValue color)
    {
        color = default;
        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!TryChannel(args[i], out channels[i]))
            {
                return false;
            }
        }

        var alpha = 1.0;
        if (args.Length == 4 && !TryAlpha(args[3], out alpha))
        {
            return false;
        }

        color = new ColorValue(channels[0], channels[1], channels[2], alpha);
        return true;
    }

    private static bool TryParseHsl(string[] args, out ColorValue color)
    {
        color = default;
        var hueText = args[0].EndsWith("deg", StringComparison.Ordinal) ? args[0][..^3] : args[0];
        if (!TryNumber(hueText, out var hue) || !TryPercent(args[1], out var s) || !TryPercent(args[2], out var l))
        {
            return false;
        }

        var alpha = 1.0;
        if (args.Length == 4 && !TryAlpha(args[3], out alpha))
        {
            return false;
        }

        var (r, g, b) = FromHsl(hue, s, l);
        color = new ColorValue(r, g, b, alpha);
        return true;
    }

    private static bool TryParseSwiftUI(string text, out ColorValue color)
    {
        color = default;
        var match = SwiftUIColor().Match(text);
        if (!match.Success)
        {
            return false;
        }

        if (!TryUnit(match.Groups["r"].Value, out var r) || !TryUnit(match.Groups["g"].Value, out var g) || !TryUnit(match.Groups["b"].Value, out var b))
        {
            return false;
        }

        var a = 1.0;
        if (match.Groups["a"].Success && !TryUnit(match.Groups["a"].Value, out a))
        {
            return false;
        }

        color = new ColorValue(r, g, b, a);
        return true;
    }

    private static bool TryChannel(string text, out double value)
    {
        value = 0;
        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent) || percent is < 0 or > 100)
            {
                return false;
            }

            value = percent / 100;
            return true;
        }

        if (!TryNumber(text, out var raw) || raw is < 0 or > 255)
        {
            return false;
        }

        value = raw / 255;
        return true;
    }

    private static bool TryAlpha(string text, out double value)
    {
        value = 1;
        if (text.EndsWith('%'))
        {
            if (!TryNumber(text[..^1], out var percent) || percent is < 0 or > 100)
            {
                return false;
            }

            value = percent / 100;
            return true;
        }

        return TryUnit(text, out value);
    }

    private static bool TryPercent(string text, out double value)
    {
        value = 0;
        var body = text.EndsWith('%') ? text[..^1] : text;
        if (!TryNumber(body, out var percent) || percent is < 0 or > 100)
        {
            return false;
        }

        value = percent / 100;
        return true;
    }

    private static bool TryUnit(string text, out double value) =>
        TryNumber(text, out value) && value is >= 0 and <= 1;

    private static bool TryNumber(string text, out double value)
    {
        value = 0;
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiDigit(c) || c is '.' or '-' or '+')))
        {
            return false;
        }

        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Posix, out value) && double.IsFinite(value);
    }

    /// <summary>Arguments separated by commas, slashes or spaces, with no leading, trailing or doubled separator.</summary>
    [GeneratedRegex(@"^[^\s,/]+(?:(?:\s*[,/]\s*|\s+)[^\s,/]+)*$")]
    private static partial Regex SeparatorShape();

    [GeneratedRegex(@"\s*[,/]\s*|\s+")]
    private static partial Regex ArgumentSplit();

    [GeneratedRegex(@"^color\s*\(\s*red\s*:\s*(?<r>[^,\s]+)\s*,\s*green\s*:\s*(?<g>[^,\s]+)\s*,\s*blue\s*:\s*(?<b>[^,\s)]+)\s*(?:,\s*opacity\s*:\s*(?<a>[^,\s)]+)\s*)?\)$")]
    private static partial Regex SwiftUIColor();
}

/// <summary>Command Bar colour conversion queries: <c>&lt;color&gt; &lt;conversion word&gt; &lt;target&gt;</c>.</summary>
public static class CommandBarColors
{
    public static readonly IReadOnlyDictionary<string, ColorFormat> Targets = new Dictionary<string, ColorFormat>(StringComparer.OrdinalIgnoreCase)
    {
        ["hex"] = ColorFormat.Hex,
        ["rgb"] = ColorFormat.Rgb,
        ["rgba"] = ColorFormat.Rgba,
        ["hsl"] = ColorFormat.Hsl,
        ["hsla"] = ColorFormat.Hsla,
        ["swift"] = ColorFormat.SwiftUI,
        ["swiftui"] = ColorFormat.SwiftUI,
    };

    /// <summary>"#a2b3b4 to rgb" → "rgb(162, 179, 180)"; null when the text is not a conversion.</summary>
    public static string? Convert(string query)
    {
        var tokens = query.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            return null;
        }

        if (!Targets.TryGetValue(tokens[^1], out var target) || !CommandBarUnits.ConversionWords.Contains(TextFoldCached(tokens[^2])))
        {
            return null;
        }

        var colorText = string.Join(' ', tokens[..^2]);
        return ColorValue.TryParse(colorText, out var color) ? color.Format(target) : null;
    }

    private static string TextFoldCached(string token) => Clipboard.TextFold.ForCommand(token);
}
