// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.MediaTools;

/// <summary>A target-size encode plan: bitrates and the output frame size.</summary>
public sealed record VideoSizePlan(int VideoBitRate, int AudioBitRate, MediaSize Size, int FrameRate);

/// <summary>A resolution-mode encode: output size and average bitrates.</summary>
public sealed record VideoLadderStep(MediaSize Size, int VideoBitRate, int AudioBitRate, bool HighestQuality);

public sealed record GifPlan(int Width, int Fps);

/// <summary>
/// The planners behind the media tools (spec 07 §6.6). Windows deviation: the
/// Microsoft AAC encoder only accepts 96/128/160/192 kbps, so the planner's
/// low audio tier is 96 kbps instead of 64 kbps.
/// </summary>
public static class MediaPlanner
{
    public const int MinimumVideoBitRate = 240_000;
    public const double TargetHeadroom = 0.94;
    public const double TargetBitsPerPixel = 0.07;
    public const int LowAudioBitRate = 96_000;
    public const int HighAudioBitRate = 128_000;
    public const int MaxVideoPasses = 3;
    public const int MaxGifPasses = 4;
    public const int MaxGifFrames = 300;
    public const int MinGifWidth = 160;
    public const int MaxGifWidth = 1600;
    public const int MinGifFps = 6;
    public const long BytesPerMegabyte = 1_000_000;

    /// <summary>Source frame rate rounded and clamped to 1–60 (30 when unknown).</summary>
    public static int FrameRate(double sourceFps) =>
        double.IsFinite(sourceFps) && sourceFps > 0 ? Math.Clamp((int)Math.Round(sourceFps), 1, 60) : 30;

    /// <summary>
    /// Bitrate plan for a target size T (bytes) and trim length d (s):
    /// A = 0 without audio, else low tier if T·8/d &lt; 1.2 Mbps, else 128 kbps;
    /// budget = ⌊T·8·0.94/d⌋ − A; V = budget·min(1, s), at least 240 kbps;
    /// affordable pixels = V/(fps·0.07); ratio = min(1, √(affordable/source pixels)),
    /// floored at min(1, 480/longest); never upscaled; even sides.
    /// </summary>
    public static VideoSizePlan? PlanVideo(long targetBytes, double duration, MediaSize source, double sourceFps, bool hasAudio, double scale = 1.0)
    {
        if (targetBytes <= 0 || !double.IsFinite(duration) || duration <= 0 || source.Width <= 0 || source.Height <= 0)
        {
            return null;
        }

        var fps = FrameRate(sourceFps);
        var audio = !hasAudio ? 0 : (targetBytes * 8.0 / duration < 1_200_000 ? LowAudioBitRate : HighAudioBitRate);
        var budget = Math.Floor(targetBytes * 8.0 * TargetHeadroom / duration) - audio;
        var video = (long)(budget * Math.Min(1.0, scale));
        if (video < MinimumVideoBitRate)
        {
            return null;
        }

        var affordable = video / (fps * TargetBitsPerPixel);
        var ratio = Math.Min(1.0, Math.Sqrt(affordable / source.Area));
        var longest = Math.Max(source.Width, source.Height);
        var floorRatio = Math.Min(1.0, 480.0 / longest);
        var newLongest = (int)Math.Round(longest * Math.Max(ratio, floorRatio));
        var size = MediaSizing.ScaleToLongest(source, newLongest);
        return new VideoSizePlan((int)Math.Min(int.MaxValue, video), audio, size, fps);
    }

    /// <summary>After a pass came out too big: next = current·(target/actual)·0.94, capped at current·0.9. 1.0 with 10/12 → ≈0.7833.</summary>
    public static double VideoRetryScale(double current, long targetBytes, long actualBytes)
    {
        if (actualBytes <= targetBytes || actualBytes <= 0)
        {
            return current;
        }

        var next = current * (targetBytes / (double)actualBytes) * TargetHeadroom;
        return Math.Min(current * 0.9, next);
    }

    /// <summary>
    /// GIF retry: drop frames before pixels. ratio = min(0.9, target·0.94/actual);
    /// fps' = max(6, round(fps·ratio)); remaining = min(1, ratio/(fps'/fps));
    /// width' = max(160, round(width·√remaining)). Null when neither shrank.
    /// 720 px at 15 fps, 12 MB vs 8 MB → 720 px at 9 fps.
    /// </summary>
    public static GifPlan? GifRetry(GifPlan current, long targetBytes, long actualBytes)
    {
        if (actualBytes <= 0 || current.Fps <= 0)
        {
            return null;
        }

        var ratio = Math.Min(0.9, targetBytes * TargetHeadroom / actualBytes);
        var fps = Math.Max(MinGifFps, (int)Math.Round(current.Fps * ratio, MidpointRounding.AwayFromZero));
        var remaining = Math.Min(1.0, ratio / (fps / (double)current.Fps));
        var width = Math.Max(MinGifWidth, (int)Math.Round(current.Width * Math.Sqrt(remaining), MidpointRounding.AwayFromZero));
        if (width >= current.Width && fps >= current.Fps)
        {
            return null;
        }

        return new GifPlan(Math.Min(width, current.Width), Math.Min(fps, current.Fps));
    }

    /// <summary>File-size mode start: width = source width clamped 160–1600; fps = max(6, min(15, ⌊300/duration⌋)).</summary>
    public static GifPlan GifStart(int sourceWidth, double duration)
    {
        var width = Math.Clamp(sourceWidth, MinGifWidth, MaxGifWidth);
        var fps = duration > 0 ? Math.Max(MinGifFps, Math.Min(15, (int)Math.Floor(MaxGifFrames / duration))) : 15;
        return new GifPlan(width, fps);
    }

    /// <summary>Frames = ⌈duration·fps⌉; must be 1–300. 25 s at 12 fps is allowed, 25.01 s is not.</summary>
    public static int GifFrameCount(double duration, int fps) => (int)Math.Ceiling(Math.Round(duration * fps, 9));

    public static bool GifFits(double duration, int fps) => GifFrameCount(duration, fps) is >= 1 and <= MaxGifFrames;

    /// <summary>"A GIF can be up to %d seconds long": max(1, ⌊300/fps⌋).</summary>
    public static int GifMaxSeconds(int fps) => Math.Max(1, MaxGifFrames / Math.Max(1, fps));

    /// <summary>Frame i is taken at min(end, start + i/fps).</summary>
    public static double GifFrameTime(int index, int fps, double start, double end) => Math.Min(end, start + (index / (double)fps));

    /// <summary>GIF frame delays are stored in 10 ms units (12 fps → 8 → 0.08 s).</summary>
    public static int GifDelayCentiseconds(int fps) => Math.Max(1, (int)Math.Round(100.0 / Math.Max(1, fps)));

    /// <summary>
    /// Resolution mode, replacing the macOS avconvert presets (which have no
    /// Windows equivalent) with an H.264 ladder: Low compression (≥ 0.82) keeps
    /// the source size at a generous bitrate; Medium picks the smallest box ≥
    /// L (L = longest side scaled to "Size", even, floored to a multiple of 16):
    /// 640×480, 960×540, 1280×720, 1920×1080, else the source; 0.4–0.58 is a
    /// medium-quality box; High compression (&lt; 0.4) is a small 480p-class output.
    /// The bitrates are a first calibration, not measured against avconvert.
    /// </summary>
    public static VideoLadderStep ResolutionStep(MediaSize source, double sourceFps, double quality, int sizeSetting, bool hasAudio)
    {
        var fps = FrameRate(sourceFps);
        var audio = hasAudio ? HighAudioBitRate : 0;
        var longest = Math.Max(source.Width, source.Height);
        if (quality >= 0.82)
        {
            var size = MediaSizing.ScaleToLongest(source, longest);
            return new VideoLadderStep(size, BitRateFor(size, fps, 0.16), audio, HighestQuality: true);
        }

        if (quality < 0.4)
        {
            var size = MediaSizing.ScaleToLongest(source, 480);
            return new VideoLadderStep(size, BitRateFor(size, fps, 0.05), hasAudio ? LowAudioBitRate : 0, HighestQuality: false);
        }

        if (quality < 0.58)
        {
            var size = MediaSizing.ScaleToLongest(source, 960);
            return new VideoLadderStep(size, BitRateFor(size, fps, 0.08), audio, HighestQuality: false);
        }

        var scaled = MediaSizing.ScaledEvenSize(source, sizeSetting);
        var l = Math.Max(16, Math.Max(scaled.Width, scaled.Height) / 16 * 16);
        var box = l switch
        {
            <= 640 => 640,
            <= 960 => 960,
            <= 1280 => 1280,
            <= 1920 => 1920,
            _ => 0,
        };
        var target = box == 0 ? MediaSizing.ScaleToLongest(source, longest) : MediaSizing.ScaleToLongest(source, box);
        return new VideoLadderStep(target, BitRateFor(target, fps, 0.1), audio, HighestQuality: box == 0);
    }

    /// <summary>bits per pixel per frame → bitrate, within 250 kbps–40 Mbps.</summary>
    public static int BitRateFor(MediaSize size, int fps, double bitsPerPixel) =>
        (int)Math.Clamp(size.Area * fps * bitsPerPixel, 250_000, 40_000_000);

    /// <summary>Estimated progress for encoders without real progress: min(0.95, elapsed / max(1 s, 0.75·trim)).</summary>
    public static double EstimatedProgress(TimeSpan elapsed, double trimSeconds) =>
        Math.Min(0.95, elapsed.TotalSeconds / Math.Max(1.0, 0.75 * trimSeconds));

    /// <summary>
    /// Trim at run time: start clamped to [0, duration]; end ≤ 0 or non-finite
    /// means the full duration, else clamped to [start, duration]. Null for a
    /// zero-length result (reported as "format not supported", like macOS).
    /// </summary>
    public static (double Start, double End)? ClampTrim(double start, double end, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return null;
        }

        var s = double.IsFinite(start) ? Math.Clamp(start, 0, duration) : 0;
        var e = !double.IsFinite(end) || end <= 0 ? duration : Math.Clamp(end, s, duration);
        return e - s > 0 ? (s, e) : null;
    }
}
