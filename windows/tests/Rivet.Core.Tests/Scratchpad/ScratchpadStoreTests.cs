// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Modules.Scratchpad;
using Xunit;

namespace Rivet.Core.Tests.Scratchpad;

public sealed class ScratchpadStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-scratchpad-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private ScratchpadRetention _retention = ScratchpadRetention.Never;

    public ScratchpadStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ScratchpadStore NewStore(Func<string, byte[], bool>? write = null) =>
        new(_folder, () => "Scratchpad", () => _retention, () => _now, write);

    [Fact]
    public void A_fresh_store_has_one_tab_named_scratchpad_1()
    {
        var store = NewStore();
        Assert.True(store.Load());
        Assert.Single(store.Document!.Pads);
        Assert.Equal("Scratchpad 1", store.Document.Selected.Name);
        Assert.True(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"pads\":[]}")]
    public void Unreadable_notes_are_never_touched_and_saving_stays_blocked(string content)
    {
        var path = Path.Combine(_folder, ScratchpadStore.FileName);
        File.WriteAllText(path, content);
        var store = NewStore();
        Assert.False(store.Load());
        Assert.True(store.LoadFailed);
        Assert.False(store.Commit(ScratchpadDocument.Fresh("Scratchpad")));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Edits_round_trip_with_the_2001_epoch()
    {
        var store = NewStore();
        store.Load();
        var id = store.Document!.SelectedId;
        store.UpdateText(id, "hello");
        Assert.True(store.Flush());
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"selectedID\"", json, StringComparison.Ordinal);
        Assert.Contains(ScratchpadCodec.FormatId(id), json, StringComparison.Ordinal);
        var expectedSeconds = (_now - ScratchpadCodec.ReferenceDate).TotalSeconds;
        Assert.Contains(((long)expectedSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), json, StringComparison.Ordinal);

        var reopened = NewStore();
        Assert.True(reopened.Load());
        Assert.Equal("hello", reopened.Document!.Selected.Text);
        Assert.Equal(_now, reopened.Document.Selected.ModifiedAt);
    }

    [Fact]
    public void Tab_operations_are_write_first()
    {
        var failWrites = false;
        var store = NewStore((path, bytes) => !failWrites && ScratchpadStore.WriteVerified(path, bytes));
        store.Load();
        failWrites = true;
        var next = store.Document!.WithNewPad("Scratchpad")!;
        Assert.False(store.Commit(next));
        Assert.Single(store.Document.Pads);
        Assert.True(store.SaveFailed);
        failWrites = false;
        Assert.True(store.Commit(next));
        Assert.Equal(2, store.Document.Pads.Count);
        Assert.False(store.SaveFailed);
    }

    [Fact]
    public void A_failed_save_keeps_edits_in_memory_and_retries_instead_of_reloading()
    {
        var failWrites = false;
        var store = NewStore((path, bytes) => !failWrites && ScratchpadStore.WriteVerified(path, bytes));
        store.Load();
        failWrites = true;
        store.UpdateText(store.Document!.SelectedId, "unsaved");
        Assert.False(store.Flush());
        Assert.True(store.HasUnsavedChanges);
        failWrites = false;
        Assert.True(store.Load());
        Assert.Equal("unsaved", store.Document.Selected.Text);
        Assert.False(store.HasUnsavedChanges);
    }

    [Fact]
    public void The_legacy_text_file_migrates_and_is_deleted_only_after_a_verified_save()
    {
        var legacy = Path.Combine(_folder, ScratchpadStore.LegacyFileName);
        File.WriteAllText(legacy, "old notes\r\nline 2", new UTF8Encoding(false));
        var failWrites = true;
        var failing = NewStore((path, bytes) => !failWrites && ScratchpadStore.WriteVerified(path, bytes));
        Assert.True(failing.Load());
        Assert.True(File.Exists(legacy));
        failWrites = false;

        var store = NewStore();
        Assert.True(store.Load());
        Assert.Equal("old notes\nline 2", store.Document!.Selected.Text);
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void Retention_clears_only_tabs_idle_strictly_longer_than_the_period()
    {
        var store = NewStore();
        store.Load();
        var first = store.Document!.SelectedId;
        store.UpdateText(first, "old");
        store.Flush();
        var second = store.Document.WithNewPad("Scratchpad")!;
        store.Commit(second);
        _now = _now.AddSeconds(10);
        store.UpdateText(second.SelectedId, "newer");
        store.Flush();

        _now = _now.AddSeconds(86_400 - 10);
        _retention = ScratchpadRetention.Day;
        var reopened = NewStore();
        reopened.Load();
        Assert.Equal("old", reopened.Document!.Find(first)!.Text);

        _now = _now.AddSeconds(1);
        var later = NewStore();
        later.Load();
        Assert.Equal(string.Empty, later.Document!.Find(first)!.Text);
        Assert.Equal("newer", later.Document.Find(second.SelectedId)!.Text);
        Assert.Equal(2, later.Document.Pads.Count);
    }

    [Fact]
    public void Future_or_missing_times_never_clear()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(ScratchpadRetentionRules.ShouldClear(null, now, ScratchpadRetention.Day));
        Assert.False(ScratchpadRetentionRules.ShouldClear(now.AddDays(3), now, ScratchpadRetention.Day));
        Assert.False(ScratchpadRetentionRules.ShouldClear(now.AddDays(-30), now, ScratchpadRetention.Never));
        Assert.True(ScratchpadRetentionRules.ShouldClear(now.AddSeconds(-604_801), now, ScratchpadRetention.Week));
        Assert.False(ScratchpadRetentionRules.ShouldClear(now.AddSeconds(-2_592_000), now, ScratchpadRetention.Month));
    }
}

public class ScratchpadDocumentTests
{
    [Fact]
    public void Default_names_fill_gaps_and_an_unnumbered_name_counts_as_slot_one()
    {
        Assert.Equal("Scratchpad 1", ScratchpadNames.NextPadName([], "Scratchpad"));
        Assert.Equal("Scratchpad 2", ScratchpadNames.NextPadName(["Scratchpad"], "Scratchpad"));
        Assert.Equal("Scratchpad 2", ScratchpadNames.NextPadName(["Scratchpad 1", "Scratchpad 3"], "Scratchpad"));
        var all = Enumerable.Range(1, 12).Select(i => $"Scratchpad {i}").ToList();
        Assert.Equal("Scratchpad 13", ScratchpadNames.NextPadName(all, "Scratchpad"));
    }

    [Fact]
    public void Names_are_cleaned_and_cut_to_40_characters()
    {
        Assert.Equal("a b c", ScratchpadNames.CleanName("  a \n\t b   c "));
        Assert.Equal(40, ScratchpadNames.CleanName(new string('x', 60)).Length);
        Assert.Equal(string.Empty, ScratchpadNames.CleanName("   "));
    }

    [Fact]
    public void Renaming_to_blank_keeps_the_name()
    {
        var doc = ScratchpadDocument.Fresh("Scratchpad");
        Assert.Same(doc, doc.WithName(doc.SelectedId, "  \n "));
        Assert.Equal("Ideas", doc.WithName(doc.SelectedId, " Ideas ").Selected.Name);
    }

    [Fact]
    public void The_last_tab_cannot_be_closed_and_selection_moves_to_the_same_index()
    {
        var doc = ScratchpadDocument.Fresh("Scratchpad");
        Assert.Null(doc.WithoutPad(doc.SelectedId));
        var three = doc.WithNewPad("S")!.WithNewPad("S")!;
        var middle = three.Pads[1].Id;
        var afterMiddle = three.WithSelected(middle).WithoutPad(middle)!;
        Assert.Equal(three.Pads[2].Id, afterMiddle.SelectedId);
        var afterLast = three.WithoutPad(three.Pads[2].Id)!;
        Assert.Equal(three.Pads[1].Id, afterLast.SelectedId);
    }

    [Fact]
    public void At_most_twelve_tabs()
    {
        var doc = ScratchpadDocument.Fresh("S");
        for (var i = 1; i < 12; i++)
        {
            doc = doc.WithNewPad("S")!;
        }

        Assert.Equal(12, doc.Pads.Count);
        Assert.Null(doc.WithNewPad("S"));
    }

    [Fact]
    public void Cleaning_drops_duplicates_fixes_selection_and_empty_times()
    {
        var id = Guid.NewGuid();
        var messy = new ScratchpadDocument(
            [
                new ScratchpadPad(id, "", "x", DateTimeOffset.UtcNow),
                new ScratchpadPad(id, "dupe", "y", null),
                new ScratchpadPad(Guid.NewGuid(), "empty", "", DateTimeOffset.UtcNow),
            ],
            Guid.NewGuid());
        var clean = ScratchpadCodec.Clean(messy, "Scratchpad");
        Assert.Equal(2, clean.Pads.Count);
        Assert.Equal("Scratchpad 1", clean.Pads[0].Name);
        Assert.Null(clean.Pads[1].ModifiedAt);
        Assert.Equal(id, clean.SelectedId);
    }

    [Fact]
    public void Export_names_replace_forbidden_characters()
    {
        Assert.Equal("a-b-c 2026-10-09.txt", ScratchpadNames.ExportFileName("a/b:c", new DateTime(2026, 10, 9)));
        Assert.Equal("Notes 2026-01-02.md", ScratchpadNames.ExportFileName("Notes", new DateTime(2026, 1, 2), "md"));
    }
}
