// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;
using Xunit;

namespace Rivet.Core.Tests.Shelf;

public class ShelfTreeTests
{
    private static ShelfItem Text(string text) => ShelfItem.TextItem(text)!;

    [Fact]
    public void Capacity_counts_leaves()
    {
        Assert.True(ShelfTree.CanAdd(199, 1));
        Assert.False(ShelfTree.CanAdd(200, 1));
        Assert.False(ShelfTree.CanAdd(199, 2));
        Assert.False(ShelfTree.CanAdd(0, 0));
        Assert.False(ShelfTree.CanAdd(-1, 1));
    }

    [Fact]
    public void Several_new_items_become_one_pile_titled_first_plus_n()
    {
        var items = ShelfTree.Append([], [Text("alpha"), Text("beta"), Text("gamma")]);
        var pile = Assert.Single(items);
        Assert.True(pile.IsPile);
        Assert.Equal("alpha +2", pile.Title);
        Assert.Equal(3, ShelfTree.LeafCount(items));
        var single = ShelfTree.Append(items, [Text("delta")]);
        Assert.False(single[1].IsPile);
    }

    [Fact]
    public void Removal_dissolves_piles_and_the_last_child_inherits_the_pin()
    {
        var a = Text("a");
        var b = Text("b");
        var pile = ShelfTree.MakePile([a, b], pinned: true);
        var items = ShelfTree.Remove([pile], new HashSet<Guid> { a.Id });
        var only = Assert.Single(items);
        Assert.Equal(b.Id, only.Id);
        Assert.True(only.Pinned);
        Assert.Empty(ShelfTree.Remove(items, new HashSet<Guid> { b.Id }));
    }

    [Fact]
    public void Clear_all_keeps_protected_items()
    {
        var pinned = Text("keep") with { Pinned = true };
        var inPinnedPile = ShelfTree.MakePile([Text("x"), Text("y")], pinned: true);
        var loose = Text("drop");
        var result = ShelfTree.RemoveUnprotected([pinned, inPinnedPile, loose]);
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, i => i.Id == loose.Id);
        Assert.Contains(inPinnedPile.Children[0].Id, ShelfTree.Protected(result));
    }

    [Fact]
    public void An_external_drop_on_a_single_tile_makes_a_new_pile_with_a_fresh_id()
    {
        var target = Text("target");
        var merged = ShelfTree.MergeExternal([target], target.Id, [Text("new1"), Text("new2")])!;
        var pile = Assert.Single(merged);
        Assert.True(pile.IsPile);
        Assert.NotEqual(target.Id, pile.Id);
        Assert.Equal("target +2", pile.Title);
        Assert.Equal(target.Id, pile.Children[0].Id);
    }

    [Fact]
    public void An_external_drop_on_a_pile_appends_flattened_leaves()
    {
        var pile = ShelfTree.MakePile([Text("a"), Text("b")]);
        var addedPile = ShelfTree.MakePile([Text("c"), Text("d")]);
        var merged = ShelfTree.MergeExternal([pile], pile.Id, [addedPile])!;
        Assert.Equal(4, merged[0].Children.Count);
        Assert.Equal("a +3", merged[0].Title);
        Assert.Equal(pile.Id, merged[0].Id);
    }

    [Fact]
    public void Merges_respect_capacity()
    {
        var many = Enumerable.Range(0, 199).Select(i => Text($"t{i}")).ToList();
        Assert.Null(ShelfTree.MergeExternal(many, many[0].Id, [Text("x"), Text("y")]));
        Assert.NotNull(ShelfTree.MergeExternal(many, many[0].Id, [Text("x")]));
    }

    [Fact]
    public void Tile_onto_tile_moves_and_refuses_moving_into_itself_or_a_descendant()
    {
        var a = Text("a");
        var b = Text("b");
        var c = Text("c");
        var moved = ShelfTree.MoveInto([a, b, c], a.Id, [b.Id])!;
        Assert.Equal(2, moved.Count);
        Assert.True(moved[0].IsPile);
        Assert.Equal([a.Id, b.Id], moved[0].Children.Select(x => x.Id));
        Assert.Null(ShelfTree.MoveInto(moved, moved[0].Id, [moved[0].Id]));
        Assert.Null(ShelfTree.MoveInto(moved, a.Id, [moved[0].Id]));
    }

    [Fact]
    public void Visible_rows_inline_expanded_piles()
    {
        var pile = ShelfTree.MakePile([Text("a"), Text("b")]);
        var after = Text("z");
        var collapsed = ShelfTree.VisibleRows([pile, after], new HashSet<Guid>());
        Assert.Equal(2, collapsed.Count);
        var expanded = ShelfTree.VisibleRows([pile, after], new HashSet<Guid> { pile.Id });
        Assert.Equal([pile.Id, pile.Children[0].Id, pile.Children[1].Id, after.Id], expanded.Select(r => r.Item.Id));
        Assert.Equal(1, expanded[1].Depth);
        Assert.Equal(pile.Id, ShelfTree.VisibleAncestor([pile, after], pile.Children[1].Id, new HashSet<Guid>()));
    }

    [Fact]
    public void Scope_is_the_selection_when_the_clicked_tile_is_selected()
    {
        var a = Text("a");
        var pile = ShelfTree.MakePile([Text("b"), Text("c")]);
        var items = new List<ShelfItem> { a, pile };
        Assert.Single(ShelfTree.Scope(items, a.Id, new HashSet<Guid> { pile.Id }));
        Assert.Equal(3, ShelfTree.Scope(items, a.Id, new HashSet<Guid> { a.Id, pile.Id }).Count);
    }

    [Fact]
    public void Text_items_are_capped_titled_and_never_blank()
    {
        Assert.Null(ShelfItem.TextItem(" \n\t "));
        var long_ = ShelfItem.TextItem(new string('x', 250_000))!;
        Assert.Equal(200_000, long_.Text!.Length);
        Assert.Equal(48, long_.Title.Length);
        Assert.Equal("first line", ShelfItem.TextItem("  first line\nsecond")!.Title);
    }

    [Fact]
    public void Links_need_a_non_file_url_and_use_the_host_as_title()
    {
        Assert.Equal("example.com", ShelfItem.LinkItem("https://example.com/path")!.Title);
        Assert.Null(ShelfItem.LinkItem("file:///C:/x.txt"));
        Assert.Null(ShelfItem.LinkItem("not a url"));
        Assert.Equal("mailto:a@b.c", ShelfItem.LinkItem("mailto:a@b.c")!.Title);
    }
}

public class ShelfSelectionTests
{
    [Fact]
    public void Click_shift_click_select_all_and_escape()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var selection = new ShelfSelection();
        selection.Click(ids[4]);
        selection.ShiftClick(ids[1], ids);
        Assert.Equal(4, selection.Count);
        selection.Click(ids[2]);
        Assert.False(selection.Contains(ids[2]));
        selection.SelectAll(ids);
        Assert.Equal(6, selection.Count);
        selection.Prune(ids.Skip(3).ToHashSet());
        Assert.Equal(3, selection.Count);
        Assert.Null(selection.Anchor);
        selection.Clear();
        Assert.Equal(0, selection.Count);
    }
}

public class ShelfCodecTests
{
    private static readonly ShelfRestoreContext AllExist = new() { Exists = _ => true, RootAvailable = _ => true };

    [Fact]
    public void Absent_data_is_an_empty_shelf()
    {
        var result = ShelfCodec.Decode(null);
        Assert.Equal(ShelfLoadOutcome.Items, result.Outcome);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[{\"kind\":\"wormhole\"}]")]
    public void Unreadable_values(string json) => Assert.Equal(ShelfLoadOutcome.Unreadable, ShelfCodec.Decode(Encoding.UTF8.GetBytes(json)).Outcome);

    [Fact]
    public void Partial_values_keep_the_readable_entries()
    {
        var json = "[{\"kind\":\"text\",\"text\":\"ok\"},{\"kind\":\"file\"},{\"kind\":\"batch\",\"children\":[{\"kind\":\"link\",\"url\":\"https://a.b\"},{\"kind\":\"nope\"}]}]";
        var result = ShelfCodec.Decode(Encoding.UTF8.GetBytes(json));
        Assert.Equal(ShelfLoadOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void Round_trip_keeps_kinds_pins_and_piles()
    {
        var file = ShelfItem.FileItem(Path.Combine(Path.GetTempPath(), "x.png"), "vol:1:2") with { Pinned = true };
        var pile = ShelfTree.MakePile([ShelfItem.TextItem("note")!, ShelfItem.LinkItem("https://example.com")!]);
        var bytes = ShelfCodec.Encode([file, pile]);
        var result = ShelfCodec.Decode(bytes);
        Assert.Equal(ShelfLoadOutcome.Items, result.Outcome);
        Assert.Equal(file.Id, result.Items[0].Id);
        Assert.True(result.Items[0].Pinned);
        Assert.Equal("vol:1:2", result.Items[0].Bookmark);
        Assert.Equal(2, result.Items[1].Children.Count);
        Assert.Contains("\"pinned\":true", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("\"pinned\":false", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_heals_moved_files_keeps_unmounted_drives_and_drops_dead_files()
    {
        var moved = ShelfItem.FileItem("C:\\old\\moved.txt", "bm");
        var unmounted = ShelfItem.FileItem("E:\\photos\\a.jpg");
        var dead = ShelfItem.FileItem("C:\\gone.txt");
        var context = new ShelfRestoreContext
        {
            Exists = p => p == "C:\\new\\moved.txt",
            RootAvailable = p => !p.StartsWith("E:", StringComparison.Ordinal),
            Heal = item => item.Bookmark == "bm" ? "C:\\new\\moved.txt" : null,
        };
        var restored = ShelfCodec.Sanitize([moved, unmounted, dead], context);
        Assert.Equal(2, restored.Count);
        Assert.Equal("C:\\new\\moved.txt", restored[0].Path);
        Assert.Equal(unmounted.Id, restored[1].Id);
    }

    [Fact]
    public void Restore_drops_blank_text_and_file_links_and_unwraps_single_piles()
    {
        var blank = new ShelfItem { Kind = ShelfItemKind.Text, Text = "   " };
        var fileLink = new ShelfItem { Kind = ShelfItemKind.Link, Url = "file:///c:/x" };
        var lonely = new ShelfItem { Kind = ShelfItemKind.Batch, Pinned = true, Children = [ShelfItem.TextItem("only")!, blank] };
        var restored = ShelfCodec.Sanitize([blank, fileLink, lonely], AllExist);
        var only = Assert.Single(restored);
        Assert.Equal("only", only.Text);
        Assert.True(only.Pinned);
    }

    [Fact]
    public void Restore_caps_at_200_leaves_and_depth_below_4()
    {
        var items = Enumerable.Range(0, 205).Select(i => ShelfItem.TextItem($"t{i}")!).ToList();
        Assert.Equal(200, ShelfTree.LeafCount(ShelfCodec.Sanitize(items, AllExist)));

        var deep = ShelfTree.MakePile([ShelfTree.MakePile([ShelfTree.MakePile([ShelfTree.MakePile([ShelfItem.TextItem("a")!, ShelfItem.TextItem("b")!]), ShelfItem.TextItem("c")!]), ShelfItem.TextItem("d")!]), ShelfItem.TextItem("e")!]);
        var sanitized = Assert.Single(ShelfCodec.Sanitize([deep], AllExist));
        Assert.True(ShelfTree.LeafCount(sanitized) < 5);
    }

    [Fact]
    public void Restored_items_go_first_and_overflow_is_dropped_whole()
    {
        var meanwhile = Enumerable.Range(0, 199).Select(i => ShelfItem.TextItem($"m{i}")!).ToList();
        var restored = new List<ShelfItem> { ShelfTree.MakePile([ShelfItem.TextItem("a")!, ShelfItem.TextItem("b")!]), ShelfItem.TextItem("c")! };
        var merged = ShelfCodec.MergeRestored(restored, meanwhile);
        Assert.Equal(200, ShelfTree.LeafCount(merged));
        Assert.Equal("c", merged[0].Text);
    }
}

public class ShelfDropParserTests
{
    [Fact]
    public void Precedence_is_files_gif_image_url_text()
    {
        var parts = ShelfDropParser.Parse(
        [
            new ShelfDropEntry { Files = ["/tmp/a.txt", "/tmp/a.txt"], Text = "ignored" },
            new ShelfDropEntry { GifData = [1, 2, 3], ImageData = [4] },
            new ShelfDropEntry { ImageData = [4, 5], Url = "https://x.y" },
            new ShelfDropEntry { Url = "https://example.com/page", Text = "Example" },
            new ShelfDropEntry { Text = "just a note" },
            new ShelfDropEntry { Text = "   " },
        ]);
        Assert.Equal([ShelfDropPartKind.File, ShelfDropPartKind.Gif, ShelfDropPartKind.Image, ShelfDropPartKind.Link, ShelfDropPartKind.Text], parts.Select(p => p.Kind));
    }

    [Fact]
    public void A_virtual_url_shortcut_next_to_a_url_becomes_a_link()
    {
        var parts = ShelfDropParser.Parse([new ShelfDropEntry { Files = ["C:\\Temp\\Example.url"], Url = "https://example.com" }]);
        Assert.Equal(ShelfDropPartKind.Link, Assert.Single(parts).Kind);
    }

    [Fact]
    public void Hover_acceptance()
    {
        Assert.True(ShelfDropParser.CanAccept([new ShelfDropEntry { Text = "x" }]));
        Assert.False(ShelfDropParser.CanAccept([new ShelfDropEntry { Text = " " }, new ShelfDropEntry { Url = "file:///c:/x" }]));
    }
}

public class ShelfGestureTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_fast_horizontal_shake_triggers_once_then_cools_down()
    {
        var shake = new ShakeDetector();
        double[] xs = [0, 60, 0, 60, 0, 60];
        var triggered = false;
        for (var i = 0; i < xs.Length; i++)
        {
            triggered |= shake.Add(Ms(i * 50), xs[i]);
        }

        Assert.True(triggered);
        var again = false;
        for (var i = 0; i < xs.Length; i++)
        {
            again |= shake.Add(Ms(400 + (i * 50)), xs[i]);
        }

        Assert.False(again);
    }

    [Fact]
    public void Slow_or_small_movement_does_not_trigger()
    {
        var shake = new ShakeDetector();
        Assert.DoesNotContain(true, Enumerable.Range(0, 8).Select(i => shake.Add(Ms(i * 50), i % 2 == 0 ? 0 : 5)).ToList());
        var slow = new ShakeDetector();
        Assert.DoesNotContain(true, Enumerable.Range(0, 8).Select(i => slow.Add(Ms(i * 300), i % 2 == 0 ? 0 : 100)).ToList());
    }

    [Fact]
    public void Excluded_sources_and_non_content_drags_never_trigger()
    {
        var shake = new ShakeDetector();
        Assert.DoesNotContain(true, Enumerable.Range(0, 6).Select(i => shake.Add(Ms(i * 50), i % 2 == 0 ? 0 : 60, sourceAllowed: false)).ToList());
    }

    [Fact]
    public void A_drag_needs_movement_past_the_threshold_and_ignores_own_windows()
    {
        var tracker = new DragGestureTracker { ThresholdX = 4, ThresholdY = 4 };
        tracker.Press(100, 100, "C:\\app.exe", onOwnWindow: false);
        Assert.False(tracker.Move(103, 103));
        Assert.True(tracker.Move(110, 100));
        Assert.False(tracker.Move(120, 100));
        Assert.True(tracker.Release());
        tracker.Press(0, 0, null, onOwnWindow: true);
        Assert.False(tracker.Move(50, 0));
        Assert.False(tracker.IsAutomaticDrag);
    }

    [Fact]
    public void Exclusions_match_paths_and_names()
    {
        Assert.True(ShelfExclusions.AllowsAutomaticOpen(null, ["x.exe"]));
        Assert.False(ShelfExclusions.AllowsAutomaticOpen("C:\\Apps\\Photoshop.exe", ["photoshop.exe"]));
        Assert.False(ShelfExclusions.AllowsAutomaticOpen("C:\\Apps\\Photoshop.exe", ["Photoshop"]));
        Assert.False(ShelfExclusions.AllowsAutomaticOpen("C:\\Apps\\Photoshop.exe", ["c:\\apps\\photoshop.exe"]));
        Assert.True(ShelfExclusions.AllowsAutomaticOpen("C:\\Apps\\Photoshop.exe", ["D:\\Photoshop.exe"]));
    }

    [Fact]
    public void Dwell_needs_the_same_match_for_150_ms()
    {
        var dwell = new DwellTracker<ShelfEdgeMatch>(TimeSpan.FromMilliseconds(150));
        var left = new ShelfEdgeMatch("1", true);
        Assert.Null(dwell.Update(left, Ms(0)));
        Assert.Null(dwell.Update(left, Ms(149)));
        Assert.Equal(left, dwell.Update(left, Ms(150)));
        Assert.Null(dwell.Update(new ShelfEdgeMatch("1", false), Ms(200)));
        Assert.Null(dwell.Update(null, Ms(400)));
    }
}

public class ShelfGeometryTests
{
    private static ScreenInfo Screen(string id, int x, int width = 1440, int height = 900, int taskbar = 40) => new()
    {
        Id = id,
        FriendlyName = id,
        Bounds = new PixelRect(x, 0, width, height),
        WorkArea = new PixelRect(x, 0, width, height - taskbar),
        Scale = 1,
        IsPrimary = x == 0,
    };

    [Fact]
    public void Grid_columns_and_frames()
    {
        Assert.Equal(3, ShelfGeometry.Columns(276));
        Assert.Equal((4d, 102d, 78d, 88d), ShelfGeometry.TileFrame(3, 3));
        Assert.Equal((4d, 200d, 78d, 88d), ShelfGeometry.TileFrame(6, 3));
        Assert.Equal(1, ShelfGeometry.Columns(10));
    }

    [Fact]
    public void Summon_hangs_16_dip_below_the_pointer_and_stays_in_the_work_area()
    {
        var screen = Screen("1", 0);
        Assert.Equal(new PixelPoint(348, 216), ShelfGeometry.SummonPosition(new PixelPoint(500, 200), 304, 257, screen.WorkArea, 1));
        Assert.Equal(new PixelPoint(8, 8), ShelfGeometry.SummonPosition(new PixelPoint(0, -100), 304, 257, screen.WorkArea, 1));
    }

    [Fact]
    public void The_dock_centres_on_the_tray_icon_and_sits_4_dip_above_the_taskbar()
    {
        var screen = Screen("1", 0);
        var icon = new PixelRect(1200, 868, 28, 24);
        var frame = ShelfGeometry.DockFrame(ShelfDockPlacement.Tray, 180, 40, screen, icon);
        Assert.Equal(new PixelRect(1124, 816, 180, 40), frame);
        var noIcon = ShelfGeometry.DockFrame(ShelfDockPlacement.Tray, 108, 40, screen, null);
        Assert.Equal(1320, noIcon.X);
        var clamped = ShelfGeometry.DockFrame(ShelfDockPlacement.Tray, 180, 40, screen, new PixelRect(1420, 868, 20, 24));
        Assert.Equal(1440 - 180 - 8, clamped.X);
        var top = ShelfGeometry.DockFrame(ShelfDockPlacement.TopCenter, 108, 40, screen, icon);
        Assert.Equal(new PixelRect(666, 4, 108, 40), top);
    }

    [Fact]
    public void An_icon_on_another_monitor_is_not_a_trustworthy_anchor()
    {
        var screen = Screen("1", 0);
        Assert.Null(ShelfGeometry.TrustedAnchor(new PixelRect(3000, 868, 28, 24), screen));
        Assert.Null(ShelfGeometry.TrustedAnchor(null, screen));
    }

    [Fact]
    public void Trigger_and_retreat_frames()
    {
        var screen = Screen("1", 0);
        var icon = new PixelRect(1200, 868, 28, 24);
        var dock = new PixelRect(1124, 816, 180, 40);
        var trigger = ShelfGeometry.TriggerFrame(dock, ShelfDockPlacement.Tray, screen, icon);
        Assert.Equal(new PixelRect(1108, 800, 212, 92), trigger);
        Assert.Equal(new PixelRect(68, 68, 364, 364), ShelfGeometry.RetreatFrame(new PixelRect(100, 100, 300, 300), 1));
    }

    [Fact]
    public void Edges_match_within_200_and_seams_never_count()
    {
        var left = Screen("L", 0);
        var right = Screen("R", 1440);
        IReadOnlyList<ScreenInfo> screens = [left, right];
        Assert.Equal(new ShelfEdgeMatch("L", true), ShelfGeometry.MatchEdge(new PixelPoint(150, 400), screens));
        Assert.Null(ShelfGeometry.MatchEdge(new PixelPoint(1400, 400), screens));
        Assert.Null(ShelfGeometry.MatchEdge(new PixelPoint(1500, 400), screens));
        Assert.Equal(new ShelfEdgeMatch("R", false), ShelfGeometry.MatchEdge(new PixelPoint(2800, 400), screens));
        Assert.Null(ShelfGeometry.MatchEdge(new PixelPoint(700, 400), screens));
    }

    [Fact]
    public void Peek_shows_a_third_and_retreats_beyond_330()
    {
        var screen = Screen("1", 0);
        Assert.Equal(new PixelPoint(-304 + 101, 400 - 128), ShelfGeometry.PeekPosition(screen, true, 304, 257, 400));
        Assert.Equal(new PixelPoint(1440 - 101, 8), ShelfGeometry.PeekPosition(screen, false, 304, 257, 0));
        Assert.False(ShelfGeometry.ShouldRetreat(new PixelPoint(330, 400), screen, left: true));
        Assert.True(ShelfGeometry.ShouldRetreat(new PixelPoint(331, 400), screen, left: true));
        Assert.Equal(new PixelPoint(8, 300), ShelfGeometry.RevealPosition(screen, true, 304, 257, 300));
    }
}

public class ShelfRulesTests
{
    [Theory]
    [InlineData(true, false, true, true, false, true, true)]
    [InlineData(true, false, true, true, true, true, false)]
    [InlineData(true, true, true, true, false, false, false)]
    [InlineData(false, false, true, true, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    public void After_drop_truth_table(bool accepted, bool merged, bool remove, bool close, bool pinned, bool expectRemove, bool expectClose)
    {
        var (r, c) = ShelfDragOutPolicy.AfterDrop(accepted, merged, remove, close, pinned);
        Assert.Equal(expectRemove, r);
        Assert.Equal(expectClose, c);
    }

    [Fact]
    public void Move_is_offered_only_without_protected_items()
    {
        Assert.True(ShelfDragOutPolicy.AllowsMove(removeAfterDrop: true, anyProtected: false));
        Assert.False(ShelfDragOutPolicy.AllowsMove(removeAfterDrop: true, anyProtected: true));
        Assert.False(ShelfDragOutPolicy.AllowsMove(removeAfterDrop: false, anyProtected: false));
    }

    [Fact]
    public void Tooltips_cap_long_text_and_describe_piles()
    {
        var text = ShelfItem.TextItem(new string('a', 600))!;
        Assert.Equal(501, ShelfTooltips.Text(text).Length);
        Assert.EndsWith("…", ShelfTooltips.Text(text), StringComparison.Ordinal);
        var pile = ShelfTree.MakePile([ShelfItem.TextItem("n")!, ShelfItem.LinkItem("https://a.b")!, ShelfItem.LinkItem("https://c.d")!]);
        Assert.Equal("3 items: 1 note, 2 links", ShelfTooltips.Text(pile));
        Assert.Equal("a.png\nPNG File", ShelfTooltips.Text(ShelfItem.FileItem("/x/a.png"), _ => "PNG File"));
    }
}

public sealed class ShelfStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-shelf-tests-" + Guid.NewGuid().ToString("N"));

    public ShelfStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private ShelfStore NewStore() => new(_folder, Path.Combine(_folder, "ShelfFiles"));

    [Fact]
    public async Task Items_persist_and_owned_payloads_are_written_to_the_store()
    {
        var store = NewStore();
        await store.LoadAsync();
        Assert.Equal(ShelfAddResult.Added, store.Add(ShelfDropParser.Parse([new ShelfDropEntry { GifData = [0x47, 0x49, 0x46] }, new ShelfDropEntry { Text = "hi" }])));
        var pile = Assert.Single(store.Items);
        var gif = pile.Children[0];
        Assert.Equal("GIF", gif.Title);
        Assert.True(File.Exists(gif.Path));
        Assert.True(store.IsOwned(gif.Path!));
        store.Flush();

        var reopened = NewStore();
        await reopened.LoadAsync();
        Assert.Equal(ShelfLoadOutcome.Items, reopened.LastLoadOutcome);
        Assert.Equal(2, reopened.LeafCount);
    }

    [Fact]
    public async Task A_full_shelf_refuses_and_deletes_the_written_payloads()
    {
        var store = NewStore();
        await store.LoadAsync();
        for (var i = 0; i < 200; i++)
        {
            store.AddText($"t{i}");
        }

        Assert.Equal(ShelfAddResult.Full, store.Add(ShelfDropParser.Parse([new ShelfDropEntry { GifData = [1, 2] }])));
        var owned = Path.Combine(_folder, "ShelfFiles");
        Assert.True(!Directory.Exists(owned) || !Directory.EnumerateFiles(owned).Any());
    }

    [Fact]
    public async Task An_unreadable_file_is_kept_aside()
    {
        File.WriteAllText(Path.Combine(_folder, ShelfStore.FileName), "{oops");
        var store = NewStore();
        await store.LoadAsync();
        Assert.Equal(ShelfLoadOutcome.Unreadable, store.LastLoadOutcome);
        Assert.Contains(Directory.EnumerateFiles(_folder), f => f.Contains(".unreadable-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_after_a_drag_keeps_pinned_items()
    {
        var store = NewStore();
        await store.LoadAsync();
        store.AddText("keep");
        store.AddText("go");
        var keep = store.Items[0].Id;
        var go = store.Items[1].Id;
        store.SetPinned(keep, true);
        store.RemoveAfterDrag([keep, go]);
        Assert.Equal(keep, Assert.Single(store.Items).Id);
        store.Remove(new HashSet<Guid> { keep });
        Assert.True(store.IsEmpty);
    }

    [Fact]
    public async Task The_living_check_reports_dead_files()
    {
        var store = NewStore();
        await store.LoadAsync();
        var real = Path.Combine(_folder, "real.txt");
        File.WriteAllText(real, "x");
        store.AddFiles([real, Path.Combine(_folder, "missing.txt")]);
        var leaves = ShelfTree.Leaves(store.Items).ToList();
        var living = store.Living(leaves, out var dead);
        Assert.Single(living);
        Assert.Single(dead);
    }
}
