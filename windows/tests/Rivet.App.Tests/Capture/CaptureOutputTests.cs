// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture;
using Rivet.App.Features.Capture.Output;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Features.Capture.Recent;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;
using Rivet.Platform.Fake.Capture;
using Xunit;

namespace Rivet.App.Tests.Capture;

/// <summary>Saving, copying, the history store and the after-capture pipeline (spec 01 §3.7, §3.8, §3.12).</summary>
public class CaptureOutputTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-capture-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ISettingsStore _settings = TestApp.Host.Services.GetRequiredService<ISettingsStore>();

    public CaptureOutputTests()
    {
        Directory.CreateDirectory(_folder);
        _settings.Set(CaptureSettings.SaveFolder, _folder);
    }

    private static IServiceProvider Services => TestApp.Host.Services;

    /// <summary>The shared host without the screenshot editor module.</summary>
    private sealed class WithoutEditor(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IScreenshotEditor) ? null : inner.GetService(serviceType);
    }

    public void Dispose()
    {
        foreach (var key in new[]
                 {
                     CaptureSettings.SaveFolder.Key, CaptureSettings.FileNamePattern.Key, CaptureSettings.SaveSubfolder.Key,
                     CaptureSettings.FileNumberNext.Key, CaptureSettings.Downscale.Key, CaptureSettings.DefaultAction.Key,
                     CaptureSettings.CopyToClipboard.Key, CaptureSettings.PreviewEnabled.Key,
                 })
        {
            _settings.Reset(key);
        }

        Services.GetRequiredService<QuickPreviewController>().Close();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PixelBuffer Image(int width = 64, int height = 48, double scale = 1)
    {
        var image = new PixelBuffer(width, height) { Scale = scale };
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = 40;
            image.Pixels[i + 1] = 120;
            image.Pixels[i + 2] = 200;
            image.Pixels[i + 3] = 255;
        }

        return image;
    }

    [AvaloniaFact]
    public async Task Saves_use_the_pattern_number_and_unique_names()
    {
        var output = Services.GetRequiredService<CaptureOutputService>();
        _settings.Set(CaptureSettings.FileNamePattern, "Shot %##");
        _settings.Set(CaptureSettings.FileNumberNext, 7);
        var first = await output.SaveAsync(Image(), applyDownscale: true);
        var second = await output.SaveAsync(Image(), applyDownscale: true);
        Assert.Equal(Path.Combine(_folder, "Shot 07.png"), first!.Path);
        Assert.Equal(Path.Combine(_folder, "Shot 08.png"), second!.Path);
        Assert.Equal(7, first.ConsumedNumber);
        Assert.Equal(9, _settings.Get(CaptureSettings.FileNumberNext));
        Assert.Equal(Path.GetFileName(_folder), first.FolderName);

        _settings.Set(CaptureSettings.FileNamePattern, "fixed");
        var a = await output.SaveAsync(Image(), applyDownscale: true);
        var b = await output.SaveAsync(Image(), applyDownscale: true);
        Assert.Equal("fixed.png", Path.GetFileName(a!.Path));
        Assert.Equal("fixed 2.png", Path.GetFileName(b!.Path));
        Assert.Null(a.ConsumedNumber);
    }

    [AvaloniaFact]
    public async Task Saves_go_into_the_dated_subfolder_or_the_base_folder_when_it_cannot_be_made()
    {
        var output = Services.GetRequiredService<CaptureOutputService>();
        _settings.Set(CaptureSettings.SaveSubfolder, "shots/%year");
        var saved = await output.SaveAsync(Image(), applyDownscale: true);
        Assert.Equal(Path.Combine(_folder, "shots", DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)), Path.GetDirectoryName(saved!.Path));

        await File.WriteAllTextAsync(Path.Combine(_folder, "blocked"), "a file, not a folder", TestContext.Current.CancellationToken);
        _settings.Set(CaptureSettings.SaveSubfolder, "blocked/inner");
        var fallback = await output.SaveAsync(Image(), applyDownscale: true);
        Assert.Equal(_folder, Path.GetDirectoryName(fallback!.Path));
    }

    [AvaloniaFact]
    public async Task Saved_pngs_carry_their_density_and_honour_save_at_1x()
    {
        var output = Services.GetRequiredService<CaptureOutputService>();
        var raw = await output.SaveAsync(Image(200, 100, scale: 2), applyDownscale: true);
        var decoded = CaptureImaging.DecodeFile(raw!.Path, requireDpi: true)!;
        Assert.Equal((200, 100, 2.0), (decoded.Width, decoded.Height, decoded.Scale));

        _settings.Set(CaptureSettings.Downscale, true);
        var small = await output.SaveAsync(Image(200, 100, scale: 2), applyDownscale: true);
        var oneX = CaptureImaging.DecodeFile(small!.Path, requireDpi: true)!;
        Assert.Equal((100, 50, 1.0), (oneX.Width, oneX.Height, oneX.Scale));
    }

    [AvaloniaFact]
    public async Task Copies_put_the_image_and_a_cached_file_on_the_clipboard()
    {
        var output = Services.GetRequiredService<CaptureOutputService>();
        var clipboard = Services.GetRequiredService<FakeCaptureClipboard>();
        var before = clipboard.ChangeCount;
        Assert.True(await output.CopyAsync(Image(), applyDownscale: true));
        Assert.True(clipboard.ChangeCount > before);
        Assert.NotNull(clipboard.Image);
        Assert.True(File.Exists(clipboard.FilePath));
        Assert.EndsWith(Path.Combine("Copied Screenshots", Path.GetFileName(clipboard.FilePath!)), clipboard.FilePath, StringComparison.Ordinal);

        // A stale automatic copy is dropped (the clipboard changed meanwhile).
        Assert.False(await output.CopyAsync(Image(), applyDownscale: true, stillWanted: () => false));
    }

    [AvaloniaFact]
    public async Task Icapture_output_reports_the_saved_record()
    {
        ICaptureOutput output = Services.GetRequiredService<CaptureOutputService>();
        Assert.Equal(_folder, output.SaveFolder);
        Assert.EndsWith(".png", output.SuggestFileName(), StringComparison.Ordinal);
        var result = await output.SaveAsync(Image(30, 20), existingRecordId: "11111111-2222-3333-4444-555555555555");
        Assert.Equal("11111111-2222-3333-4444-555555555555", result!.Record.Id);
        Assert.Equal(CaptureKind.Screenshot, result.Record.Kind);
        Assert.Equal((30, 20), (result.Record.PixelWidth, result.Record.PixelHeight));
        Assert.True(File.Exists(result.Path));
    }

    [AvaloniaFact]
    public async Task The_save_action_saves_and_shows_the_confirmation_preview()
    {
        _settings.Set(CaptureSettings.DefaultAction, "save");
        var router = Services.GetRequiredService<CaptureRouter>();
        await router.RouteAsync(new CapturedImage(Image(), new PixelRect(10, 10, 64, 48)));
        Assert.Single(Directory.GetFiles(_folder, "*.png"));
        Assert.True(Services.GetRequiredService<QuickPreviewController>().IsOpen);
    }

    [AvaloniaFact]
    public async Task The_copy_action_copies_and_a_disabled_confirmation_shows_nothing()
    {
        _settings.Set(CaptureSettings.DefaultAction, "copy");
        _settings.Set(CaptureSettings.PreviewEnabled, false);
        var clipboard = Services.GetRequiredService<FakeCaptureClipboard>();
        var before = clipboard.ChangeCount;
        await Services.GetRequiredService<CaptureRouter>().RouteAsync(new CapturedImage(Image(), default));
        Assert.True(clipboard.ChangeCount > before);
        Assert.False(Services.GetRequiredService<QuickPreviewController>().IsOpen);
        Assert.Empty(Directory.GetFiles(_folder, "*.png"));
    }

    [AvaloniaFact]
    public async Task Edit_without_an_editor_saves_instead()
    {
        _settings.Set(CaptureSettings.DefaultAction, "edit");
        var router = new CaptureRouter(new WithoutEditor(Services));
        await router.RouteAsync(new CapturedImage(Image(), default));
        Assert.Single(Directory.GetFiles(_folder, "*.png"));
    }

    [AvaloniaFact]
    public async Task Ask_each_time_only_shows_the_preview()
    {
        _settings.Set(CaptureSettings.DefaultAction, string.Empty);
        await Services.GetRequiredService<CaptureRouter>().RouteAsync(new CapturedImage(Image(), new PixelRect(100, 100, 64, 48)));
        Assert.True(Services.GetRequiredService<QuickPreviewController>().IsOpen);
        Assert.Empty(Directory.GetFiles(_folder, "*.png"));
    }
}

/// <summary>The recent-captures store on its own folder.</summary>
public class RecentCapturesStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-recent-tests-" + Guid.NewGuid().ToString("N"));

    public RecentCapturesStoreTests() => RecentCapturesStore.OrphanGrace = TimeSpan.Zero;

    public void Dispose()
    {
        RecentCapturesStore.OrphanGrace = TimeSpan.FromMinutes(1);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PixelBuffer Image(int size = 32) => new(size, size) { Scale = 1.25 };

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Screenshots_persist_with_their_files_and_anchor()
    {
        var store = new RecentCapturesStore(_folder);
        var id = store.AddScreenshot(Image(), new PixelRect(5, 6, 32, 32));
        await WaitFor(() => store.Entries.Count == 1);
        await store.FlushAsync();
        var entry = store.Entries[0];
        Assert.Equal(id, entry.Id);
        Assert.True(File.Exists(store.ScreenshotPath(entry)));
        Assert.True(File.Exists(store.ThumbnailPath(entry)));
        Assert.Equal(1.25, entry.Scale);

        var reloaded = new RecentCapturesStore(_folder);
        Assert.Single(reloaded.Items);
        Assert.Equal((5, 6), (reloaded.Entries[0].AnchorX, reloaded.Entries[0].AnchorY));
        Assert.Equal(CaptureKind.Screenshot, reloaded.Items[0].Kind);
    }

    [Fact]
    public async Task A_recording_saved_again_at_the_same_path_replaces_its_entry()
    {
        var store = new RecentCapturesStore(_folder);
        var video = Path.Combine(_folder, "take.mp4");
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(video, "not really a video", TestContext.Current.CancellationToken);
        var record = new CaptureRecord { Id = Guid.NewGuid().ToString(), Kind = CaptureKind.Recording, FilePath = video, CreatedAt = DateTimeOffset.Now, Duration = TimeSpan.FromSeconds(12) };
        store.Add(record);
        store.Add(record with { Id = Guid.NewGuid().ToString(), CreatedAt = DateTimeOffset.Now.AddSeconds(1) });
        await WaitFor(() => store.Entries.Count >= 1);
        await store.FlushAsync();
        Assert.Single(store.Entries);
        Assert.Equal(video, store.Items[0].FilePath);
        Assert.Equal(TimeSpan.FromSeconds(12), store.Items[0].Duration);

        store.Clear();
        await store.FlushAsync();
        Assert.Empty(store.Entries);
        Assert.True(File.Exists(video)); // recordings themselves are never deleted
    }

    [Fact]
    public async Task A_screenshot_saved_back_under_its_id_replaces_its_row()
    {
        var store = new RecentCapturesStore(_folder);
        var id = store.AddScreenshot(Image(), default);
        await WaitFor(() => store.Entries.Count == 1);
        await store.FlushAsync();
        var oldPng = store.ScreenshotPath(store.Entries[0])!;

        // The editor saves an edited history capture back with the same id (from outside the history folder).
        var source = _folder + "-saved";
        Directory.CreateDirectory(source);
        try
        {
            var edited = Path.Combine(source, "edited.png");
            CaptureImaging.WritePngAtomically(CaptureImaging.EncodePng(Image(48)), edited);
            store.Add(new CaptureRecord { Id = id, Kind = CaptureKind.Screenshot, FilePath = edited, CreatedAt = DateTimeOffset.Now });
            await WaitFor(() => store.Entries is [{ PixelWidth: 48 }]);
            await store.FlushAsync();

            var entry = Assert.Single(store.Entries);
            Assert.Equal(id, entry.Id);
            Assert.True(File.Exists(store.ScreenshotPath(entry)));
            Assert.True(File.Exists(store.ThumbnailPath(entry)));
            Assert.False(File.Exists(oldPng)); // the replaced files are cleaned up
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Removing_an_entry_deletes_its_cached_files()
    {
        var store = new RecentCapturesStore(_folder);
        store.AddScreenshot(Image(), default);
        await WaitFor(() => store.Entries.Count == 1);
        await store.FlushAsync();
        var entry = store.Entries[0];
        var png = store.ScreenshotPath(entry)!;
        store.Remove(entry.Id);
        await store.FlushAsync();
        Assert.False(File.Exists(png));
    }

    [Fact]
    public void A_corrupt_index_freezes_the_history_instead_of_wiping_it()
    {
        Directory.CreateDirectory(_folder);
        var orphan = Path.Combine(_folder, $"{Guid.NewGuid()}.png");
        File.WriteAllBytes(orphan, [1, 2, 3]);
        File.WriteAllText(Path.Combine(_folder, "history.json"), "{ not json");
        var store = new RecentCapturesStore(_folder);
        Assert.True(store.IsFrozen);
        Assert.Empty(store.Items);
        store.Clear();
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void A_missing_index_with_cached_images_also_freezes()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, $"{Guid.NewGuid()}-thumbnail.png"), [1]);
        Assert.True(new RecentCapturesStore(_folder).IsFrozen);
        Assert.False(new RecentCapturesStore(Path.Combine(_folder, "fresh")).IsFrozen);
    }

    [Fact]
    public void Entries_whose_files_vanished_are_pruned_on_load()
    {
        Directory.CreateDirectory(_folder);
        var present = Guid.NewGuid().ToString();
        File.WriteAllBytes(Path.Combine(_folder, $"{present}.png"), [1]);
        var entries = new[]
        {
            new { id = present, kind = "screenshot", createdAt = DateTimeOffset.Now, screenshotName = $"{present}.png" },
            new { id = Guid.NewGuid().ToString(), kind = "screenshot", createdAt = DateTimeOffset.Now, screenshotName = "gone.png" },
            new { id = Guid.NewGuid().ToString(), kind = "screenshot", createdAt = DateTimeOffset.Now, screenshotName = "..\\escape.png" },
        };
        File.WriteAllText(Path.Combine(_folder, "history.json"), JsonSerializer.Serialize(entries));
        var store = new RecentCapturesStore(_folder);
        Assert.Single(store.Entries);
        Assert.Equal(present, store.Entries[0].Id);
    }
}
