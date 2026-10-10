// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.Recording;

/// <summary>
/// Builds the pointer-track entry for a cursor picture: its size and hot spot
/// as drawn on the recorded monitor (video pixels), its content identity, and
/// a PNG — the sharper rendition when one was obtained, so zooms stay crisp.
/// </summary>
public static class CursorShapeFactory
{
    /// <summary>Largest sharper rendition kept, per side.</summary>
    public const int MaxSharperSide = 256;

    /// <param name="baseImage">The cursor at its own size (premultiplied BGRA).</param>
    /// <param name="hotX">Hot spot in <paramref name="baseImage"/> pixels.</param>
    /// <param name="hotY">Hot spot in <paramref name="baseImage"/> pixels.</param>
    /// <param name="displayFactor">How much larger the cursor appears on the recorded monitor than its bitmap.</param>
    /// <param name="sharper">Optional larger rendition of the same cursor; ignored when its aspect differs or it is not larger.</param>
    public static CursorShapeSnapshot Create(PixelBuffer baseImage, double hotX, double hotY, double displayFactor, PixelBuffer? sharper = null)
    {
        if (!double.IsFinite(displayFactor) || displayFactor <= 0)
        {
            displayFactor = 1;
        }

        var width = (float)(baseImage.Width * displayFactor);
        var height = (float)(baseImage.Height * displayFactor);
        var hx = (float)(Math.Clamp(hotX, 0, baseImage.Width - 1) * displayFactor);
        var hy = (float)(Math.Clamp(hotY, 0, baseImage.Height - 1) * displayFactor);
        var identity = CursorIdentity.Hash(Packed(baseImage), width, height, hx, hy);

        var source = IsUsableSharper(baseImage, sharper) ? sharper! : baseImage;
        using var image = SkiaConvert.ToImage(source);
        byte[] png;
        if (source == baseImage && displayFactor > 1.01)
        {
            // No sharper picture: at least store it at the size it is drawn, smoothly scaled.
            var w = Math.Clamp((int)Math.Round(width), 1, MaxSharperSide);
            var h = Math.Clamp((int)Math.Round(height), 1, MaxSharperSide);
            using var scaled = SkiaConvert.Resize(image, w, h);
            png = SkiaConvert.EncodePng(scaled);
        }
        else
        {
            png = SkiaConvert.EncodePng(image);
        }

        return new CursorShapeSnapshot(identity, width, height, hx, hy, png);
    }

    /// <summary>A sharper copy must be wider than the base and keep its aspect ratio within 0.02 (the cursor may have changed meanwhile).</summary>
    public static bool IsUsableSharper(PixelBuffer baseImage, PixelBuffer? sharper)
    {
        if (sharper is null || sharper.Width <= baseImage.Width || sharper.Width > MaxSharperSide || sharper.Height > MaxSharperSide)
        {
            return false;
        }

        var a = baseImage.Width / (double)baseImage.Height;
        var b = sharper.Width / (double)sharper.Height;
        return Math.Abs(a - b) <= 0.02;
    }

    /// <summary>Tightly packed rows (the hash must not depend on the stride).</summary>
    private static byte[] Packed(PixelBuffer image)
    {
        var row = image.Width * 4;
        if (image.Stride == row)
        {
            return image.Pixels.Length == row * image.Height ? image.Pixels : image.Pixels[..(row * image.Height)];
        }

        var packed = new byte[row * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, packed, y * row, row);
        }

        return packed;
    }

    /// <summary>A PNG of any buffer (tests and the development fake).</summary>
    public static byte[] EncodePng(PixelBuffer image)
    {
        using var source = SkiaConvert.ToImage(image);
        return SkiaConvert.EncodePng(source);
    }

    /// <summary>Decodes a stored cursor PNG back to pixels (tests).</summary>
    public static PixelBuffer? DecodePng(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        return bitmap is null ? null : SkiaConvert.ToPixelBuffer(bitmap);
    }
}
