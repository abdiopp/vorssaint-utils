// SPDX-License-Identifier: GPL-3.0-or-later
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>Decodes pictures with their EXIF orientation applied, no larger than needed.</summary>
public static class OrientedImage
{
    /// <summary>Null when the file is missing or not a picture Skia can read.</summary>
    public static SKImage? Load(string path, int maxLongEdge = 0)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var codec = SKCodec.Create(path);
            if (codec is null)
            {
                return null;
            }

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            // Let the codec subsample big JPEGs cheaply before the exact resize.
            if (maxLongEdge > 0)
            {
                var scale = maxLongEdge / (float)Math.Max(info.Width, info.Height);
                if (scale < 1)
                {
                    var scaled = codec.GetScaledDimensions(Math.Max(scale, 0.0625f));
                    if (scaled.Width >= Math.Min(info.Width, maxLongEdge / 2))
                    {
                        info = info.WithSize(scaled.Width, scaled.Height);
                    }
                }
            }

            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                using var fallback = SKBitmap.Decode(codec, codec.Info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Premul));
                return fallback is null ? null : Orient(SKImage.FromBitmap(fallback), codec.EncodedOrigin, maxLongEdge);
            }

            return Orient(SKImage.FromBitmap(bitmap), codec.EncodedOrigin, maxLongEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static SKImage Orient(SKImage image, SKEncodedOrigin origin, int maxLongEdge)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? image.Height : image.Width;
        var h = swap ? image.Width : image.Height;
        var scale = maxLongEdge > 0 ? Math.Min(1f, maxLongEdge / (float)Math.Max(w, h)) : 1f;
        if (origin == SKEncodedOrigin.TopLeft && scale >= 1)
        {
            return image;
        }

        var outW = Math.Max(1, (int)Math.Round(w * scale));
        var outH = Math.Max(1, (int)Math.Round(h * scale));
        using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Concat(OriginMatrix(origin, image.Width, image.Height));
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        image.Dispose();
        return surface.Snapshot();
    }

    /// <summary>Maps the stored pixels to the upright picture.</summary>
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
