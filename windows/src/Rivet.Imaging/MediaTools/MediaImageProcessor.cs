// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.MediaTools;

/// <summary>Platform codecs the processor can fall back to (HEIC, TIFF and other WIC formats on Windows).</summary>
public sealed record MediaCodecHooks
{
    /// <summary>Decodes with orientation applied, longest side ≤ maxPixel (0 = full); null when unsupported.</summary>
    public Func<string, int, PixelBuffer?>? Decode { get; init; }

    /// <summary>Encodes HEIC at a 0.1–1 quality; null when no encoder is installed.</summary>
    public Func<PixelBuffer, double, byte[]?>? EncodeHeic { get; init; }
}

/// <summary>
/// The image tool's per-file pipeline (spec 07 §3.6.6) on SkiaSharp: read the
/// size with EXIF orientation, compute and check the target size, decode
/// downsampled with orientation applied, render on an 8-bit canvas (the
/// source colour profile is not kept), fill the background, draw the image
/// (Fit letterboxes, Fill crops), draw the watermark, and encode
/// JPEG/PNG/WebP/HEIC/PDF. Keeping metadata copies the Exif block for
/// JPEG → JPEG only.
/// </summary>
public static class MediaImageProcessor
{
    public sealed class ProcessException(string message) : Exception(message);

    /// <summary>The upright size of an image file without decoding its pixels.</summary>
    public static MediaSize? ReadSize(string path, MediaCodecHooks? hooks = null)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is not null)
            {
                var size = new MediaSize(codec.Info.Width, codec.Info.Height);
                return MediaSizing.Oriented(size, (int)codec.EncodedOrigin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var decoded = hooks?.Decode?.Invoke(path, 0);
        return decoded is null ? null : new MediaSize(decoded.Width, decoded.Height);
    }

    /// <summary>Decodes upright, downsampled so the longest side is about <paramref name="maxPixel"/> (0 = full size).</summary>
    public static SKImage? Decode(string path, int maxPixel, MediaCodecHooks? hooks = null)
    {
        try
        {
            using var codec = SKCodec.Create(path);
            if (codec is not null)
            {
                var info = codec.Info;
                var longest = Math.Max(info.Width, info.Height);
                var scaled = new SKSizeI(info.Width, info.Height);
                if (maxPixel > 0 && longest > maxPixel)
                {
                    scaled = codec.GetScaledDimensions((float)maxPixel / longest);
                    if (scaled.Width < 1 || scaled.Height < 1 || Math.Max(scaled.Width, scaled.Height) < maxPixel)
                    {
                        scaled = new SKSizeI(info.Width, info.Height);
                    }
                }

                var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
                using var bitmap = new SKBitmap(decodeInfo);
                var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    return null;
                }

                using var upright = ApplyOrigin(bitmap, codec.EncodedOrigin);
                return SKImage.FromBitmap(upright);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var buffer = hooks?.Decode?.Invoke(path, maxPixel);
        return buffer is null ? null : SkiaConvert.ToImage(buffer);
    }

    /// <summary>A copy rotated/flipped so the EXIF orientation becomes "top-left".</summary>
    public static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or 0)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = source.Width, h = source.Height;
        var result = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(w, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, h);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(h, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(h, w);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, w);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }

    /// <summary>Draws <paramref name="source"/> on a <paramref name="target"/>-sized canvas with the background and watermark.</summary>
    public static SKImage Render(SKImage source, MediaSize target, ImageOptions options, SKImage? logo)
    {
        var info = SkiaConvert.InfoFor(target.Width, target.Height);
        using var surface = SKSurface.Create(info) ?? throw new ProcessException("Surface");
        var canvas = surface.Canvas;
        var background = MediaImageFormats.EffectiveBackground(options.Format, options.Background);
        canvas.Clear(background switch
        {
            ImageBackground.White => SKColors.White,
            ImageBackground.Black => SKColors.Black,
            _ => SKColors.Transparent,
        });

        var (x, y, w, h) = MediaSizing.DrawRect(new MediaSize(source.Width, source.Height), target, options.Resize);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.DrawImage(source, new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }

        if (options.Watermark.IsActive)
        {
            DrawWatermark(canvas, target.Width, target.Height, options.Watermark, logo);
        }

        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>The watermark: logo then text, white with a soft dark shadow, at the chosen corner.</summary>
    public static void DrawWatermark(SKCanvas canvas, int width, int height, WatermarkOptions options, SKImage? logo)
    {
        using var typeface = WatermarkTypeface();
        using var font = new SKFont(typeface, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var text = options.Text.Trim();
        (double, double) Measure(double size)
        {
            font.Size = (float)size;
            var widthPx = font.MeasureText(text);
            var metrics = font.Metrics;
            return (widthPx, metrics.Descent - metrics.Ascent);
        }

        var placement = WatermarkLayout.Compute(width, height, options, logo is null ? null : new MediaSize(logo.Width, logo.Height), Measure);
        if (placement is null)
        {
            return;
        }

        if (placement.HasLogo && logo is not null)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)Math.Round(255 * placement.Opacity)) };
            var rect = new SKRect((float)placement.LogoX, (float)placement.LogoY, (float)(placement.LogoX + placement.LogoWidth), (float)(placement.LogoY + placement.LogoHeight));
            canvas.DrawImage(logo, rect, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }

        if (placement.HasText)
        {
            font.Size = (float)placement.FontSize;
            var metrics = font.Metrics;
            var baseline = (float)(placement.TextY - metrics.Ascent);
            var sigma = (float)(placement.ShadowBlur / 2);
            using var shadow = SKImageFilter.CreateDropShadowOnly(0, 1, sigma, sigma, SKColors.Black.WithAlpha((byte)Math.Round(255 * placement.ShadowOpacity)));
            using (var shadowPaint = new SKPaint { IsAntialias = true, ImageFilter = shadow, Color = SKColors.White })
            {
                canvas.DrawText(text, (float)placement.TextX, baseline, font, shadowPaint);
            }

            using var textPaint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)Math.Round(255 * placement.Opacity)) };
            canvas.DrawText(text, (float)placement.TextX, baseline, font, textPaint);
        }
    }

    /// <summary>Encodes for the output format. JPEG and PDF get quality q (0.1–1); PNG is lossless.</summary>
    public static byte[] Encode(SKImage image, ImageOptions options, MediaCodecHooks? hooks = null)
    {
        var quality = (int)Math.Round(Math.Clamp(options.Quality, 0.1, 1) * 100);
        switch (options.Format)
        {
            case ImageOutputFormat.Png:
                return SkiaConvert.Encode(image, SKEncodedImageFormat.Png, 100);
            case ImageOutputFormat.WebP:
                return SkiaConvert.Encode(image, SKEncodedImageFormat.Webp, quality);
            case ImageOutputFormat.Heic:
                var heic = hooks?.EncodeHeic?.Invoke(SkiaConvert.ToPixelBuffer(image), Math.Clamp(options.Quality, 0.1, 1));
                return heic ?? throw new ProcessException("HEIC");
            case ImageOutputFormat.Pdf:
                using (var stream = new MemoryStream())
                {
                    var jpeg = EncodeOpaqueJpeg(image, quality);
                    JpegPdfWriter.Write(stream, jpeg, image.Width, image.Height);
                    return stream.ToArray();
                }

            default:
                return EncodeOpaqueJpeg(image, quality);
        }
    }

    /// <summary>
    /// Processes one file into <paramref name="tempOutput"/> and returns the output
    /// size. Throws <see cref="ProcessException"/> with "unsupported" or "tooLarge".
    /// </summary>
    public static MediaSize Process(string input, string tempOutput, ImageOptions options, SKImage? logo, MediaCodecHooks? hooks = null)
    {
        var sourceSize = ReadSize(input, hooks) ?? throw new ProcessException("unsupported");
        var target = MediaSizing.TargetSize(sourceSize, options.Resize);
        if (!MediaSizing.IsSafe(target))
        {
            throw new ProcessException("tooLarge");
        }

        var decodeMax = MediaSizing.DecodeMaxPixel(sourceSize, options.Resize);
        using var source = Decode(input, options.Resize.Kind == ImageResizeKind.None ? 0 : decodeMax, hooks) ?? throw new ProcessException("unsupported");

        // Decoding may have produced a slightly different size; recompute and re-check.
        var actualSource = new MediaSize(source.Width, source.Height);
        target = options.Resize.Kind == ImageResizeKind.None ? actualSource : MediaSizing.TargetSize(sourceSize, options.Resize);
        if (!MediaSizing.IsSafe(target))
        {
            throw new ProcessException("tooLarge");
        }

        using var rendered = Render(source, target, options, logo);
        var bytes = Encode(rendered, options, hooks);
        if (!options.StripMetadata && options.Format == ImageOutputFormat.Jpeg && IsJpeg(input))
        {
            try
            {
                var app1 = JpegExif.ExtractApp1(File.ReadAllBytes(input));
                if (app1 is not null)
                {
                    bytes = JpegExif.Insert(bytes, app1, target.Width, target.Height);
                }
            }
            catch (IOException)
            {
                // Metadata is best effort; the image itself is fine.
            }
        }

        File.WriteAllBytes(tempOutput, bytes);
        return target;
    }

    /// <summary>A small preview of the processed result (the preview card), or null.</summary>
    public static SKImage? Preview(string input, ImageOptions options, SKImage? logo, int maxSide, MediaCodecHooks? hooks = null)
    {
        var size = ReadSize(input, hooks);
        if (size is null)
        {
            return null;
        }

        var target = MediaSizing.TargetSize(size.Value, options.Resize);
        if (!MediaSizing.IsSafe(target))
        {
            return null;
        }

        var factor = Math.Min(1.0, maxSide / (double)Math.Max(target.Width, target.Height));
        var small = new MediaSize(Math.Max(1, (int)Math.Round(target.Width * factor)), Math.Max(1, (int)Math.Round(target.Height * factor)));
        using var source = Decode(input, Math.Max(small.Width, small.Height) * 2, hooks);
        if (source is null)
        {
            return null;
        }

        // The watermark is drawn at full size proportions, so render at the small size with a scaled margin.
        var scaledOptions = options with { Watermark = options.Watermark with { Margin = Math.Max(1, (int)Math.Round(options.Watermark.Margin * factor)) } };
        return Render(source, small, scaledOptions, logo);
    }

    public static SKImage? LoadLogo(string path, MediaCodecHooks? hooks = null) =>
        string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : Decode(path, 2048, hooks);

    private static byte[] EncodeOpaqueJpeg(SKImage image, int quality)
    {
        // JPEG has no alpha: flatten onto white first (the background step already did for opaque formats).
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(image.Width, image.Height));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, 0, 0);
        using var flat = surface.Snapshot();
        return SkiaConvert.Encode(flat, SKEncodedImageFormat.Jpeg, quality);
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif";

    private static SKTypeface WatermarkTypeface()
    {
        foreach (var family in new[] { "Segoe UI", "Segoe UI Variable Text", "Helvetica Neue", "Arial", "Inter" })
        {
            var typeface = SKTypeface.FromFamilyName(family, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }

            typeface?.Dispose();
        }

        return SKTypeface.FromFamilyName(null, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
    }
}
