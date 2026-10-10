// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public class RegionSnappingTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(31.9, 32)]
    [InlineData(33, 32)]
    [InlineData(641.4, 640)]
    [InlineData(401.9, 400)]
    [InlineData(1000, 1000)]
    [InlineData(double.NaN, 32)]
    [InlineData(double.PositiveInfinity, 32)]
    public void Even_side(double value, int expected) => Assert.Equal(expected, RegionSnapping.EvenSide(value));

    [Fact]
    public void Spec_vector_fractional_selection()
    {
        var snapped = RegionSnapping.Snap(10.7, 20.2, 641.4, 401.9, new PixelRect(0, 0, 2940, 1912));
        Assert.Equal(new PixelRect(10, 20, 640, 400), snapped);
    }

    [Fact]
    public void Spec_vector_selection_at_the_corner_stays_inside()
    {
        var bounds = new PixelRect(0, 0, 2940, 1912);
        var snapped = RegionSnapping.Snap(2900, 1900, 400, 400, bounds);
        Assert.True(snapped.Width >= 32 && snapped.Height >= 32);
        Assert.True(snapped.Right <= bounds.Right && snapped.Bottom <= bounds.Bottom);
        Assert.True(snapped.X >= bounds.X && snapped.Y >= bounds.Y);
        Assert.Equal(0, snapped.Width % 2);
        Assert.Equal(0, snapped.Height % 2);
    }

    [Fact]
    public void Non_finite_selection_is_the_whole_display()
    {
        var bounds = new PixelRect(0, 0, 1921, 1080);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), RegionSnapping.Snap(double.NaN, 0, 100, 100, bounds));
    }

    [Fact]
    public void Monitors_left_of_the_primary_have_negative_origins()
    {
        var bounds = new PixelRect(-2560, -200, 2560, 1440);
        var snapped = RegionSnapping.Snap(-2600.5, -150.2, 501, 301, bounds);
        Assert.Equal(new PixelRect(-2560, -151, 460, 300), snapped);
    }

    [Fact]
    public void A_selection_outside_the_monitor_falls_back_to_the_monitor()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(bounds, RegionSnapping.Snap(5000, 5000, 100, 100, bounds));
    }

    [Fact]
    public void A_drag_up_and_left_is_normalized()
    {
        var bounds = new PixelRect(0, 0, 1920, 1080);
        Assert.Equal(new PixelRect(100, 100, 200, 100), RegionSnapping.Snap(300, 200, -200, -100, bounds));
    }
}

public class ElapsedLabelTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(754, "12:34")]
    [InlineData(3723, "1:02:03")]
    [InlineData(-5, "0:00")]
    [InlineData(59.99, "0:59")]
    [InlineData(double.NaN, "0:00")]
    public void Spec_vectors(double seconds, string expected) => Assert.Equal(expected, ElapsedLabel.Format(seconds));
}

public class EncoderBitRateTests
{
    [Fact]
    public void Spec_vectors()
    {
        Assert.Equal(30.35, EncoderBitRate.Compute(2940, 1912, 60, EncoderBitRate.HighBitsPerPixel) / 1e6, 2);
        Assert.Equal(20.24, EncoderBitRate.Compute(2940, 1912, 60, EncoderBitRate.BalancedBitsPerPixel) / 1e6, 2);
        Assert.Equal(4.22, EncoderBitRate.Compute(1470, 956, 60, EncoderBitRate.SmallBitsPerPixel) / 1e6, 2);
        Assert.Equal(800_000, EncoderBitRate.Compute(64, 64, 60, EncoderBitRate.HighBitsPerPixel));
        Assert.Equal(60_000_000, EncoderBitRate.Compute(5120, 2880, 60, EncoderBitRate.HighBitsPerPixel));
    }

    [Fact]
    public void Master_uses_high_and_a_two_second_key_frame_interval()
    {
        Assert.Equal(EncoderBitRate.Compute(1920, 1080, 30, 0.09), EncoderBitRate.ForMaster(1920, 1080, 30));
        Assert.Equal(120, EncoderBitRate.MasterGopFrames(60));
        Assert.Equal(60, EncoderBitRate.MasterGopFrames(30));
    }

    [Fact]
    public void Video_size_limits_keep_common_sizes_and_scale_huge_ones()
    {
        Assert.Equal((1920, 1080), VideoSizeLimits.Fit(1920, 1080));
        Assert.Equal((3840, 2160), VideoSizeLimits.Fit(3840, 2160));
        Assert.Equal((1440, 2560), VideoSizeLimits.Fit(1440, 2560));

        foreach (var (w, h) in new[] { (5120, 1440), (5120, 2880), (7680, 4320), (6016, 3384) })
        {
            var (fw, fh) = VideoSizeLimits.Fit(w, h);
            Assert.True(fw <= VideoSizeLimits.MaxSide && fh <= VideoSizeLimits.MaxSide, $"{w}x{h} → {fw}x{fh}");
            Assert.True((long)fw * fh <= VideoSizeLimits.MaxPixels);
            Assert.Equal(0, fw % 2);
            Assert.Equal(0, fh % 2);
            Assert.Equal(w / (double)h, fw / (double)fh, 1);
        }
    }
}

public class WindowFitTests
{
    [Fact]
    public void Same_size_within_even_rounding_is_a_crop()
    {
        Assert.True(WindowFit.Compute(1201, 801, 1200, 800).IsCrop);
        Assert.True(WindowFit.Compute(1200, 800, 1200, 800).IsCrop);
    }

    [Fact]
    public void A_resized_window_is_scaled_to_fit_and_letterboxed()
    {
        var fit = WindowFit.Compute(1600, 800, 1200, 800);
        Assert.False(fit.IsCrop);
        Assert.Equal(0.75, fit.Scale, 6);
        Assert.Equal(0, fit.OffsetX, 6);
        Assert.Equal(100, fit.OffsetY, 6);
    }

    [Fact]
    public void Pointer_normalization_follows_the_window()
    {
        var window = new PixelRect(500, 300, 1200, 800);
        var (x, y) = WindowFit.Normalize(1100, 700, window, 1200, 800);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);

        // Moved: same relative point, same normalized position.
        var moved = window with { X = -400, Y = 50 };
        (x, y) = WindowFit.Normalize(200, 450, moved, 1200, 800);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);

        // Resized to twice the width: letterboxed, the centre stays the centre.
        var wide = new PixelRect(0, 0, 2400, 800);
        (x, y) = WindowFit.Normalize(1200, 400, wide, 1200, 800);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
    }
}

public class RecordingFileNameTests
{
    [Fact]
    public void Uses_the_fixed_format_and_numbers_clashes()
    {
        var time = new DateTime(2026, 10, 9, 14, 5, 9);
        Assert.Equal("Recording 2026-10-09 at 14.05.09.mp4", RecordingFileName.Build("Recording", time, ".mp4", _ => false));
        var taken = new HashSet<string> { "Recording 2026-10-09 at 14.05.09.mp4", "Recording 2026-10-09 at 14.05.09 2.mp4" };
        Assert.Equal("Recording 2026-10-09 at 14.05.09 3.mp4", RecordingFileName.Build("Recording", time, ".mp4", taken.Contains));
    }

    [Fact]
    public void Localized_prefixes_are_kept_and_unsafe_characters_dropped()
    {
        var time = new DateTime(2026, 1, 2, 3, 4, 5);
        Assert.Equal("Gravação 2026-01-02 at 03.04.05.mp4", RecordingFileName.Build("Gravação", time, ".mp4", _ => false));
        Assert.StartsWith("Recording 2026", RecordingFileName.Build(":?*", time, ".mp4", _ => false), StringComparison.Ordinal);
    }
}

public class CursorIdentityTests
{
    [Fact]
    public void Fnv1a_matches_the_reference_values()
    {
        Assert.Equal(0xcbf29ce484222325UL, CursorIdentity.Fnv1a([]));
        Assert.Equal(0xaf63dc4c8601ec8cUL, CursorIdentity.Fnv1a(Encoding.ASCII.GetBytes("a")));
        Assert.Equal(0x85944171f73967e8UL, CursorIdentity.Fnv1a(Encoding.ASCII.GetBytes("foobar")));
    }

    [Fact]
    public void Identity_covers_pixels_size_and_hot_spot()
    {
        byte[] pixels = [0, 0, 0, 255, 255, 255, 255, 255];
        var a = CursorIdentity.Hash(pixels, 2, 1, 0, 0);
        Assert.Equal(a, CursorIdentity.Hash(pixels, 2, 1, 0, 0));
        Assert.NotEqual(a, CursorIdentity.Hash(pixels, 2, 1, 1, 0));
        Assert.NotEqual(a, CursorIdentity.Hash(pixels, 1, 2, 0, 0));
        Assert.NotEqual(a, CursorIdentity.Hash([0, 0, 0, 255, 255, 255, 255, 254], 2, 1, 0, 0));
    }
}
