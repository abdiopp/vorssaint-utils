// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.ScreenshotEditor;

/// <summary>The eight annotation colours (spec 01 §3.10.5), shared by the canvas and export.</summary>
public enum AnnotationColor
{
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Purple,
    Black,
    White,
}

/// <summary>An sRGB colour with components 0…1.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    /// <summary>0xAARRGGBB with full alpha.</summary>
    public uint ToArgb() =>
        0xFF000000u
        | ((uint)Math.Round(Math.Clamp(R, 0, 1) * 255) << 16)
        | ((uint)Math.Round(Math.Clamp(G, 0, 1) * 255) << 8)
        | (uint)Math.Round(Math.Clamp(B, 0, 1) * 255);
}

public static class AnnotationColors
{
    public static IReadOnlyList<AnnotationColor> All { get; } = Enum.GetValues<AnnotationColor>();

    public static Rgb RgbOf(AnnotationColor color) => color switch
    {
        AnnotationColor.Red => new(0.93, 0.26, 0.21),
        AnnotationColor.Orange => new(1.00, 0.58, 0.00),
        AnnotationColor.Yellow => new(1.00, 0.80, 0.00),
        AnnotationColor.Green => new(0.20, 0.78, 0.35),
        AnnotationColor.Blue => new(0.04, 0.52, 1.00),
        AnnotationColor.Purple => new(0.69, 0.32, 0.87),
        AnnotationColor.Black => new(0.09, 0.09, 0.11),
        _ => new(1.00, 1.00, 1.00),
    };

    public static string Id(AnnotationColor color) => color.ToString().ToLowerInvariant();

    public static AnnotationColor Parse(string? id, AnnotationColor fallback = AnnotationColor.Red)
    {
        foreach (var color in All)
        {
            if (Id(color) == id)
            {
                return color;
            }
        }

        return fallback;
    }

    /// <summary>Accessible colour names ("Red" … "White").</summary>
    public static string TitleKey(AnnotationColor color) => $"screenshot.watermarkColor{color}";
}

/// <summary>The three stroke thicknesses (points at 1x; multiplied by the capture scale in pixels).</summary>
public enum StrokeWidth
{
    Small,
    Medium,
    Large,
}

public static class StrokeWidths
{
    public static IReadOnlyList<StrokeWidth> All { get; } = Enum.GetValues<StrokeWidth>();

    public static double Points(StrokeWidth width) => width switch
    {
        StrokeWidth.Small => 2,
        StrokeWidth.Large => 7,
        _ => 4,
    };

    public static string Id(StrokeWidth width) => width.ToString().ToLowerInvariant();

    public static StrokeWidth Parse(string? id) => id switch
    {
        "small" => StrokeWidth.Small,
        "large" => StrokeWidth.Large,
        _ => StrokeWidth.Medium,
    };

    public static string TitleKey(StrokeWidth width) => $"win.screenshotEditor.stroke{width}";
}

/// <summary>The five arrow styles (spec 01 §3.10.4.2, §6.6).</summary>
public enum ArrowStyle
{
    Filled,
    Outline,
    Open,
    DoubleEnded,
    Scribbly,
}

public static class ArrowStyles
{
    public static IReadOnlyList<ArrowStyle> All { get; } = Enum.GetValues<ArrowStyle>();

    public static string Id(ArrowStyle style) => style switch
    {
        ArrowStyle.Outline => "outline",
        ArrowStyle.Open => "open",
        ArrowStyle.DoubleEnded => "doubleEnded",
        ArrowStyle.Scribbly => "scribbly",
        _ => "filled",
    };

    /// <summary>Unknown styles fall back to solid.</summary>
    public static ArrowStyle Parse(string? id) => All.FirstOrDefault(s => Id(s) == id, ArrowStyle.Filled);

    public static string TitleKey(ArrowStyle style) => style switch
    {
        ArrowStyle.Outline => "screenshot.arrowStyleOutline",
        ArrowStyle.Open => "screenshot.arrowStyleOpen",
        ArrowStyle.DoubleEnded => "screenshot.arrowStyleDoubleEnded",
        ArrowStyle.Scribbly => "screenshot.arrowStyleScribbly",
        _ => "screenshot.arrowStyleFilled",
    };
}

/// <summary>What the blur tool does to its area (spec 01 §3.10.4.1).</summary>
public enum BlurStyle
{
    Pixelate,
    Blur,
    Erase,
}

public static class BlurStyles
{
    public static IReadOnlyList<BlurStyle> All { get; } = Enum.GetValues<BlurStyle>();

    public static string Id(BlurStyle style) => style.ToString().ToLowerInvariant();

    public static BlurStyle Parse(string? id) => id switch
    {
        "blur" => BlurStyle.Blur,
        "erase" => BlurStyle.Erase,
        _ => BlurStyle.Pixelate,
    };

    public static string TitleKey(BlurStyle style) => style switch
    {
        BlurStyle.Blur => "screenshot.toolBlur",
        BlurStyle.Erase => "screenshot.blurStyleErase",
        _ => "screenshot.toolPixelate",
    };

    public const int MinLevel = 1;
    public const int MaxLevel = 5;
    public const int DefaultLevel = 3;

    /// <summary>A capture never starts lighter than level 3, so text cannot stay readable by accident.</summary>
    public static int OpeningLevel(int remembered) => Math.Max(Math.Clamp(remembered, MinLevel, MaxLevel), DefaultLevel);
}

/// <summary>The twelve stickers.</summary>
public enum StickerKind
{
    Check,
    Cross,
    Star,
    Heart,
    ThumbsUp,
    ThumbsDown,
    Smile,
    Laugh,
    Party,
    Fire,
    Warning,
    Eyes,
}

public static class Stickers
{
    public static IReadOnlyList<StickerKind> All { get; } = Enum.GetValues<StickerKind>();

    public static string Glyph(StickerKind sticker) => sticker switch
    {
        StickerKind.Check => "✅",
        StickerKind.Cross => "❌",
        StickerKind.Star => "⭐️",
        StickerKind.Heart => "❤️",
        StickerKind.ThumbsUp => "\U0001F44D",
        StickerKind.ThumbsDown => "\U0001F44E",
        StickerKind.Smile => "\U0001F600",
        StickerKind.Laugh => "\U0001F602",
        StickerKind.Party => "\U0001F389",
        StickerKind.Fire => "\U0001F525",
        StickerKind.Warning => "⚠️",
        _ => "\U0001F440",
    };

    public static string Id(StickerKind sticker) => sticker switch
    {
        StickerKind.ThumbsUp => "thumbsUp",
        StickerKind.ThumbsDown => "thumbsDown",
        _ => sticker.ToString().ToLowerInvariant(),
    };

    public static StickerKind Parse(string? id) => All.FirstOrDefault(s => Id(s) == id, StickerKind.Check);

    public static string TitleKey(StickerKind sticker) => $"win.screenshotEditor.sticker{sticker}";
}

/// <summary>Text size presets in points at 1x (spec 01 §3.10.5).</summary>
public static class TextSizes
{
    public static IReadOnlyList<int> Presets { get; } = [10, 12, 14, 16, 19, 24, 28, 36, 48, 64, 72, 96];

    public const int Default = 19;

    /// <summary>A stored 0 means the default; anything else is clamped to the preset range.</summary>
    public static int Sanitize(int size) => size == 0 ? Default : Math.Clamp(size, Presets[0], Presets[^1]);

    public static int? Smaller(int size)
    {
        var s = Sanitize(size);
        for (var i = Presets.Count - 1; i >= 0; i--)
        {
            if (Presets[i] < s)
            {
                return Presets[i];
            }
        }

        return null;
    }

    public static int? Larger(int size)
    {
        var s = Sanitize(size);
        foreach (var preset in Presets)
        {
            if (preset > s)
            {
                return preset;
            }
        }

        return null;
    }
}
