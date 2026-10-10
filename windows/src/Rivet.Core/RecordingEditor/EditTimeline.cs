// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor;

/// <summary>A stretch of the recording in source seconds.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Length => End - Start;

    public bool Contains(double t) => t >= Start && t <= End;
}

/// <summary>
/// Trim, cut-outs and the clocks of the edited video (spec 02 §6.4).
/// Source time is the recording's own clock; output time is the edited video
/// at 1×; export time is output time divided by the export speed.
/// </summary>
public sealed class EditTimeline
{
    public const double MinimumTrim = 0.2;
    public const double MinimumCut = 0.1;
    public const double MinimumOutput = 0.4;

    public EditTimeline(double duration, double trimStart, double trimEnd, IEnumerable<CutRange> cuts)
    {
        Duration = double.IsFinite(duration) && duration > 0 ? duration : 0;
        Trim = SanitizedTrim(trimStart, trimEnd, Duration);
        Cuts = NormalizeCuts(cuts, Duration);
        KeptRanges = ComputeKeptRanges(Trim, Cuts);
        OutputDuration = KeptRanges.Sum(r => r.Length);
    }

    public double Duration { get; }

    public TimeRange Trim { get; }

    public IReadOnlyList<CutRange> Cuts { get; }

    public IReadOnlyList<TimeRange> KeptRanges { get; }

    public double OutputDuration { get; }

    public static EditTimeline For(EditDocument document, double duration) =>
        new(duration, document.TrimStart, document.TrimEnd, document.Cuts);

    /// <summary>§6.4 <c>sanitizedTrim</c>: 0 as the end means "to the end"; at least 0.2 s long.</summary>
    public static TimeRange SanitizedTrim(double start, double end, double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return new TimeRange(0, 0);
        }

        var e = double.IsFinite(end) && end > 0 ? Math.Min(end, duration) : duration;
        var s = double.IsFinite(start) ? Math.Clamp(start, 0, duration) : 0;
        var span = Math.Min(MinimumTrim, duration);
        if (e - s < span)
        {
            var s2 = Math.Max(0, Math.Min(s, duration - span));
            return new TimeRange(s2, s2 + span);
        }

        return new TimeRange(s, e);
    }

    /// <summary>§6.4 <c>normalizeCuts</c>: clamped, at least 0.1 s, sorted, merged when touching.</summary>
    public static IReadOnlyList<CutRange> NormalizeCuts(IEnumerable<CutRange> cuts, double duration)
    {
        var clamped = new List<CutRange>();
        foreach (var cut in cuts)
        {
            if (!double.IsFinite(cut.Start) || !double.IsFinite(cut.End))
            {
                continue;
            }

            var s = Math.Clamp(Math.Min(cut.Start, cut.End), 0, Math.Max(0, duration));
            var e = Math.Clamp(Math.Max(cut.Start, cut.End), 0, Math.Max(0, duration));
            if (e - s >= MinimumCut)
            {
                clamped.Add(new CutRange(s, e));
            }
        }

        clamped.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<CutRange>();
        foreach (var cut in clamped)
        {
            if (merged.Count > 0 && cut.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, cut.End) };
            }
            else
            {
                merged.Add(cut);
            }
        }

        return merged;
    }

    /// <summary>§6.4 <c>keptRanges</c>.</summary>
    public static IReadOnlyList<TimeRange> ComputeKeptRanges(TimeRange trim, IReadOnlyList<CutRange> cuts)
    {
        var result = new List<TimeRange>();
        if (trim.Length <= 0)
        {
            return result;
        }

        var cursor = trim.Start;
        foreach (var cut in cuts.OrderBy(c => c.Start))
        {
            var s = Math.Max(cut.Start, trim.Start);
            var e = Math.Min(cut.End, trim.End);
            if (e <= s)
            {
                continue;
            }

            if (s > cursor)
            {
                result.Add(new TimeRange(cursor, s));
            }

            cursor = Math.Max(cursor, e);
        }

        if (trim.End > cursor)
        {
            result.Add(new TimeRange(cursor, trim.End));
        }

        result.RemoveAll(r => r.Length <= 0.001);
        return result;
    }

    /// <summary>Source time shown at output time <paramref name="output"/>.</summary>
    public double SourceTime(double output)
    {
        if (KeptRanges.Count == 0)
        {
            return Trim.Start;
        }

        var r = Math.Max(0, double.IsFinite(output) ? output : 0);
        foreach (var range in KeptRanges)
        {
            if (r <= range.Length)
            {
                return range.Start + r;
            }

            r -= range.Length;
        }

        return KeptRanges[^1].End;
    }

    /// <summary>Output time of a source moment, or null when it was cut out or trimmed away.</summary>
    public double? OutputTime(double source)
    {
        var acc = 0.0;
        foreach (var range in KeptRanges)
        {
            if (source < range.Start)
            {
                return null;
            }

            if (source <= range.End)
            {
                return acc + (source - range.Start);
            }

            acc += range.Length;
        }

        return null;
    }

    /// <summary>
    /// Output time for a seek to a source moment. A removed moment goes to the
    /// nearest kept moment (the macOS build always jumps to the start of the
    /// nearest range, which sends a scrub past the trim end back to the last
    /// range's start; Windows clamps instead, as the spec suggests).
    /// </summary>
    public double NearestOutputTime(double source)
    {
        if (OutputTime(source) is { } exact)
        {
            return exact;
        }

        if (KeptRanges.Count == 0)
        {
            return 0;
        }

        var best = 0.0;
        var bestDistance = double.MaxValue;
        var acc = 0.0;
        foreach (var range in KeptRanges)
        {
            var startDistance = Math.Abs(source - range.Start);
            if (startDistance < bestDistance)
            {
                bestDistance = startDistance;
                best = acc;
            }

            var endDistance = Math.Abs(source - range.End);
            if (endDistance < bestDistance)
            {
                bestDistance = endDistance;
                best = acc + range.Length;
            }

            acc += range.Length;
        }

        return best;
    }

    /// <summary>The cut whose range, widened by <paramref name="tolerance"/>, contains <paramref name="time"/>.</summary>
    public static int CutIndexAt(IReadOnlyList<CutRange> cuts, double time, double tolerance = 0.15)
    {
        for (var i = 0; i < cuts.Count; i++)
        {
            if (time >= cuts[i].Start - tolerance && time <= cuts[i].End + tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Output length left after removing <paramref name="cut"/> as well.</summary>
    public double OutputDurationWith(CutRange cut)
    {
        var cuts = Cuts.Append(cut);
        return ComputeKeptRanges(Trim, NormalizeCuts(cuts, Duration)).Sum(r => r.Length);
    }
}

/// <summary>Export speed (§6.4, §3.30): video, GIF and links only; the preview stays at 1×.</summary>
public static class ExportSpeed
{
    public const double Min = 0.25;
    public const double Max = 4;

    public static IReadOnlyList<double> Presets { get; } = [0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    public static double Sanitize(double v) =>
        !double.IsFinite(v) || v <= 0 ? 1 : Math.Clamp(v, Min, Max);

    /// <summary>The value the speed popover writes (hundredths).</summary>
    public static double Rounded(double v) => Sanitize(RecorderMath.Round(v * 100) / 100);

    public static double ExportTime(double outputTime, double speed) => outputTime / Sanitize(speed);

    public static double EditedTime(double exportTime, double speed) => exportTime * Sanitize(speed);

    public static double ExportDuration(double outputDuration, double speed) => outputDuration / Sanitize(speed);
}
