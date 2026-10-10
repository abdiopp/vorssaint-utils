// SPDX-License-Identifier: GPL-3.0-or-later
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// The privacy blur (spec 02 §6.14): a mosaic whose cells are averaged from
/// the area (blocks destroy the letters), then a Gaussian blur of 0.6 × the
/// cell size with clamped edges (the blur destroys the blocks, and no sliver
/// of the original shows at the border). The grid is anchored at the area's
/// corner and only pixels inside the area contribute.
/// </summary>
public static class PrivacyBlur
{
    /// <summary>Returns the hidden version of <paramref name="rect"/> (its size), or null when the image has no CPU pixels.</summary>
    public static unsafe SKImage? Render(SKImage source, SKRectI rect, int block)
    {
        rect = SKRectI.Intersect(rect, new SKRectI(0, 0, source.Width, source.Height));
        if (rect.Width < 1 || rect.Height < 1)
        {
            return null;
        }

        block = Math.Max(2, block);
        using var pixmap = source.PeekPixels();
        SKBitmap? copy = null;
        SKPixmap? readable = pixmap;
        if (readable is null || readable.ColorType != SKColorType.Bgra8888)
        {
            copy = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (!source.ReadPixels(copy.Info, copy.GetPixels(), copy.RowBytes, 0, 0))
            {
                copy.Dispose();
                return null;
            }

            readable = copy.PeekPixels();
        }

        try
        {
            var cols = (rect.Width + block - 1) / block;
            var rows = (rect.Height + block - 1) / block;
            using var cells = new SKBitmap(new SKImageInfo(cols, rows, SKColorType.Bgra8888, SKAlphaType.Premul));
            var basePtr = (byte*)readable!.GetPixels();
            var rowBytes = readable.RowBytes;
            var cellPtr = (byte*)cells.GetPixels();
            for (var cy = 0; cy < rows; cy++)
            {
                var y0 = rect.Top + (cy * block);
                var y1 = Math.Min(rect.Bottom, y0 + block);
                for (var cx = 0; cx < cols; cx++)
                {
                    var x0 = rect.Left + (cx * block);
                    var x1 = Math.Min(rect.Right, x0 + block);
                    long b = 0, g = 0, r = 0, a = 0;
                    for (var y = y0; y < y1; y++)
                    {
                        var p = basePtr + (y * rowBytes) + (x0 * 4);
                        for (var x = x0; x < x1; x++, p += 4)
                        {
                            b += p[0];
                            g += p[1];
                            r += p[2];
                            a += p[3];
                        }
                    }

                    var count = Math.Max(1, (y1 - y0) * (x1 - x0));
                    var q = cellPtr + (cy * cells.RowBytes) + (cx * 4);
                    q[0] = (byte)(b / count);
                    q[1] = (byte)(g / count);
                    q[2] = (byte)(r / count);
                    q[3] = (byte)(a / count);
                }
            }

            cells.NotifyPixelsChanged();
            using var cellImage = SKImage.FromBitmap(cells);
            var info = new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var mosaicSurface = SKSurface.Create(info);
            mosaicSurface.Canvas.DrawImage(cellImage, SKRect.Create(cols * block, rows * block), new SKSamplingOptions(SKFilterMode.Nearest));
            using var mosaic = mosaicSurface.Snapshot();

            using var blurSurface = SKSurface.Create(info);
            var sigma = (float)(0.6 * block);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
            blurSurface.Canvas.DrawImage(mosaic, 0, 0, paint);
            return blurSurface.Snapshot();
        }
        finally
        {
            copy?.Dispose();
        }
    }
}
