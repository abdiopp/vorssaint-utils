// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>Decoded 2-D codes: the joined payload and, for exactly one http(s) link, the URL to offer.</summary>
public sealed record QrResult(string Payload, Uri? Url, int Count);

/// <summary>
/// Finds 2-D codes in an image with ZXing (spec 01 §3.17, §6.18): QR, Aztec,
/// Data Matrix and PDF417. 1-D barcodes are excluded because they fire on
/// striped UI. ZXing.Net has no Micro QR reader.
/// </summary>
public static class QrDetector
{
    /// <summary>Very large captures are scanned at up to this many pixels.</summary>
    public const long MaxScanPixels = 16_000_000;

    private static readonly IList<BarcodeFormat> Formats =
        [BarcodeFormat.QR_CODE, BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417];

    public static QrResult? Detect(PixelBuffer image)
    {
        using var sk = SkiaConvert.ToImage(image);
        return Detect(sk);
    }

    public static QrResult? Detect(SKImage image)
    {
        try
        {
            var source = image;
            var factor = 1.0;
            var pixels = (long)image.Width * image.Height;
            if (pixels > MaxScanPixels)
            {
                factor = Math.Sqrt(MaxScanPixels / (double)pixels);
                source = SkiaConvert.Resize(image, Math.Max(1, (int)(image.Width * factor)), Math.Max(1, (int)(image.Height * factor)));
            }

            try
            {
                var bytes = ReadBgraOnWhite(source);
                var luminance = new RGBLuminanceSource(bytes, source.Width, source.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
                var reader = new BarcodeReaderGeneric
                {
                    AutoRotate = false,
                    Options = new DecodingOptions { TryHarder = true, PossibleFormats = Formats, TryInverted = true },
                };
                var results = reader.DecodeMultiple(luminance);
                if (results is null || results.Length == 0)
                {
                    return null;
                }

                var codes = results
                    .Where(r => !string.IsNullOrEmpty(r.Text))
                    .Select(r => (r.Text, BoxOf(r, factor)))
                    .ToList();
                var payload = EditorFiles.JoinQrPayloads(codes, image.Height);
                if (payload is null)
                {
                    return null;
                }

                var count = codes.Count(c => !string.IsNullOrWhiteSpace(c.Text));
                return new QrResult(payload, count == 1 ? EditorFiles.OpenableUrl(payload) : null, count);
            }
            finally
            {
                if (!ReferenceEquals(source, image))
                {
                    source.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("screenshotEditor", "QR scan failed.", ex);
            return null;
        }
    }

    /// <summary>Pixels composited on white, so transparent areas of a capture read as background, not as dark modules.</summary>
    private static byte[] ReadBgraOnWhite(SKImage image)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(image.Width, image.Height));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, 0, 0);
        using var flat = surface.Snapshot();
        var buffer = SkiaConvert.ToPixelBuffer(flat);
        return buffer.Pixels;
    }

    private static ImgRect BoxOf(Result result, double factor)
    {
        var points = result.ResultPoints;
        if (points is null || points.Length == 0)
        {
            return default;
        }

        var minX = points.Min(p => p.X) / factor;
        var minY = points.Min(p => p.Y) / factor;
        var maxX = points.Max(p => p.X) / factor;
        var maxY = points.Max(p => p.Y) / factor;
        return new ImgRect(minX, minY, maxX - minX, maxY - minY);
    }
}
