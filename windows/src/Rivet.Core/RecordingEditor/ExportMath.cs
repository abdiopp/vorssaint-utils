// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.RecordingEditor;

/// <summary>An export quality preset (spec 02 §6.3).</summary>
public sealed record QualityPreset(ExportQuality Quality, double Scale, double BitsPerPixel, int KeyFrameIntervalSeconds)
{
    public const int MinBitRate = 800_000;
    public const int MaxBitRate = 60_000_000;

    public static QualityPreset Small { get; } = new(ExportQuality.Small, 0.5, 0.05, 10);

    public static QualityPreset Balanced { get; } = new(ExportQuality.Balanced, 1, 0.06, 10);

    public static QualityPreset High { get; } = new(ExportQuality.High, 1, 0.09, 2);

    public static QualityPreset Of(ExportQuality quality) => quality switch
    {
        ExportQuality.Small => Small,
        ExportQuality.High => High,
        _ => Balanced,
    };

    /// <summary><c>round(clamp(W·H·fps·bpp, 0.8 Mb/s, 60 Mb/s))</c> — a ceiling, not an estimate.</summary>
    public int BitRate(int width, int height, int fps) =>
        (int)RecorderMath.Round(Math.Clamp((double)Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, fps) * BitsPerPixel, MinBitRate, MaxBitRate));
}

/// <summary>Frame rates, progress and the GIF budget (spec 02 §3.35, §6.16, §6.18).</summary>
public static class ExportMath
{
    public const int MaxGifFrames = 300;
    public const int AudioBitRate = 160_000;
    public const int AudioSampleRate = 48_000;

    /// <summary>The editor and export frame rate: snapped to 30 or 60 (anything else → 60).</summary>
    public static int SnapFrameRate(double fps) =>
        double.IsFinite(fps) && RecorderMath.Round(fps) == 30 ? 30 : 60;

    public static int ExpectedFrames(double exportDuration, int fps) =>
        Math.Max(1, RecorderMath.RoundToInt(exportDuration * fps));

    /// <summary>Never parks near 100 % while audio is still being written.</summary>
    public static double VideoProgress(long framesWritten, int expectedFrames) =>
        Math.Min(0.9, 0.9 * framesWritten / Math.Max(1, expectedFrames));

    public const double FinalizingProgress = 0.95;

    public static double GifProgress(int index, int frameCount) => Math.Min(0.99, (index + 1) / (double)Math.Max(1, frameCount));

    /// <summary><c>max(1, floor(D_out × fps))</c>.</summary>
    public static int GifFrameCount(double exportDuration, int gifFps) =>
        Math.Max(1, (int)Math.Floor(Math.Max(0, exportDuration) * gifFps + 1e-9));

    public static bool GifTooLong(double exportDuration, int gifFps) => GifFrameCount(exportDuration, gifFps) > MaxGifFrames;

    /// <summary>The longest GIF in whole seconds of export time: 37, 25 or 20 at 8, 12 or 15 fps.</summary>
    public static int GifMaxSeconds(int gifFps) => MaxGifFrames / Math.Max(1, gifFps);

    /// <summary>GIF size: the canvas scaled so its long edge is at most the setting (never upscaled), even.</summary>
    public static (int Width, int Height) GifSizeFor(int canvasWidth, int canvasHeight, GifSize size)
    {
        var factor = GifScale(canvasWidth, canvasHeight, size);
        return (RecorderMath.EvenSide(canvasWidth * factor), RecorderMath.EvenSide(canvasHeight * factor));
    }

    public static double GifScale(int canvasWidth, int canvasHeight, GifSize size) =>
        Math.Min(1, EditNames.LongEdge(size) / (double)Math.Max(1, Math.Max(canvasWidth, canvasHeight)));

    /// <summary>GIF delay in centiseconds (1/12 s → 8).</summary>
    public static int GifDelayCentiseconds(int gifFps) => Math.Max(2, RecorderMath.RoundToInt(100.0 / Math.Max(1, gifFps)));
}

/// <summary>File names and elapsed labels (spec 02 §6.23, §6.25).</summary>
public static class RecordingNames
{
    /// <summary><c>"&lt;prefix&gt; yyyy-MM-dd 'at' HH.mm.ss"</c> with a fixed culture and a 24-hour clock.</summary>
    public static string BaseName(string prefix, DateTime time) =>
        prefix + " " + time.ToString("yyyy-MM-dd 'at' HH.mm.ss", CultureInfo.InvariantCulture);

    /// <summary>A free path in <paramref name="folder"/>: base, then "base 2" … "base 9999".</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var candidate = Path.Combine(folder, Sanitize(baseName) + ext);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            if (n > 9999)
            {
                return Path.Combine(folder, Sanitize(baseName) + " " + Guid.NewGuid().ToString("N")[..6] + ext);
            }

            candidate = Path.Combine(folder, $"{Sanitize(baseName)} {n}{ext}");
        }

        return candidate;
    }

    /// <summary>Removes characters Windows does not allow in file names (a translated prefix might carry one).</summary>
    public static string Sanitize(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var chars = name.Select(c => invalid.Contains(c) || c < 32 ? '-' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "Recording" : result;
    }

    /// <summary><c>m:ss</c> below an hour, <c>h:mm:ss</c> above; negative → 0:00.</summary>
    public static string Elapsed(double seconds)
    {
        var total = double.IsFinite(seconds) ? Math.Max(0, (long)seconds) : 0;
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }

    /// <summary>The speed as the button shows it: up to two decimals in the user's locale, then "×".</summary>
    public static string Speed(double speed, CultureInfo culture) =>
        ExportSpeed.Sanitize(speed).ToString("0.##", culture) + "×";
}

/// <summary>Lane slotting, moving, resizing and snapping (spec 02 §6.21, §3.21).</summary>
public static class LaneMath
{
    public const double DefaultZoomLength = 2.0;
    public const double MinimumLength = 0.4;
    public const double SnapPixels = 8;

    /// <summary>Room for a new zoom at <paramref name="t"/>: 2 s, starting after a zoom it lands in, ending at the next one.</summary>
    public static TimeRange? SlotForNewZoom(double t, IEnumerable<ZoomSegment> existing, double duration)
    {
        if (duration <= MinimumLength)
        {
            return null;
        }

        var start = Math.Clamp(t, 0, duration - MinimumLength);
        var limit = duration;
        foreach (var seg in existing.OrderBy(s => s.Start))
        {
            if (seg.End <= start)
            {
                continue;
            }

            if (seg.Start <= start)
            {
                start = seg.End;
                continue;
            }

            limit = seg.Start;
            break;
        }

        if (limit - start < MinimumLength)
        {
            return null;
        }

        return new TimeRange(start, Math.Min(limit, start + DefaultZoomLength));
    }

    /// <summary>Neighbour limits for a zoom at its current position (a drag stops at a neighbour).</summary>
    public static (double Lower, double Upper) ZoomBounds(ZoomSegment zoom, IEnumerable<ZoomSegment> all, double duration)
    {
        var others = all.Where(z => z.Id != zoom.Id).ToList();
        var lower = others.Where(z => z.End <= zoom.Start + 1e-9).Select(z => z.End).DefaultIfEmpty(0).Max();
        var upper = others.Where(z => z.Start >= zoom.End - 1e-9).Select(z => z.Start).DefaultIfEmpty(duration).Min();
        return (lower, upper);
    }

    public static TimeRange MoveZoom(ZoomSegment zoom, double proposedStart, IEnumerable<ZoomSegment> all, double duration)
    {
        var (lower, upper) = ZoomBounds(zoom, all, duration);
        var length = zoom.Length;
        var start = Math.Max(lower, Math.Min(proposedStart, upper - length));
        return new TimeRange(start, start + length);
    }

    public static TimeRange ResizeZoom(ZoomSegment zoom, bool startEdge, double t, IEnumerable<ZoomSegment> all, double duration)
    {
        var (lower, upper) = ZoomBounds(zoom, all, duration);
        return startEdge
            ? new TimeRange(Math.Max(lower, Math.Min(t, zoom.End - MinimumLength)), zoom.End)
            : new TimeRange(zoom.Start, Math.Min(upper, Math.Max(t, zoom.Start + MinimumLength)));
    }

    /// <summary>Text, image and blur blocks may overlap: keep the length, stay inside the recording.</summary>
    public static TimeRange MoveFree(double start, double end, double proposedStart, double duration)
    {
        var length = end - start;
        var s = Math.Clamp(proposedStart, 0, Math.Max(0, duration - length));
        return new TimeRange(s, s + length);
    }

    public static TimeRange ResizeFree(double start, double end, bool startEdge, double t, double duration) =>
        startEdge
            ? new TimeRange(Math.Clamp(t, 0, Math.Max(0, end - MinimumLength)), end)
            : new TimeRange(start, Math.Clamp(t, Math.Min(duration, start + MinimumLength), duration));

    /// <summary>Nearest candidate within 8 px (converted to time) wins; otherwise <paramref name="t"/>.</summary>
    public static double Snap(double t, IEnumerable<double> candidates, double laneWidth, double duration)
    {
        if (laneWidth <= 0 || duration <= 0)
        {
            return t;
        }

        var tolerance = SnapPixels / laneWidth * duration;
        var best = t;
        var bestDistance = double.MaxValue;
        foreach (var c in candidates)
        {
            var d = Math.Abs(c - t);
            if (d <= tolerance && d < bestDistance)
            {
                best = c;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>A new caption, image or blur at <paramref name="t"/>: start clamped to leave 0.4 s.</summary>
    public static double NewItemStart(double t, double duration) => Math.Clamp(t, 0, Math.Max(0, duration - MinimumLength));
}
