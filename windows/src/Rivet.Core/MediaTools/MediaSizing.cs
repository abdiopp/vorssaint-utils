// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.MediaTools;

public enum ImageResizeKind
{
    None,
    MaxDimension,
    Width,
    Height,
    Exact,
}

public enum ExactResizeMode
{
    Stretch,
    Fit,
    Fill,
}

public sealed record ImageResize(ImageResizeKind Kind, int MaxDimension = 1600, int Width = 1600, int Height = 1200, ExactResizeMode ExactMode = ExactResizeMode.Stretch);

public readonly record struct MediaSize(int Width, int Height)
{
    public long Area => (long)Width * Height;

    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>Size arithmetic for video, GIF and image outputs (spec 07 §6.6).</summary>
public static class MediaSizing
{
    public const int MaxRenderDimension = 20_000;
    public const long MaxRenderPixels = 67_108_864; // 8192²

    /// <summary>scale = min(1, max(2, M)/longest); each side rounded, made even, at least 2. 1920×1080 at 1000 → 1000×562.</summary>
    public static MediaSize ScaledEvenSize(MediaSize source, int maxDimension)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= 0)
        {
            return new MediaSize(2, 2);
        }

        var scale = Math.Min(1.0, Math.Max(2, maxDimension) / (double)longest);
        return new MediaSize(Even(source.Width * scale), Even(source.Height * scale));
    }

    /// <summary>Then floors each side to a multiple of 16 (at least 16). 320×180 at 180 → 176×96.</summary>
    public static MediaSize ScaledVideoSize(MediaSize source, int maxDimension)
    {
        var even = ScaledEvenSize(source, maxDimension);
        return new MediaSize(Math.Max(16, even.Width / 16 * 16), Math.Max(16, even.Height / 16 * 16));
    }

    /// <summary>Scales to a new longest edge (never up); even sides, minimum 2.</summary>
    public static MediaSize ScaleToLongest(MediaSize source, int longest)
    {
        var current = Math.Max(source.Width, source.Height);
        if (current <= 0)
        {
            return new MediaSize(2, 2);
        }

        var scale = Math.Min(1.0, longest / (double)current);
        return new MediaSize(Even(source.Width * scale), Even(source.Height * scale));
    }

    /// <summary>Rounds (half away from zero), then drops to the even number below if odd; at least 2. 1419.05 → 1418, 562.5 → 562.</summary>
    public static int Even(double value)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Max(2, rounded & ~1);
    }

    /// <summary>Each side 1–20,000 and the area at most 8192².</summary>
    public static bool IsSafe(MediaSize size) =>
        size.Width is >= 1 and <= MaxRenderDimension && size.Height is >= 1 and <= MaxRenderDimension && size.Area <= MaxRenderPixels;

    /// <summary>EXIF orientations 5–8 swap width and height.</summary>
    public static MediaSize Oriented(MediaSize stored, int exifOrientation) =>
        exifOrientation is >= 5 and <= 8 ? new MediaSize(stored.Height, stored.Width) : stored;

    /// <summary>
    /// The output size for an image: sizes round to the nearest integer (min 1),
    /// not forced even. Width/Height/Custom can upscale; Max side never does.
    /// </summary>
    public static MediaSize TargetSize(MediaSize source, ImageResize resize)
    {
        double w = source.Width, h = source.Height;
        if (w <= 0 || h <= 0)
        {
            return new MediaSize(1, 1);
        }

        return resize.Kind switch
        {
            ImageResizeKind.MaxDimension => Scale(Math.Min(1.0, Math.Max(1, resize.MaxDimension) / Math.Max(w, h))),
            ImageResizeKind.Width => new MediaSize(Math.Max(1, resize.Width), Round(h * resize.Width / w)),
            ImageResizeKind.Height => new MediaSize(Round(w * resize.Height / h), Math.Max(1, resize.Height)),
            ImageResizeKind.Exact => new MediaSize(Math.Max(1, resize.Width), Math.Max(1, resize.Height)),
            _ => source,
        };

        MediaSize Scale(double factor) => new(Round(w * factor), Round(h * factor));
    }

    /// <summary>
    /// The largest side to decode at (downsampled decoding). Non-custom modes:
    /// ⌈max(target)⌉. Custom: k = min(sx, sy) for Fit, max(sx, sy) for Fill and
    /// Stretch; scale = min(1, k); ⌈longest source side · scale⌉.
    /// Custom Fit on 1000×100 → 100×100 decodes 100; Fill/Stretch decode 1000.
    /// </summary>
    public static int DecodeMaxPixel(MediaSize source, ImageResize resize)
    {
        var target = TargetSize(source, resize);
        if (resize.Kind != ImageResizeKind.Exact)
        {
            return Math.Max(target.Width, target.Height);
        }

        var sx = target.Width / (double)source.Width;
        var sy = target.Height / (double)source.Height;
        var k = resize.ExactMode == ExactResizeMode.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        var scale = Math.Min(1, Math.Max(0, k));
        return (int)Math.Ceiling(Math.Max(source.Width, source.Height) * scale);
    }

    /// <summary>Where the source is drawn on a target canvas (Fit letterboxes, Fill crops, everything else stretches).</summary>
    public static (double X, double Y, double Width, double Height) DrawRect(MediaSize source, MediaSize target, ImageResize resize)
    {
        if (resize.Kind != ImageResizeKind.Exact || resize.ExactMode == ExactResizeMode.Stretch)
        {
            return (0, 0, target.Width, target.Height);
        }

        var sx = target.Width / (double)source.Width;
        var sy = target.Height / (double)source.Height;
        var k = resize.ExactMode == ExactResizeMode.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        var w = source.Width * k;
        var h = source.Height * k;
        return ((target.Width - w) / 2, (target.Height - h) / 2, w, h);
    }

    private static int Round(double value) => Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
}
