// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Capture;

/// <summary>Formats the colour picker can copy (<c>colorPickerFormat</c>).</summary>
public enum ColorFormat
{
    Hex,
    Rgb,
    Hsl,

    /// <summary>
    /// <c>Color.FromArgb(255, 30, 144, 255)</c>: compiles as-is with WPF,
    /// WinUI/UWP and WinForms colours. Replaces the macOS SwiftUI format.
    /// </summary>
    CSharp,
}

/// <summary>Colour formatting for the picker (spec 01 §6.19). No alpha.</summary>
public static class ColorFormatter
{
    public static string SanitizeFormatKey(string value) => value switch
    {
        "hex" or "rgb" or "hsl" or "csharp" => value,
        "swiftui" => "csharp",
        _ => "hex",
    };

    public static ColorFormat Parse(string value) => SanitizeFormatKey(value) switch
    {
        "rgb" => ColorFormat.Rgb,
        "hsl" => ColorFormat.Hsl,
        "csharp" => ColorFormat.CSharp,
        _ => ColorFormat.Hex,
    };

    public static string ToKey(ColorFormat format) => format switch
    {
        ColorFormat.Rgb => "rgb",
        ColorFormat.Hsl => "hsl",
        ColorFormat.CSharp => "csharp",
        _ => "hex",
    };

    /// <summary>Formats a colour given as 0xAARRGGBB (alpha ignored).</summary>
    public static string Format(uint argb, ColorFormat format, bool bareHex = false)
    {
        var r = (byte)(argb >> 16);
        var g = (byte)(argb >> 8);
        var b = (byte)argb;
        return Format(r / 255.0, g / 255.0, b / 255.0, format, bareHex);
    }

    /// <summary>Formats sRGB components in 0…1 (clamped).</summary>
    public static string Format(double red, double green, double blue, ColorFormat format, bool bareHex = false)
    {
        var r = Clamp01(red);
        var g = Clamp01(green);
        var b = Clamp01(blue);
        var inv = CultureInfo.InvariantCulture;
        switch (format)
        {
            case ColorFormat.Rgb:
                return string.Create(inv, $"rgb({To255(r)}, {To255(g)}, {To255(b)})");
            case ColorFormat.Hsl:
                var (h, s, l) = ToHsl(r, g, b);
                return string.Create(inv, $"hsl({RoundAway(h)}, {RoundAway(s * 100)}%, {RoundAway(l * 100)}%)");
            case ColorFormat.CSharp:
                return string.Create(inv, $"Color.FromArgb(255, {To255(r)}, {To255(g)}, {To255(b)})");
            default:
                var hex = string.Create(inv, $"{To255(r):X2}{To255(g):X2}{To255(b):X2}");
                return bareHex ? hex : "#" + hex;
        }
    }

    /// <summary>HSL with H in degrees [0, 360), S and L in 0…1.</summary>
    public static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var l = (max + min) / 2;
        if (delta <= 0.000001)
        {
            return (0, 0, l);
        }

        var s = Math.Clamp(delta / (1 - Math.Abs((2 * l) - 1)), 0, 1);
        double h;
        if (max == r)
        {
            h = 60 * Mod((g - b) / delta, 6);
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

        return (h, s, l);
    }

    private static double Mod(double value, double modulus)
    {
        var m = value % modulus;
        return m < 0 ? m + modulus : m;
    }

    private static int To255(double c) => RoundAway(c * 255);

    private static int RoundAway(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;
}
