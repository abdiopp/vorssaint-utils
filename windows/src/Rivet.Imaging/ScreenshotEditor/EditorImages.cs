// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>Opening image files and bytes for editing ("Edit latest", "Edit clipboard image", shelf and history images).</summary>
public static class EditorImageLoader
{
    /// <summary>Decodes a file; null for non-images and images over 60 megapixels. PNG density gives the scale (dpi / 96).</summary>
    public static PixelBuffer? LoadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static PixelBuffer? Decode(byte[] bytes, double? scale = null)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || (long)codec.Info.Width * codec.Info.Height > EditorFiles.MaxEditablePixels)
        {
            return null;
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
        using var bitmap = SKBitmap.Decode(codec, info);
        if (bitmap is null)
        {
            return null;
        }

        var resolved = scale ?? (codec.EncodedFormat == SKEncodedImageFormat.Png ? PngWriter.ReadScale(bytes) : null) ?? 1;
        return SkiaConvert.ToPixelBuffer(bitmap, resolved);
    }
}

/// <summary>Small images for the editor's menus and swatches, drawn by the real renderer.</summary>
public static class EditorSamples
{
    /// <summary>The arrow style menu sample: a 36 × 16 point template at <paramref name="pixelsPerPoint"/>.</summary>
    public static SKImage ArrowStyle(ArrowStyle style, float pixelsPerPoint, SKColor color)
    {
        var w = Math.Max(1, (int)Math.Ceiling(36 * pixelsPerPoint));
        var h = Math.Max(1, (int)Math.Ceiling(16 * pixelsPerPoint));
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(w, h));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixelsPerPoint);
        var stroke = 2.2f;
        using var path = SkiaPaths.ToSkPath(ArrowGeometry.Path(style, new ImgPoint(4, 12), new ImgPoint(32, 4), stroke, ArrowGeometry.MenuSampleSeed));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            Style = ArrowGeometry.IsFilled(style) ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
            StrokeWidth = stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawPath(path, paint);
        return surface.Snapshot();
    }

    /// <summary>A swatch tile showing a backdrop look (fill only, no capture).</summary>
    public static SKImage BackdropSwatch(BackdropStyle look, int width, int height, SKImage? plate)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(Math.Max(1, width), Math.Max(1, height)));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var geometry = new BackdropGeometry(0, 0, Math.Max(1, width), Math.Max(1, height));
        BackdropRenderer.DrawPlate(canvas, look with { Blur = 0 }, plate, geometry);
        return surface.Snapshot();
    }

    /// <summary>A saved-watermark tile: text drawn in its colour or the picture, over a neutral plate.</summary>
    public static SKImage WatermarkSwatch(WatermarkStyle mark, int width, int height, AnnotationFonts fonts, ImageFileCache images)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(Math.Max(1, width), Math.Max(1, height)));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x7A, 0x7A, 0x80));
        var s = mark.Sanitized();
        if (s.Kind == WatermarkKind.Image && images.Get(s.ImagePath, 256) is { } picture)
        {
            var fit = Math.Min((width - 8f) / picture.Width, (height - 8f) / picture.Height);
            var w = picture.Width * fit;
            var h = picture.Height * fit;
            canvas.DrawImage(picture, SKRect.Create((width - w) / 2, (height - h) / 2, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else if (s.Kind == WatermarkKind.Text)
        {
            var size = height * 0.42f;
            var measured = fonts.MeasureRuns(s.Text, size, fonts.Semibold);
            if (measured.Width > width - 6)
            {
                size *= (width - 6) / measured.Width;
                measured = fonts.MeasureRuns(s.Text, size, fonts.Semibold);
            }

            using var paint = new SKPaint { IsAntialias = true, Color = SkiaPaths.Color(s.Color) };
            fonts.DrawTopLeft(canvas, s.Text, (width - measured.Width) / 2, (height - measured.Height) / 2, size, fonts.Semibold, paint);
        }

        return surface.Snapshot();
    }
}
