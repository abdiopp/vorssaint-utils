// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Shelf;
using Rivet.App.Settings;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Shelf;
using SkiaSharp;
using Xunit;
using PixelPoint = Rivet.Core.Platform.PixelPoint;

namespace Rivet.App.Tests.Shelf;

public class ShelfAppTests
{
    private static ShelfService Service()
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Shelf, true);
        host.Settings.Set(FeatureKeys.ShelfEnabled, true);
        var service = host.Services.GetRequiredService<ShelfService>();
        service.Sync(true);
        service.Store.Remove(ShelfTree.AllItems(service.Store.Items).Select(i => i.Id).ToHashSet());
        service.Selection.Clear();
        service.HideClassic();
        service.CollapseDocked();
        return service;
    }

    private static string SampleFile(string name, bool image = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-shelf-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        if (image)
        {
            using var surface = SKSurface.Create(new SKImageInfo(160, 120));
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(160, 120), [SKColors.SkyBlue, SKColors.SeaGreen], SKShaderTileMode.Clamp) };
            surface.Canvas.DrawRect(0, 0, 160, 120, paint);
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        else
        {
            File.WriteAllText(path, "hello");
        }

        return path;
    }

    private static void Pump(Func<bool>? until = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            Dispatcher.UIThread.RunJobs();
            if (until?.Invoke() != false)
            {
                return;
            }

            Thread.Sleep(15);
        }
        while (DateTime.UtcNow < deadline);
    }

    private static void Fill(ShelfService service)
    {
        service.Drop([new ShelfDropEntry { Files = [SampleFile("Holiday.png", image: true)] }], null, ShelfSurface.Classic);
        service.Drop([new ShelfDropEntry { Files = [SampleFile("Quarterly report.pdf"), SampleFile("Budget.xlsx"), SampleFile("Notes.txt")] }], null, ShelfSurface.Classic);
        service.Drop([new ShelfDropEntry { Url = "https://example.org/articles/42" }], null, ShelfSurface.Classic);
        service.Drop([new ShelfDropEntry { Text = "Call the printer shop before Friday" }], null, ShelfSurface.Classic);
        var first = service.Store.Items[0];
        service.Store.SetPinned(first.Id, true);
        service.Selection.Click(service.Store.Items[3].Id);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_card_renders_empty_and_with_items(string theme)
    {
        var service = Service();
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        TestApp.Snapshot(new ShelfCardView(service, ShelfSurface.Classic), $"shelf-card-empty-{theme}", 340, theme: variant);

        Fill(service);
        var pile = service.Store.Items[1];
        Assert.True(pile.IsPile);
        Assert.Equal("Quarterly report.pdf +2", pile.Title);
        Assert.Equal(6, service.LeafCount);

        // Thumbnails load off the UI thread; let them land before the snapshot.
        _ = service.Thumbnails.Get(service.Store.Items[0], 1);
        Pump(() => service.Thumbnails.Get(service.Store.Items[0], 1).Image is not null);
        TestApp.Snapshot(new ShelfCardView(service, ShelfSurface.Classic), $"shelf-card-items-{theme}", 340, theme: variant);

        service.ToggleExpanded(pile.Id);
        TestApp.Snapshot(new ShelfCardView(service, ShelfSurface.Docked), $"shelf-card-docked-expanded-{theme}", 340, theme: variant);
        service.ToggleExpanded(pile.Id);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void The_pill_and_badge_render(string theme)
    {
        var service = Service();
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        service.AddFiles([SampleFile("a.txt"), SampleFile("b.txt")]);
        foreach (var (placement, name) in new[] { (ShelfDockPlacement.Tray, "pill"), (ShelfDockPlacement.TopCenter, "badge") })
        {
            var pill = new ShelfPillWindow(service);
            pill.Refresh(placement, service.LeafCount, caught: false);
            var plate = pill.Plate;
            pill.Content = null;
            TestApp.Snapshot(new Border { Padding = new Thickness(16), Child = plate, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }, $"shelf-{name}-{theme}", 260, theme: variant);
            pill.Close();
        }
    }

    [AvaloniaFact]
    public void The_settings_page_renders()
    {
        var host = TestApp.Host;
        Service();
        foreach (var theme in new[] { "light", "dark" })
        {
            var vm = new SettingsViewModel(host.Services);
            vm.Navigate(ShelfModule.PageId);
            Assert.Equal(ShelfModule.PageId, vm.CurrentPageId);
            var window = new SettingsWindow(vm) { Width = 1080, Height = 1100, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
            window.Show();
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame!.Save(Path.Combine(TestApp.SnapshotDirectory, $"settings-shelf-{theme}.png"));
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_multi_item_drop_becomes_one_pile_and_capacity_is_enforced()
    {
        var service = Service();
        Assert.True(service.Drop([new ShelfDropEntry { Files = [SampleFile("1.txt"), SampleFile("2.txt")] }], null, ShelfSurface.Classic));
        Assert.Single(service.Store.Items);
        Assert.Equal(2, service.LeafCount);

        var many = Enumerable.Range(0, ShelfConstants.MaxLeaves).Select(i => $"C:\\missing\\{i}.txt").ToList();
        Assert.False(service.Drop([new ShelfDropEntry { Files = many }], null, ShelfSurface.Classic));
        Assert.Equal(2, service.LeafCount);
    }

    [AvaloniaFact]
    public void Shelf_intake_adds_files_and_text_only_while_enabled()
    {
        var service = Service();
        var intake = TestApp.Host.Services.GetRequiredService<IShelfIntake>();
        Assert.Same(service, intake);
        Assert.True(intake.IsAvailable);
        intake.AddText("A note");
        Assert.Equal(ShelfItemKind.Text, Assert.Single(service.Store.Items).Kind);

        service.Sync(false);
        Assert.False(intake.IsAvailable);
        intake.AddText("Ignored");
        Assert.Single(service.Store.Items);
        service.Sync(true);
    }

    [AvaloniaFact]
    public void A_shake_during_a_drag_summons_the_card_at_the_pointer()
    {
        var service = Service();
        TestApp.Host.Settings.Set(ShelfSettings.ShakeToOpen, true);
        service.Sync(true);
        var monitor = service.Monitor!;
        monitor.Press(new PixelPoint(500, 400), onOwnWindow: false);
        var x = 500;
        for (var i = 0; i < 12; i++)
        {
            x += i % 2 == 0 ? 90 : -90;
            monitor.Move(new PixelPoint(x, 400));
        }

        Assert.True(service.IsClassicVisible);
        Assert.True(service.ClassicWindow!.IsSpeculative);

        // Released without a drop while empty: the card goes away.
        monitor.Release();
        service.FinishDragEnd();
        Assert.False(service.IsClassicVisible);
    }

    [AvaloniaFact]
    public void A_drag_starting_on_the_shelf_itself_never_opens_it()
    {
        var service = Service();
        var monitor = service.Monitor!;
        monitor.Press(new PixelPoint(500, 400), onOwnWindow: true);
        var x = 500;
        for (var i = 0; i < 12; i++)
        {
            x += i % 2 == 0 ? 90 : -90;
            monitor.Move(new PixelPoint(x, 400));
        }

        Assert.False(service.IsClassicVisible);
        monitor.Release();
    }

    [AvaloniaFact]
    public void An_excluded_app_does_not_open_the_shelf_automatically()
    {
        var service = Service();
        Assert.True(ShelfExclusions.AllowsAutomaticOpen(null, ["photoshop.exe"]));
        TestApp.Host.Settings.Set(ShelfSettings.AutomaticExclusions, ["C:\\Tools\\paint.exe"]);
        service.OnShake(new PixelPoint(10, 10), "C:\\Tools\\paint.exe");
        Assert.False(service.IsClassicVisible);
        service.OnShake(new PixelPoint(10, 10), "C:\\Windows\\explorer.exe");
        Assert.True(service.IsClassicVisible);
        TestApp.Host.Settings.Set(ShelfSettings.AutomaticExclusions, []);
    }

    [AvaloniaFact]
    public void The_explorer_selection_ticket_adds_new_files_then_toggles()
    {
        var service = Service();
        var a = SampleFile("a.txt");
        var b = SampleFile("b.txt");
        service.ShortcutPressed();
        Assert.True(service.IsClassicVisible);
        Assert.Empty(service.Store.Items);
        service.HideClassic();

        TestApp.Host.Settings.Set(ShelfSettings.ShortcutAddsExplorerSelection, true);
        service.ShortcutPressed();
        var ticket = typeof(ShelfService).GetField("_ticket", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var current = (int)ticket.GetValue(service)!;
        service.ResolveTicket(current, [a, b]);
        Assert.Equal(2, service.LeafCount);
        Assert.True(service.IsClassicVisible);

        // Nothing new: the shortcut just toggles (hides).
        service.ResolveTicket(current, [a]);
        Assert.False(service.IsClassicVisible);

        // A stale ticket is discarded.
        service.ResolveTicket(current - 1, [SampleFile("c.txt")]);
        Assert.Equal(2, service.LeafCount);
        TestApp.Host.Settings.Set(ShelfSettings.ShortcutAddsExplorerSelection, false);
    }

    [AvaloniaFact]
    public void Drag_out_removes_unprotected_items_and_keeps_pinned_ones()
    {
        var service = Service();
        service.Drop([new ShelfDropEntry { Files = [SampleFile("keep.txt")] }], null, ShelfSurface.Classic);
        service.Drop([new ShelfDropEntry { Files = [SampleFile("go.txt")] }], null, ShelfSurface.Classic);
        var keep = service.Store.Items[0];
        var go = service.Store.Items[1];
        service.Store.SetPinned(keep.Id, true);
        service.Selection.SelectAll([keep.Id, go.Id]);
        var leaves = service.PrepareDragOut(go.Id)!;
        Assert.Equal(2, leaves.Count);
        Assert.False(service.AllowsMove(leaves));
        service.CompleteDragOut(leaves, accepted: true, merged: false, ShelfSurface.Classic, surfacePinned: false);
        Assert.Equal(keep.Id, Assert.Single(service.Store.Items).Id);
    }

    [AvaloniaFact]
    public void A_dead_file_is_dropped_with_a_message_and_no_drag_starts()
    {
        var service = Service();
        var path = SampleFile("gone.txt");
        service.AddFiles([path]);
        File.Delete(path);
        Assert.Null(service.PrepareDragOut(service.Store.Items[0].Id));
        Assert.Empty(service.Store.Items);
    }

    [AvaloniaFact]
    public void Clear_all_keeps_pins_and_reveal_goes_to_explorer()
    {
        var service = Service();
        service.AddFiles([SampleFile("x.txt")]);
        service.AddText("note");
        service.Store.SetPinned(service.Store.Items[0].Id, true);
        service.Reveal(ShelfTree.Leaves(service.Store.Items).ToList());
        Assert.Single(TestApp.Host.Services.GetRequiredService<FakeShelfPlatform>().Revealed, p => p.EndsWith("x.txt", StringComparison.Ordinal));
        service.ClearAll();
        Assert.Single(service.Store.Items);
        Assert.True(service.Store.Items[0].Pinned);
    }

    [AvaloniaFact]
    public void The_docked_pill_appears_with_items_and_expands_on_request()
    {
        var service = Service();
        TestApp.Host.Settings.Set(ShelfSettings.DropZoneEnabled, true);
        Assert.False(service.IsPillVisible);
        service.AddText("hello");
        Assert.True(service.IsPillVisible);
        service.ExpandDocked();
        Assert.True(service.IsDockedExpanded);
        Assert.False(service.IsPillVisible);
        service.CollapseDocked();
        Assert.True(service.IsPillVisible);
        service.Summon();
        Assert.False(service.IsPillVisible);
        service.HideClassic();
    }
}
