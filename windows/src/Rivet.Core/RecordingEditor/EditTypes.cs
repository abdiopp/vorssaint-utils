// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>Export quality (§6.3). JSON: small, balanced, high.</summary>
public enum ExportQuality
{
    Small,
    Balanced,
    High,
}

/// <summary>GIF long edge (§6.18). JSON: small (420), medium (600), large (800).</summary>
public enum GifSize
{
    Small,
    Medium,
    Large,
}

/// <summary>Canvas shape. JSON: original, wide (16:9), square (1:1), vertical (9:16).</summary>
public enum CanvasAspect
{
    Original,
    Wide,
    Square,
    Vertical,
}

/// <summary>Pointer smoothing. JSON: off, light, smooth, cinematic.</summary>
public enum PointerSmoothing
{
    Off,
    Light,
    Smooth,
    Cinematic,
}

/// <summary>Where a caption or image sits on the canvas.</summary>
public enum OverlayAnchor
{
    TopLeading,
    Top,
    TopTrailing,
    Leading,
    Center,
    Trailing,
    BottomLeading,
    Bottom,
    BottomTrailing,
}

/// <summary>Caption colours.</summary>
public enum CaptionPalette
{
    White,
    Black,
    Accent,
    Yellow,
    Red,
    Green,
}

/// <summary>A cut-out stretch in source seconds.</summary>
public readonly record struct CutRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>A zoom block (§3.22). No focus means it follows the pointer.</summary>
public sealed record ZoomSegment
{
    public const double MinimumLength = 0.4;
    public const double DefaultAmount = 1.8;
    public const double MinAmount = 1.2;
    public const double MaxAmount = 3.0;

    public required string Id { get; init; }

    public double Start { get; init; }

    public double End { get; init; }

    public double Amount { get; init; } = DefaultAmount;

    public double? FocusX { get; init; }

    public double? FocusY { get; init; }

    public bool IsAimed => FocusX.HasValue && FocusY.HasValue;

    public double Length => End - Start;

    public static double SanitizeAmount(double amount) => RecorderMath.ClampOr(amount, MinAmount, MaxAmount, DefaultAmount);
}

/// <summary>A caption (§3.23).</summary>
public sealed record TextOverlay
{
    public const int MaxLength = 200;
    public const double DefaultSize = 0.06;
    public const double MinSize = 0.03;
    public const double MaxSize = 0.16;

    public required string Id { get; init; }

    public string Text { get; init; } = string.Empty;

    public double Start { get; init; }

    public double End { get; init; }

    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.Bottom;

    /// <summary>Fraction of the canvas height.</summary>
    public double Size { get; init; } = DefaultSize;

    public CaptionPalette Palette { get; init; } = CaptionPalette.White;
}

/// <summary>A picture overlay (§3.24); <see cref="Path"/> is the take's private copy.</summary>
public sealed record ImageOverlay
{
    public const double DefaultSize = 0.18;
    public const double MinSize = 0.04;
    public const double MaxSize = 0.6;
    public const double MinOpacity = 0.05;

    public required string Id { get; init; }

    public required string Path { get; init; }

    public double Start { get; init; }

    public double End { get; init; }

    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.BottomTrailing;

    /// <summary>Fraction of the canvas width.</summary>
    public double Size { get; init; } = DefaultSize;

    public double Opacity { get; init; } = 1;
}

/// <summary>A privacy blur (§3.25); the rectangle is normalized to the recorded picture.</summary>
public sealed record BlurRegion
{
    public const int DefaultStrength = 3;

    public required string Id { get; init; }

    public double Start { get; init; }

    public double End { get; init; }

    public double X { get; init; } = 0.35;

    public double Y { get; init; } = 0.40;

    public double Width { get; init; } = 0.30;

    public double Height { get; init; } = 0.20;

    public int Strength { get; init; } = DefaultStrength;
}

/// <summary>JSON spellings of the document's enums (macOS raw values).</summary>
public static class EditNames
{
    public static string Of(ExportQuality v) => v switch
    {
        ExportQuality.Small => "small",
        ExportQuality.High => "high",
        _ => "balanced",
    };

    public static string Of(GifSize v) => v switch
    {
        GifSize.Small => "small",
        GifSize.Large => "large",
        _ => "medium",
    };

    public static string Of(CanvasAspect v) => v switch
    {
        CanvasAspect.Wide => "wide",
        CanvasAspect.Square => "square",
        CanvasAspect.Vertical => "vertical",
        _ => "original",
    };

    public static string Of(PointerSmoothing v) => v switch
    {
        PointerSmoothing.Off => "off",
        PointerSmoothing.Light => "light",
        PointerSmoothing.Cinematic => "cinematic",
        _ => "smooth",
    };

    public static string Of(OverlayAnchor v) => v switch
    {
        OverlayAnchor.TopLeading => "topLeading",
        OverlayAnchor.Top => "top",
        OverlayAnchor.TopTrailing => "topTrailing",
        OverlayAnchor.Leading => "leading",
        OverlayAnchor.Center => "center",
        OverlayAnchor.Trailing => "trailing",
        OverlayAnchor.BottomLeading => "bottomLeading",
        OverlayAnchor.Bottom => "bottom",
        _ => "bottomTrailing",
    };

    public static string Of(CaptionPalette v) => v switch
    {
        CaptionPalette.Black => "black",
        CaptionPalette.Accent => "accent",
        CaptionPalette.Yellow => "yellow",
        CaptionPalette.Red => "red",
        CaptionPalette.Green => "green",
        _ => "white",
    };

    public static ExportQuality? Quality(string? s) => s switch
    {
        "small" => ExportQuality.Small,
        "balanced" => ExportQuality.Balanced,
        "high" => ExportQuality.High,
        _ => null,
    };

    public static GifSize? Gif(string? s) => s switch
    {
        "small" => GifSize.Small,
        "medium" => GifSize.Medium,
        "large" => GifSize.Large,
        _ => null,
    };

    public static CanvasAspect? Aspect(string? s) => s switch
    {
        "original" => CanvasAspect.Original,
        "wide" => CanvasAspect.Wide,
        "square" => CanvasAspect.Square,
        "vertical" => CanvasAspect.Vertical,
        _ => null,
    };

    public static PointerSmoothing? Smoothing(string? s) => s switch
    {
        "off" => PointerSmoothing.Off,
        "light" => PointerSmoothing.Light,
        "smooth" => PointerSmoothing.Smooth,
        "cinematic" => PointerSmoothing.Cinematic,
        _ => null,
    };

    public static OverlayAnchor? Anchor(string? s) => s switch
    {
        "topLeading" => OverlayAnchor.TopLeading,
        "top" => OverlayAnchor.Top,
        "topTrailing" => OverlayAnchor.TopTrailing,
        "leading" => OverlayAnchor.Leading,
        "center" => OverlayAnchor.Center,
        "trailing" => OverlayAnchor.Trailing,
        "bottomLeading" => OverlayAnchor.BottomLeading,
        "bottom" => OverlayAnchor.Bottom,
        "bottomTrailing" => OverlayAnchor.BottomTrailing,
        _ => null,
    };

    public static CaptionPalette? Palette(string? s) => s switch
    {
        "white" => CaptionPalette.White,
        "black" => CaptionPalette.Black,
        "accent" => CaptionPalette.Accent,
        "yellow" => CaptionPalette.Yellow,
        "red" => CaptionPalette.Red,
        "green" => CaptionPalette.Green,
        _ => null,
    };

    /// <summary>The GIF long edge in pixels.</summary>
    public static int LongEdge(GifSize size) => size switch
    {
        GifSize.Small => 420,
        GifSize.Large => 800,
        _ => 600,
    };

    /// <summary>sRGB colour of a caption palette entry (§3.23).</summary>
    public static RgbValue Color(CaptionPalette palette) => palette switch
    {
        CaptionPalette.Black => new RgbValue(0.06, 0.06, 0.07),
        CaptionPalette.Accent => new RgbValue(0.04, 0.52, 1.00),
        CaptionPalette.Yellow => new RgbValue(1.00, 0.80, 0.00),
        CaptionPalette.Red => new RgbValue(0.96, 0.26, 0.21),
        CaptionPalette.Green => new RgbValue(0.20, 0.78, 0.35),
        _ => new RgbValue(1, 1, 1),
    };

    /// <summary>Anchor → (ux, uy) ∈ {0, 0.5, 1}² (§6.15).</summary>
    public static (double X, double Y) Unit(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.TopLeading => (0, 0),
        OverlayAnchor.Top => (0.5, 0),
        OverlayAnchor.TopTrailing => (1, 0),
        OverlayAnchor.Leading => (0, 0.5),
        OverlayAnchor.Center => (0.5, 0.5),
        OverlayAnchor.Trailing => (1, 0.5),
        OverlayAnchor.BottomLeading => (0, 1),
        OverlayAnchor.Bottom => (0.5, 1),
        _ => (1, 1),
    };
}
