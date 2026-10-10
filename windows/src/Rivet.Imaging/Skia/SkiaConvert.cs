// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Platform;
using SkiaSharp;

namespace Rivet.Imaging.Skia;

/// <summary>Conversions between <see cref="PixelBuffer"/>, SkiaSharp images and encoded files.</summary>
public static class SkiaConvert
{
    public static SKImageInfo InfoFor(int width, int height) =>
        new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());

    /// <summary>Copies the buffer into a new bitmap.</summary>
    public static SKBitmap ToBitmap(PixelBuffer buffer)
    {
        var bitmap = new SKBitmap(InfoFor(buffer.Width, buffer.Height));
        var rowBytes = buffer.Width * 4;
        var destination = bitmap.GetPixels();
        if (buffer.Stride == bitmap.RowBytes)
        {
            Marshal.Copy(buffer.Pixels, 0, destination, buffer.Stride * buffer.Height);
        }
        else
        {
            for (var y = 0; y < buffer.Height; y++)
            {
                Marshal.Copy(buffer.Pixels, y * buffer.Stride, destination + (y * bitmap.RowBytes), rowBytes);
            }
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    public static SKImage ToImage(PixelBuffer buffer)
    {
        using var bitmap = ToBitmap(buffer);
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>Copies an image into a new BGRA premultiplied buffer.</summary>
    public static PixelBuffer ToPixelBuffer(SKImage image, double scale = 1.0)
    {
        var info = InfoFor(image.Width, image.Height);
        var buffer = new PixelBuffer(image.Width, image.Height) { Scale = scale };
        var handle = GCHandle.Alloc(buffer.Pixels, GCHandleType.Pinned);
        try
        {
            image.ReadPixels(info, handle.AddrOfPinnedObject(), buffer.Stride, 0, 0);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    public static PixelBuffer ToPixelBuffer(SKBitmap bitmap, double scale = 1.0)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return ToPixelBuffer(image, scale);
    }

    public static byte[] EncodePng(SKImage image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Encode(SKImage image, SKEncodedImageFormat format, int quality)
    {
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static void SavePng(SKImage image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>Decodes an image file, downscaling so the longest side is at most <paramref name="maxPixels"/>.</summary>
    public static SKImage? LoadImage(string path, int maxPixels = 0)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is null)
            {
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = SKBitmap.Decode(codec, info);
            if (bitmap is null)
            {
                return null;
            }

            var longest = Math.Max(bitmap.Width, bitmap.Height);
            if (maxPixels > 0 && longest > maxPixels)
            {
                var factor = maxPixels / (double)longest;
                var size = new SKImageInfo(
                    Math.Max(1, (int)Math.Round(bitmap.Width * factor)),
                    Math.Max(1, (int)Math.Round(bitmap.Height * factor)),
                    SKColorType.Bgra8888, SKAlphaType.Premul);
                using var scaled = bitmap.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
                return scaled is null ? null : SKImage.FromBitmap(scaled);
            }

            return SKImage.FromBitmap(bitmap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>High-quality resize.</summary>
    public static SKImage Resize(SKImage image, int width, int height)
    {
        using var surface = SKSurface.Create(InfoFor(width, height));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return surface.Snapshot();
    }

    public static SKImage Crop(SKImage image, SKRectI rect)
    {
        var bounded = SKRectI.Intersect(rect, new SKRectI(0, 0, image.Width, image.Height));
        return image.Subset(bounded) ?? image;
    }
}
