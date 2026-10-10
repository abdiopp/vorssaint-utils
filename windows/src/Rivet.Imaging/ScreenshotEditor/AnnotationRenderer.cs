// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>
/// Pixel caches one renderer owns (the canvas and each export keep their own).
/// The canvas draws on Avalonia's render thread while the UI thread may ask
/// for a sample (to validate a blur change), so swapping is locked and a
/// replaced cache is disposed at the start of the next frame, never while a
/// frame may still be drawing with it.
/// </summary>
public sealed class EditorRenderCaches : IDisposable
{
    private readonly object _gate = new();
    private readonly List<BlurSampleCache> _retired = [];
    private BlurSampleCache? _samples;

    public EraseCache Erase { get; } = new();

    public ImageFileCache Images { get; } = new(8);

    /// <summary>Samples of <paramref name="image"/>; a new base image (crop, undo) starts fresh.</summary>
    public BlurSampleCache SamplesFor(SkiaEditorImage image)
    {
        lock (_gate)
        {
            if (_samples is null || !ReferenceEquals(_samples.Image, image))
            {
                if (_samples is not null)
                {
                    _retired.Add(_samples);
                }

                _samples = new BlurSampleCache(image);
            }

            return _samples;
        }
    }

    public void BeginFrame(SkiaEditorImage image)
    {
        lock (_gate)
        {
            foreach (var old in _retired)
            {
                old.Dispose();
            }

            _retired.Clear();
        }

        SamplesFor(image).BeginFrame();
        Erase.BeginFrame();
    }

    public void EndFrame()
    {
        BlurSampleCache? samples;
        lock (_gate)
        {
            samples = _samples;
        }

        samples?.EndFrame();
        Erase.EndFrame();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _samples?.Dispose();
            foreach (var old in _retired)
            {
                old.Dispose();
            }

            _retired.Clear();
        }

        Erase.Dispose();
        Images.Dispose();
    }
}

/// <summary>What the renderer needs to draw the capture's content.</summary>
public sealed record EditorRenderState
{
    public required SkiaEditorImage Image { get; init; }

    public required IReadOnlyList<Annotation> Annotations { get; init; }

    /// <summary>Pixels per DIP of the capture (stroke weights and text sizes scale with it).</summary>
    public double Scale { get; init; } = 1;

    public bool Shadows { get; init; }

    /// <summary>Recognized text runs; null while recognition is pending or failed (text-only covers everything).</summary>
    public IReadOnlyList<ImgRect>? TextRuns { get; init; }

    public WatermarkStyle Watermark { get; init; } = WatermarkStyle.None;

    /// <summary>The card's corner radius R in image pixels (keeps the watermark clear of rounded corners).</summary>
    public double CornerRadius { get; init; }

    /// <summary>A text mark in the inline editor, not drawn on the canvas.</summary>
    public Guid? HiddenAnnotation { get; init; }

    public AnnotationFonts Fonts { get; init; } = AnnotationFonts.Shared;

    public bool HasBlurAreas => Annotations.Any(a => a.Kind == AnnotationKind.Blur);
}

/// <summary>
/// Draws annotations in image space (spec 01 §3.10.4). Used by the live
/// canvas (under a zoom transform) and by every export, so what is seen is
/// what leaves.
/// </summary>
public static class AnnotationRenderer
{
    public static void Draw(SKCanvas canvas, Annotation a, EditorRenderState state, EditorRenderCaches caches)
    {
        var scale = state.Scale;
        var w = (float)(StrokeWidths.Points(a.Stroke) * scale);
        switch (a.Kind)
        {
            case AnnotationKind.Arrow:
            {
                using var path = SkiaPaths.ToSkPath(ArrowGeometry.Path(a.ArrowStyle, a.Start, a.End, w, a.Seed));
                using var paint = ArrowGeometry.IsFilled(a.ArrowStyle) ? FillPaint(a.Color) : StrokePaint(a.Color, w);
                ApplyShadow(paint, state, ShadowKind.Shape);
                canvas.DrawPath(path, paint);
                break;
            }

            case AnnotationKind.Line:
            {
                using var paint = StrokePaint(a.Color, w);
                ApplyShadow(paint, state, ShadowKind.Shape);
                canvas.DrawLine(SkiaPaths.P(a.Start), SkiaPaths.P(a.End), paint);
                break;
            }

            case AnnotationKind.Rect:
            {
                using var paint = StrokePaint(a.Color, w);
                ApplyShadow(paint, state, ShadowKind.Shape);
                canvas.DrawRect(SkiaPaths.R(a.Rect), paint);
                break;
            }

            case AnnotationKind.Ellipse:
            {
                using var paint = StrokePaint(a.Color, w);
                ApplyShadow(paint, state, ShadowKind.Shape);
                canvas.DrawOval(SkiaPaths.R(a.Rect), paint);
                break;
            }

            case AnnotationKind.Freehand:
            {
                if (a.Points.Count < 2)
                {
                    break;
                }

                using var path = SkiaPaths.ToSkPath(PenGeometry.Smooth(a.Points));
                using var paint = StrokePaint(a.Color, w);
                ApplyShadow(paint, state, ShadowKind.Shape);
                canvas.DrawPath(path, paint);
                break;
            }

            case AnnotationKind.Highlight:
            {
                using var paint = new SKPaint { Color = SkiaPaths.Color(a.Color, 0.42), BlendMode = SKBlendMode.Multiply, IsAntialias = true };
                canvas.DrawRect(SkiaPaths.R(a.Rect), paint);
                break;
            }

            case AnnotationKind.Redact:
            {
                using var paint = new SKPaint { Color = SkiaPaths.Color(a.Color), IsAntialias = true };
                canvas.DrawRect(SkiaPaths.R(a.Rect), paint);
                break;
            }

            case AnnotationKind.Blur:
                DrawBlurArea(canvas, a, state, caches);
                break;
            case AnnotationKind.Text:
                DrawText(canvas, a, state);
                break;
            case AnnotationKind.Sticker:
            {
                var rect = SkiaPaths.R(a.Rect);
                var size = (float)AnnotationMetrics.StickerFontSize(a.Rect, scale);
                using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black };
                ApplyShadow(paint, state, ShadowKind.Sticker);
                state.Fonts.DrawEmoji(canvas, Stickers.Glyph(a.Sticker), rect, size, paint);
                break;
            }

            case AnnotationKind.Counter:
                DrawCounter(canvas, a, state);
                break;
        }
    }

    private enum ShadowKind
    {
        Shape,
        Text,
        Sticker,
    }

    /// <summary>
    /// Optional annotation shadows: shapes 1·scale down, blur 3·scale, black 38 %;
    /// text black 55 %, blur 2.5·scale; stickers black 45 %, blur 3·scale.
    /// CoreGraphics blur values are about twice Skia's sigma.
    /// </summary>
    private static void ApplyShadow(SKPaint paint, EditorRenderState state, ShadowKind kind)
    {
        if (!state.Shadows)
        {
            return;
        }

        var s = (float)state.Scale;
        var (blur, alpha) = kind switch
        {
            ShadowKind.Text => (2.5f, 0.55),
            ShadowKind.Sticker => (3f, 0.45),
            _ => (3f, 0.38),
        };
        var sigma = blur * s / 2;
        paint.ImageFilter = SKImageFilter.CreateDropShadow(0, s, sigma, sigma, new SKColor(0, 0, 0, (byte)Math.Round(alpha * 255)));
    }

    public static SKImageFilter ShapeShadowFilter(double scale)
    {
        var s = (float)scale;
        return SKImageFilter.CreateDropShadow(0, s, 1.5f * s, 1.5f * s, new SKColor(0, 0, 0, 97));
    }

    private static SKPaint FillPaint(AnnotationColor color) =>
        new() { Color = SkiaPaths.Color(color), IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPaint StrokePaint(AnnotationColor color, float width) => new()
    {
        Color = SkiaPaths.Color(color),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    private static void DrawText(SKCanvas canvas, Annotation a, EditorRenderState state)
    {
        if (a.Text.Length == 0 || a.Id == state.HiddenAnnotation)
        {
            return;
        }

        var origin = AnnotationMetrics.TextDrawOrigin(a.Rect);
        var size = (float)AnnotationMetrics.TextFontPixels(a.TextSize, state.Scale);
        using var paint = new SKPaint { Color = SkiaPaths.Color(a.Color), IsAntialias = true };
        ApplyShadow(paint, state, ShadowKind.Text);
        state.Fonts.DrawTopLeft(canvas, a.Text, (float)origin.X, (float)origin.Y, size, state.Fonts.Semibold, paint);
    }

    private static void DrawCounter(SKCanvas canvas, Annotation a, EditorRenderState state)
    {
        var d = (float)AnnotationMetrics.CounterDiameter(state.Image.Width, state.Image.Height);
        var center = SkiaPaths.P(a.Rect.Center);
        using (var fill = FillPaint(a.Color))
        {
            ApplyShadow(fill, state, ShadowKind.Shape);
            canvas.DrawCircle(center, d / 2, fill);
        }

        var ring = (float)AnnotationMetrics.CounterRingWidth(d);
        using (var ringPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 230), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = ring,
        })
        {
            canvas.DrawCircle(center, (d / 2) - 1 - (ring / 2), ringPaint);
        }

        using var label = new SKPaint { Color = SkiaPaths.Color(AnnotationMetrics.CounterLabelColor(a.Color)), IsAntialias = true };
        state.Fonts.DrawCentered(canvas, a.Number.ToString(CultureInfo.InvariantCulture), center.X, center.Y,
            (float)AnnotationMetrics.CounterFontSize(d), state.Fonts.Bold, label);
    }

    /// <summary>
    /// Pixelate and blur stretch a base-image sample over the area, erase paints the
    /// interpolated fill; all in REPLACE mode so translucent captures never show glyphs
    /// through. Text-only areas cover only the recognized runs (or everything while unknown).
    /// </summary>
    private static void DrawBlurArea(SKCanvas canvas, Annotation a, EditorRenderState state, EditorRenderCaches caches)
    {
        var imageRect = new ImgRect(0, 0, state.Image.Width, state.Image.Height);
        var clip = a.Rect.Intersect(imageRect);
        if (clip.IsEmpty)
        {
            return;
        }

        var areas = a.TextOnly ? TextRuns.Covering(state.TextRuns, a.Rect) : [a.Rect];
        if (areas.Count == 0)
        {
            return;
        }

        using var replace = new SKPaint { BlendMode = SKBlendMode.Src, IsAntialias = true };
        if (a.BlurStyle == BlurStyle.Erase)
        {
            foreach (var run in areas)
            {
                var piece = run.Intersect(clip);
                if (piece.IsEmpty)
                {
                    continue;
                }

                var patch = caches.Erase.Get(state.Image, run, state.TextRuns);
                if (patch is null)
                {
                    continue;
                }

                canvas.Save();
                canvas.ClipRect(SkiaPaths.R(piece), SKClipOperation.Intersect, antialias: false);
                canvas.DrawImage(patch.Image, patch.Destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), replace);
                canvas.Restore();
            }

            return;
        }

        var sample = caches.SamplesFor(state.Image).Get(a.BlurStyle, a.BlurLevel);
        using var region = new SKPath();
        foreach (var run in areas)
        {
            var piece = run.Intersect(clip);
            if (!piece.IsEmpty)
            {
                region.AddRect(SkiaPaths.R(piece));
            }
        }

        if (region.IsEmpty)
        {
            return;
        }

        var sampling = a.BlurStyle == BlurStyle.Blur
            ? new SKSamplingOptions(SKCubicResampler.Mitchell)
            : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        canvas.Save();
        canvas.ClipPath(region, SKClipOperation.Intersect, antialias: false);
        canvas.DrawImage(sample, state.Image.Bounds, sampling, replace);
        canvas.Restore();
    }
}

/// <summary>A small cache of decoded image files (watermark pictures, backdrop images, thumbnails).</summary>
public sealed class ImageFileCache(int capacity) : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<(string Key, DateTime Stamp, SKImage? Image)> _entries = new();

    /// <summary>Decodes (and caches) <paramref name="path"/> with its longest side at most <paramref name="maxPixels"/>; null when missing.</summary>
    public SKImage? Get(string? path, int maxPixels)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        DateTime stamp;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var key = $"{maxPixels}|{path}";
        lock (_gate)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key && node.Value.Stamp == stamp)
                {
                    _entries.Remove(node);
                    _entries.AddFirst(node);
                    return node.Value.Image;
                }
            }
        }

        var image = Skia.SkiaConvert.LoadImage(path, maxPixels);
        lock (_gate)
        {
            _entries.AddFirst((key, stamp, image));
            while (_entries.Count > Math.Max(1, capacity))
            {
                // Images handed out may still be drawing; let the GC finalize evicted ones.
                _entries.RemoveLast();
            }
        }

        return image;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
