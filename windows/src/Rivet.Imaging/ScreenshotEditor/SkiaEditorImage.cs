// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>
/// The editor's base image: an immutable raster image (BGRA, premultiplied)
/// whose pixels the blur samples and the erase fill read directly. Undo
/// snapshots hold references to these, so they are never mutated.
/// </summary>
public sealed class SkiaEditorImage : IEditorImage
{
    public SkiaEditorImage(SKImage image)
    {
        Image = image.IsLazyGenerated || image.IsTextureBacked ? image.ToRasterImage(true) : image;
    }

    public SKImage Image { get; }

    public int Width => Image.Width;

    public int Height => Image.Height;

    public SKRect Bounds => SKRect.Create(Width, Height);

    public static SkiaEditorImage FromPixelBuffer(PixelBuffer buffer) => new(SkiaConvert.ToImage(buffer));

    /// <summary>A copy of the given pixel rectangle (the result owns its pixels).</summary>
    public SkiaEditorImage Crop(int x, int y, int width, int height)
    {
        var rect = SKRectI.Intersect(new SKRectI(x, y, x + width, y + height), new SKRectI(0, 0, Width, Height));
        var info = SkiaConvert.InfoFor(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
        using var bitmap = new SKBitmap(info);
        Image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, rect.Left, rect.Top);
        bitmap.SetImmutable();
        return new SkiaEditorImage(SKImage.FromBitmap(bitmap));
    }

    public PixelBuffer ToPixelBuffer(double scale) => SkiaConvert.ToPixelBuffer(Image, scale);

    /// <summary>Direct pixel access; the pixmap is valid while this image is alive.</summary>
    public SKPixmap PeekPixels() => Image.PeekPixels() ?? throw new InvalidOperationException("The base image is not raster-backed.");

    /// <summary>Copies the pixels as tightly packed BGRA rows.</summary>
    public byte[] CopyBgra()
    {
        var info = SkiaConvert.InfoFor(Width, Height);
        var bytes = new byte[Width * Height * 4];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Image.ReadPixels(info, handle.AddrOfPinnedObject(), Width * 4, 0, 0);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}

/// <summary>Turns the editor's vector paths into Skia paths.</summary>
public static class SkiaPaths
{
    public static SKPath ToSkPath(VectorPath path)
    {
        var sk = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var c in path.Commands)
        {
            switch (c.Verb)
            {
                case PathVerb.Move:
                    sk.MoveTo(P(c.Point));
                    break;
                case PathVerb.Line:
                    sk.LineTo(P(c.Point));
                    break;
                case PathVerb.Quad:
                    sk.QuadTo(P(c.Control), P(c.Point));
                    break;
                case PathVerb.Arc:
                    var oval = SKRect.Create((float)(c.Point.X - c.Radius), (float)(c.Point.Y - c.Radius), (float)(2 * c.Radius), (float)(2 * c.Radius));
                    sk.ArcTo(oval, (float)(c.StartAngle * 180 / Math.PI), (float)(c.Sweep * 180 / Math.PI), false);
                    break;
                case PathVerb.Close:
                    sk.Close();
                    break;
            }
        }

        return sk;
    }

    public static SKPoint P(ImgPoint p) => new((float)p.X, (float)p.Y);

    public static SKRect R(ImgRect r) => SKRect.Create((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    public static SKColor Color(Rgb rgb, double alpha = 1) =>
        new((byte)Math.Round(Math.Clamp(rgb.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(rgb.G, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(rgb.B, 0, 1) * 255), (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255));

    public static SKColor Color(AnnotationColor color, double alpha = 1) => Color(AnnotationColors.RgbOf(color), alpha);
}
