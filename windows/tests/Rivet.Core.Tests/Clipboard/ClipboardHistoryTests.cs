// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.App;
using Rivet.Core.Clipboard;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.Clipboard;

/// <summary>Spec 06 §3.2, §5.1, §6.2: the history list, persistence and search.</summary>
public class ClipboardHistoryTests
{
    private static ClipboardEntry Text(string text, bool pinned = false) =>
        new() { Text = text, PinnedAt = pinned ? DateTimeOffset.UtcNow : null };

    [Fact]
    public void Dedup_keeps_id_and_pin_and_moves_to_the_top_of_its_group()
    {
        var history = new ClipboardHistory();
        var first = history.Record(Text("alpha") with { SourceApp = "a.exe" });
        history.Record(Text("beta"));
        history.Pin(first.Id);
        history.Record(Text("gamma"));
        var again = history.Record(Text("alpha") with { SourceApp = "b.exe" });

        Assert.Equal(first.Id, again.Id);
        Assert.True(again.IsPinned);
        Assert.Equal("b.exe", again.SourceApp);
        Assert.Equal(["alpha", "gamma", "beta"], history.Entries.Select(e => e.Text));
        Assert.Equal(again.Id, history.LatestCopyId);
    }

    [Fact]
    public void A_copy_from_the_panel_keeps_the_old_source()
    {
        var history = new ClipboardHistory();
        history.Record(Text("alpha") with { SourceApp = "a.exe" });
        var again = history.Record(Text("alpha") with { SourceApp = null }, keepOldSource: true);
        Assert.Equal("a.exe", again.SourceApp);
    }

    [Fact]
    public void Limit_trims_recent_entries_only()
    {
        var history = new ClipboardHistory { Limit = 20 };
        var pinned = history.Record(Text("keep"));
        history.Pin(pinned.Id);
        for (var i = 0; i < 30; i++)
        {
            history.Record(Text("item " + i));
        }

        Assert.Equal(21, history.Entries.Count);
        Assert.Equal(20, history.RecentCount);
        Assert.True(history.Entries[0].IsPinned);
        Assert.Equal("item 29", history.Entries[1].Text);
        history.Limit = 0;
        history.Record(Text("more"));
        Assert.Equal(22, history.Entries.Count);
    }

    [Fact]
    public void Limit_setting_falls_back_to_50_for_unknown_values()
    {
        var store = SettingsStore.InMemory();
        store.Set(ClipboardSettings.Limit, 37);
        Assert.Equal(50, store.Get(ClipboardSettings.Limit));
        store.Set(ClipboardSettings.Limit, 0);
        Assert.Equal(0, store.Get(ClipboardSettings.Limit));
    }

    [Fact]
    public void Reuse_moves_recent_entries_and_keeps_pinned_positions()
    {
        var history = new ClipboardHistory();
        var a = history.Record(Text("a"));
        var b = history.Record(Text("b"));
        var c = history.Record(Text("c"));
        var p = history.Record(Text("p"));
        history.Pin(p.Id);
        // Order now: p | c b a
        history.Reuse([a.Id, p.Id, b.Id]);
        Assert.Equal(["p", "a", "b", "c"], history.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Move_stays_inside_its_group()
    {
        var history = new ClipboardHistory();
        var a = history.Record(Text("a"));
        var b = history.Record(Text("b"));
        var p = history.Record(Text("p"));
        history.Pin(p.Id);
        Assert.False(history.Move(b.Id, -1)); // would cross into the pinned group
        Assert.True(history.Move(b.Id, 1));
        Assert.Equal(["p", "a", "b"], history.Entries.Select(e => e.Text));
        Assert.False(history.Move(b.Id, 1));
        Assert.False(history.CanMove(a.Id, -1));
    }

    [Fact]
    public void Clear_unpinned_deletes_only_what_it_counted()
    {
        var history = new ClipboardHistory();
        history.Record(Text("a"));
        var p = history.Record(Text("p"));
        history.Pin(p.Id);
        var counted = history.RecentIds();
        history.Record(Text("copied while the dialog was open"));
        Assert.Equal(1, history.ClearUnpinned(counted));
        Assert.Equal(["p", "copied while the dialog was open"], history.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Edits_must_be_storable_and_clear_latest_copy()
    {
        var history = new ClipboardHistory();
        var entry = history.Record(Text("draft"));
        Assert.Equal(ClipboardEditResult.NotStorable, history.Edit(entry.Id, "   "));
        Assert.Equal(ClipboardEditResult.Unchanged, history.Edit(entry.Id, "draft"));
        Assert.Equal(ClipboardEditResult.Saved, history.Edit(entry.Id, "final"));
        Assert.Null(history.LatestCopyId);
        Assert.Equal(ClipboardEditResult.NotStorable, history.Edit(entry.Id, new string('x', ClipboardHistory.MaxTextLength + 1)));
    }

    [Fact]
    public void Json_uses_the_swift_layout_and_reference_date()
    {
        var copied = new DateTimeOffset(2025, 7, 1, 12, 30, 15, TimeSpan.Zero);
        var entry = new ClipboardEntry { Id = Guid.Parse("0f3b7e7a-1c2d-4e5f-8a9b-0c1d2e3f4a5b"), Text = "hi", CopiedAt = copied, SourceApp = "c:\\x.exe" };
        var json = Encoding.UTF8.GetString(ClipboardHistoryFile.Encode(entry));
        Assert.Contains("\"id\":\"0F3B7E7A-1C2D-4E5F-8A9B-0C1D2E3F4A5B\"", json);
        Assert.Contains("\"kind\":\"text\"", json);
        Assert.Contains("\"sourceBundleID\":\"c:\\\\x.exe\"", json);
        Assert.Equal(773065815, ClipboardHistoryFile.ToSwiftSeconds(copied), 3);

        var decoded = ClipboardHistoryFile.Decode(Encoding.UTF8.GetBytes("[" + json + ",{\"text\":\"legacy\"},{\"kind\":\"image\"}]"));
        Assert.Equal(2, decoded.Count);
        Assert.Equal(entry.Id, decoded[0].Id);
        Assert.Equal(copied, decoded[0].CopiedAt);
        Assert.Equal(ClipboardEntryKind.Text, decoded[1].Kind);
        Assert.NotEqual(Guid.Empty, decoded[1].Id);
        Assert.Empty(ClipboardHistoryFile.Decode("not json"u8));
    }

    [Fact]
    public void Image_file_names_never_escape_the_store()
    {
        var decoded = ClipboardHistoryFile.Decode("[{\"kind\":\"image\",\"imageFile\":\"..\\\\..\\\\evil.png\"}]"u8);
        Assert.Empty(decoded);
    }

    [Fact]
    public void Encoding_writes_pinned_first_and_skips_what_does_not_fit()
    {
        var entries = new List<ClipboardEntry> { Text("recent"), Text(new string('x', 400)), Text("pinned", pinned: true) };
        var (bytes, written) = ClipboardHistoryFile.EncodeAll(entries, budget: 300);
        Assert.Equal(["pinned", "recent"], written.Select(e => e.Text));
        Assert.True(bytes.Length <= 300);
        Assert.Equal(2, ClipboardHistoryFile.Decode(bytes).Count);
    }

    [Fact]
    public void Search_ranks_like_the_spec_example()
    {
        var entries = new List<ClipboardEntry> { Text("Deploy checklist final"), Text("Final database deploy plan"), Text("unrelated") };
        var result = new ClipboardSearch().Rank("deploy final", entries);
        Assert.Equal(["Deploy checklist final", "Final database deploy plan"], result.Select(e => e.Text));
    }

    [Fact]
    public void Search_scores_exact_prefix_and_pins()
    {
        var entries = new List<ClipboardEntry> { Text("password manager"), Text("pass"), Text("compass", pinned: true) };
        var result = new ClipboardSearch().Rank("pass", entries);
        Assert.Equal("pass", result[0].Text);
        Assert.Equal(1200 + 900 + 700 + 140, ClipboardSearch.Score("pass", ["pass"], "pass", ["pass"], false));
        Assert.Equal(30 + 700 + 40, ClipboardSearch.Score("compass", ["compass"], "pass", ["pass"], true));
    }

    [Fact]
    public void Search_is_case_diacritic_and_width_insensitive_and_finds_images_and_files()
    {
        var entries = new List<ClipboardEntry>
        {
            Text("Crème Brûlée"),
            new() { Kind = ClipboardEntryKind.Image, ImageHash = "h", ImageFile = "a.png", ImageWidth = 640, ImageHeight = 480 },
            new() { Kind = ClipboardEntryKind.Files, FilePaths = ["C:\\Users\\me\\report.pdf", "C:\\Users\\me\\photo.JPG"] },
        };
        var search = new ClipboardSearch();
        Assert.Single(search.Rank("CREME brulee", entries));
        Assert.Single(search.Rank("ｃｒｅｍｅ", entries));
        Assert.Equal(2, search.Rank("image", entries).Count);
        Assert.Equal(ClipboardEntryKind.Image, search.Rank("640×480", entries).Single().Kind);
        Assert.Equal(ClipboardEntryKind.Files, search.Rank("report", entries).Single().Kind);
        Assert.Equal(3, search.Rank("", entries).Count);
    }

    [Fact]
    public void Highlights_cover_every_occurrence_in_the_original_text()
    {
        var ranges = ClipboardSearch.Highlights("Café and cafe", "cafe");
        Assert.Equal([(0, 4), (9, 4)], ranges);
    }

    [Fact]
    public void Previews_follow_the_spec()
    {
        var entry = Text("line one\n\tline two");
        Assert.Equal("line one  line two", entry.Preview);
        Assert.Equal("line one\n line two", entry.CardPreview);
        Assert.Equal(new string('a', 5) + "…", Text("aaaaaaaa").MenuBarText(5, "Image"));
        var image = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageWidth = 10, ImageHeight = 20 };
        Assert.Equal("Image · 10×20", image.MenuBarText(20, "Image"));
        Assert.Equal("10×20", image.Preview);
        var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = ["C:\\a\\b.txt", "C:\\a\\c.png"] };
        Assert.Equal("b.txt, c.png", files.Preview);
        Assert.Equal("Image b.txt c.png", files.SearchableText("Image"));
        Assert.Equal(2001, Text(new string('x', 2001)).Preview.Length);
        Assert.NotNull(Text("#336699").Color);
        Assert.Null(Text("color #336699").Color);
    }

    [Theory]
    [InlineData("my password is hunter2", true)]
    [InlineData("Authorization: Bearer abc", true)]
    [InlineData("https://example.com/a1b2c3d4e5f6g7h8i9j0?x=1", false)]
    [InlineData("{0f3b7e7a-1c2d-4e5f-8a9b-0c1d2e3f4a5b}", false)]
    [InlineData("Xk9#mP2$vL7@qR4!wN6&", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123", false)]
    [InlineData("short1!", false)]
    [InlineData("a perfectly normal sentence with 1 digit!", false)]
    public void Sensitive_text_heuristic(string text, bool expected) =>
        Assert.Equal(expected, ClipboardSensitiveText.LooksSensitive(text));

    [Theory]
    [InlineData("https://example.com/a", null, "https://example.com/a")]
    [InlineData("hello", null, "hello")]
    [InlineData(null, "https://example.com/a", "https://example.com/a")]
    [InlineData("//example.com/a", "https://example.com/a", "https://example.com/a")]
    [InlineData("example.com/a", "https://example.com/a", "https://example.com/a")]
    [InlineData("https://a.com/1\nhttps://b.com/2", "https://a.com/1", "https://a.com/1\nhttps://b.com/2")]
    [InlineData("Read this", "https://example.com/a", "Read this")]
    public void Preferred_text_rule(string? plain, string? url, string? expected) =>
        Assert.Equal(expected, ClipboardPreferredText.Preferred(plain, url));

    [Theory]
    [InlineData(5u, 10u, 10u, 5u)]   // the clipboard server restarted
    [InlineData(12u, 10u, 11u, 12u)]
    [InlineData(11u, 10u, 11u, null)] // overtaken by our own write
    [InlineData(10u, 10u, 10u, null)]
    public void Change_counter_acceptance(uint read, uint since, uint last, uint? expected) =>
        Assert.Equal(expected, ClipboardChangeCount.Accepted(read, since, last));

    [Fact]
    public void Auto_clear_decide_table()
    {
        var now = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(20);
        Assert.Equal(AutoClearDecision.NoteChange, ClipboardAutoClearRules.Decide(5, 4, null, now, now, delay));
        Assert.Equal(AutoClearDecision.Wait, ClipboardAutoClearRules.Decide(5, 5, 5, now, now.AddMinutes(-5), delay));
        Assert.Equal(AutoClearDecision.Clear, ClipboardAutoClearRules.Decide(5, 5, 3, now, now.AddSeconds(-20), delay));
        Assert.Equal(AutoClearDecision.Wait, ClipboardAutoClearRules.Decide(5, 5, null, now, now.AddSeconds(-19), delay));
    }

    [Fact]
    public void Auto_clear_delay_is_clamped_not_reset()
    {
        var store = SettingsStore.InMemory();
        store.Set(ClipboardSettings.AutoClearDelaySeconds, 4);
        Assert.Equal(5, store.Get(ClipboardSettings.AutoClearDelaySeconds));
        store.Set(ClipboardSettings.AutoClearDelaySeconds, 99999);
        Assert.Equal(3600, store.Get(ClipboardSettings.AutoClearDelaySeconds));
    }

    [Fact]
    public void Json_preview_layout()
    {
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ],\n  \"c\": {}\n}", ClipboardJsonFormat.Pretty("{\"a\":1,\"b\":[1, 2],\"c\":{ }}"));
        Assert.Equal("[\n  \"x y\",\n  1.50,\n  1e3\n]", ClipboardJsonFormat.Pretty(" [\"x y\" , 1.50,1e3] "));
        var once = ClipboardJsonFormat.Pretty("{\"k\":\"v\\\"q\",\"n\":[[]]}")!;
        Assert.Equal(once, ClipboardJsonFormat.Pretty(once));
        Assert.Null(ClipboardJsonFormat.Pretty("not json"));
        Assert.Null(ClipboardJsonFormat.Pretty("\"just a string\""));
        Assert.Null(ClipboardJsonFormat.Pretty("{broken"));
    }

    [Fact]
    public void Rtf_and_html_become_plain_text()
    {
        const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red255\green0\blue0;}\f0 Hello \b World\b0\par Caf\'e9 \u8364?\tab end}";
        Assert.Equal("Hello World\nCafé €\tend", RichText.RtfToText(rtf));
        const string html = "Version:0.9\r\nStartHTML:00000097\r\nEndHTML:00000171\r\nStartFragment:00000131\r\nEndFragment:00000135\r\n<html><body>\r\n<!--StartFragment-->Hi!<!--EndFragment-->\r\n</body>\r\n</html>";
        Assert.Contains("Hi", RichText.HtmlFragment(html));
        Assert.Equal("Line &one\nLine two", RichText.HtmlToText("<p>Line &amp;one</p><p>Line <b>two</b></p><script>x()</script>"));
    }

    [Fact]
    public void Paste_plain_forwards_media_and_converts_rich_text()
    {
        Assert.Null(PastePlainService.PlainText(new ClipboardContent { Formats = ["CF_HDROP", "CF_UNICODETEXT"], Text = "file.txt" }));
        Assert.Null(PastePlainService.PlainText(new ClipboardContent { Formats = ["PNG"], Text = "x" }));
        Assert.Null(PastePlainService.PlainText(new ClipboardContent { Formats = ["CF_UNICODETEXT"], Text = "secret", IsConcealed = true }));
        Assert.Equal("plain", PastePlainService.PlainText(new ClipboardContent { Formats = ["CF_UNICODETEXT", "HTML Format"], Text = "plain", Html = "<b>x</b>" }));
        Assert.Equal("Bold", PastePlainService.PlainText(new ClipboardContent { Formats = ["Rich Text Format"], Rtf = @"{\rtf1 \b Bold\b0}" }));
    }

    [Fact]
    public void Batch_writes_follow_the_spec()
    {
        var a = Text("one");
        var b = Text("two");
        Assert.Equal("one", ClipboardBatch.Build([a], _ => null, _ => true)!.Text);
        Assert.Equal("one\ntwo", ClipboardBatch.Build([a, b], _ => null, _ => true)!.Text);
        var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = ["C:\\a.txt", "C:\\gone.txt"] };
        Assert.Equal(["C:\\a.txt"], ClipboardBatch.Build([files], _ => null, p => p == "C:\\a.txt")!.Files!);
        Assert.Null(ClipboardBatch.Build([files], _ => null, _ => false));
        var image = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageFile = "i.png", ImageHash = "h", ImageWidth = 1, ImageHeight = 1 };
        Assert.Null(ClipboardBatch.Build([image], _ => null, _ => true));
        var rich = ClipboardBatch.Build([a, image], _ => [1, 2, 3], _ => true)!;
        Assert.Equal("one", rich.Text);
        Assert.Contains("data:image/png;base64,AQID", rich.HtmlFragment);
        Assert.Contains(@"\pngblip", rich.Rtf);
    }

    // ── The capture pipeline ───────────────────────────────────────────

    private static (ClipboardHistoryService Service, TestClipboard Clipboard, ClipboardWatcher Watcher, SettingsStore Settings, TestForeground Foreground) Capture()
    {
        var clipboard = new TestClipboard();
        var watcher = new ClipboardWatcher(new ClipboardLane(clipboard));
        var settings = SettingsStore.InMemory();
        settings.Set(ClipboardSettings.Enabled, true);
        var foreground = new TestForeground();
        var paths = AppPaths.ForTemporaryDirectory(Path.Combine(Path.GetTempPath(), "rivet-clip-" + Guid.NewGuid()));
        var service = new ClipboardHistoryService(settings, watcher, foreground, paths);
        return (service, clipboard, watcher, settings, foreground);
    }

    [Fact]
    public void Start_takes_a_baseline_without_recording()
    {
        var (service, clipboard, _, _, _) = Capture();
        clipboard.CopyText("already there");
        service.Sync(true);
        Assert.True(service.IsRunning);
        Assert.True(clipboard.Listening);
        Assert.Empty(service.History.Entries);
    }

    [Fact]
    public void Copies_are_recorded_with_their_source_and_privacy_rules_apply()
    {
        var (service, clipboard, watcher, settings, _) = Capture();
        service.Sync(true);
        clipboard.CopyText("  hello world  ", owner: "c:\\apps\\notes.exe");
        watcher.CheckNow();
        var entry = Assert.Single(service.History.Entries);
        Assert.Equal("hello world", entry.Text);
        Assert.Equal("c:\\apps\\notes.exe", entry.SourceApp);

        clipboard.Copy(new ClipboardContent { Text = "secret from a password manager", Formats = ["CF_UNICODETEXT", ClipboardFormats.ExcludeFromMonitors], IsConcealed = true });
        watcher.CheckNow();
        clipboard.CopyText("Xk9#mP2$vL7@qR4!wN6&");
        watcher.CheckNow();
        Assert.Single(service.History.Entries);

        settings.Set(ClipboardSettings.IgnoredApps, ["C:\\Program Files\\Vault\\vault.exe"]);
        clipboard.CopyText("from an ignored app", owner: "d:\\other\\VAULT.EXE");
        watcher.CheckNow();
        Assert.Single(service.History.Entries);

        clipboard.CopyText("recorded again");
        watcher.CheckNow();
        Assert.Equal(2, service.History.Entries.Count);
        service.Flush();
    }

    [Fact]
    public async Task Own_copies_are_recorded_with_no_app_but_history_rewrites_are_not()
    {
        var (service, clipboard, watcher, _, _) = Capture();
        service.Sync(true);
        clipboard.Write(ClipboardWriteData.FromText("an answer"), ClipboardWriteMarks.OwnSource);
        watcher.CheckNow();
        var answer = Assert.Single(service.History.Entries);
        Assert.Null(answer.SourceApp);

        clipboard.CopyText("first");
        watcher.CheckNow();
        var copied = await service.CopyAsync([answer]);
        Assert.True(copied);
        watcher.CheckNow();
        Assert.Equal(2, service.History.Entries.Count);
        Assert.Equal("an answer", service.History.Entries[0].Text);
        Assert.Equal(answer.Id, service.History.LatestCopyId);
        service.Flush();
    }

    [Fact]
    public async Task Copying_stale_files_fails_and_leaves_the_clipboard()
    {
        var (service, clipboard, watcher, _, _) = Capture();
        service.Sync(true);
        clipboard.Copy(new ClipboardContent { Files = ["Z:\\nowhere\\gone.txt"], Formats = ["CF_HDROP"] });
        watcher.CheckNow();
        var files = Assert.Single(service.History.Entries);
        Assert.Equal(ClipboardEntryKind.Files, files.Kind);
        var before = clipboard.SequenceNumber;
        Assert.False(await service.CopyAsync([files]));
        Assert.Equal(before, clipboard.SequenceNumber);
        service.Flush();
    }

    [Fact]
    public void History_persists_and_reloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-clip-" + Guid.NewGuid());
        var clipboard = new TestClipboard();
        var watcher = new ClipboardWatcher(new ClipboardLane(clipboard));
        var settings = SettingsStore.InMemory();
        settings.Set(ClipboardSettings.Enabled, true);
        var service = new ClipboardHistoryService(settings, watcher, new TestForeground(), AppPaths.ForTemporaryDirectory(root));
        service.Sync(true);
        clipboard.CopyText("persist me");
        watcher.CheckNow();
        service.History.Pin(service.History.Entries[0].Id);
        service.Flush();
        service.Dispose();

        var reloaded = new ClipboardHistoryService(settings, new ClipboardWatcher(new ClipboardLane(new TestClipboard())), new TestForeground(), AppPaths.ForTemporaryDirectory(root));
        var entry = Assert.Single(reloaded.History.Entries);
        Assert.Equal("persist me", entry.Text);
        Assert.True(entry.IsPinned);
    }
}
