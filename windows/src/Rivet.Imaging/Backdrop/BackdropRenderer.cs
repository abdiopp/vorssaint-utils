// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.Backdrop;

/// <summary>Where the capture sits on its backdrop, in output pixels.</summary>
public readonly record struct BackdropGeometry(int Padding, int CornerRadius, int CanvasWidth, int CanvasHeight)
{
    public SKRect CardRect(int imageWidth, int imageHeight) =>
        SKRect.Create(Padding, Padding, imageWidth, imageHeight);
}

/// <summary>
/// Renders a flattened capture onto its backdrop (spec 01 §6.13):
/// <code>
/// short = min(W, H)
/// P = max(24, round(short · (0.035 + 0.14 · padding)))
/// R = round(cornerRadius · short · 0.2)
/// canvas = (W + 2P) × (H + 2P)
/// fill: solid | diagonal 2-stop gradient | aspect-filled image
/// blur > 0: blur the plate, radius = blur · min(canvas) · 0.035, edges clamped
/// card shadow: 6·scale px down, blur 22·scale, black 40 %, painted only outside the card
/// card: the image at (P, P) clipped to a rounded rect of radius R
/// no backdrop and R > 0: the image with transparent rounded corners
/// </code>
/// </summary>
public static class BackdropRenderer
{
    public static BackdropGeometry Measure(int imageWidth, int imageHeight, BackdropStyle style)
    {
        var s = style.Sanitized();
        var shortSide = Math.Min(imageWidth, imageHeight);
        var radius = (int)Math.Round(s.CornerRadius * shortSide * 0.2);
        if (!s.HasBackdrop)
        {
            return new BackdropGeometry(0, radius, imageWidth, imageHeight);
        }

        var padding = Math.Max(24, (int)Math.Round(shortSide * (0.035 + (0.14 * s.Padding))));
        return new BackdropGeometry(padding, radius, imageWidth + (2 * padding), imageHeight + (2 * padding));
    }

    /// <summary>
    /// Returns the composed image. <paramref name="imageLoader"/> resolves image
    /// backdrops (a missing image renders as no backdrop). <paramref name="scale"/>
    /// scales the shadow for high-DPI captures (1 = 96 DPI).
    /// </summary>
    public static SKImage Render(SKImage capture, BackdropStyle style, Func<string, SKImage?>? imageLoader = null, float scale = 1f)
    {
        var s = style.Sanitized();
        SKImage? plateImage = null;
        if (s.Kind == BackdropKind.Image)
        {
            plateImage = s.ImagePath is null ? null : (imageLoader ?? DefaultLoader)(s.ImagePath);
            if (plateImage is null)
            {
                s = new BackdropStyle { CornerRadius = s.CornerRadius };
            }
        }

        try
        {
            var geometry = Measure(capture.Width, capture.Height, s);
            if (!s.HasBackdrop && geometry.CornerRadius == 0)
            {
                return capture;
            }

            using var surface = SKSurface.Create(SkiaConvert.InfoFor(geometry.CanvasWidth, geometry.CanvasHeight));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var card = geometry.CardRect(capture.Width, capture.Height);
            var cardRRect = new SKRoundRect(card, geometry.CornerRadius);

            if (s.HasBackdrop)
            {
                DrawPlate(canvas, s, plateImage, geometry);
                DrawShadow(canvas, cardRRect, geometry, scale);
            }

            canvas.Save();
            canvas.ClipRoundRect(cardRRect, SKClipOperation.Intersect, antialias: true);
            canvas.DrawImage(capture, card, new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Restore();
            return surface.Snapshot();
        }
        finally
        {
            plateImage?.Dispose();
        }
    }

    /// <summary>Paints the backdrop plate (fill + optional blur) over the whole canvas.</summary>
    public static void DrawPlate(SKCanvas canvas, BackdropStyle style, SKImage? plateImage, BackdropGeometry geometry)
    {
        var bounds = SKRect.Create(geometry.CanvasWidth, geometry.CanvasHeight);
        var blurRadius = style.Blur * Math.Min(geometry.CanvasWidth, geometry.CanvasHeight) * 0.035;
        using var paint = new SKPaint { IsAntialias = true };
        if (blurRadius > 0.5)
        {
            paint.ImageFilter = SKImageFilter.CreateBlur((float)blurRadius, (float)blurRadius, SKShaderTileMode.Clamp);
        }

        canvas.Save();
        canvas.ClipRect(bounds);
        if (style.Kind == BackdropKind.Image && plateImage is not null)
        {
            var fill = AspectFill(plateImage.Width, plateImage.Height, bounds);
            canvas.DrawImage(plateImage, fill, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }
        else
        {
            var colors = style.ResolvedColors();
            if (colors.Count >= 2)
            {
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(geometry.CanvasWidth, geometry.CanvasHeight),
                    [ToSk(colors[0]), ToSk(colors[1])],
                    SKShaderTileMode.Clamp);
            }
            else
            {
                paint.Color = colors.Count == 1 ? ToSk(colors[0]) : SKColors.Transparent;
            }

            canvas.DrawRect(bounds, paint);
        }

        canvas.Restore();
    }

    /// <summary>The card shadow, painted only outside the card so translucent captures show no plate.</summary>
    public static void DrawShadow(SKCanvas canvas, SKRoundRect card, BackdropGeometry geometry, float scale)
    {
        var sigma = 22f * scale / 2f;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.Black,
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 6f * scale, sigma, sigma, new SKColor(0, 0, 0, 102)),
        };
        canvas.Save();
        canvas.ClipRoundRect(card, SKClipOperation.Difference, antialias: true);
        canvas.DrawRoundRect(card, paint);
        canvas.Restore();
    }

    public static SKRect AspectFill(int sourceWidth, int sourceHeight, SKRect target)
    {
        var scale = Math.Max(target.Width / sourceWidth, target.Height / sourceHeight);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return SKRect.Create(target.MidX - (w / 2), target.MidY - (h / 2), w, h);
    }

    public static SKColor ToSk(RgbColor c)
    {
        var k = c.Clamped();
        return new SKColor((byte)Math.Round(k.R * 255), (byte)Math.Round(k.G * 255), (byte)Math.Round(k.B * 255));
    }

    private static SKImage? DefaultLoader(string path) => SkiaConvert.LoadImage(path, 4096);
}
