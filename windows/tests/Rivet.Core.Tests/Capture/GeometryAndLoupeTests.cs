// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Xunit;

namespace Rivet.Core.Tests.Capture;

/// <summary>Selection math (spec 01 §6.1, §6.2, §3.1) and loupe math (§6.3).</summary>
public class GeometryAndLoupeTests
{
    private static readonly RectD Display = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(100, 100, 300, 250)]
    [InlineData(300, 250, 100, 100)]
    [InlineData(300, 100, 100, 250)]
    [InlineData(100, 250, 300, 100)]
    public void A_drag_in_any_direction_gives_a_positive_rectangle(double x0, double y0, double x1, double y1)
    {
        var rect = CaptureGeometry.SelectionRect(new PointD(x0, y0), new PointD(x1, y1), square: false, fromCenter: false, Display);
        Assert.Equal(new RectD(100, 100, 200, 150), rect);
    }

    [Fact]
    public void Shift_makes_a_square_in_the_drag_direction()
    {
        var rect = CaptureGeometry.SelectionRect(new PointD(500, 500), new PointD(400, 560), square: true, fromCenter: false, Display);
        Assert.Equal(new RectD(400, 500, 100, 100), rect);

        // sign(0) counts as positive.
        var flat = CaptureGeometry.SelectionRect(new PointD(500, 500), new PointD(500, 530), square: true, fromCenter: false, Display);
        Assert.Equal(new RectD(500, 500, 30, 30), flat);
    }

    [Fact]
    public void Alt_grows_the_rectangle_from_its_centre()
    {
        var rect = CaptureGeometry.SelectionRect(new PointD(500, 400), new PointD(560, 380), square: false, fromCenter: true, Display);
        Assert.Equal(new RectD(440, 380, 120, 40), rect);
    }

    [Fact]
    public void The_selection_is_clamped_to_its_display()
    {
        var rect = CaptureGeometry.SelectionRect(new PointD(1800, 1000), new PointD(2100, 1300), square: false, fromCenter: false, Display);
        Assert.Equal(new RectD(1800, 1000, 120, 80), rect);
    }

    [Theory]
    [InlineData(1.0, 3.9, true)]
    [InlineData(1.0, 4.0, false)]
    [InlineData(1.5, 5.9, true)]
    [InlineData(1.5, 6.0, false)]
    public void Under_four_DIPs_is_a_click(double scale, double moved, bool click)
    {
        Assert.Equal(click, CaptureGeometry.IsClick(new PointD(10, 10), new PointD(10 + moved, 10), scale));
    }

    [Fact]
    public void Selections_under_two_by_two_DIPs_are_ignored()
    {
        Assert.True(CaptureGeometry.IsTooSmall(new RectD(0, 0, 2.9, 10), 1.5));
        Assert.False(CaptureGeometry.IsTooSmall(new RectD(0, 0, 3, 3), 1.5));
    }

    [Fact]
    public void Image_rectangles_round_outward_and_clamp()
    {
        var display = new PixelRect(-1920, 0, 1920, 1080);
        var rect = CaptureGeometry.ToImageRect(new RectD(-1900.4, 10.6, 100.2, 50.1), display, 1920, 1080);
        Assert.Equal(new PixelRect(19, 10, 101, 51), rect);
        var clamped = CaptureGeometry.ToImageRect(new RectD(-10, -10, 50, 50), new PixelRect(0, 0, 100, 100), 100, 100);
        Assert.Equal(new PixelRect(0, 0, 40, 40), clamped);
    }

    [Fact]
    public void Regions_snap_to_even_sizes_with_a_32_pixel_minimum_inside_the_display()
    {
        var display = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(new PixelRect(11, 21, 200, 138), CaptureGeometry.SnapRegion(new PixelRect(11, 21, 201, 139), display));

        var tiny = CaptureGeometry.SnapRegion(new PixelRect(500, 500, 5, 5), display);
        Assert.Equal((32, 32), (tiny.Width, tiny.Height));
        Assert.True(tiny.X <= 500 && tiny.Right >= 505);

        // Running off the display shrinks rather than shifts.
        var edge = CaptureGeometry.SnapRegion(new PixelRect(1800, 1000, 300, 300), display);
        Assert.Equal(new PixelRect(1800, 1000, 120, 80), edge);

        // A small one at the very edge grows inward.
        var corner = CaptureGeometry.SnapRegion(new PixelRect(1915, 1075, 5, 5), display);
        Assert.Equal(new PixelRect(1888, 1048, 32, 32), corner);
    }

    [Fact]
    public void The_display_with_the_largest_overlap_wins_ties_go_to_the_pointer()
    {
        var left = new ScreenInfo { Id = "L", FriendlyName = "L", Bounds = new PixelRect(-1000, 0, 1000, 800), WorkArea = new PixelRect(-1000, 0, 1000, 760), Scale = 1, IsPrimary = false };
        var right = new ScreenInfo { Id = "R", FriendlyName = "R", Bounds = new PixelRect(0, 0, 1000, 800), WorkArea = new PixelRect(0, 0, 1000, 760), Scale = 1, IsPrimary = true };
        ScreenInfo[] screens = [left, right];
        Assert.Equal("R", CaptureGeometry.DisplayWithLargestOverlap(screens, new PixelRect(-100, 0, 300, 100), new PixelPoint(-500, 10))!.Id);
        Assert.Equal("L", CaptureGeometry.DisplayWithLargestOverlap(screens, new PixelRect(-100, 0, 200, 100), new PixelPoint(-500, 10))!.Id);
        Assert.Null(CaptureGeometry.DisplayWithLargestOverlap(screens, default, new PixelPoint(0, 0)));
    }

    // ── Loupe ───────────────────────────────────────────────────────────
    [Theory]
    [InlineData(0.5, 27)]
    [InlineData(1, 13)]
    [InlineData(2, 7)]
    [InlineData(4, 3)]
    [InlineData(4.3333, 3)]
    [InlineData(100, 3)]
    [InlineData(0.01, 27)]
    public void Sample_sides_are_odd(double zoom, int side)
    {
        Assert.Equal(side, LoupeMath.SampleSide(zoom));
        Assert.Equal(1, LoupeMath.SampleSide(zoom) % 2);
    }

    [Fact]
    public void Zoom_is_clamped_to_half_and_thirteen_thirds()
    {
        Assert.Equal(0.5, LoupeMath.Clamp(0.1));
        Assert.Equal(13 / 3.0, LoupeMath.Clamp(9), 6);
        Assert.Equal(1, LoupeMath.Clamp(double.NaN));
    }

    [Fact]
    public void The_grid_shows_only_when_cells_are_at_least_six_DIPs()
    {
        Assert.True(LoupeMath.GridVisible(13));
        Assert.True(LoupeMath.GridVisible(22));
        Assert.False(LoupeMath.GridVisible(23));
        Assert.False(LoupeMath.GridVisible(27));
    }

    [Fact]
    public void Wheel_up_zooms_in_in_both_modes()
    {
        Assert.True(LoupeMath.FastZoom(1, 1, continuous: false) > 1);
        Assert.True(LoupeMath.FastZoom(1, -1, continuous: false) < 1);
        Assert.True(LoupeMath.SteppedZoom(1, 1) > 1);
        Assert.True(LoupeMath.SteppedZoom(1, -1) < 1);
    }

    [Fact]
    public void Every_stepped_notch_changes_exactly_one_level_and_is_reversible()
    {
        var zoom = LoupeMath.MinZoom;
        var sides = new List<int> { LoupeMath.SampleSide(zoom) };
        while (LoupeMath.SampleSide(zoom) > LoupeMath.MinSide)
        {
            zoom = LoupeMath.SteppedZoom(zoom, 1);
            sides.Add(LoupeMath.SampleSide(zoom));
        }

        Assert.Equal(Enumerable.Range(0, 13).Select(i => 27 - (2 * i)), sides);
        for (var i = sides.Count - 2; i >= 0; i--)
        {
            zoom = LoupeMath.SteppedZoom(zoom, -1);
            Assert.Equal(sides[i], LoupeMath.SampleSide(zoom));
        }

        Assert.Equal(LoupeMath.MinZoom, zoom);
    }

    [Fact]
    public void Fast_mode_on_a_plain_wheel_crosses_the_range_in_a_few_notches()
    {
        var zoom = LoupeMath.MinZoom;
        var notches = 0;
        while (zoom < LoupeMath.MaxZoom && notches < 20)
        {
            zoom = LoupeMath.FastZoom(zoom, 1, continuous: false);
            notches++;
        }

        Assert.InRange(notches, 4, 6);
        Assert.Equal(1.15, LoupeMath.FastZoom(1, 1, continuous: true), 6);
    }

    [Fact]
    public void Alt_swaps_fast_and_stepped()
    {
        Assert.False(LoupeMath.UseStepped(steppedByDefault: false, altHeld: false));
        Assert.True(LoupeMath.UseStepped(steppedByDefault: false, altHeld: true));
        Assert.True(LoupeMath.UseStepped(steppedByDefault: true, altHeld: false));
        Assert.False(LoupeMath.UseStepped(steppedByDefault: true, altHeld: true));
    }

    [Fact]
    public void The_initial_zoom_is_remembered_or_the_default()
    {
        Assert.Equal(2, LoupeMath.InitialZoom(rememberLast: true, lastZoom: 2, defaultZoom: 1));
        Assert.Equal(1, LoupeMath.InitialZoom(rememberLast: false, lastZoom: 2, defaultZoom: 1));
        Assert.Equal(1, LoupeMath.InitialZoom(rememberLast: true, lastZoom: double.PositiveInfinity, defaultZoom: 1));
    }

    [Fact]
    public void Sample_squares_centre_on_a_pixel_and_slide_inward_at_edges()
    {
        Assert.Equal(new PixelRect(94, 44, 13, 13), LoupeMath.SampleRect(new PointD(100.7, 50.2), 1920, 1080, 13));
        Assert.Equal(new PixelRect(0, 0, 13, 13), LoupeMath.SampleRect(new PointD(2, 3), 1920, 1080, 13));
        Assert.Equal(new PixelRect(1907, 1067, 13, 13), LoupeMath.SampleRect(new PointD(1919.9, 1079.9), 1920, 1080, 13));

        // An even request is made odd for the capture loupe; the crop loupe uses even squares centred on an edge.
        Assert.Equal(13, LoupeMath.SampleRect(new PointD(100, 100), 1920, 1080, 14).Width);
        var crop = LoupeMath.SampleRect(new PointD(100, 100), 1920, 1080, 14, centredOnPixel: false);
        Assert.Equal(new PixelRect(93, 93, 14, 14), crop);
    }

    [Fact]
    public void The_highlight_marks_exactly_the_pixel_the_picker_copies()
    {
        var point = new PointD(100.99, 50.01);
        var target = LoupeMath.TargetPixel(point, 1920, 1080);
        Assert.Equal(new PixelPoint(100, 50), target);
        var sample = LoupeMath.SampleRect(point, 1920, 1080, 13);
        var cell = LoupeMath.TargetCell(target, sample, new RectD(0, 0, 130, 130));
        Assert.Equal(new RectD(60, 60, 10, 10), cell);
        Assert.Equal(new PixelPoint(1919, 1079), LoupeMath.TargetPixel(new PointD(5000, 5000), 1920, 1080));
    }

    [Fact]
    public void The_loupe_block_flips_near_edges_and_stays_inside()
    {
        var o = LoupeMath.BlockOrigin(new PointD(500, 500), 1920, 1080);
        Assert.Equal(new PointD(516, 500 - LoupeMath.BlockHeight - 16), o);
        var right = LoupeMath.BlockOrigin(new PointD(1900, 500), 1920, 1080);
        Assert.Equal(1900 - LoupeMath.Frame - 16, right.X);
        var top = LoupeMath.BlockOrigin(new PointD(500, 20), 1920, 1080);
        Assert.Equal(36, top.Y);
        var scaled = LoupeMath.BlockOrigin(new PointD(100, 600), 1920, 1080, unit: 2);
        Assert.Equal(132, scaled.X);
    }

    [Fact]
    public void A_nudge_is_one_device_pixel_or_ten_with_shift()
    {
        Assert.Equal(1, LoupeMath.NudgePixels(shift: false));
        Assert.Equal(10, LoupeMath.NudgePixels(shift: true));
    }
}
