// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using Xunit;

namespace Rivet.Core.Tests.Capture;

/// <summary>Caps (§6.21), text and QR joining (§6.17, §6.18), the preview (§3.7, §6.4), window lists (§6.22), §6.23, §6.24.</summary>
public class PoliciesTests
{
    private sealed record Entry(string Name, bool Screenshot, long? Size);

    private const long Mb = 1024 * 1024;

    [Fact]
    public void Recent_captures_keep_twelve_and_the_newest_screenshot()
    {
        var entries = Enumerable.Range(0, 20).Select(i => new Entry($"s{i}", true, 1)).ToList();
        Assert.Equal(12, RecentCapturesPolicy.Apply(entries, e => e.Screenshot, e => e.Size).Count);

        var heavy = new List<Entry> { new("huge", true, 300 * Mb), new("big", true, 200 * Mb), new("rec", false, null), new("small", true, 10 * Mb), new("unknown", true, null) };
        var kept = RecentCapturesPolicy.Apply(heavy, e => e.Screenshot, e => e.Size).Select(e => e.Name).ToList();
        Assert.Equal(["huge", "rec", "unknown"], kept);

        var budget = new List<Entry> { new("a", true, 100 * Mb), new("b", true, 200 * Mb), new("c", true, 150 * Mb) };
        Assert.Equal(["a", "c"], RecentCapturesPolicy.Apply(budget, e => e.Screenshot, e => e.Size).Select(e => e.Name));
    }

    [Fact]
    public void Copied_file_pruning_never_evicts_the_published_file()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var files = new List<CachedFile>
        {
            new("/c/current.png", now.AddDays(-3), 500 * Mb),
            new("/c/old.png", now.AddHours(-25), 1),
            new("/c/fresh.png", now.AddMinutes(-5), 1),
            new("/c/broken.png", now.AddMinutes(-1), -1),
        };
        var delete = CopiedFilesPolicy.FilesToDelete(files, "/c/current.png", now);
        Assert.Contains("/c/old.png", delete);
        Assert.Contains("/c/broken.png", delete);
        Assert.DoesNotContain("/c/current.png", delete);
        Assert.DoesNotContain("/c/fresh.png", delete);

        var many = Enumerable.Range(0, 150).Select(i => new CachedFile($"/c/{i:000}.png", now.AddSeconds(-i), 1)).ToList();
        var pruned = CopiedFilesPolicy.FilesToDelete(many, "/c/000.png", now);
        Assert.Equal(50, pruned.Count);
        Assert.DoesNotContain("/c/000.png", pruned);
        Assert.Contains("/c/149.png", pruned);

        var bulky = Enumerable.Range(0, 5).Select(i => new CachedFile($"/c/b{i}.png", now.AddSeconds(-i), 100 * Mb)).ToList();
        Assert.Equal(["/c/b2.png", "/c/b3.png", "/c/b4.png"], CopiedFilesPolicy.FilesToDelete(bulky, null, now));
    }

    private static OcrLine Line(string text, double x, double y, double h = 20) => new(text, new RectD(x, y, 200, h), []);

    [Fact]
    public void Text_is_joined_in_reading_order()
    {
        var page = new OcrPage(1000, 1000, [Line("second", 10, 100), Line("right", 600, 12), Line("left", 10, 10), Line("  ", 10, 500)]);
        Assert.Equal("left\nright\nsecond", OcrTextJoiner.Join(page, removeLineBreaks: false));
        Assert.Equal("left right second", OcrTextJoiner.Join(page, removeLineBreaks: true));
    }

    [Fact]
    public void Cjk_lines_join_without_spaces_but_hangul_keeps_them()
    {
        var japanese = new OcrPage(1000, 1000, [Line("日本語の", 10, 10), Line("文章です", 10, 60)]);
        Assert.Equal("日本語の文章です", OcrTextJoiner.Join(japanese, removeLineBreaks: true));
        var korean = new OcrPage(1000, 1000, [Line("한국어", 10, 10), Line("문장", 10, 60)]);
        Assert.Equal("한국어 문장", OcrTextJoiner.Join(korean, removeLineBreaks: true));
        var mixed = new OcrPage(1000, 1000, [Line("中文", 10, 10), Line("English", 10, 60)]);
        Assert.Equal("中文 English", OcrTextJoiner.Join(mixed, removeLineBreaks: true));
    }

    [Fact]
    public void Windows_ocr_spaces_between_cjk_characters_are_removed()
    {
        Assert.Equal("日本語のテキスト", OcrTextJoiner.CleanCjkSpacing("日 本 語 の テ キ ス ト"));
        Assert.Equal("漢字 and words", OcrTextJoiner.CleanCjkSpacing("漢 字 and words"));
        Assert.Equal("한국어 문장", OcrTextJoiner.CleanCjkSpacing("한국어 문장"));
    }

    [Fact]
    public void Ocr_languages_follow_the_app_language()
    {
        Assert.Equal(["de-DE", "en-US"], OcrTextJoiner.PreferredLanguages("de"));
        Assert.Equal(["zh-Hant", "en-US"], OcrTextJoiner.PreferredLanguages("zh-TW"));
        Assert.Equal(["en-US"], OcrTextJoiner.PreferredLanguages("en-US"));
    }

    private static DetectedCode Code(string payload, double x = 0, double y = 0) => new(payload, new RectD(x, y, 50, 50), "QR_CODE");

    [Fact]
    public void Qr_payloads_join_in_reading_order()
    {
        Assert.Equal("top\nbottom", QrPayloads.Join([Code("bottom", 0, 500), Code("top", 300, 10), Code(" ", 0, 900)], 1000));
    }

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("  http://example.com  ", true)]
    [InlineData("mailto:someone@example.com", false)]
    [InlineData("tel:+123", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("example.com", false)]
    [InlineData("myapp://open", false)]
    public void Only_single_http_links_are_openable(string payload, bool openable)
    {
        Assert.Equal(openable, QrPayloads.OpenableUrl([Code(payload)]) is not null);
    }

    [Fact]
    public void Several_codes_are_never_opened()
    {
        Assert.Null(QrPayloads.OpenableUrl([Code("https://a.example"), Code("https://b.example", 0, 300)]));
    }

    [Fact]
    public void The_preview_decision_matrix_matches_the_spec()
    {
        Assert.Equal(new PreviewDecision(true, PreviewPolicy.RecoveryTimeout), PreviewPolicy.Decide(ScreenshotDefaultAction.Ask, true, true, 3));
        Assert.False(PreviewPolicy.Decide(ScreenshotDefaultAction.Edit, true, true, 3).Show);
        Assert.Equal(new PreviewDecision(true, TimeSpan.FromSeconds(5)), PreviewPolicy.Decide(ScreenshotDefaultAction.Save, true, true, 5));
        Assert.True(PreviewPolicy.Decide(ScreenshotDefaultAction.Copy, true, true, 0).IsPersistent);
        Assert.False(PreviewPolicy.Decide(ScreenshotDefaultAction.SaveAndCopy, true, false, 3).Show);
        Assert.Equal(new PreviewDecision(true, PreviewPolicy.RecoveryTimeout), PreviewPolicy.Decide(ScreenshotDefaultAction.Save, false, false, 3));
    }

    [Fact]
    public void Only_timed_previews_take_focus_and_only_when_asked()
    {
        Assert.True(PreviewPolicy.TakesFocus(new PreviewDecision(true, TimeSpan.FromSeconds(3)), preference: true));
        Assert.False(PreviewPolicy.TakesFocus(new PreviewDecision(true, TimeSpan.FromSeconds(3)), preference: false));
        Assert.False(PreviewPolicy.TakesFocus(new PreviewDecision(true, null), preference: true));
    }

    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Fixed_corners_sit_sixteen_dips_inside_the_work_area()
    {
        Assert.Equal(new PixelPoint(16, 16), PreviewPolicy.Place(PreviewPosition.TopLeft, false, default, default, Work, 1));
        Assert.Equal(new PixelPoint(1920 - 16 - 350, 1040 - 16 - 210), PreviewPolicy.Place(PreviewPosition.BottomRight, false, default, default, Work, 1));
        Assert.Equal(new PixelPoint(24, 1040 - 24 - 315), PreviewPolicy.Place(PreviewPosition.BottomLeft, false, default, default, Work, 1.5));
    }

    [Fact]
    public void Automatic_goes_to_the_corner_once_an_action_ran()
    {
        var anchor = new PixelRect(100, 100, 400, 300);
        Assert.Equal(PreviewPolicy.Place(PreviewPosition.BottomRight, false, default, default, Work, 1),
            PreviewPolicy.Place(PreviewPosition.Automatic, true, anchor, new PixelPoint(50, 50), Work, 1));
    }

    [Fact]
    public void Automatic_sits_beside_the_capture()
    {
        var anchor = new PixelRect(100, 300, 400, 300);
        Assert.Equal(new PixelPoint(514, 345), PreviewPolicy.Place(PreviewPosition.Automatic, false, anchor, new PixelPoint(0, 0), Work, 1));

        // No room on the right: to the left.
        var right = new PixelRect(1500, 300, 400, 300);
        Assert.Equal(1500 - 14 - 350, PreviewPolicy.Place(PreviewPosition.Automatic, false, right, default, Work, 1).X);

        // A full-width area: at the pointer, clamped.
        var wide = new PixelRect(0, 0, 1920, 1040);
        var p = PreviewPolicy.Place(PreviewPosition.Automatic, false, wide, new PixelPoint(900, 500), Work, 1);
        Assert.Equal(914, p.X);
        Assert.InRange(p.Y, 10, 1040 - 10 - 210);
    }

    private static CaptureWindowInfo Window(int handle, PixelRect bounds, string title = "App", int pid = 10, bool own = false, bool protectedWindow = false, double scale = 1) =>
        new() { Handle = handle, Bounds = bounds, Title = title, ProcessId = pid, IsOwnProcess = own, IsProtected = protectedWindow, Scale = scale };

    [Fact]
    public void Pickable_windows_follow_size_and_own_window_rules()
    {
        CaptureWindowInfo[] list =
        [
            Window(1, new PixelRect(0, 0, 39, 500)),
            Window(2, new PixelRect(0, 0, 500, 500), own: true),
            Window(3, new PixelRect(0, 0, 500, 500), own: true, protectedWindow: true),
            Window(4, new PixelRect(0, 0, 59, 59), scale: 1.5),
            Window(5, new PixelRect(0, 0, 60, 60), scale: 1.5),
        ];
        Assert.Equal([5], WindowPicking.Pickable(list, hideOwnWindows: true).Select(w => (int)w.Handle));
        Assert.Equal([2, 5], WindowPicking.Pickable(list, hideOwnWindows: false).Select(w => (int)w.Handle));
        Assert.Equal([5], WindowPicking.Pickable(list, hideOwnWindows: false, alsoProtected: new HashSet<nint> { 2 }).Select(w => (int)w.Handle));
    }

    [Fact]
    public void Focus_borders_of_other_apps_are_decorations()
    {
        var window = Window(2, new PixelRect(100, 100, 800, 600), pid: 20);
        var border = Window(1, new PixelRect(92, 92, 816, 616), title: string.Empty, pid: 30);
        var uneven = Window(1, new PixelRect(92, 90, 816, 620), title: string.Empty, pid: 30);
        var titled = border with { Title = "Overlay" };
        Assert.True(WindowPicking.IsDecoration([border, window], 0));
        Assert.False(WindowPicking.IsDecoration([uneven, window], 0));
        Assert.False(WindowPicking.IsDecoration([titled, window], 0));
        Assert.False(WindowPicking.IsDecoration([border with { ProcessId = 20 }, window], 0));
        Assert.Single(WindowPicking.Pickable([border, window], hideOwnWindows: true));
    }

    [Fact]
    public void The_frontmost_window_wins_a_click()
    {
        CaptureWindowInfo[] list = [Window(1, new PixelRect(0, 0, 100, 100)), Window(2, new PixelRect(0, 0, 500, 500))];
        Assert.Equal(1, (int)WindowPicking.HitTest(list, new PixelPoint(50, 50))!.Handle);
        Assert.Equal(2, (int)WindowPicking.HitTest(list, new PixelPoint(300, 50))!.Handle);
        Assert.Null(WindowPicking.HitTest(list, new PixelPoint(700, 50)));
    }

    [Fact]
    public void Attached_dialogs_are_in_front_inside_the_frame_and_of_the_same_app()
    {
        var target = Window(10, new PixelRect(100, 100, 800, 600), pid: 1);
        var dialog = Window(11, new PixelRect(300, 300, 300, 200), pid: 1);
        var sheet = Window(12, new PixelRect(200, 120, 400, 100), pid: 1);
        var stranger = Window(13, new PixelRect(300, 300, 100, 100), pid: 2);
        var outside = Window(14, new PixelRect(50, 50, 200, 200), pid: 1);
        var behind = Window(15, new PixelRect(300, 300, 50, 50), pid: 1);
        CaptureWindowInfo[] list = [dialog, stranger, sheet, outside, target, behind];
        Assert.Equal([12, 11], WindowPicking.AttachedWindows(list, target).Select(w => (int)w.Handle));
    }

    [Fact]
    public void Clipboard_image_scale_is_inferred_from_logical_size()
    {
        Assert.Equal(2, ClipboardImageScale.Infer(2000, 1000, 1000, 500));
        Assert.Equal(1, ClipboardImageScale.Infer(2000, 1000, 1000, 800));
        Assert.Equal(1, ClipboardImageScale.Infer(5000, 5000, 1000, 1000));
        Assert.Equal(1, ClipboardImageScale.Infer(0, 1000, 1000, 500));
        Assert.Equal(1.5, ClipboardImageScale.FromDpi(144));
        Assert.Null(ClipboardImageScale.FromDpi(30));
        Assert.Null(ClipboardImageScale.FromDpi(double.NaN));
    }

    [Fact]
    public void The_countdown_ring_drains_over_0_92_seconds()
    {
        Assert.Equal(1, CountdownRing.Progress(0));
        Assert.Equal(0.5, CountdownRing.Progress(0.46), 6);
        Assert.Equal(0, CountdownRing.Progress(1.2));
        var (from, to) = CountdownRing.Arc(1);
        Assert.Equal(0.04, from, 9);
        Assert.Equal(0.96, to, 9);
        Assert.Equal(0.04, CountdownRing.Arc(0).To, 9);
    }

    [Fact]
    public void Relative_times_bucket_into_minutes_hours_and_days()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(("now", 0L), RelativeTime.Bucket(now.AddSeconds(-30), now));
        Assert.Equal(("minutes", 5L), RelativeTime.Bucket(now.AddMinutes(-5), now));
        Assert.Equal(("hours", 3L), RelativeTime.Bucket(now.AddHours(-3.5), now));
        Assert.Equal(("days", 2L), RelativeTime.Bucket(now.AddDays(-2), now));
    }
}
