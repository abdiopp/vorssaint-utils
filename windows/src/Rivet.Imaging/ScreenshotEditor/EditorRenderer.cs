// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>A finished export and the pixels-per-DIP it now carries (1 after a 1x downscale).</summary>
public sealed class EditorExport(SKImage image, double scale) : IDisposable
{
    public SKImage Image { get; } = image;

    public double Scale { get; } = scale;

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// The one renderer for the canvas and every export (spec 01 §6.15):
/// base image → annotations in drawing order → watermark (inside the image),
/// then rounded corners or the backdrop, then the optional 1x downscale.
/// </summary>
public static class EditorRenderer
{
    /// <summary>Draws the capture's content in image space, clipped to the image (marks never spill onto a margin).</summary>
    public static void DrawContent(SKCanvas canvas, EditorRenderState state, EditorRenderCaches caches)
    {
        canvas.Save();
        canvas.ClipRect(state.Image.Bounds);
        canvas.DrawImage(state.Image.Image, 0, 0);
        foreach (var annotation in state.Annotations)
        {
            AnnotationRenderer.Draw(canvas, annotation, state, caches);
        }

        WatermarkRenderer.Draw(canvas, state, caches);
        canvas.Restore();
    }

    /// <summary>The W × H flattened capture (base, marks and watermark).</summary>
    public static SKImage Flatten(EditorRenderState state, EditorRenderCaches caches)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(state.Image.Width, state.Image.Height));
        surface.Canvas.Clear(SKColors.Transparent);
        DrawContent(surface.Canvas, state, caches);
        return surface.Snapshot();
    }

    /// <summary>
    /// The export: with <paramref name="includeBackdrop"/> the backdrop is composed (§6.13);
    /// without it (Pin) a corner radius still rounds the capture into transparent corners.
    /// <paramref name="downscaleTo1x"/> resamples everything to <c>round(size / scale)</c>.
    /// </summary>
    public static EditorExport Export(EditorRenderState state, BackdropStyle backdrop, bool includeBackdrop, bool downscaleTo1x, EditorRenderCaches? caches = null)
    {
        var owned = caches is null;
        caches ??= new EditorRenderCaches();
        try
        {
            var style = backdrop.Sanitized();
            var geometry = BackdropRenderer.Measure(state.Image.Width, state.Image.Height, style);
            var flat = Flatten(state with { CornerRadius = geometry.CornerRadius, HiddenAnnotation = null }, caches);
            var effective = includeBackdrop ? style : new BackdropStyle { CornerRadius = style.CornerRadius };
            // The backdrop renderer disposes the plate it loads, so it gets a fresh decode rather than a cached image.
            var composed = BackdropRenderer.Render(flat, effective, path => SkiaConvert.LoadImage(path, 4096), (float)state.Scale);
            if (!ReferenceEquals(composed, flat))
            {
                flat.Dispose();
            }

            var scale = state.Scale;
            if (downscaleTo1x && scale > 1)
            {
                var w = Math.Max(1, SpecMath.RoundToInt(composed.Width / scale));
                var h = Math.Max(1, SpecMath.RoundToInt(composed.Height / scale));
                var resized = SkiaConvert.Resize(composed, w, h);
                composed.Dispose();
                return new EditorExport(resized, 1);
            }

            return new EditorExport(composed, scale);
        }
        finally
        {
            if (owned)
            {
                caches.Dispose();
            }
        }
    }
}

/// <summary>Draws the watermark (spec 01 §6.14): over the marks, inside the image, never on the backdrop margin.</summary>
public static class WatermarkRenderer
{
    public static void Draw(SKCanvas canvas, EditorRenderState state, EditorRenderCaches caches)
    {
        var style = state.Watermark.Sanitized();
        if (style.Kind == WatermarkKind.None)
        {
            return;
        }

        var w = state.Image.Width;
        var h = state.Image.Height;
        double contentW, contentH;
        SKImage? picture = null;
        float fontPx = 0;
        if (style.Kind == WatermarkKind.Text)
        {
            fontPx = (float)WatermarkGeometry.TextFontPixels(w, h, style.Size);
            var measured = state.Fonts.MeasureRuns(style.Text, fontPx, state.Fonts.Semibold);
            contentW = Math.Ceiling(measured.Width);
            contentH = Math.Ceiling(measured.Height);
        }
        else
        {
            picture = caches.Images.Get(style.ImagePath, 4096);
            if (picture is null)
            {
                // A picture watermark whose file is missing draws nothing.
                return;
            }

            (contentW, contentH) = WatermarkGeometry.ImageSize(w, style.Size, picture.Width, picture.Height);
        }

        if (contentW <= 0 || contentH <= 0)
        {
            return;
        }

        var place = WatermarkGeometry.Place(w, h, contentW, contentH, style.Anchor, style.Rotation, state.CornerRadius);
        canvas.Save();
        canvas.ClipRect(state.Image.Bounds);
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(style.Opacity * 255)) };
        if (state.Shadows)
        {
            layer.ImageFilter = AnnotationRenderer.ShapeShadowFilter(state.Scale);
        }

        canvas.SaveLayer(layer);
        canvas.Translate((float)place.Center.X, (float)place.Center.Y);
        // Positive degrees tilt the mark up to the right: counter-clockwise on a y-down canvas.
        canvas.RotateRadians((float)-place.RotationRadians);
        canvas.Scale((float)place.Fit);
        var left = (float)(-contentW / 2);
        var top = (float)(-contentH / 2);
        if (picture is not null)
        {
            canvas.DrawImage(picture, SKRect.Create(left, top, (float)contentW, (float)contentH), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else
        {
            using var paint = new SKPaint { Color = SkiaPaths.Color(style.Color), IsAntialias = true };
            state.Fonts.DrawTopLeft(canvas, style.Text, left, top, fontPx, state.Fonts.Semibold, paint);
        }

        canvas.Restore();
        canvas.Restore();
    }
}

/// <summary>PNG encoding with the capture's density in a pHYs chunk (Windows convention: 96 DPI × scale).</summary>
public static class PngWriter
{
    public const double BaseDpi = 96;

    public static byte[] Encode(SKImage image, double scale)
    {
        var png = SkiaConvert.EncodePng(image);
        return WithDpi(png, BaseDpi * (double.IsFinite(scale) && scale > 0 ? scale : 1));
    }

    public static void Save(SKImage image, double scale, string path)
    {
        var bytes = Encode(image, scale);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Inserts (or replaces) the pHYs chunk right after IHDR.</summary>
    public static byte[] WithDpi(byte[] png, double dpi)
    {
        if (png.Length < 33 || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            return png;
        }

        var stripped = RemoveChunk(png, "pHYs");
        var ppm = (uint)Math.Round(dpi / 0.0254);
        var data = new byte[9];
        WriteBigEndian(data, 0, ppm);
        WriteBigEndian(data, 4, ppm);
        data[8] = 1;
        var chunk = MakeChunk("pHYs", data);
        const int insertAt = 8 + 25;
        var result = new byte[stripped.Length + chunk.Length];
        Buffer.BlockCopy(stripped, 0, result, 0, insertAt);
        Buffer.BlockCopy(chunk, 0, result, insertAt, chunk.Length);
        Buffer.BlockCopy(stripped, insertAt, result, insertAt + chunk.Length, stripped.Length - insertAt);
        return result;
    }

    /// <summary>The DPI stored in a PNG's pHYs chunk, or null.</summary>
    public static double? ReadDpi(byte[] png)
    {
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)ReadBigEndian(png, offset);
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "pHYs" && length == 9 && offset + 8 + 9 <= png.Length)
            {
                var ppm = ReadBigEndian(png, offset + 8);
                return png[offset + 16] == 1 ? ppm * 0.0254 : null;
            }

            if (type == "IDAT" || length < 0)
            {
                return null;
            }

            offset += 12 + length;
        }

        return null;
    }

    /// <summary>The capture scale a PNG declares (dpi / 96), accepted within 0.5…4.</summary>
    public static double? ReadScale(byte[] png) =>
        ReadDpi(png) is { } dpi && dpi / BaseDpi is var s && s is >= 0.5 and <= 4 ? Math.Round(s * 100) / 100 : null;

    private static byte[] RemoveChunk(byte[] png, string name)
    {
        var offset = 8;
        var output = new List<byte>(png.Length);
        output.AddRange(png.AsSpan(0, 8).ToArray());
        while (offset + 12 <= png.Length)
        {
            var length = (int)ReadBigEndian(png, offset);
            var total = 12 + length;
            if (length < 0 || offset + total > png.Length)
            {
                output.AddRange(png.AsSpan(offset).ToArray());
                return output.ToArray();
            }

            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type != name)
            {
                output.AddRange(png.AsSpan(offset, total).ToArray());
            }

            offset += total;
        }

        return output.ToArray();
    }

    private static byte[] MakeChunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        WriteBigEndian(chunk, 0, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        WriteBigEndian(chunk, 8 + data.Length, Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadBigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
