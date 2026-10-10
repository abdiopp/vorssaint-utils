// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.Capture;

/// <summary>Pixel-buffer operations shared by the capture tools (crop, encode, scale, sample).</summary>
public static class CaptureImaging
{
    /// <summary>Copies a sub-rectangle (clamped to the image); null when nothing remains.</summary>
    public static PixelBuffer? Crop(PixelBuffer source, PixelRect rect)
    {
        var r = rect.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (r.IsEmpty)
        {
            return null;
        }

        var result = new PixelBuffer(r.Width, r.Height) { Scale = source.Scale };
        var rowBytes = r.Width * 4;
        for (var y = 0; y < r.Height; y++)
        {
            Buffer.BlockCopy(source.Pixels, ((r.Y + y) * source.Stride) + (r.X * 4), result.Pixels, y * result.Stride, rowBytes);
        }

        return result;
    }

    /// <summary>The colour of one pixel as 0xAARRGGBB, un-premultiplied.</summary>
    public static uint ReadPixel(PixelBuffer image, int x, int y)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        var i = (y * image.Stride) + (x * 4);
        var b = image.Pixels[i];
        var g = image.Pixels[i + 1];
        var r = image.Pixels[i + 2];
        var a = image.Pixels[i + 3];
        if (a is not 0 and not 255)
        {
            r = (byte)Math.Min(255, r * 255 / a);
            g = (byte)Math.Min(255, g * 255 / a);
            b = (byte)Math.Min(255, b * 255 / a);
        }

        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>PNG bytes with DPI = 96 × scale.</summary>
    public static byte[] EncodePng(PixelBuffer image)
    {
        using var skImage = SkiaConvert.ToImage(image);
        return PngMetadata.WithDpi(SkiaConvert.EncodePng(skImage), 96 * image.Scale);
    }

    /// <summary>Writes a PNG atomically (temporary file in the same folder, then a move).</summary>
    public static void WritePngAtomically(byte[] png, string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>
    /// Decodes a PNG (or any image Skia reads). The scale comes from the
    /// stored DPI (0.5…4×); <paramref name="requireDpi"/> rejects files without
    /// a valid one (the latest-capture store), otherwise the scale defaults to 1.
    /// </summary>
    public static PixelBuffer? Decode(byte[] data, bool requireDpi = false)
    {
        var dpi = PngMetadata.ReadDpi(data);
        var scale = dpi is { } d ? ClipboardImageScale.FromDpi(d) : null;
        if (requireDpi && scale is null)
        {
            return null;
        }

        try
        {
            using var bitmap = SKBitmap.Decode(data);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return null;
            }

            using var converted = bitmap.ColorType == SKColorType.Bgra8888 && bitmap.AlphaType == SKAlphaType.Premul
                ? null
                : ConvertToBgraPremul(bitmap);
            return SkiaConvert.ToPixelBuffer(converted ?? bitmap, scale ?? 1);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static PixelBuffer? DecodeFile(string path, bool requireDpi = false, long maxBytes = 512L * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > maxBytes)
            {
                return null;
            }

            return Decode(File.ReadAllBytes(path), requireDpi);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// "Save at 1x size": a capture from a scaled display is resampled to its
    /// 100 % size, <c>(round(w / scale), round(h / scale))</c>, and reports scale 1.
    /// </summary>
    public static PixelBuffer ToOneX(PixelBuffer image)
    {
        if (image.Scale <= 1.0001)
        {
            return image;
        }

        var w = Math.Max(1, (int)Math.Round(image.Width / image.Scale, MidpointRounding.AwayFromZero));
        var h = Math.Max(1, (int)Math.Round(image.Height / image.Scale, MidpointRounding.AwayFromZero));
        return Resize(image, w, h, 1);
    }

    /// <summary>The same pixels with a different scale.</summary>
    public static PixelBuffer WithScale(PixelBuffer image, double scale) =>
        new(image.Width, image.Height, image.Pixels, image.Stride) { Scale = scale };

    /// <summary>Images that claim less than 96 DPI (72 DPI stamps) are treated as 1×.</summary>
    public static PixelBuffer AtLeastOneX(PixelBuffer image) => image.Scale < 1 ? WithScale(image, 1) : image;

    /// <summary>The output form of a raw capture: 1x-scaled when the setting asks for it.</summary>
    public static PixelBuffer ForOutput(PixelBuffer image, bool downscale) => downscale ? ToOneX(image) : image;

    /// <summary>Fits the image inside <paramref name="maxSide"/> (never upscales).</summary>
    public static PixelBuffer Thumbnail(PixelBuffer image, int maxSide)
    {
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide)
        {
            return image;
        }

        var factor = maxSide / (double)longest;
        return Resize(image, Math.Max(1, (int)Math.Round(image.Width * factor)), Math.Max(1, (int)Math.Round(image.Height * factor)), 1);
    }

    public static PixelBuffer Resize(PixelBuffer image, int width, int height, double scale)
    {
        using var source = SkiaConvert.ToImage(image);
        using var resized = SkiaConvert.Resize(source, width, height);
        return SkiaConvert.ToPixelBuffer(resized, scale);
    }

    /// <summary>True when every pixel is fully transparent or pure black (a failed window render).</summary>
    public static bool IsBlank(PixelBuffer image)
    {
        var pixels = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 0; x < image.Width * 4; x += 4)
            {
                if (pixels[row + x] != 0 || pixels[row + x + 1] != 0 || pixels[row + x + 2] != 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Sets alpha to 255 (GDI leaves it at 0 for most content).</summary>
    public static void MakeOpaque(PixelBuffer image)
    {
        var pixels = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Stride;
            for (var x = 3; x < image.Width * 4; x += 4)
            {
                pixels[row + x] = 255;
            }
        }
    }

    /// <summary>Draws layers over a base image at the given offsets (window plus attached dialogs).</summary>
    public static PixelBuffer Composite(PixelBuffer baseImage, IReadOnlyList<(PixelBuffer Image, PixelPoint Offset)> layers)
    {
        if (layers.Count == 0)
        {
            return baseImage;
        }

        using var surface = SKSurface.Create(SkiaConvert.InfoFor(baseImage.Width, baseImage.Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using (var b = SkiaConvert.ToImage(baseImage))
        {
            canvas.DrawImage(b, 0, 0);
        }

        foreach (var (image, offset) in layers)
        {
            using var layer = SkiaConvert.ToImage(image);
            canvas.DrawImage(layer, SKRect.Create(offset.X, offset.Y, image.Width, image.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var snapshot = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(snapshot, baseImage.Scale);
    }

    private static SKBitmap ConvertToBgraPremul(SKBitmap bitmap)
    {
        var converted = new SKBitmap(SkiaConvert.InfoFor(bitmap.Width, bitmap.Height));
        using var canvas = new SKCanvas(converted);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(bitmap, 0, 0);
        return converted;
    }
}
