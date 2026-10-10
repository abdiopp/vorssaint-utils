// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.RecordingEditor;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public class CanvasTests
{
    private static readonly RecorderBackdrop Ocean = new() { Kind = RecorderBackdropKind.Preset, PresetId = "ocean", Padding = 0.1 / 0.18 };

    [Fact]
    public void Plain_original_keeps_the_source()
    {
        var layout = CanvasLayout.Compute(960, 640, RecorderBackdrop.None, CanvasAspect.Original, 1);
        Assert.Equal((960, 640), (layout.CanvasWidth, layout.CanvasHeight));
        Assert.Equal(new IntRect(0, 0, 960, 640), layout.Card);
        Assert.Equal(0, layout.CornerRadius);
        Assert.False(layout.NeedsPlate);
    }

    [Fact]
    public void A_margin_grows_the_canvas_and_centres_the_card()
    {
        var layout = CanvasLayout.Compute(960, 640, Ocean, CanvasAspect.Original, 1);
        Assert.True(layout.CanvasWidth > 960 && layout.CanvasHeight > 640);
        Assert.Equal(0.1, layout.Margin, 9);
        Assert.Equal((1200, 800), (layout.CanvasWidth, layout.CanvasHeight));
        Assert.Equal(new IntRect(120, 80, 960, 640), layout.Card);
        Assert.True(layout.NeedsPlate);
    }

    [Fact]
    public void Shapes_crop_without_a_background_and_frame_with_one()
    {
        var crop = CanvasLayout.Compute(640, 640, RecorderBackdrop.None, CanvasAspect.Wide, 1);
        Assert.True(crop.CanvasWidth <= 640 && crop.CanvasHeight <= 640);
        Assert.Equal(16.0 / 9.0, crop.CanvasWidth / (double)crop.CanvasHeight, 1);

        var framed = CanvasLayout.Compute(640, 640, Ocean, CanvasAspect.Wide, 1);
        Assert.True(framed.CanvasWidth > 640);
        Assert.Equal(16.0 / 9.0, framed.CanvasWidth / (double)framed.CanvasHeight, 1);

        var square = CanvasLayout.Compute(960, 640, RecorderBackdrop.None, CanvasAspect.Square, 1);
        Assert.Equal((640, 640), (square.CanvasWidth, square.CanvasHeight));
        Assert.Equal(new IntRect(-160, 0, 960, 640), square.Card);
    }

    [Fact]
    public void Long_edge_is_capped_and_sizes_are_even()
    {
        var big = CanvasLayout.Compute(6000, 4000, new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = "candy", Padding = 0.3 / 0.18 }, CanvasAspect.Original, 1);
        Assert.True(Math.Max(big.CanvasWidth, big.CanvasHeight) <= 3840);
        Assert.Equal(0, big.CanvasWidth % 2);
        Assert.Equal(0, big.CanvasHeight % 2);

        var small = CanvasLayout.Compute(1471, 957, RecorderBackdrop.None, CanvasAspect.Original, 0.5);
        Assert.Equal((734, 478), (small.OutputWidth, small.OutputHeight));
    }

    [Fact]
    public void Corners_follow_the_card()
    {
        var layout = CanvasLayout.Compute(960, 640, Looks.StudioBackdrop, CanvasAspect.Original, 1);
        Assert.Equal(RecorderMath.RoundToInt(0.35 * Math.Min(layout.Card.Width, layout.Card.Height) * 0.09), layout.CornerRadius);
        Assert.True(layout.HasShadow);
        // Studio: margin ≈ 8.1 % per side, canvas ≈ 1.19× the source.
        Assert.Equal(0.081, layout.Margin, 3);
        Assert.InRange(layout.CanvasWidth / 960.0, 1.18, 1.2);
    }

    [Fact]
    public void Export_size_readout_uses_the_quality_scale()
    {
        Assert.Equal((1470, 956), CanvasLayout.ExportSize(2940, 1912, RecorderBackdrop.None, CanvasAspect.Original, ExportQuality.Small));
        Assert.Equal((2940, 1912), CanvasLayout.ExportSize(2940, 1912, RecorderBackdrop.None, CanvasAspect.Original, ExportQuality.Balanced));
    }
}

public class OverlayAndBlurTests
{
    [Fact]
    public void Caption_opacity_vectors()
    {
        Assert.Equal(0, OverlayLayout.Opacity(2, 6, 1.9));
        Assert.Equal(0, OverlayLayout.Opacity(2, 6, 6.1));
        Assert.Equal(1, OverlayLayout.Opacity(2, 6, 4));
        Assert.InRange(OverlayLayout.Opacity(2, 6, 2.1), 0.001, 0.999);
        Assert.InRange(OverlayLayout.Opacity(2, 6, 5.9), 0.001, 0.999);
        Assert.True(OverlayLayout.Opacity(1, 1.3, 1.15) > 0.99);
    }

    [Fact]
    public void Anchor_placement_vectors()
    {
        Assert.Equal((25.0, 25.0), OverlayLayout.Place(OverlayAnchor.TopLeading, 1000, 500, 100, 50));
        Assert.Equal((875.0, 425.0), OverlayLayout.Place(OverlayAnchor.BottomTrailing, 1000, 500, 100, 50));
        Assert.Equal((450.0, 225.0), OverlayLayout.Place(OverlayAnchor.Center, 1000, 500, 100, 50));
    }

    [Fact]
    public void Image_size_vectors()
    {
        Assert.Equal((500, 250), OverlayLayout.ImageSize(200, 100, 0.5, 1000, 500));
        Assert.Equal((45, 450), OverlayLayout.ImageSize(100, 1000, 0.6, 1000, 500));
        foreach (var (w, h) in new[] { (1000, 500), (500, 1000) })
        {
            foreach (var (iw, ih) in new[] { (300, 100), (100, 300) })
            {
                var (dw, dh) = OverlayLayout.ImageSize(iw, ih, 0.6, w, h);
                foreach (var anchor in Enum.GetValues<OverlayAnchor>())
                {
                    var (x, y) = OverlayLayout.Place(anchor, w, h, dw, dh);
                    var m = OverlayLayout.MarginOf(w, h);
                    Assert.True(x >= m - 0.5 && y >= m - 0.5 && x + dw <= w - m + 0.5 && y + dh <= h - m + 0.5, $"{anchor} {w}x{h} {iw}x{ih}");
                }
            }
        }
    }

    [Fact]
    public void Caption_font_size()
    {
        Assert.Equal(65, OverlayLayout.CaptionFontSize(1080, 0.06));
        Assert.Equal(8, OverlayLayout.CaptionFontSize(100, 0.03));
    }

    [Theory]
    [InlineData(300, 24, 3, 8)]
    [InlineData(600, 90, 3, 30)]
    [InlineData(900, 900, 3, 48)]
    [InlineData(600, 90, 1, 12)]
    [InlineData(600, 90, 5, 66)]
    public void Blur_block_vectors(int w, int h, int strength, int expected) =>
        Assert.Equal(expected, BlurMath.BlockSize(w, h, strength));

    [Fact]
    public void Smallest_blur_block_is_two()
    {
        Assert.True(BlurMath.BlockSize(300, 24, 1) >= 2);
        Assert.Equal(new IntRect(35, 40, 31, 21), BlurMath.PixelRect(new BlurRegion { Id = "b", X = 0.355, Y = 0.401, Width = 0.3, Height = 0.2 }, 100, 100));
        Assert.Null(BlurMath.PixelRect(new BlurRegion { Id = "b", X = 1, Y = 0, Width = 0.01, Height = 0.5 }, 100, 100));
    }

    [Fact]
    public void Stage_mapping_vectors()
    {
        Assert.Equal((0.6, 0.5), StageMapping.ToPicture(300, 250, 500, 500, 1000, 500));
        Assert.True(StageMapping.ToPicture(10, 10, 500, 500, 1000, 500).V < 0);
        Assert.Equal((0.75, 0.75), StageMapping.ToPicture(150, 125, 200, 200, 200, 100));
    }

    [Fact]
    public void Stage_mapping_square_vector()
    {
        var (u, v) = StageMapping.ToPicture(150, 150, 200, 200, 200, 200);
        Assert.Equal((0.75, 0.75), (u, v));
    }
}

public class ExportMathTests
{
    [Fact]
    public void Bit_rate_vectors()
    {
        Assert.InRange(QualityPreset.High.BitRate(2940, 1912, 60) / 1e6, 30.35, 30.36);
        Assert.InRange(QualityPreset.Balanced.BitRate(2940, 1912, 60) / 1e6, 20.23, 20.24);
        Assert.InRange(QualityPreset.Small.BitRate(1470, 956, 60) / 1e6, 4.21, 4.22);
        Assert.Equal(800_000, QualityPreset.High.BitRate(64, 64, 60));
        Assert.Equal(60_000_000, QualityPreset.High.BitRate(5120, 2880, 60));
        Assert.Equal(2, QualityPreset.High.KeyFrameIntervalSeconds);
        Assert.Equal(0.5, QualityPreset.Small.Scale);
    }

    [Fact]
    public void Gif_budget_vectors()
    {
        Assert.Equal(37, ExportMath.GifMaxSeconds(8));
        Assert.Equal(25, ExportMath.GifMaxSeconds(12));
        Assert.Equal(20, ExportMath.GifMaxSeconds(15));
        // 2 s source with trim [0.5, 1.5] at 2× and 12 fps → exactly 6 frames.
        var timeline = new EditTimeline(2, 0.5, 1.5, []);
        Assert.Equal(6, ExportMath.GifFrameCount(ExportSpeed.ExportDuration(timeline.OutputDuration, 2), 12));
        Assert.Equal(8, ExportMath.GifDelayCentiseconds(12));
        Assert.False(ExportMath.GifTooLong(25, 12));
        Assert.True(ExportMath.GifTooLong(25.1, 12));
        // A 10 s GIF at 12 fps and 2× → 60 frames.
        Assert.Equal(60, ExportMath.GifFrameCount(ExportSpeed.ExportDuration(10, 2), 12));
        Assert.Equal((600, 400), ExportMath.GifSizeFor(1200, 800, GifSize.Medium));
        Assert.Equal((300, 200), ExportMath.GifSizeFor(300, 200, GifSize.Large));
    }

    [Fact]
    public void Frame_rates_and_progress()
    {
        Assert.Equal(30, ExportMath.SnapFrameRate(30));
        Assert.Equal(30, ExportMath.SnapFrameRate(29.97));
        Assert.Equal(60, ExportMath.SnapFrameRate(24));
        Assert.Equal(60, ExportMath.SnapFrameRate(0));
        Assert.Equal(0.45, ExportMath.VideoProgress(50, 100), 9);
        Assert.Equal(0.9, ExportMath.VideoProgress(100, 100), 9);
        Assert.Equal(0.99, ExportMath.GifProgress(299, 300), 9);
        Assert.Equal(384, ExportMath.ExpectedFrames(6.4, 60));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    public void Elapsed_label_vectors(double seconds, string expected) =>
        Assert.Equal(expected, RecordingNames.Elapsed(seconds));

    [Fact]
    public void File_names_use_a_fixed_clock_and_count_up()
    {
        var name = RecordingNames.BaseName("Recording", new DateTime(2026, 10, 9, 14, 5, 9));
        Assert.Equal("Recording 2026-10-09 at 14.05.09", name);
        var folder = Path.Combine(Path.GetTempPath(), "rivet-names-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var first = RecordingNames.UniquePath(folder, name, ".mp4");
            File.WriteAllText(first, "x");
            var second = RecordingNames.UniquePath(folder, name, "mp4");
            Assert.EndsWith("at 14.05.09 2.mp4", second, StringComparison.Ordinal);
            Assert.Equal("a-b", RecordingNames.Sanitize("a:b"));
            Assert.Equal("1,25×", RecordingNames.Speed(1.25, CultureInfo.GetCultureInfo("de-DE")));
            Assert.Equal("1×", RecordingNames.Speed(1, CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

public class LaneMathTests
{
    private static ZoomSegment Z(string id, double s, double e) => new() { Id = id, Start = s, End = e };

    [Fact]
    public void New_zoom_slots()
    {
        Assert.Equal(new TimeRange(1, 3), LaneMath.SlotForNewZoom(1, [], 10));
        Assert.Equal(new TimeRange(4, 6), LaneMath.SlotForNewZoom(3, [Z("a", 2, 4)], 10));     // inside a zoom → after it
        Assert.Equal(new TimeRange(1, 1.5), LaneMath.SlotForNewZoom(1, [Z("a", 1.5, 3)], 10));  // ends at the next zoom
        Assert.Null(LaneMath.SlotForNewZoom(1, [Z("a", 1.2, 3)], 10));                          // less than 0.4 s of room
        Assert.Null(LaneMath.SlotForNewZoom(0, [], 0.4));
        Assert.Equal(new TimeRange(9.6, 10), LaneMath.SlotForNewZoom(9.9, [], 10));
    }

    [Fact]
    public void Zoom_moves_stop_at_neighbours()
    {
        var a = Z("a", 1, 2);
        var b = Z("b", 3, 4);
        var c = Z("c", 6, 7);
        Assert.Equal(new TimeRange(2, 3), LaneMath.MoveZoom(b, 0.5, [a, b, c], 10));
        Assert.Equal(new TimeRange(5, 6), LaneMath.MoveZoom(b, 9, [a, b, c], 10));
        Assert.Equal(new TimeRange(3.6, 4), LaneMath.ResizeZoom(b, true, 3.9, [a, b, c], 10));
        Assert.Equal(new TimeRange(3, 6), LaneMath.ResizeZoom(b, false, 9, [a, b, c], 10));
    }

    [Fact]
    public void Free_blocks_keep_length_inside_the_recording()
    {
        Assert.Equal(new TimeRange(8, 10), LaneMath.MoveFree(1, 3, 9, 10));
        Assert.Equal(new TimeRange(0, 2), LaneMath.MoveFree(1, 3, -4, 10));
        Assert.Equal(new TimeRange(2.6, 3), LaneMath.ResizeFree(1, 3, true, 2.9, 10));
        Assert.Equal(new TimeRange(1, 1.4), LaneMath.ResizeFree(1, 3, false, 1.1, 10));
    }

    [Fact]
    public void Snapping_takes_the_nearest_candidate_within_eight_pixels()
    {
        // 800 px lane over 10 s: 8 px = 0.1 s.
        Assert.Equal(5, LaneMath.Snap(5.08, [5, 5.3], 800, 10));
        Assert.Equal(5.12, LaneMath.Snap(5.12, [5], 800, 10));
        Assert.Equal(5.2, LaneMath.Snap(5.24, [5.2, 5.3], 800, 10));
    }
}
