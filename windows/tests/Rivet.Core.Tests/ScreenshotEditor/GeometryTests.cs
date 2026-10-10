// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Xunit;

namespace Rivet.Core.Tests.ScreenshotEditor;

public class ArrowGeometryTests
{
    private static readonly ImgPoint Tail = new(0, 0);
    private static readonly ImgPoint Tip = new(100, 0);

    [Fact]
    public void Head_length_follows_the_formula()
    {
        // L = min(max(10, 3.4w), max(0.72·dist, min(1.2w, dist)))
        Assert.Equal(13.6, ArrowGeometry.HeadLength(4, 100), 9);
        Assert.Equal(10, ArrowGeometry.HeadLength(2, 100), 9);
        Assert.Equal(23.8, ArrowGeometry.HeadLength(7, 400), 9);
        Assert.Equal(4.8, ArrowGeometry.HeadLength(4, 5), 9);      // short: min(1.2w, dist)
        Assert.Equal(3, ArrowGeometry.HeadLength(4, 3), 9);        // shorter than 1.2w: the distance itself
    }

    [Fact]
    public void Head_points_open_behind_the_tip()
    {
        var head = ArrowGeometry.Head(Tail, Tip, 4);
        Assert.Equal(87.7468233965271, head.Left.X, 9);
        Assert.Equal(5.90081885199879, head.Left.Y, 9);
        Assert.Equal(87.7468233965271, head.Right.X, 9);
        Assert.Equal(-5.90081885199879, head.Right.Y, 9);
        Assert.Equal(87.7468233965271, head.Base.X, 9);
        Assert.Equal(0, head.Base.Y, 9);
        Assert.True(head.Left.X < Tip.X && head.Right.X < Tip.X);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    public void A_short_arrows_head_never_passes_its_tail(double length)
    {
        foreach (var w in new[] { 2.0, 4, 7, 14 })
        {
            var head = ArrowGeometry.Head(Tail, new ImgPoint(length, 0), w);
            Assert.True(head.Base.X >= -1e-9, $"base {head.Base.X} for length {length}, w {w}");
            Assert.True(head.Left.X >= -1e-9 && head.Right.X >= -1e-9);
        }
    }

    [Fact]
    public void Solid_arrow_is_one_closed_contour_with_a_round_tail()
    {
        var path = ArrowGeometry.Solid(Tail, Tip, 4);
        var verbs = path.Commands.Select(c => c.Verb).ToArray();
        Assert.Equal(
            [PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Arc, PathVerb.Close],
            verbs);
        var arc = path.Commands[7];
        Assert.Equal(2, arc.Radius, 9);
        Assert.Equal(-Math.PI / 2, arc.StartAngle, 9);
        Assert.Equal(-Math.PI, arc.Sweep, 9);   // passes behind the tail (through angle π)
        Assert.Equal(new ImgPoint(0, 2), path.Commands[0].Point);
        Assert.Equal(Tip, path.Commands[3].Point);

        // The arc's midpoint lies behind the tail, so the cap has no notch.
        var mid = arc.StartAngle + (arc.Sweep / 2);
        Assert.Equal(-2, arc.Point.X + (arc.Radius * Math.Cos(mid)), 9);
    }

    [Fact]
    public void Stroked_styles_draw_shaft_and_heads_as_one_path()
    {
        Assert.Equal(2, ArrowGeometry.Outline(Tail, Tip, 4).Commands.Count(c => c.Verb == PathVerb.Move));
        Assert.Equal(1, ArrowGeometry.Outline(Tail, Tip, 4).Commands.Count(c => c.Verb == PathVerb.Close));
        Assert.Equal(2, ArrowGeometry.Open(Tail, Tip, 4).Commands.Count(c => c.Verb == PathVerb.Move));
        var both = ArrowGeometry.DoubleEnded(Tail, Tip, 4);
        Assert.Equal(3, both.Commands.Count(c => c.Verb == PathVerb.Move));
        // The second head points back at the tail.
        Assert.Contains(both.Commands, c => c.Verb == PathVerb.Line && c.Point == Tail);
    }

    [Fact]
    public void Five_styles_and_unknown_ids_fall_back_to_solid()
    {
        Assert.Equal(5, ArrowStyles.All.Count);
        Assert.Equal(ArrowStyle.Filled, ArrowStyles.Parse("nonsense"));
        Assert.Equal(ArrowStyle.DoubleEnded, ArrowStyles.Parse("doubleEnded"));
        foreach (var style in ArrowStyles.All)
        {
            Assert.Equal(style, ArrowStyles.Parse(ArrowStyles.Id(style)));
            Assert.False(ArrowGeometry.Path(style, Tail, Tip, 4, 7).IsEmpty);
        }
    }

    [Fact]
    public void Scribble_generator_matches_the_reference_sequence()
    {
        var rng = new ScribbleRandom(1);
        Assert.Equal(-0.6896001215851183, rng.Next(), 12);
        Assert.Equal(0.2781034054210427, rng.Next(), 12);
        Assert.Equal(0.7485286516276712, rng.Next(), 12);

        var zero = new ScribbleRandom(0);
        Assert.Equal(0.18532935953476226, zero.Next(), 12);

        var menu = new ScribbleRandom(ArrowGeometry.MenuSampleSeed);
        Assert.Equal(0.09145471555751494, menu.Next(), 12);
        Assert.Equal(-0.4783036446201785, menu.Next(), 12);
    }

    [Fact]
    public void Scribbly_arrows_are_stable_per_seed_and_differ_between_seeds()
    {
        var a = ArrowGeometry.ScribblyPolylines(Tail, new ImgPoint(300, 120), 4, 42);
        var b = ArrowGeometry.ScribblyPolylines(Tail, new ImgPoint(300, 120), 4, 42);
        var c = ArrowGeometry.ScribblyPolylines(Tail, new ImgPoint(300, 120), 4, 43);
        Assert.Equal(a.Shaft, b.Shaft);
        Assert.Equal(a.LeftWing, b.LeftWing);
        Assert.NotEqual(a.Shaft, c.Shaft);

        // Ends are exact; the shaft has clamp(ceil(dist / max(10, 3w)), 4, 24) segments.
        Assert.Equal(Tail, a.Shaft[0]);
        var dist = Math.Sqrt((300 * 300) + (120 * 120));
        Assert.Equal(Math.Clamp((int)Math.Ceiling(dist / 12), 4, 24) + 1, a.Shaft.Count);
        Assert.Equal(4, a.LeftWing.Count);
        Assert.Equal(new ImgPoint(300, 120), a.LeftWing[^1]);
    }
}

public class PenGeometryTests
{
    [Fact]
    public void Smooths_through_midpoints_and_ends_on_the_last_sample()
    {
        var points = new[] { new ImgPoint(0, 0), new ImgPoint(10, 0), new ImgPoint(10, 10) };
        var path = PenGeometry.Smooth(points);
        var c = path.Commands;
        Assert.Equal(PathVerb.Move, c[0].Verb);
        Assert.Equal(new ImgPoint(5, 0), c[1].Point);
        Assert.Equal(new ImgPoint(0, 0), c[1].Control);
        Assert.Equal(new ImgPoint(10, 5), c[2].Point);
        Assert.Equal(new ImgPoint(10, 0), c[2].Control);
        Assert.Equal(PathVerb.Line, c[3].Verb);
        Assert.Equal(new ImgPoint(10, 10), c[3].Point);
    }

    [Fact]
    public void A_single_sample_draws_nothing() => Assert.True(PenGeometry.Smooth([new ImgPoint(1, 1)]).IsEmpty);
}

public class HitTestingTests
{
    private const int W = 1000;
    private const int H = 800;

    [Fact]
    public void Segment_distance_projects_and_clamps()
    {
        Assert.Equal(5, HitTesting.DistanceToSegment(new ImgPoint(5, 5), new ImgPoint(0, 0), new ImgPoint(10, 0)), 9);
        Assert.Equal(5, HitTesting.DistanceToSegment(new ImgPoint(-3, 4), new ImgPoint(0, 0), new ImgPoint(10, 0)), 9);
        Assert.Equal(5, HitTesting.DistanceToSegment(new ImgPoint(3, 4), new ImgPoint(0, 0), new ImgPoint(0, 0)), 9);
    }

    [Fact]
    public void Arrows_hit_within_tolerance_plus_half_the_stroke()
    {
        var arrow = new Annotation { Kind = AnnotationKind.Arrow, Start = new(0, 0), End = new(100, 0), Stroke = StrokeWidth.Medium };
        // scale 2: tol = 20, + 4·2/2 = 24
        Assert.True(HitTesting.Hits(arrow, new ImgPoint(50, 24), 2, W, H));
        Assert.False(HitTesting.Hits(arrow, new ImgPoint(50, 24.5), 2, W, H));
    }

    [Fact]
    public void Area_shapes_count_only_their_edge_ring_for_creation_taps()
    {
        var rect = new Annotation { Kind = AnnotationKind.Rect, Rect = new ImgRect(100, 100, 200, 200) };
        var centre = new ImgPoint(200, 200);
        Assert.True(HitTesting.Hits(rect, centre, 1, W, H));
        Assert.False(HitTesting.Hits(rect, centre, 1, W, H, creationTap: true));
        Assert.True(HitTesting.Hits(rect, new ImgPoint(104, 200), 1, W, H, creationTap: true));
        Assert.True(HitTesting.Hits(rect, new ImgPoint(96, 200), 1, W, H, creationTap: true));   // grown by tol/2 = 5
        Assert.False(HitTesting.Hits(rect, new ImgPoint(94, 200), 1, W, H, creationTap: true));

        // A rect too small to have an interior counts entirely.
        var tiny = rect with { Rect = new ImgRect(100, 100, 15, 15) };
        Assert.True(HitTesting.Hits(tiny, new ImgPoint(107, 107), 1, W, H, creationTap: true));

        // Text and stickers are solid even for creation taps.
        var text = new Annotation { Kind = AnnotationKind.Text, Rect = new ImgRect(100, 100, 200, 40), Text = "Hi" };
        Assert.True(HitTesting.Hits(text, new ImgPoint(200, 120), 1, W, H, creationTap: true));
    }

    [Fact]
    public void Counters_hit_within_their_radius_plus_four_points()
    {
        var counter = new Annotation { Kind = AnnotationKind.Counter, Rect = new ImgRect(500, 400, 0, 0) };
        var d = AnnotationMetrics.CounterDiameter(W, H);  // max(22, 800/24) = 33.33
        Assert.Equal(800 / 24.0, d, 9);
        Assert.True(HitTesting.Hits(counter, new ImgPoint(500 + (d / 2) + 3.9, 400), 1, W, H));
        Assert.False(HitTesting.Hits(counter, new ImgPoint(500 + (d / 2) + 4.1, 400), 1, W, H));
    }

    [Fact]
    public void Pens_hit_near_any_sample()
    {
        var pen = new Annotation { Kind = AnnotationKind.Freehand, Points = [new(0, 0), new(50, 50)] };
        Assert.True(HitTesting.Hits(pen, new ImgPoint(57, 50), 1, W, H));
        Assert.False(HitTesting.Hits(pen, new ImgPoint(25, 40), 1, W, H));
    }

    [Fact]
    public void Topmost_annotation_wins()
    {
        var below = new Annotation { Kind = AnnotationKind.Redact, Rect = new ImgRect(0, 0, 100, 100) };
        var above = new Annotation { Kind = AnnotationKind.Redact, Rect = new ImgRect(50, 50, 100, 100) };
        Assert.Same(above, HitTesting.TopmostAt([below, above], new ImgPoint(75, 75), 1, W, H));
    }

    [Fact]
    public void Handles_are_found_in_spec_order_and_resizing_flips()
    {
        var r = new ImgRect(10, 10, 100, 50);
        Assert.Equal(ResizeHandle.TopLeft, HitTesting.HandleAt(r, new ImgPoint(12, 8), 12));
        Assert.Equal(ResizeHandle.Right, HitTesting.HandleAt(r, new ImgPoint(108, 35), 12));
        Assert.Null(HitTesting.HandleAt(r, new ImgPoint(60, 35), 12));
        var flipped = HitTesting.Resize(r, ResizeHandle.Right, new ImgPoint(0, 0));
        Assert.Equal(new ImgRect(0, 10, 10, 50), flipped);
        Assert.Equal(new ImgRect(10, 10, 100, 70), HitTesting.Resize(r, ResizeHandle.Bottom, new ImgPoint(999, 80)));
    }

    [Fact]
    public void Endpoints_are_hit_below_twelve_points()
    {
        var line = new Annotation { Kind = AnnotationKind.Line, Start = new(0, 0), End = new(100, 0) };
        Assert.Equal(0, HitTesting.EndpointAt(line, new ImgPoint(11, 0), 1));
        Assert.Equal(1, HitTesting.EndpointAt(line, new ImgPoint(95, 5), 1));
        Assert.Null(HitTesting.EndpointAt(line, new ImgPoint(12, 0), 1));
    }
}

public class CropMathTests
{
    private static readonly ImgRect Image = new(0, 0, 400, 300);

    [Fact]
    public void Edges_round_to_the_nearest_pixel_not_outward()
    {
        Assert.Equal(new ImgRect(10, 20, 100, 51), CropMath.Snap(new ImgRect(10.4, 19.6, 99.9, 51.2), Image));
        Assert.Equal(new ImgRect(11, 21, 100, 50), CropMath.Snap(new ImgRect(10.5, 20.5, 100, 50), Image));
        Assert.Equal(new ImgRect(0, 0, 400, 300), CropMath.Snap(new ImgRect(-5, -5, 500, 500), Image));
    }

    [Fact]
    public void Moving_never_changes_the_size()
    {
        var draft = new ImgRect(50, 50, 100, 80);
        var moved = CropMath.Move(draft, 1000, -1000, Image);
        Assert.Equal(new ImgRect(300, 0, 100, 80), moved);
    }

    [Fact]
    public void A_drag_inside_a_full_image_draft_starts_a_new_selection()
    {
        Assert.True(CropMath.StartsNewSelection(Image, Image, new ImgPoint(200, 150)));
        var draft = new ImgRect(50, 50, 100, 80);
        Assert.False(CropMath.StartsNewSelection(draft, Image, new ImgPoint(60, 60)));
        Assert.True(CropMath.StartsNewSelection(draft, Image, new ImgPoint(300, 200)));
        Assert.False(CropMath.StartsNewSelection(draft, Image, new ImgPoint(-1, 10)));
    }

    [Fact]
    public void The_crop_loupe_centres_on_the_edge_with_an_even_side()
    {
        var (x, y, w, h) = CropMath.LoupeSample(new ImgPoint(100, 50), 400, 300);
        Assert.Equal((93, 43, 14, 14), (x, y, w, h));
        var odd = CropMath.LoupeSample(new ImgPoint(100, 50), 400, 300, 13);
        Assert.Equal(14, odd.Width);
        Assert.Equal((0, 0), (CropMath.LoupeSample(new ImgPoint(2, 2), 400, 300).X, CropMath.LoupeSample(new ImgPoint(2, 2), 400, 300).Y));
    }
}

public class LayoutMathTests
{
    [Fact]
    public void Window_limits_follow_the_visible_frame()
    {
        var (minW, minH, maxW, maxH) = EditorLayoutMath.WindowLimits(1920, 1040);
        Assert.Equal(980, minW, 9);     // min(980, max(760, 1459))
        Assert.Equal(680, minH, 9);     // min(680, max(560, 748.8))
        Assert.Equal(1728, maxW, 9);
        Assert.Equal(915.2, maxH, 9);

        var small = EditorLayoutMath.WindowLimits(700, 500);
        Assert.Equal(700, small.MinW, 9);   // never larger than the display
        Assert.Equal(500, small.MinH, 9);
    }

    [Fact]
    public void Initial_size_fits_the_image_plus_chrome()
    {
        // A small capture gets the minimum.
        Assert.Equal((980.0, 680.0), EditorLayoutMath.InitialSize(1920, 1040, 300, 200));
        // A large one is capped at the maximum.
        var (w, h) = EditorLayoutMath.InitialSize(1920, 1040, 3000, 2000);
        Assert.True(w <= 1728 + 1e-9 && h <= 915.2 + 1e-9);
    }

    [Fact]
    public void Fit_zoom_never_enlarges_beyond_one_point_per_pixel()
    {
        Assert.Equal(1, EditorLayoutMath.FitZoom(1000, 800, 200, 100), 9);
        Assert.Equal((1000 - 28) / 2000.0, EditorLayoutMath.FitZoom(1000, 800, 2000, 500), 9);
        // Each available side is at least 80.
        Assert.Equal(80 / 800.0, EditorLayoutMath.FitZoom(10, 10, 800, 800), 9);
    }

    [Fact]
    public void Zoom_steps_and_labels()
    {
        Assert.Equal(1 + (24 * 0.014), EditorLayoutMath.WheelFactor(100), 9);
        Assert.Equal(1 - (24 * 0.014), EditorLayoutMath.WheelFactor(-100), 9);
        Assert.Equal(200, EditorLayoutMath.ZoomPercent(1, 2));
        Assert.Equal(100, EditorLayoutMath.ZoomPercent(EditorLayoutMath.ActualSizeZoom(1.5), 1.5));
        Assert.Equal(0.05, EditorLayoutMath.ClampZoom(0.001), 9);
        Assert.Equal(3, EditorLayoutMath.ClampZoom(10), 9);
        Assert.Equal(new ImgPoint(90, 40), EditorLayoutMath.ViewToImage(200, 100, 2, 10));
    }
}

public class MetricsTests
{
    [Fact]
    public void Sticker_side_and_placement()
    {
        // short = 800, scale 2: minimum = min(104, 360) = 104; side = max(104, min(128, 256)) = 128
        Assert.Equal(128, AnnotationMetrics.StickerSide(1200, 800, 2), 9);
        // tiny image: short = 100 → minimum = min(52, 45) = 45; side = max(45, min(16, 128)) = 45
        Assert.Equal(45, AnnotationMetrics.StickerSide(100, 300, 1), 9);
        var rect = AnnotationMetrics.StickerRect(new ImgPoint(5, 5), 50, new ImgRect(0, 0, 100, 100));
        Assert.Equal(new ImgRect(0, 0, 50, 50), rect);
        Assert.Equal(41, AnnotationMetrics.StickerFontSize(rect, 1), 9);
    }

    [Fact]
    public void Counter_constants()
    {
        Assert.Equal(22, AnnotationMetrics.CounterDiameter(300, 200), 9);
        Assert.Equal(1.5, AnnotationMetrics.CounterRingWidth(22), 9);
        Assert.Equal(5, AnnotationMetrics.CounterRingWidth(100), 9);
        Assert.Equal(52, AnnotationMetrics.CounterFontSize(100), 9);
        Assert.Equal(new Rgb(0.09, 0.09, 0.11), AnnotationMetrics.CounterLabelColor(AnnotationColor.White));
    }

    [Fact]
    public void Text_bounds_measure_a_space_when_empty()
    {
        var measurer = new FixedMeasurer();
        var empty = AnnotationMetrics.TextBounds(string.Empty, new ImgPoint(10, 20), 19, 2, measurer);
        Assert.Equal(new ImgRect(10, 20, Math.Ceiling(0.5 * 38) + 4, Math.Ceiling(1.25 * 38)), empty);
        Assert.Equal(new ImgPoint(12, 20), AnnotationMetrics.TextDrawOrigin(empty));
    }

    [Fact]
    public void Text_sizes_clamp_and_step_through_presets()
    {
        Assert.Equal(19, TextSizes.Sanitize(0));
        Assert.Equal(10, TextSizes.Sanitize(3));
        Assert.Equal(96, TextSizes.Sanitize(500));
        Assert.Equal(24, TextSizes.Larger(19));
        Assert.Equal(16, TextSizes.Smaller(19));
        Assert.Null(TextSizes.Larger(96));
        Assert.Null(TextSizes.Smaller(10));
        Assert.Equal(24, TextSizes.Larger(20));
    }

    [Fact]
    public void A_capture_never_opens_below_blur_level_three()
    {
        Assert.Equal(3, BlurStyles.OpeningLevel(1));
        Assert.Equal(5, BlurStyles.OpeningLevel(5));
        Assert.Equal(3, BlurStyles.OpeningLevel(-4));
    }

    internal sealed class FixedMeasurer : ITextMeasurer
    {
        /// <summary>Every character is half the font size wide; lines are 1.25 × the size tall.</summary>
        public (double Width, double Height) Measure(string text, double fontSizePixels) =>
            (text.Length * fontSizePixels * 0.5, fontSizePixels * 1.25);
    }
}
