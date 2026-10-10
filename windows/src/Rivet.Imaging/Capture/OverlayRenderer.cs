// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using SkiaSharp;

namespace Rivet.Imaging.Capture;

/// <summary>The loupe's content for one frame.</summary>
public sealed record LoupeScene
{
    /// <summary>Pointer in display-local physical pixels.</summary>
    public required PointD Pointer { get; init; }

    /// <summary>The image the loupe samples (the display's frozen still or live snapshot).</summary>
    public required SKImage Source { get; init; }

    /// <summary>Source square (odd side) in source pixels.</summary>
    public required PixelRect Sample { get; init; }

    /// <summary>The pixel being picked, in source pixels.</summary>
    public required PixelPoint Target { get; init; }

    /// <summary>The colour of <see cref="Target"/> as 0xAARRGGBB.</summary>
    public required uint Color { get; init; }

    /// <summary>Formatted value ("#1E90FF").</summary>
    public required string Value { get; init; }

    /// <summary>Show "✓ value" after C.</summary>
    public bool Copied { get; init; }
}

/// <summary>Everything one display's overlay draws (physical pixels, display-local).</summary>
public sealed record OverlayScene
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>DPI scale of the display (constants are in DIPs × scale).</summary>
    public required double Scale { get; init; }

    /// <summary>Frozen still drawn under everything; null in live mode (transparent overlay).</summary>
    public SKImage? Background { get; init; }

    /// <summary>Dim opacity: 0.22 frozen, 0.18 live.</summary>
    public double Dim { get; init; } = 0.22;

    public RectD? Selection { get; init; }

    public RectD? Highlight { get; init; }

    public RectD? Ghost { get; init; }

    /// <summary>"W × H" under the selection.</summary>
    public string? Badge { get; init; }

    public LoupeScene? Loupe { get; init; }
}

/// <summary>
/// Draws the selector overlay (spec 01 §3.4.5, §3.4.8, §3.4.13) with the
/// exact constants of the macOS app. The canvas must be in physical pixels
/// with the origin at the display's top-left.
/// </summary>
public static class OverlayRenderer
{
    public static readonly SKColor SelectionBlue = new(46, 140, 255);       // rgb(0.18, 0.55, 1.0)
    public static readonly SKColor HighlightBlue = new(89, 158, 255);       // rgb(0.35, 0.62, 1.0)

    public static void Draw(SKCanvas canvas, OverlayScene scene)
    {
        var s = (float)scene.Scale;
        var bounds = SKRect.Create(scene.Width, scene.Height);
        if (scene.Background is { } background)
        {
            canvas.DrawImage(background, bounds, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        // Dim with a hole for the selection (or the highlighted window).
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, (byte)Math.Round(255 * scene.Dim)), IsAntialias = true })
        using (var path = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            path.AddRect(bounds);
            if (scene.Selection is { IsEmpty: false } sel)
            {
                path.AddRoundRect(ToSk(sel), 8 * s, 8 * s);
            }
            else if (scene.Highlight is { IsEmpty: false } hl)
            {
                path.AddRoundRect(ToSk(hl.Inflate(-1.25 * s, -1.25 * s)), 9 * s, 9 * s);
            }

            canvas.DrawPath(path, dim);
        }

        if (scene.Selection is null && scene.Highlight is { IsEmpty: false } highlight)
        {
            var rect = ToSk(highlight.Inflate(-1.25 * s, -1.25 * s));
            using var fill = new SKPaint { Color = HighlightBlue.WithAlpha(36), IsAntialias = true };
            using var stroke = new SKPaint { Color = HighlightBlue.WithAlpha(242), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f * s };
            canvas.DrawRoundRect(rect, 9 * s, 9 * s, fill);
            canvas.DrawRoundRect(rect, 9 * s, 9 * s, stroke);
        }

        if (scene.Selection is null && scene.Ghost is { IsEmpty: false } ghost)
        {
            using var dash = SKPathEffect.CreateDash([5 * s, 4 * s], 0);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(166), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s), PathEffect = dash };
            canvas.DrawRect(ToSk(ghost), paint);
        }

        if (scene.Selection is { IsEmpty: false } selection)
        {
            DrawSelection(canvas, selection, s);
            if (scene.Badge is { } badge)
            {
                DrawBadge(canvas, selection, badge, s, scene.Width, scene.Height);
            }
        }

        if (scene.Loupe is { } loupe)
        {
            DrawLoupe(canvas, loupe, s, scene.Width, scene.Height);
        }
    }

    private static void DrawSelection(SKCanvas canvas, RectD selection, float s)
    {
        var outer = ToSk(selection.Inflate(1.5 * s, 1.5 * s));
        using (var glow = new SKPaint
        {
            Color = SelectionBlue.WithAlpha(250),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3 * s,
            ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 4.5f * s, 4.5f * s, SelectionBlue.WithAlpha(140)),
        })
        {
            canvas.DrawRoundRect(outer, 9 * s, 9 * s, glow);
        }

        var inner = ToSk(selection.Inflate(0.5 * s, 0.5 * s));
        using var white = new SKPaint { Color = SKColors.White.WithAlpha(242), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s) };
        canvas.DrawRoundRect(inner, 8 * s, 8 * s, white);
    }

    private static void DrawBadge(SKCanvas canvas, RectD selection, string text, float s, int width, int height)
    {
        using var font = CaptureFonts.Mono(11 * s, bold: true);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var textWidth = font.MeasureText(text);
        var metrics = font.Metrics;
        var textHeight = metrics.Descent - metrics.Ascent;
        var w = textWidth + (14 * s);
        var h = textHeight + (6 * s);
        var x = (float)selection.MidX - (w / 2);
        var y = (float)selection.Bottom + (10 * s);
        var margin = 6 * s;
        x = Math.Clamp(x, margin, Math.Max(margin, width - w - margin));
        if (y + h > height - margin)
        {
            // No room below: tuck it inside the bottom of the selection.
            y = (float)selection.Bottom - h - (10 * s);
        }

        y = Math.Clamp(y, margin, Math.Max(margin, height - h - margin));
        using var plate = new SKPaint { Color = new SKColor(0, 0, 0, 184), IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Create(x, y, w, h), 5 * s, 5 * s, plate);
        canvas.DrawText(text, x + (7 * s), y + (3 * s) - metrics.Ascent, font, textPaint);
    }

    private static void DrawLoupe(SKCanvas canvas, LoupeScene loupe, float s, int width, int height)
    {
        var origin = LoupeMath.BlockOrigin(loupe.Pointer, width, height, s);
        var frameSize = (float)LoupeMath.Frame * s;
        var frame = SKRect.Create((float)origin.X, (float)origin.Y, frameSize, frameSize);
        var radius = 9 * s;

        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(90), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(6 * s, 6 * s) })
        {
            canvas.DrawRoundRect(frame, radius, radius, shadow);
        }

        canvas.Save();
        using (var clip = new SKRoundRect(frame, radius, radius))
        {
            canvas.ClipRoundRect(clip, antialias: true);
        }

        var sample = loupe.Sample;
        canvas.DrawImage(loupe.Source, SKRect.Create(sample.X, sample.Y, sample.Width, sample.Height), frame, new SKSamplingOptions(SKFilterMode.Nearest));
        var side = Math.Max(sample.Width, sample.Height);
        if (LoupeMath.GridVisible(side))
        {
            using var grid = new SKPaint { Color = SKColors.Black.WithAlpha(71), StrokeWidth = Math.Max(1, s) };
            var cellW = frame.Width / sample.Width;
            var cellH = frame.Height / sample.Height;
            for (var i = 1; i < sample.Width; i++)
            {
                canvas.DrawLine(frame.Left + (i * cellW), frame.Top, frame.Left + (i * cellW), frame.Bottom, grid);
            }

            for (var i = 1; i < sample.Height; i++)
            {
                canvas.DrawLine(frame.Left, frame.Top + (i * cellH), frame.Right, frame.Top + (i * cellH), grid);
            }
        }

        // Reticle: a ring hugging the target pixel plus four arms to the frame edges.
        var cell = LoupeMath.TargetCell(loupe.Target, sample, new RectD(frame.Left, frame.Top, frame.Width, frame.Height));
        var ring = SKRect.Create((float)cell.X - s, (float)cell.Y - s, (float)cell.Width + (2 * s), (float)cell.Height + (2 * s));
        using (var reticle = new SKPath())
        {
            reticle.AddRect(ring);
            reticle.MoveTo(ring.MidX, frame.Top);
            reticle.LineTo(ring.MidX, ring.Top);
            reticle.MoveTo(ring.MidX, ring.Bottom);
            reticle.LineTo(ring.MidX, frame.Bottom);
            reticle.MoveTo(frame.Left, ring.MidY);
            reticle.LineTo(ring.Left, ring.MidY);
            reticle.MoveTo(ring.Right, ring.MidY);
            reticle.LineTo(frame.Right, ring.MidY);
            using var dark = new SKPaint { Color = SKColors.Black.WithAlpha(194), Style = SKPaintStyle.Stroke, StrokeWidth = 3 * s, IsAntialias = true };
            using var light = new SKPaint { Color = SKColors.White.WithAlpha(235), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s), IsAntialias = true };
            canvas.DrawPath(reticle, dark);
            canvas.DrawPath(reticle, light);
        }

        canvas.Restore();
        using (var border = new SKPaint { Color = SKColors.White.WithAlpha(242), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * s, IsAntialias = true })
        {
            canvas.DrawRoundRect(frame, radius, radius, border);
        }

        // Info bar: swatch, value, coordinates.
        using var font = CaptureFonts.Mono(10.5f * s, bold: true);
        var text = loupe.Copied ? $"✓ {loupe.Value}" : $"{loupe.Value}   {loupe.Target.X}, {loupe.Target.Y}";
        var textWidth = font.MeasureText(text);
        var barHeight = (float)LoupeMath.InfoHeight * s;
        var barWidth = Math.Max(frameSize, (12 * s) + (8 * s) + textWidth + (16 * s));
        var barX = Math.Clamp(frame.Left, 8 * s, Math.Max(8 * s, width - barWidth - (8 * s)));
        var barY = frame.Bottom + ((float)LoupeMath.InfoGap * s);
        var bar = SKRect.Create(barX, barY, barWidth, barHeight);
        using (var plate = new SKPaint { Color = new SKColor(0, 0, 0, 184), IsAntialias = true })
        {
            canvas.DrawRoundRect(bar, 7 * s, 7 * s, plate);
        }

        var swatch = SKRect.Create(bar.Left + (8 * s), bar.MidY - (6 * s), 12 * s, 12 * s);
        using (var fill = new SKPaint { Color = new SKColor(loupe.Color | 0xFF000000), IsAntialias = true })
        using (var edge = new SKPaint { Color = SKColors.White.WithAlpha(217), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s), IsAntialias = true })
        {
            canvas.DrawRoundRect(swatch, 2 * s, 2 * s, fill);
            canvas.DrawRoundRect(swatch, 2 * s, 2 * s, edge);
        }

        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var metrics = font.Metrics;
        canvas.DrawText(text, swatch.Right + (8 * s), bar.MidY - ((metrics.Ascent + metrics.Descent) / 2), font, textPaint);
    }

    private static SKRect ToSk(RectD r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
}

/// <summary>Fonts for Skia-drawn chrome, preferring the Windows UI fonts.</summary>
public static class CaptureFonts
{
    private static readonly Lazy<SKTypeface> MonoBold = new(() => Find(["Cascadia Mono", "Consolas", "Menlo", "DejaVu Sans Mono"], SKFontStyle.Bold));
    private static readonly Lazy<SKTypeface> UiBold = new(() => Find(["Segoe UI Variable Text", "Segoe UI", "Helvetica Neue", "Arial"], SKFontStyle.Bold));
    private static readonly Lazy<SKTypeface> UiRegular = new(() => Find(["Segoe UI Variable Text", "Segoe UI", "Helvetica Neue", "Arial"], SKFontStyle.Normal));

    public static SKFont Mono(float size, bool bold = true) => new(MonoBold.Value, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };

    public static SKFont Ui(float size, bool bold = false) => new(bold ? UiBold.Value : UiRegular.Value, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };

    private static SKTypeface Find(string[] families, SKFontStyle style)
    {
        foreach (var family in families)
        {
            var typeface = SKTypeface.FromFamilyName(family, style);
            if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }

            typeface?.Dispose();
        }

        return SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
    }
}
