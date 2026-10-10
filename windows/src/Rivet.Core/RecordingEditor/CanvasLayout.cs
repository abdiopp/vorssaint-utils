// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>An integer rectangle in output pixels.</summary>
public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Canvas, card, corners and shadow for a frame (spec 02 §6.12). Without a
/// background a shape crops the recording (largest centred crop); with one
/// the canvas grows around the card instead, never cropping.
/// </summary>
public sealed record CanvasLayout
{
    public const int MaxLongEdge = 3840;

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    /// <summary>Canvas at full size (the quality readout multiplies this).</summary>
    public required int CanvasWidth { get; init; }

    public required int CanvasHeight { get; init; }

    /// <summary>Canvas at the output scale (what is actually rendered).</summary>
    public required int OutputWidth { get; init; }

    public required int OutputHeight { get; init; }

    /// <summary>Where the (zoomed) recording sits, in output pixels; may exceed the canvas when cropping.</summary>
    public required IntRect Card { get; init; }

    public required int CornerRadius { get; init; }

    /// <summary>Margin fraction per side (0…0.18).</summary>
    public required double Margin { get; init; }

    public required bool HasBackdrop { get; init; }

    /// <summary>True when the card fills the canvas by cropping (no background, shape ≠ original).</summary>
    public required bool Fill { get; init; }

    /// <summary>A plate is drawn when the canvas differs from the source or there is a background.</summary>
    public bool NeedsPlate => HasBackdrop || CanvasWidth != EvenSource.W || CanvasHeight != EvenSource.H;

    public bool HasShadow => HasBackdrop || CornerRadius > 0;

    /// <summary>Drop shadow offset (px, downward) and blur (CoreGraphics units ≈ 2σ).</summary>
    public double ShadowOffset => Math.Min(OutputHeight * 0.012, 26);

    public double ShadowBlur => Math.Min(OutputHeight * 0.045, 90);

    private (int W, int H) EvenSource => (RecorderMath.EvenSide(SourceWidth), RecorderMath.EvenSide(SourceHeight));

    public static double AspectRatio(CanvasAspect aspect) => aspect switch
    {
        CanvasAspect.Wide => 16.0 / 9.0,
        CanvasAspect.Square => 1,
        CanvasAspect.Vertical => 9.0 / 16.0,
        _ => 0,
    };

    public static CanvasLayout Compute(int sourceWidth, int sourceHeight, RecorderBackdrop backdrop, CanvasAspect aspect, double outputScale)
    {
        var style = backdrop.Sanitized();
        var hasBackdrop = style.HasBackdrop;
        var margin = hasBackdrop ? Math.Clamp(style.Padding * 0.18, 0, 0.35) : 0;
        var inner = 1 - (2 * margin);
        int canvasW, canvasH;
        if (inner <= 0.05)
        {
            canvasW = RecorderMath.EvenSide(sourceWidth);
            canvasH = RecorderMath.EvenSide(sourceHeight);
        }
        else
        {
            var w = sourceWidth / inner;
            var h = sourceHeight / inner;
            var r = AspectRatio(aspect);
            if (r > 0)
            {
                if (!hasBackdrop)
                {
                    if (w / h > r)
                    {
                        w = h * r;
                    }
                    else
                    {
                        h = w / r;
                    }
                }
                else if (w / h < r)
                {
                    w = h * r;
                }
                else
                {
                    h = w / r;
                }
            }

            var longest = Math.Max(w, h);
            if (longest > MaxLongEdge)
            {
                var k = MaxLongEdge / longest;
                w *= k;
                h *= k;
            }

            canvasW = RecorderMath.EvenSide(w);
            canvasH = RecorderMath.EvenSide(h);
        }

        var scale = double.IsFinite(outputScale) && outputScale > 0 ? outputScale : 1;
        var outW = RecorderMath.EvenSide(canvasW * scale);
        var outH = RecorderMath.EvenSide(canvasH * scale);
        var fill = !hasBackdrop && aspect != CanvasAspect.Original;
        var availW = outW * (1 - (2 * margin));
        var availH = outH * (1 - (2 * margin));
        var f = fill
            ? Math.Max(availW / sourceWidth, availH / sourceHeight)
            : Math.Min(availW / sourceWidth, availH / sourceHeight);
        var cardW = Math.Max(1, RecorderMath.RoundToInt(sourceWidth * f));
        var cardH = Math.Max(1, RecorderMath.RoundToInt(sourceHeight * f));
        var cardX = RecorderMath.RoundToInt((outW - cardW) / 2.0);
        var cardY = RecorderMath.RoundToInt((outH - cardH) / 2.0);
        var corner = hasBackdrop
            ? RecorderMath.RoundToInt(Math.Clamp(style.CornerRadius, 0, 1) * Math.Min(cardW, cardH) * 0.09)
            : 0;

        return new CanvasLayout
        {
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            CanvasWidth = canvasW,
            CanvasHeight = canvasH,
            OutputWidth = outW,
            OutputHeight = outH,
            Card = new IntRect(cardX, cardY, cardW, cardH),
            CornerRadius = corner,
            Margin = margin,
            HasBackdrop = hasBackdrop,
            Fill = fill,
        };
    }

    /// <summary>The "Quality" readout: <c>even(canvas × quality scale)</c>.</summary>
    public static (int Width, int Height) ExportSize(int sourceWidth, int sourceHeight, RecorderBackdrop backdrop, CanvasAspect aspect, ExportQuality quality)
    {
        var layout = Compute(sourceWidth, sourceHeight, backdrop, aspect, QualityPreset.Of(quality).Scale);
        return (layout.OutputWidth, layout.OutputHeight);
    }
}

/// <summary>Captions and image overlays: fades and placement (spec 02 §6.15).</summary>
public static class OverlayLayout
{
    public const double Ramp = 0.25;

    /// <summary>Eased in and out over min(0.25 s, half the length); 0 outside [start, end].</summary>
    public static double Opacity(double start, double end, double t)
    {
        if (t < start || t > end)
        {
            return 0;
        }

        var ramp = Math.Min(Ramp, (end - start) / 2);
        if (ramp <= 0)
        {
            return 1;
        }

        if (t < start + ramp)
        {
            return RecorderMath.Smoothstep((t - start) / ramp);
        }

        if (t > end - ramp)
        {
            return RecorderMath.Smoothstep((end - t) / ramp);
        }

        return 1;
    }

    /// <summary>Margin from the canvas edges: 5 % of the short side.</summary>
    public static double MarginOf(double canvasWidth, double canvasHeight) => 0.05 * Math.Min(canvasWidth, canvasHeight);

    /// <summary>Top-left of content of size (cw, ch) at an anchor on a W×H canvas.</summary>
    public static (double X, double Y) Place(OverlayAnchor anchor, double canvasWidth, double canvasHeight, double contentWidth, double contentHeight)
    {
        var m = MarginOf(canvasWidth, canvasHeight);
        var (ux, uy) = EditNames.Unit(anchor);
        return (m + ((canvasWidth - contentWidth - (2 * m)) * ux), m + ((canvasHeight - contentHeight - (2 * m)) * uy));
    }

    /// <summary>Drawn image size: W × size wide, kept inside the margins, at least 1 px.</summary>
    public static (int Width, int Height) ImageSize(int imageWidth, int imageHeight, double size, double canvasWidth, double canvasHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            return (1, 1);
        }

        var w = canvasWidth * size;
        var h = w * imageHeight / imageWidth;
        var m = MarginOf(canvasWidth, canvasHeight);
        var maxW = Math.Max(1, canvasWidth - (2 * m));
        var maxH = Math.Max(1, canvasHeight - (2 * m));
        var k = Math.Min(1, Math.Min(maxW / w, maxH / h));
        return (Math.Max(1, RecorderMath.RoundToInt(w * k)), Math.Max(1, RecorderMath.RoundToInt(h * k)));
    }

    /// <summary>Caption font size in pixels: <c>max(8, round(H × size))</c>.</summary>
    public static int CaptionFontSize(double canvasHeight, double size) =>
        Math.Max(8, RecorderMath.RoundToInt(canvasHeight * size));
}

/// <summary>Privacy blur sizing and timing (spec 02 §6.14).</summary>
public static class BlurMath
{
    public static double StrengthFactor(int strength) => strength switch
    {
        1 => 0.4,
        2 => 0.65,
        4 => 1.5,
        5 => 2.2,
        _ => 1,
    };

    /// <summary>The area in source pixels, rounded outward; null when under 1 px either way.</summary>
    public static IntRect? PixelRect(BlurRegion blur, int width, int height)
    {
        var x0 = (int)Math.Floor(blur.X * width);
        var y0 = (int)Math.Floor(blur.Y * height);
        var x1 = (int)Math.Ceiling((blur.X + blur.Width) * width);
        var y1 = (int)Math.Ceiling((blur.Y + blur.Height) * height);
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);
        return x1 - x0 < 1 || y1 - y0 < 1 ? null : new IntRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Mosaic cell size from the area (not the frame) and the strength.</summary>
    public static int BlockSize(int rectWidth, int rectHeight, int strength)
    {
        var side = Math.Min(rectWidth, rectHeight);
        var baseSize = side > 0 ? Math.Clamp(RecorderMath.RoundToInt(side / 3.0), 8, 48) : 8;
        return Math.Max(2, RecorderMath.RoundToInt(baseSize * StrengthFactor(strength)));
    }

    /// <summary>Hard edges in time: hidden on every moment in [start, end].</summary>
    public static bool Covers(BlurRegion blur, double sourceTime) => sourceTime >= blur.Start && sourceTime <= blur.End;
}

/// <summary>Stage point → picture mapping for aiming and blur drawing (spec 02 §6.22).</summary>
public static class StageMapping
{
    /// <summary>Normalized picture coordinates of a point in an aspect-fit view (not clamped).</summary>
    public static (double U, double V) ToPicture(double x, double y, double viewWidth, double viewHeight, double sourceWidth, double sourceHeight)
    {
        var fit = Math.Min(viewWidth / sourceWidth, viewHeight / sourceHeight);
        var shownW = sourceWidth * fit;
        var shownH = sourceHeight * fit;
        var ox = (viewWidth - shownW) / 2;
        var oy = (viewHeight - shownH) / 2;
        return ((x - ox) / shownW, (y - oy) / shownH);
    }

    /// <summary>The picture's rectangle inside the view (aspect-fit, centred).</summary>
    public static (double X, double Y, double Width, double Height) FitRect(double viewWidth, double viewHeight, double contentWidth, double contentHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        var fit = Math.Min(viewWidth / contentWidth, viewHeight / contentHeight);
        var w = contentWidth * fit;
        var h = contentHeight * fit;
        return ((viewWidth - w) / 2, (viewHeight - h) / 2, w, h);
    }
}
