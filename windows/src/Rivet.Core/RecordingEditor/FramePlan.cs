// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;

namespace Rivet.Core.RecordingEditor;

/// <summary>
/// How cursor bitmaps map onto video pixels (spec 02 §6.11). Pointer track v4
/// (Windows) stores bitmaps in physical pixels of the recorded monitor, so they
/// draw 1:1; v3 (macOS) stores points, scaled by the display and accessibility
/// scales. The fallback arrow is 28 points wide.
/// </summary>
public sealed record PointerMetrics(double ShapeScale, double ArrowScale, float FirstShapeWidth)
{
    public const double FallbackWidth = 28;
    public const double FallbackHeight = 40;
    public const double FallbackHotX = 4.5;
    public const double FallbackHotY = 4.0;

    public static PointerMetrics For(PointerTrack track, int version)
    {
        var system = Math.Max(1, track.SystemScale);
        var display = track.DisplayScale > 0 ? track.DisplayScale : 1;
        var first = track.Shapes.Count > 0 ? track.Shapes[0].Width : 0;
        return version >= 4
            ? new PointerMetrics(1, display, first)
            : new PointerMetrics(display * system, display * system, first);
    }

    /// <summary>The click ring's scale reference: the first cursor's width (or the arrow's) times the size.</summary>
    public double PointerPixelSize(double pointerSize) =>
        (FirstShapeWidth > 0 ? FirstShapeWidth * ShapeScale : FallbackWidth * ArrowScale) * pointerSize;
}

/// <summary>What the compositor draws for one frame.</summary>
public readonly record struct FrameState
{
    public int PlanIndex { get; init; }

    /// <summary>Source time of the picture (exact, from the edited time).</summary>
    public double SourceTime { get; init; }

    public double Zoom { get; init; }

    public double TravelX { get; init; }

    public double TravelY { get; init; }

    public bool PointerVisible { get; init; }

    /// <summary>Normalized pointer position (top-left origin).</summary>
    public double PointerX { get; init; }

    public double PointerY { get; init; }

    public int ShapeIndex { get; init; }

    public double PointerOpacity { get; init; }

    public double PressScale { get; init; }

    /// <summary>Click ring progress 0…1, or null for no ring.</summary>
    public double? RingProgress { get; init; }
}

/// <summary>
/// Everything that varies per output frame, precomputed once per document
/// change, frame rate and output scale (spec 02 §6.13): source times, the
/// sprung camera, the drawn pointer, click effects, and the canvas layout.
/// The same plan drives the preview (editor frame rate, 1×) and the export
/// (export frame rate; frames are looked up at <c>round(t · fps)</c> on the
/// unscaled edited clock, <c>t = T · speed</c>).
/// </summary>
public sealed class FramePlan
{
    private readonly ZoomSegment[] _segments;

    private FramePlan(
        EditDocument document, double duration, int fps, EditTimeline timeline, CanvasLayout layout, PointerPath path,
        PointerMetrics metrics, double[] sourceTimes, float[] zoom, float[] travelX, float[] travelY,
        float[] pressScale, float[] ring, ZoomSegment[] segments)
    {
        Document = document;
        Duration = duration;
        Fps = fps;
        Timeline = timeline;
        Layout = layout;
        Path = path;
        Metrics = metrics;
        SourceTimes = sourceTimes;
        Zoom = zoom;
        TravelX = travelX;
        TravelY = travelY;
        PressScaleValues = pressScale;
        RingValues = ring;
        _segments = segments;
    }

    public EditDocument Document { get; }

    public double Duration { get; }

    public int Fps { get; }

    public EditTimeline Timeline { get; }

    public CanvasLayout Layout { get; }

    public PointerPath Path { get; }

    public PointerMetrics Metrics { get; }

    public double[] SourceTimes { get; }

    public float[] Zoom { get; }

    public float[] TravelX { get; }

    public float[] TravelY { get; }

    public float[] PressScaleValues { get; }

    /// <summary>Ring progress per frame; NaN for no ring.</summary>
    public float[] RingValues { get; }

    public int FrameCount => SourceTimes.Length;

    public bool DrawsPointer => Document.ShowsPointer && Path.HasSamples;

    public bool DrawsRing => DrawsPointer && Document.ShowsClickRing;

    public IReadOnlyList<ZoomSegment> ActiveSegments => _segments;

    /// <summary>
    /// §6.13: a compositor is needed for a drawn pointer, active zooms, a
    /// canvas that differs from the source, cuts, captions, images or blurs.
    /// (This port always composes; the flag documents the macOS rule and the
    /// privacy invariant: a document that needs composition never exports bare.)
    /// </summary>
    public bool NeedsCompositor =>
        DrawsPointer || _segments.Length > 0 || Layout.NeedsPlate || Document.Cuts.Count > 0
        || Document.Texts.Count > 0 || Document.Images.Count > 0 || Document.Blurs.Count > 0;

    public static FramePlan Build(
        EditDocument document, double duration, int sourceWidth, int sourceHeight, int fps,
        PointerTrack pointer, int pointerVersion, double outputScale)
    {
        fps = Math.Max(1, fps);
        var doc = document.Sanitized(duration);
        var timeline = EditTimeline.For(doc, duration);
        var layout = CanvasLayout.Compute(sourceWidth, sourceHeight, doc.BackdropStyle, doc.Aspect, outputScale);
        var path = PointerPath.Build(pointer, duration, fps, doc.PointerSmoothing, doc.ShowsPointer);
        var clusters = pointer.IsEmpty ? FocusClusters.Empty : FocusClusters.Build(path.X, path.Y, fps, doc.ZoomAmount);
        var segments = doc.ZoomEnabled ? doc.ZoomSegments.ToArray() : [];

        var n = Math.Max(1, RecorderMath.RoundToInt(timeline.OutputDuration * fps) + 1);
        var sourceTimes = new double[n];
        var targetZoom = new double[n];
        var targetX = new double[n];
        var targetY = new double[n];
        var press = new float[n];
        var ring = new float[n];
        var clicks = new ClickTimeline(pointer.Clicks).CreateCursor();
        for (var i = 0; i < n; i++)
        {
            var s = timeline.SourceTime(i / (double)fps);
            sourceTimes[i] = s;
            var state = ZoomRamps.StateAt(s, segments);
            targetZoom[i] = 1 + ((state.Amount - 1) * state.Progress);
            if (state.IsAimed)
            {
                targetX[i] = CameraMotion.AimedTravel(state.FocusX!.Value, state.Amount);
                targetY[i] = CameraMotion.AimedTravel(state.FocusY!.Value, state.Amount);
            }
            else
            {
                var (fx, fy) = clusters.FocusAt(s);
                targetX[i] = CameraMotion.FollowTravel(fx);
                targetY[i] = CameraMotion.FollowTravel(fy);
            }

            press[i] = (float)clicks.PressScale(s);
            ring[i] = clicks.RingProgress(s) is { } p ? (float)p : float.NaN;
        }

        var zoom = CameraMotion.Spring(targetZoom, fps);
        for (var i = 0; i < zoom.Length; i++)
        {
            zoom[i] = Math.Max(1, zoom[i]);
        }

        return new FramePlan(
            doc, duration, fps, timeline, layout, path, PointerMetrics.For(pointer, pointerVersion), sourceTimes, zoom,
            CameraMotion.Spring(targetX, fps), CameraMotion.Spring(targetY, fps), press, ring, segments);
    }

    /// <summary><c>clamp(round(t · fps), 0, n − 1)</c>.</summary>
    public int IndexFor(double editedTime) => Math.Clamp(RecorderMath.RoundToInt(editedTime * Fps), 0, FrameCount - 1);

    /// <summary>The frame at an edited (output, 1×) time.</summary>
    public FrameState StateAt(double editedTime)
    {
        var i = IndexFor(editedTime);
        var s = Timeline.SourceTime(editedTime);
        var planSource = SourceTimes[i];
        var j = Path.IndexOf(planSource);
        var x = Path.X[j];
        var y = Path.Y[j];
        var opacity = Path.Opacity[j];
        var visible = DrawsPointer && Path.Visible[j] && opacity > 0.01 && x > -0.2 && x < 1.2 && y > -0.2 && y < 1.2;
        var ring = RingValues[i];
        return new FrameState
        {
            PlanIndex = i,
            SourceTime = s,
            Zoom = Zoom[i],
            TravelX = TravelX[i],
            TravelY = TravelY[i],
            PointerVisible = visible,
            PointerX = x,
            PointerY = y,
            ShapeIndex = Path.Shape[j],
            PointerOpacity = opacity,
            PressScale = PressScaleValues[i],
            RingProgress = DrawsRing && visible && !float.IsNaN(ring) ? ring : null,
        };
    }

    /// <summary>
    /// A blur applies when it covers the plan frame on either side of the
    /// edited time, so retimed frames and frames held across a cut seam never
    /// uncover what it hides (§6.14).
    /// </summary>
    public bool BlurActive(BlurRegion blur, double editedTime)
    {
        var f = Math.Clamp((int)Math.Floor((editedTime * Fps) + 1e-9), 0, FrameCount - 1);
        var c = Math.Clamp((int)Math.Ceiling((editedTime * Fps) - 1e-9), 0, FrameCount - 1);
        return BlurMath.Covers(blur, SourceTimes[f]) || BlurMath.Covers(blur, SourceTimes[c])
               || BlurMath.Covers(blur, Timeline.SourceTime(editedTime));
    }

    public double TextOpacity(TextOverlay text, int planIndex) =>
        OverlayLayout.Opacity(text.Start, text.End, SourceTimes[planIndex]);

    public double ImageOpacity(ImageOverlay image, int planIndex) =>
        image.Opacity * OverlayLayout.Opacity(image.Start, image.End, SourceTimes[planIndex]);

    /// <summary>The largest zoom the plan reaches (for choosing a sharp enough preview decode size).</summary>
    public double MaxZoom => Zoom.Length == 0 ? 1 : Zoom.Max();
}
