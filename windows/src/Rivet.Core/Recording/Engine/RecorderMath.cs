// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using Rivet.Core.Platform;

namespace Rivet.Core.Recording.Engine;

/// <summary>
/// Snaps a selection to what the encoder accepts (spec 02 §6.1): whole
/// pixels, even width and height, at least 32 px per side, inside the
/// monitor (shrinking rather than shifting).
/// </summary>
public static class RegionSnapping
{
    public const int MinimumSide = 32;

    /// <summary><c>evenSide</c>: floor, at least 32, rounded down to even. Non-finite → 32.</summary>
    public static int EvenSide(double value)
    {
        if (!double.IsFinite(value))
        {
            return MinimumSide;
        }

        var floored = Math.Max(MinimumSide, Math.Floor(value));
        var side = floored >= int.MaxValue ? int.MaxValue - 1 : (int)floored;
        return side % 2 == 0 ? side : side - 1;
    }

    public static PixelRect Snap(PixelRect rect, PixelRect bounds) => Snap(rect.X, rect.Y, rect.Width, rect.Height, bounds);

    /// <summary><c>snappedPixelRect</c>; <paramref name="bounds"/> is the monitor in physical pixels.</summary>
    public static PixelRect Snap(double x, double y, double width, double height, PixelRect bounds)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return new PixelRect(bounds.X, bounds.Y, EvenSide(bounds.Width), EvenSide(bounds.Height));
        }

        if (width < 0)
        {
            x += width;
            width = -width;
        }

        if (height < 0)
        {
            y += height;
            height = -height;
        }

        double cx, cy, cw, ch;
        var x1 = Math.Max(x, bounds.X);
        var y1 = Math.Max(y, bounds.Y);
        var x2 = Math.Min(x + width, bounds.Right);
        var y2 = Math.Min(y + height, bounds.Bottom);
        if (x2 > x1 && y2 > y1)
        {
            (cx, cy, cw, ch) = (x1, y1, x2 - x1, y2 - y1);
        }
        else
        {
            (cx, cy, cw, ch) = (bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        var ox = (int)Math.Floor(cx);
        var oy = (int)Math.Floor(cy);
        var w = EvenSide(cw);
        var h = EvenSide(ch);
        if (ox + w > bounds.Right)
        {
            ox = Math.Max(bounds.X, bounds.Right - w);
        }

        if (oy + h > bounds.Bottom)
        {
            oy = Math.Max(bounds.Y, bounds.Bottom - h);
        }

        w = EvenSide(Math.Min(w, bounds.Right - ox));
        h = EvenSide(Math.Min(h, bounds.Bottom - oy));
        return new PixelRect(ox, oy, w, h);
    }
}

/// <summary>The pill's elapsed label (spec 02 §6.25): <c>m:ss</c> below an hour, <c>h:mm:ss</c> above.</summary>
public static class ElapsedLabel
{
    public static string Format(double seconds)
    {
        var total = double.IsFinite(seconds) && seconds > 0 ? (long)Math.Floor(seconds) : 0;
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    public static string Format(TimeSpan elapsed) => Format(elapsed.TotalSeconds);
}

/// <summary>Encoder bit rates (spec 02 §6.3). The master is always encoded with the "High" preset.</summary>
public static class EncoderBitRate
{
    public const double SmallBitsPerPixel = 0.05;
    public const double BalancedBitsPerPixel = 0.06;
    public const double HighBitsPerPixel = 0.09;
    public const long Minimum = 800_000;
    public const long Maximum = 60_000_000;

    public static long Compute(int width, int height, int fps, double bitsPerPixel)
    {
        var raw = (double)Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, fps) * bitsPerPixel;
        return (long)Math.Round(Math.Clamp(raw, Minimum, Maximum));
    }

    /// <summary>Average bit rate of the master movie, in bits per second.</summary>
    public static long ForMaster(int width, int height, int fps) => Compute(width, height, fps, HighBitsPerPixel);

    /// <summary>Maximum key-frame interval of the master: 2 s, expressed in frames.</summary>
    public static int MasterGopFrames(int fps) => Math.Max(1, fps) * 2;
}

/// <summary>
/// Frame-size limits of the H.264 encoders Windows ships or drivers expose
/// (level 5.1/5.2: 36,864 macroblocks, and most hardware tops out at 4096 per
/// side). Larger regions (5K and super-ultrawide monitors) are encoded scaled
/// down; the pointer track is normalized, so it is unaffected.
/// </summary>
public static class VideoSizeLimits
{
    public const int MaxSide = 4096;
    public const long MaxPixels = 4096L * 2304;

    public static (int Width, int Height) Fit(int width, int height)
    {
        width = Math.Max(RegionSnapping.MinimumSide, width);
        height = Math.Max(RegionSnapping.MinimumSide, height);
        if (width <= MaxSide && height <= MaxSide && (long)width * height <= MaxPixels)
        {
            return (RegionSnapping.EvenSide(width), RegionSnapping.EvenSide(height));
        }

        var scale = Math.Min(MaxSide / (double)Math.Max(width, height), Math.Sqrt(MaxPixels / ((double)width * height)));
        return (RegionSnapping.EvenSide(width * scale), RegionSnapping.EvenSide(height * scale));
    }
}

/// <summary>How a window's content maps into the fixed output picture.</summary>
public readonly record struct FitTransform(double Scale, double OffsetX, double OffsetY, bool IsCrop);

/// <summary>
/// Window recordings keep the output size chosen at start. While the window
/// keeps (about) that size its content is copied 1:1; after a resize it is
/// scaled to fit with its aspect ratio kept (letterboxed). The video path and
/// the pointer normalization both use this, so the redrawn pointer stays on
/// the content even when the window moves or resizes (a fix over macOS, §3.7).
/// </summary>
public static class WindowFit
{
    public static FitTransform Compute(int contentWidth, int contentHeight, int outputWidth, int outputHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || outputWidth <= 0 || outputHeight <= 0)
        {
            return new FitTransform(1, 0, 0, true);
        }

        // Within the even-size rounding of the start size: crop, never resample.
        if (contentWidth - outputWidth is >= 0 and <= 1 && contentHeight - outputHeight is >= 0 and <= 1)
        {
            return new FitTransform(1, 0, 0, true);
        }

        var scale = Math.Min(outputWidth / (double)contentWidth, outputHeight / (double)contentHeight);
        return new FitTransform(scale, (outputWidth - (contentWidth * scale)) / 2, (outputHeight - (contentHeight * scale)) / 2, false);
    }

    /// <summary>Normalized (0…1, may leave the range) position of a screen point in the output picture.</summary>
    public static (double X, double Y) Normalize(double screenX, double screenY, PixelRect content, int outputWidth, int outputHeight)
    {
        var fit = Compute(content.Width, content.Height, outputWidth, outputHeight);
        return (
            (fit.OffsetX + ((screenX - content.X) * fit.Scale)) / outputWidth,
            (fit.OffsetY + ((screenY - content.Y) * fit.Scale)) / outputHeight);
    }
}

/// <summary>File names of saved recordings (spec 02 §6.23).</summary>
public static class RecordingFileName
{
    /// <summary>Characters Windows refuses in file names (checked on every OS so names are portable).</summary>
    private const string InvalidCharacters = "<>:\"/\\|?*";

    /// <summary>
    /// <c>"&lt;prefix&gt; yyyy-MM-dd at HH.mm.ss.ext"</c> with a fixed invariant format
    /// (24-hour clock); on a clash <c>"… 2.ext"</c>, <c>"… 3.ext"</c> … up to 9999.
    /// </summary>
    public static string Build(string prefix, DateTime localTime, string extension, Func<string, bool> exists)
    {
        var safePrefix = string.Concat(prefix.Where(c => !char.IsControl(c) && !InvalidCharacters.Contains(c) && !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (safePrefix.Length == 0)
        {
            safePrefix = "Recording";
        }

        var stem = $"{safePrefix} {localTime.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture)}";
        var name = stem + extension;
        if (!exists(name))
        {
            return name;
        }

        for (var i = 2; i <= 9999; i++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{stem} {i}{extension}");
            if (!exists(name))
            {
                return name;
            }
        }

        return $"{stem} {Guid.NewGuid():N}{extension}";
    }
}

/// <summary>
/// Content identity of a cursor image (spec 02 §6.24): FNV-1a 64 over the
/// premultiplied BGRA bytes, then width, height, hotX, hotY as little-endian
/// float32. Handles can change while the picture stays the same, so shapes
/// are deduplicated by this hash.
/// </summary>
public static class CursorIdentity
{
    private const ulong OffsetBasis = 0xcbf29ce484222325;
    private const ulong Prime = 0x00000100000001B3;

    public static ulong Hash(ReadOnlySpan<byte> pixels, float width, float height, float hotX, float hotY)
    {
        var hash = Fnv1a(OffsetBasis, pixels);
        Span<byte> number = stackalloc byte[4];
        foreach (var value in (ReadOnlySpan<float>)[width, height, hotX, hotY])
        {
            BinaryPrimitives.WriteSingleLittleEndian(number, value);
            hash = Fnv1a(hash, number);
        }

        return hash;
    }

    public static ulong Fnv1a(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            hash ^= b;
            hash = unchecked(hash * Prime);
        }

        return hash;
    }

    public static ulong Fnv1a(ReadOnlySpan<byte> bytes) => Fnv1a(OffsetBasis, bytes);
}
