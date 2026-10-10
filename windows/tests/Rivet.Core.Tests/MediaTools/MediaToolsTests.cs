// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Localization;
using Rivet.Core.Modules.MediaTools;
using Xunit;

namespace Rivet.Core.Tests.MediaTools;

public class MediaSizingTests
{
    [Fact]
    public void Even_sizes_and_video_sizes()
    {
        Assert.Equal(new MediaSize(1000, 562), MediaSizing.ScaledEvenSize(new MediaSize(1920, 1080), 1000));
        Assert.Equal(new MediaSize(176, 96), MediaSizing.ScaledVideoSize(new MediaSize(320, 180), 180));
        Assert.Equal(new MediaSize(1920, 1080), MediaSizing.ScaledEvenSize(new MediaSize(1920, 1080), 4000));
    }

    [Fact]
    public void Resize_modes()
    {
        var source = new MediaSize(1920, 1080);
        Assert.Equal(new MediaSize(1000, 563), MediaSizing.TargetSize(source, new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 1000)));
        Assert.Equal(new MediaSize(800, 600), MediaSizing.TargetSize(new MediaSize(1600, 1200), new ImageResize(ImageResizeKind.Width, Width: 800)));
        Assert.Equal(new MediaSize(667, 500), MediaSizing.TargetSize(new MediaSize(1600, 1200), new ImageResize(ImageResizeKind.Height, Height: 500)));
        Assert.Equal(source, MediaSizing.TargetSize(source, new ImageResize(ImageResizeKind.MaxDimension, MaxDimension: 4000)));
        Assert.Equal(new MediaSize(3200, 2400), MediaSizing.TargetSize(new MediaSize(1600, 1200), new ImageResize(ImageResizeKind.Width, Width: 3200)));
    }

    [Fact]
    public void Decode_sizes_for_custom_modes()
    {
        var source = new MediaSize(1000, 100);
        Assert.Equal(100, MediaSizing.DecodeMaxPixel(source, new ImageResize(ImageResizeKind.Exact, Width: 100, Height: 100, ExactMode: ExactResizeMode.Fit)));
        Assert.Equal(1000, MediaSizing.DecodeMaxPixel(source, new ImageResize(ImageResizeKind.Exact, Width: 100, Height: 100, ExactMode: ExactResizeMode.Fill)));
        Assert.Equal(1000, MediaSizing.DecodeMaxPixel(source, new ImageResize(ImageResizeKind.Exact, Width: 100, Height: 100, ExactMode: ExactResizeMode.Stretch)));
    }

    [Fact]
    public void Safety_limits()
    {
        Assert.True(MediaSizing.IsSafe(new MediaSize(9000, 100)));
        Assert.False(MediaSizing.IsSafe(new MediaSize(9000, 9000)));
        Assert.False(MediaSizing.IsSafe(new MediaSize(20001, 1)));
        Assert.False(MediaSizing.IsSafe(new MediaSize(0, 10)));
    }

    [Fact]
    public void Exif_orientations_five_to_eight_swap_sides()
    {
        Assert.Equal(new MediaSize(3, 4), MediaSizing.Oriented(new MediaSize(4, 3), 6));
        Assert.Equal(new MediaSize(4, 3), MediaSizing.Oriented(new MediaSize(4, 3), 3));
    }

    [Fact]
    public void Fit_letterboxes_and_fill_crops()
    {
        var fit = MediaSizing.DrawRect(new MediaSize(200, 100), new MediaSize(100, 100), new ImageResize(ImageResizeKind.Exact, Width: 100, Height: 100, ExactMode: ExactResizeMode.Fit));
        Assert.Equal((0d, 25d, 100d, 50d), fit);
        var fill = MediaSizing.DrawRect(new MediaSize(200, 100), new MediaSize(100, 100), new ImageResize(ImageResizeKind.Exact, Width: 100, Height: 100, ExactMode: ExactResizeMode.Fill));
        Assert.Equal((-50d, 0d, 200d, 100d), fill);
    }
}

public class MediaPlannerTests
{
    [Fact]
    public void The_worked_example_projects_to_18_to_20_megabytes()
    {
        var plan = MediaPlanner.PlanVideo(20_000_000, 60, new MediaSize(1920, 1080), 30, hasAudio: true)!;
        Assert.Equal(128_000, plan.AudioBitRate);
        Assert.Equal(2_378_666, plan.VideoBitRate);
        Assert.Equal(new MediaSize(1418, 798), plan.Size);
        var projected = (plan.VideoBitRate + plan.AudioBitRate) * 60.0 / 8;
        Assert.InRange(projected, 18_000_000, 20_000_000);
    }

    [Fact]
    public void One_megabyte_for_two_minutes_has_no_plan()
    {
        Assert.Null(MediaPlanner.PlanVideo(1_000_000, 120, new MediaSize(1920, 1080), 30, hasAudio: true));
    }

    [Fact]
    public void Two_megabytes_for_a_minute_without_audio_is_480x270()
    {
        var plan = MediaPlanner.PlanVideo(2_000_000, 60, new MediaSize(1920, 1080), 30, hasAudio: false)!;
        Assert.Equal(0, plan.AudioBitRate);
        Assert.Equal(new MediaSize(480, 270), plan.Size);
    }

    [Fact]
    public void Windows_uses_96_kbps_for_the_low_audio_tier()
    {
        var plan = MediaPlanner.PlanVideo(8_000_000, 60, new MediaSize(1280, 720), 30, hasAudio: true)!;
        Assert.Equal(MediaPlanner.LowAudioBitRate, plan.AudioBitRate);
        Assert.Equal(96_000, plan.AudioBitRate);
    }

    [Fact]
    public void Video_retry_scale()
    {
        Assert.Equal(0.7833, MediaPlanner.VideoRetryScale(1.0, 10, 12), 4);
        Assert.True(MediaPlanner.VideoRetryScale(1.0, 99, 100) <= 0.9);
        Assert.Equal(1.0, MediaPlanner.VideoRetryScale(1.0, 100, 90));
    }

    [Fact]
    public void Gif_retry_drops_frames_before_pixels()
    {
        Assert.Equal(new GifPlan(720, 9), MediaPlanner.GifRetry(new GifPlan(720, 15), 8_000_000, 12_000_000));
        Assert.Null(MediaPlanner.GifRetry(new GifPlan(160, 6), 1, 12_000_000));
        var deeper = MediaPlanner.GifRetry(new GifPlan(800, 6), 1_000_000, 12_000_000)!;
        Assert.Equal(6, deeper.Fps);
        Assert.True(deeper.Width < 800);
    }

    [Fact]
    public void Gif_length_limits()
    {
        Assert.True(MediaPlanner.GifFits(25, 12));
        Assert.False(MediaPlanner.GifFits(25.01, 12));
        Assert.Equal(25, MediaPlanner.GifMaxSeconds(12));
        Assert.Equal(10, MediaPlanner.GifMaxSeconds(30));
        Assert.Equal(8, MediaPlanner.GifDelayCentiseconds(12));
        Assert.Equal(new GifPlan(1600, 15), MediaPlanner.GifStart(3840, 10));
        Assert.Equal(new GifPlan(160, 6), MediaPlanner.GifStart(100, 120));
    }

    [Fact]
    public void Trim_is_clamped_at_run_time()
    {
        Assert.Equal((0d, 10d), MediaPlanner.ClampTrim(0, 0, 10));
        Assert.Equal((2d, 10d), MediaPlanner.ClampTrim(2, 50, 10));
        Assert.Null(MediaPlanner.ClampTrim(12, 4, 10));
        Assert.Equal((3d, 5d), MediaPlanner.ClampTrim(3, 5, 10));
    }

    [Fact]
    public void The_resolution_ladder_picks_the_smallest_box()
    {
        var step = MediaPlanner.ResolutionStep(new MediaSize(3840, 2160), 30, MediaCompression.Medium, 1600, true);
        Assert.Equal(new MediaSize(1920, 1080), step.Size);
        var low = MediaPlanner.ResolutionStep(new MediaSize(3840, 2160), 30, MediaCompression.Low, 1600, true);
        Assert.Equal(new MediaSize(3840, 2160), low.Size);
        Assert.True(low.HighestQuality);
        var high = MediaPlanner.ResolutionStep(new MediaSize(1920, 1080), 30, MediaCompression.High, 1600, true);
        Assert.Equal(480, high.Size.Width);
        Assert.Equal(new MediaSize(640, 360), MediaPlanner.ResolutionStep(new MediaSize(1920, 1080), 30, MediaCompression.Medium, 640, true).Size);
    }

    [Fact]
    public void Compression_levels_highlight_the_nearest()
    {
        Assert.Equal(MediaCompression.Medium, MediaCompression.Nearest(0.7));
        Assert.Equal(MediaCompression.High, MediaCompression.Nearest(0.1));
        Assert.Equal(MediaCompression.Low, MediaCompression.Nearest(1.0));
    }
}

public sealed class MediaNamingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-media-naming-" + Guid.NewGuid().ToString("N"));

    public MediaNamingTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(".Clip.mov", "Clip")]
    [InlineData(".mov", "mov")]
    [InlineData("...", "Output")]
    [InlineData(" photo .jpg", "photo")]
    public void Base_names(string input, string expected) => Assert.Equal(expected, MediaNaming.BaseName(input));

    [Fact]
    public void Rename_tokens()
    {
        var name = MediaNaming.ExpandPattern("{name}-{counter:03}-{date}-{time}-{width}x{height}-{ext}", "/x/Photo One.jpg", 7, 800, 600, "png", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("Photo One-007-20240101-000000-800x600-png", name);
        Assert.Equal("Photo One", MediaNaming.ExpandPattern("", "/x/Photo One.jpg", 1, 1, 1, "png", DateTime.UtcNow));
        Assert.Equal("{unknown}-1", MediaNaming.ExpandPattern("{unknown}-{index}", "/x/a.jpg", 1, 1, 1, "png", DateTime.UtcNow));
        Assert.Equal("01-001-0001", MediaNaming.ExpandPattern("{index:02}-{counter:03}-{index:04}", "/x/a.jpg", 1, 1, 1, "png", DateTime.UtcNow));
    }

    [Fact]
    public void Sanitizing_handles_paths_and_windows_rules()
    {
        Assert.Equal("bad-name", MediaNaming.Sanitize("../bad/name"));
        Assert.Equal("a-b-c-d", MediaNaming.Sanitize("a<b>c|d"));
        Assert.Equal("CON_", MediaNaming.Sanitize("CON"));
        Assert.Equal("Output", MediaNaming.Sanitize(" ..- "));
        Assert.Equal("x", MediaNaming.Sanitize("x. "));
    }

    [Fact]
    public void Names_are_capped_at_255_bytes_minus_extension_and_suffix_reserve()
    {
        var truncated = MediaNaming.Truncate(new string('a', 400), "png");
        Assert.Equal(247, Encoding.UTF8.GetByteCount(truncated));
        var multibyte = MediaNaming.Truncate(new string('é', 200), "png");
        Assert.True(Encoding.UTF8.GetByteCount(multibyte) <= 247);
    }

    [Fact]
    public void Collisions_get_numbered_suffixes()
    {
        File.WriteAllText(Path.Combine(_folder, "photo.jpg"), "x");
        var used = new HashSet<string>();
        var first = MediaNaming.Unique(_folder, "photo", "jpg", used);
        Assert.Equal("photo 2.jpg", Path.GetFileName(first));
        var second = MediaNaming.Unique(_folder, "photo", "jpg", used);
        Assert.Equal("photo 3.jpg", Path.GetFileName(second));
        Assert.Equal("clip-compressed.mp4", Path.GetFileName(MediaNaming.Unique(_folder, MediaNaming.DefaultVideoStem("clip.mov"), "mp4")));
    }

    [Fact]
    public void Commits_replace_atomically_and_clean_up()
    {
        var destination = Path.Combine(_folder, "out.txt");
        File.WriteAllText(destination, "old");
        var identity = new PortableFileIdentity();
        string tempFolder;
        using (var commit = new MediaOutputCommit(destination, identity))
        {
            tempFolder = commit.TempFolder;
            File.WriteAllText(commit.TempFile, "new");
            commit.Commit();
        }

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(Directory.Exists(tempFolder));

        using (var failed = new MediaOutputCommit(destination, identity))
        {
            File.WriteAllText(failed.TempFile, "partial");
            tempFolder = failed.TempFolder;
        }

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(Directory.Exists(tempFolder));
    }

    [Fact]
    public void Commit_without_replace_refuses_an_existing_file()
    {
        var destination = Path.Combine(_folder, "keep.txt");
        File.WriteAllText(destination, "keep");
        using var commit = new MediaOutputCommit(destination, new PortableFileIdentity());
        File.WriteAllText(commit.TempFile, "new");
        Assert.Throws<IOException>(() => commit.Commit(replace: false));
        Assert.Equal("keep", File.ReadAllText(destination));
    }

    [Fact]
    public void The_same_file_guard_compares_full_paths()
    {
        var identity = new PortableFileIdentity();
        var path = Path.Combine(_folder, "a.jpg");
        Assert.True(identity.AreSameFile(path, Path.Combine(_folder, ".", "a.jpg")));
        Assert.False(identity.AreSameFile(path, Path.Combine(_folder, "b.jpg")));
    }
}

public class MediaOptionsTests
{
    [Fact]
    public void Presets_replace_every_option_and_turn_the_watermark_off()
    {
        Assert.Equal(ImageOutputFormat.Jpeg, MediaImagePresets.Web.Format);
        Assert.Equal(0.72, MediaImagePresets.Web.Quality);
        Assert.Equal("{name}-web", MediaImagePresets.Web.RenamePattern);
        Assert.Equal(2048, MediaImagePresets.Social.Resize.MaxDimension);
        Assert.Equal(ImageOutputFormat.Pdf, MediaImagePresets.Docs.Format);
        Assert.Equal(ImageBackground.White, MediaImagePresets.Docs.Background);
        Assert.True(MediaImagePresets.Docs.PreserveModificationDate);
        Assert.False(MediaImagePresets.Docs.Watermark.IsActive);
    }

    [Fact]
    public void Profiles_decode_leniently_and_fix_names_and_ids()
    {
        var json = """
        [
          {"id":"A","name":"","options":{"quality":0.5,"format":"png","resizeMode":{"kind":"width","width":640},"watermark":{"kind":"text","text":"(c)","margin":0},"renamePattern":{"rawValue":"{name}-x"}}},
          {"id":"A","name":"Second"},
          "garbage"
        ]
        """;
        var profiles = MediaImageProfiles.Decode(json, i => $"Profile {i}");
        Assert.Equal(2, profiles.Count);
        Assert.Equal("Profile 1", profiles[0].Name);
        Assert.Equal(ImageOutputFormat.Png, profiles[0].Options.Format);
        Assert.Equal(640, profiles[0].Options.Resize.Width);
        Assert.Equal(32, profiles[0].Options.Watermark.Margin);
        Assert.Equal("{name}-x", profiles[0].Options.RenamePattern);
        Assert.NotEqual(profiles[0].Id, profiles[1].Id);
        var reencoded = MediaImageProfiles.Decode(MediaImageProfiles.Encode(profiles, i => $"Profile {i}"), i => $"Profile {i}");
        Assert.Equal(profiles.Select(p => p.Options), reencoded.Select(p => p.Options));
    }

    [Fact]
    public void Backups_clear_logo_paths()
    {
        var options = ImageOptions.Default with { Watermark = new WatermarkOptions(WatermarkKind.TextAndLogo, "hi", "C:\\logo.png") };
        var backup = MediaImageProfiles.ForBackup(options);
        Assert.Equal(WatermarkKind.Text, backup.Watermark.Kind);
        Assert.Equal(string.Empty, backup.Watermark.LogoPath);
        var logoOnly = MediaImageProfiles.ForBackup(options with { Watermark = new WatermarkOptions(WatermarkKind.Logo, "", "C:\\logo.png") });
        Assert.Equal(WatermarkKind.Off, logoOnly.Watermark.Kind);
    }

    [Fact]
    public void Jpeg_and_pdf_force_a_white_background()
    {
        Assert.Equal(ImageBackground.White, MediaImageFormats.EffectiveBackground(ImageOutputFormat.Jpeg, ImageBackground.Transparent));
        Assert.Equal(ImageBackground.Transparent, MediaImageFormats.EffectiveBackground(ImageOutputFormat.Png, ImageBackground.Transparent));
        Assert.Equal(ImageBackground.Black, MediaImageFormats.EffectiveBackground(ImageOutputFormat.Pdf, ImageBackground.Black));
    }

    [Fact]
    public void Watermark_layout_follows_the_spec_geometry()
    {
        var options = new WatermarkOptions(WatermarkKind.TextAndLogo, "Rivet", "logo.png", WatermarkPosition.BottomRight, 0.5, 32, 0.2);
        var placement = WatermarkLayout.Compute(1000, 800, options, new MediaSize(100, 50), size => (size * 3, size))!;
        Assert.Equal(36, placement.FontSize, 6); // 800 · 0.045
        Assert.Equal(160, placement.LogoWidth, 6); // fits 160 square
        Assert.Equal(80, placement.LogoHeight, 6);
        Assert.Equal(12, placement.TextX - placement.LogoWidth - placement.LogoX, 6); // gap = 800 · 0.015
        Assert.Equal(1000 - 32, placement.TextX + placement.TextWidth, 6);
        Assert.Equal(800 - 32, placement.LogoY + placement.LogoHeight, 6);
        Assert.Equal(0.225, placement.ShadowOpacity, 6);

        var topLeft = WatermarkLayout.Compute(1000, 800, options with { Position = WatermarkPosition.TopLeft }, new MediaSize(100, 50), size => (size * 3, size))!;
        Assert.Equal(32, topLeft.LogoX, 6);
        Assert.Equal(32, topLeft.LogoY, 6);
        Assert.Null(WatermarkLayout.Compute(100, 100, new WatermarkOptions(WatermarkKind.Text, "   "), null, s => (s, s)));
    }

    [Fact]
    public void Wide_watermarks_shrink_to_fit()
    {
        var options = new WatermarkOptions(WatermarkKind.Text, new string('x', 50), Margin: 10);
        var placement = WatermarkLayout.Compute(200, 200, options, null, size => (size * 50, size))!;
        // Shrinking stops at an 8 px font, so a very long text may still overflow.
        Assert.Equal(8, placement.FontSize, 6);
        Assert.Equal(400, placement.TextWidth, 6);
    }

    [Fact]
    public void Ocr_languages_follow_the_app_language_then_english()
    {
        Assert.Equal(["de-DE", "en-US"], MediaText.OcrLanguages(AppLanguage.De));
        Assert.Equal(["zh-Hant", "en-US"], MediaText.OcrLanguages(AppLanguage.ZhHK));
        Assert.Equal(["en-US"], MediaText.OcrLanguages(AppLanguage.EnUS));
        Assert.Equal(["sk-SK", "en-US"], MediaText.OcrLanguages(AppLanguage.Sk));
    }

    [Fact]
    public void Bytes_use_1000_based_units()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal("999 bytes", MediaText.FormatBytes(999, culture));
        Assert.Equal("1 KB", MediaText.FormatBytes(1000, culture));
        Assert.Equal("12.3 MB", MediaText.FormatBytes(12_345_678, culture));
    }
}

public class MediaJobStateTests
{
    [Fact]
    public void Stale_updates_are_dropped_and_cancel_publishes_at_once()
    {
        var state = new MediaJobState();
        var (first, firstToken) = state.Begin();
        var (second, _) = state.Begin();
        Assert.True(firstToken.IsCancellationRequested);
        state.Report(first, 0.5);
        Assert.Equal(0, state.Progress);
        state.Report(second, 0.42);
        Assert.Equal(0.42, state.Progress);
        state.Cancel();
        Assert.Equal(MediaJobPhase.Cancelled, state.Phase);
        state.Complete(second, new MediaJobResult { Tool = MediaTool.Image });
        Assert.Equal(MediaJobPhase.Cancelled, state.Phase);
    }

    [Fact]
    public void Completion_sets_progress_to_one()
    {
        var state = new MediaJobState();
        var (id, _) = state.Begin();
        state.Complete(id, new MediaJobResult { Tool = MediaTool.Video, Outputs = ["x"] });
        Assert.Equal(MediaJobPhase.Completed, state.Phase);
        Assert.Equal(1, state.Progress);
        Assert.Equal(1, state.Result!.Succeeded);
    }
}
