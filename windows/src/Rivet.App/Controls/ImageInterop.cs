// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Rivet.Core.Platform;
using SkiaSharp;

namespace Rivet.App.Controls;

/// <summary>
/// Turns SkiaSharp images and pixel buffers into Avalonia bitmaps (copies pixels).
/// Bitmaps are always created at 96 DPI: Avalonia 12.1.3 crops bitmaps created
/// at other DPIs. Size the Image control yourself (pixels ÷ scale) for crisp output.
/// </summary>
public static class ImageInterop
{
    public static WriteableBitmap ToBitmap(PixelBuffer buffer)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(buffer.Width, buffer.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        var rowBytes = buffer.Width * 4;
        for (var y = 0; y < buffer.Height; y++)
        {
            Marshal.Copy(buffer.Pixels, y * buffer.Stride, locked.Address + (y * locked.RowBytes), rowBytes);
        }

        return bitmap;
    }

    public static WriteableBitmap ToBitmap(SKImage image, double scale = 1.0)
    {
        _ = scale;
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        image.ReadPixels(info, locked.Address, locked.RowBytes, 0, 0);
        return bitmap;
    }

    /// <summary>Decodes an image file into a bitmap no wider than <paramref name="maxWidth"/> pixels.</summary>
    public static Bitmap? LoadThumbnail(string path, int maxWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, maxWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
