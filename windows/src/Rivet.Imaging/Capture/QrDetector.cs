// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using ZXing;
using ZXing.Common;

namespace Rivet.Imaging.Capture;

/// <summary>
/// 2-D code detection with ZXing.Net (spec 01 §3.17): QR, Aztec, Data Matrix
/// and PDF417. 1-D barcodes are left out because striped UI triggers them;
/// Micro QR is not supported by ZXing.Net. ZXing looks for Data Matrix and
/// Aztec symbols around the image centre only, so when the whole image finds
/// nothing, overlapping tiles are searched for those two formats.
/// </summary>
public static class QrDetector
{
    private static readonly BarcodeFormat[] Formats =
        [BarcodeFormat.QR_CODE, BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX, BarcodeFormat.PDF_417];

    private static readonly BarcodeFormat[] CentredFormats = [BarcodeFormat.AZTEC, BarcodeFormat.DATA_MATRIX];

    private const int MaxTiles = 64;

    /// <summary>Every code with a non-empty payload, deduplicated. Never throws.</summary>
    public static IReadOnlyList<DetectedCode> Detect(PixelBuffer image)
    {
        try
        {
            var packed = Packed(image);
            var codes = new List<DetectedCode>();
            var reader = Reader(Formats, tryHarder: (long)image.Width * image.Height <= 8_000_000);
            Add(codes, reader.DecodeMultiple(packed, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32), 0, 0);
            if (codes.Count == 0)
            {
                ScanTiles(image, codes);
            }

            return codes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("capture", "QR detection failed.", ex);
            return [];
        }
    }

    /// <summary>
    /// Overlapping tiles of half the short side, stepping by a quarter tile: a
    /// code up to about half a tile wide lies wholly inside some tile with the
    /// tile's centre on it, which is what ZXing's Data Matrix and Aztec detectors need.
    /// </summary>
    private static void ScanTiles(PixelBuffer image, List<DetectedCode> codes)
    {
        var tile = Math.Max(160, Math.Min(image.Width, image.Height) / 2);
        if (tile >= image.Width && tile >= image.Height)
        {
            return;
        }

        var step = Math.Max(40, tile / 4);
        while (((image.Width - tile) / step + 1) * ((image.Height - tile) / step + 1) > MaxTiles)
        {
            step += step / 2;
        }
        var reader = Reader(CentredFormats, tryHarder: true);
        var scanned = 0;
        for (var top = 0; top < image.Height && scanned < MaxTiles; top += step)
        {
            for (var left = 0; left < image.Width && scanned < MaxTiles; left += step)
            {
                var rect = new PixelRect(Math.Min(left, Math.Max(0, image.Width - tile)), Math.Min(top, Math.Max(0, image.Height - tile)), Math.Min(tile, image.Width), Math.Min(tile, image.Height));
                if (CaptureImaging.Crop(image, rect) is not { } crop)
                {
                    continue;
                }

                scanned++;
                var result = reader.Decode(Packed(crop), crop.Width, crop.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
                if (result is not null)
                {
                    Add(codes, [result], rect.X, rect.Y);
                }
            }
        }
    }

    private static BarcodeReaderGeneric Reader(BarcodeFormat[] formats, bool tryHarder) => new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            PossibleFormats = formats,
            TryInverted = true,
            TryHarder = tryHarder,
        },
    };

    private static void Add(List<DetectedCode> codes, Result[]? results, int offsetX, int offsetY)
    {
        foreach (var result in results ?? [])
        {
            if (string.IsNullOrEmpty(result.Text))
            {
                continue;
            }

            var bounds = Bounds(result).Offset(offsetX, offsetY);
            if (codes.Any(c => c.Payload == result.Text && Math.Abs(c.Bounds.MidX - bounds.MidX) < 16 && Math.Abs(c.Bounds.MidY - bounds.MidY) < 16))
            {
                continue;
            }

            codes.Add(new DetectedCode(result.Text, bounds, result.BarcodeFormat.ToString()));
        }
    }

    private static RectD Bounds(Result result)
    {
        var points = result.ResultPoints;
        if (points is null || points.Length == 0)
        {
            return default;
        }

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new RectD(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private static byte[] Packed(PixelBuffer image)
    {
        var rowBytes = image.Width * 4;
        if (image.Stride == rowBytes && image.Pixels.Length == rowBytes * image.Height)
        {
            return image.Pixels;
        }

        var packed = new byte[rowBytes * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, y * image.Stride, packed, y * rowBytes, rowBytes);
        }

        return packed;
    }
}
