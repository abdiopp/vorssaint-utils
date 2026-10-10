// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;

namespace Rivet.Core.RecordingEditor;

/// <summary>Automatic zooms from clicks and typing (spec 02 §6.5).</summary>
public static class AutoZoom
{
    public const double LeadIn = 0.3;
    public const double Hold = 2.5;
    public const double MergeGap = 2.5;
    public const double IgnoreTail = 1.0;
    public const double EndMargin = 0.8;
    public const double MinimumLength = 0.9;
    public const double TypingTail = 1.4;
    public const double TypingWindow = 8.0;

    /// <summary>
    /// One zoom per click burst: presses up to 5.3 s apart become one zoom,
    /// typing only extends the zoom of the click that focused the field, the
    /// zoom starts 0.3 s before the click, and every zoom ends ≥ 0.8 s before the end.
    /// </summary>
    public static IReadOnlyList<ZoomSegment> Generate(
        IEnumerable<PointerClick> clicks, IEnumerable<double>? typingTimes, double duration, double amount)
    {
        if (!double.IsFinite(duration) || duration <= 0)
        {
            return [];
        }

        var presses = clicks.Where(c => c.IsDown && c.Time < duration - IgnoreTail && float.IsFinite(c.Time))
            .Select(c => (double)c.Time).Order().ToList();
        var merged = new List<(double Start, double End)>();
        foreach (var p in presses)
        {
            var seg = (Start: Math.Max(0.001, p - LeadIn), End: p + Hold);
            if (merged.Count > 0 && seg.Start - merged[^1].End <= MergeGap)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, seg.End));
            }
            else
            {
                merged.Add(seg);
            }
        }

        if (typingTimes is not null)
        {
            foreach (var t in typingTimes.Where(double.IsFinite).Order())
            {
                for (var i = merged.Count - 1; i >= 0; i--)
                {
                    if (merged[i].Start <= t && t <= merged[i].End + TypingWindow)
                    {
                        merged[i] = (merged[i].Start, Math.Max(merged[i].End, t + TypingTail));
                        break;
                    }
                }
            }
        }

        var limit = Math.Max(0, duration - EndMargin);
        var result = new List<ZoomSegment>();
        foreach (var (start, end) in merged)
        {
            var e = Math.Min(end, limit);
            if (e - start >= MinimumLength)
            {
                result.Add(new ZoomSegment
                {
                    Id = EditDocument.NewId(),
                    Start = start,
                    End = e,
                    Amount = ZoomSegment.SanitizeAmount(amount),
                });
            }
        }

        return result;
    }
}

/// <summary>The zoom in effect at a moment.</summary>
public readonly record struct ZoomState(double Progress, double Amount, double? FocusX, double? FocusY)
{
    public static ZoomState None { get; } = new(0, 1, null, null);

    public bool IsAimed => FocusX.HasValue && FocusY.HasValue;
}

/// <summary>Zoom ramps over time (spec 02 §6.6).</summary>
public static class ZoomRamps
{
    public const double RampIn = 0.42;
    public const double RampOut = 0.65;

    /// <summary>Segments normalized and sorted; the owner of a moment wins over a fading predecessor.</summary>
    public static ZoomState StateAt(double t, IReadOnlyList<ZoomSegment> segments)
    {
        ZoomSegment? fading = null;
        foreach (var seg in segments)
        {
            if (seg.Start - 0.001 <= t && t <= seg.End)
            {
                return State(seg, t);
            }

            if (seg.End < t && t <= seg.End + RampOut)
            {
                fading = seg;
            }
        }

        return fading is null ? ZoomState.None : State(fading, t);
    }

    public static ZoomState State(ZoomSegment seg, double t)
    {
        var rampIn = Math.Min(RampIn, seg.Length / 2);
        double p;
        if (t >= seg.End)
        {
            p = RecorderMath.Smootherstep(1 - ((t - seg.End) / RampOut));
        }
        else if (rampIn > 0 && t <= seg.Start + rampIn)
        {
            p = RecorderMath.Smootherstep((t - seg.Start) / rampIn);
        }
        else
        {
            p = 1;
        }

        return new ZoomState(p, seg.Amount, seg.FocusX, seg.FocusY);
    }
}

/// <summary>Where the camera looks while following the pointer (§6.7 step 3).</summary>
public sealed class FocusClusters
{
    private readonly double[] _times;
    private readonly (double X, double Y)[] _centres;

    private FocusClusters(double[] times, (double X, double Y)[] centres)
    {
        _times = times;
        _centres = centres;
    }

    public int Count => _times.Length;

    public static FocusClusters Empty { get; } = new([], []);

    /// <summary>
    /// Groups the drawn path (one point per source frame) into boxes no wider
    /// than 50 % and no taller than 70 % of what a <paramref name="zoomAmount"/>
    /// zoom shows; the camera aims at a box's centre from the moment it begins.
    /// </summary>
    public static FocusClusters Build(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, int fps, double zoomAmount)
    {
        if (xs.Length == 0)
        {
            return Empty;
        }

        var maxW = 0.5 / Math.Max(1, zoomAmount);
        var maxH = 0.7 / Math.Max(1, zoomAmount);
        double minX = xs[0], maxX = xs[0], minY = ys[0], maxY = ys[0];
        var started = 0.0;
        var times = new List<double>();
        var centres = new List<(double, double)>();
        for (var i = 0; i < xs.Length; i++)
        {
            var gx0 = Math.Min(minX, xs[i]);
            var gx1 = Math.Max(maxX, xs[i]);
            var gy0 = Math.Min(minY, ys[i]);
            var gy1 = Math.Max(maxY, ys[i]);
            if (gx1 - gx0 <= maxW && gy1 - gy0 <= maxH)
            {
                (minX, maxX, minY, maxY) = (gx0, gx1, gy0, gy1);
            }
            else
            {
                times.Add(started);
                centres.Add(((minX + maxX) / 2, (minY + maxY) / 2));
                (minX, maxX, minY, maxY) = (xs[i], xs[i], ys[i], ys[i]);
                started = i / (double)fps;
            }
        }

        times.Add(started);
        centres.Add(((minX + maxX) / 2, (minY + maxY) / 2));
        return new FocusClusters(times.ToArray(), centres.ToArray());
    }

    /// <summary>Centre of the last cluster that began at or before <paramref name="t"/>.</summary>
    public (double X, double Y) FocusAt(double t)
    {
        if (_times.Length == 0)
        {
            return (0.5, 0.5);
        }

        var lo = 0;
        var hi = _times.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_times[mid] <= t)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _centres[Math.Max(0, found)];
    }
}

/// <summary>Camera targets, the spring and the viewport (§6.7).</summary>
public static class CameraMotion
{
    public const double SpringK = 200;
    public const double SpringMass = 2.25;
    public const double SpringDamping = 40;
    public const double SubstepRate = 240;
    public const double EdgeBand = 0.25;

    /// <summary>Hand-aimed zoom: centre the viewport on the spot, flush at the edges.</summary>
    public static double AimedTravel(double focus, double amount)
    {
        var v = 1 / Math.Max(1, amount);
        var span = 1 - v;
        return span <= 0.0001 ? 0.5 : RecorderMath.Clamp01((focus - (v / 2)) / span);
    }

    /// <summary>Follow-pointer travel with a 0.25 edge band: calm in the middle, flush near the edges.</summary>
    public static double FollowTravel(double focus) => RecorderMath.Clamp01((focus - EdgeBand) / 0.5);

    /// <summary>
    /// A critically-damped-ish spring (ζ ≈ 0.94) over a frame sequence with
    /// fixed 240 Hz semi-implicit Euler substeps, so the result does not
    /// depend on the frame rate and motion carries across re-aims and cuts.
    /// </summary>
    public static float[] Spring(ReadOnlySpan<double> targets, int fps)
    {
        var result = new float[targets.Length];
        if (targets.Length == 0)
        {
            return result;
        }

        var frame = 1.0 / fps;
        var step = 1.0 / SubstepRate;
        var value = targets[0];
        var velocity = 0.0;
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var elapsed = 0.0;
            while (elapsed < frame)
            {
                var dt = Math.Min(step, frame - elapsed);
                var a = ((SpringK * (target - value)) - (SpringDamping * velocity)) / SpringMass;
                velocity += a * dt;
                value += velocity * dt;
                elapsed += step;
            }

            result[i] = (float)value;
        }

        return result;
    }

    /// <summary>Viewport origin and visible size (normalized) for zoom <paramref name="z"/> and travel.</summary>
    public static (double OriginX, double OriginY, double Visible) Viewport(double z, double travelX, double travelY)
    {
        var zoom = Math.Max(1, z);
        var visible = 1 / zoom;
        return (travelX * (1 - visible), travelY * (1 - visible), visible);
    }
}
