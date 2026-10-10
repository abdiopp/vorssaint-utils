// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): fine for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.RecordingEditor;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;
using Xunit;

namespace Rivet.App.Tests.RecordingEditor;

public class RecordingEditorTests
{
    private static string NewTake(bool withAudio = true)
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-editor-tests", "Take-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
        SampleTake.Write(folder, withAudio: withAudio);
        return folder;
    }

    private static EditorSession Load(string folder)
    {
        var task = EditorSession.LoadAsync(folder, TestApp.Host.Services);
        Pump(() => task.IsCompleted, TimeSpan.FromSeconds(10));
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> or the timeout.</summary>
    private static void Pump(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static EditorWindow Open(EditorSession session, ThemeVariant theme)
    {
        var window = new EditorWindow(session) { Width = 1280, Height = 800, RequestedThemeVariant = theme };
        window.Show();
        Pump(() => session.Playback.FramesRendered > 0 && session.Thumbnails.All(t => t is not null)
                   && (!session.HasSystemAudio || session.SystemWaveform is not null), TimeSpan.FromSeconds(15));
        return window;
    }

    private static void Snapshot(Window window, string name)
    {
        Pump(() => false, TimeSpan.FromMilliseconds(150));
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, name + ".png"));
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Editor_window_renders(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var session = Load(NewTake());
        Assert.True(session.IsReady);
        Assert.NotEmpty(session.Document.ZoomSegments);   // generated once from the clicks
        Assert.True(session.Document.ZoomsGenerated);
        var window = Open(session, variant);
        session.Playback.Seek(2.3);
        Pump(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, $"recording-editor-{theme}");

        // Studio look, a caption selected ("This text") and a cut selection on the filmstrip.
        session.ApplyLook(RecorderLook.Studio);
        session.AddTextAt(3);
        session.SetCutSelection(new TimeRange(7.5, 8.6));
        Pump(() => false, TimeSpan.FromMilliseconds(400));
        Snapshot(window, $"recording-editor-studio-text-{theme}");

        // A blur being placed (raw recording on the stage) and the Zoom tab.
        session.AddBlurAt(1);
        Pump(() => false, TimeSpan.FromMilliseconds(400));
        Snapshot(window, $"recording-editor-blur-{theme}");
        session.ClearSelection();
        session.SetTab(InspectorTab.Zoom);
        Pump(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, $"recording-editor-zoom-tab-{theme}");
        session.SetTab(InspectorTab.Pointer);
        Pump(() => false, TimeSpan.FromMilliseconds(200));
        Snapshot(window, $"recording-editor-pointer-tab-{theme}");

        // "This zoom" (aimed) and "This image".
        var zoom = session.Document.ZoomSegments[0];
        session.Select(LaneKind.Zoom, zoom.Id);
        session.SetZoomFocus(zoom.Id, 0.3, 0.3);
        Pump(() => false, TimeSpan.FromMilliseconds(300));
        Snapshot(window, $"recording-editor-zoom-panel-{theme}");
        var logo = Path.Combine(Path.GetTempPath(), "rivet-editor-tests", $"logo-{theme}.png");
        using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(200, 100)))
        {
            surface.Canvas.Clear(new SkiaSharp.SKColor(0xFF, 0x45, 0x3A));
            using var image = surface.Snapshot();
            Imaging.Skia.SkiaConvert.SavePng(image, logo);
        }

        var add = session.AddImageAsync(logo, 0);
        Pump(() => add.IsCompleted, TimeSpan.FromSeconds(5));
        Pump(() => false, TimeSpan.FromMilliseconds(400));
        Assert.Equal(LaneKind.Image, session.SelectedKind);
        Snapshot(window, $"recording-editor-image-panel-{theme}");
        window.ForceClose();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Popovers_render(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var session = Load(NewTake(withAudio: false));
        session.SaveBackdropCustom(new RecorderBackdrop { Kind = RecorderBackdropKind.Gradient, Colors = [new RgbValue(0.91, 0.12, 0.39), new RgbValue(1, 0.8, 0)] });
        session.SetBackdropLook(new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = "candy" });

        var picker = new Border { Padding = new Thickness(14), Child = new BackgroundPicker(session) };
        picker.Styles.Add(new EditorStyles());
        TestApp.Snapshot(picker, $"recording-editor-background-{theme}", 330, theme: variant);

        session.SetExportSpeed(1.25);
        var speed = new Border { Padding = new Thickness(14), Child = SpeedPopover.Build(session, () => { }) };
        speed.Styles.Add(new EditorStyles());
        TestApp.Snapshot(speed, $"recording-editor-speed-{theme}", 370, theme: variant);
        session.Dispose();
    }

    [AvaloniaFact]
    public void Persisted_document_reopens_and_the_take_is_deleted_on_close()
    {
        var folder = NewTake(withAudio: false);
        var session = Load(folder);
        session.AddTextAt(2);
        Assert.True(File.Exists(Path.Combine(folder, EditDocument.FileName)));
        var saved = EditDocument.Read(folder)!;
        Assert.Single(saved.Texts);

        // A second open reads the same document (and does not regenerate zooms).
        session.Dispose();
        var again = Load(folder);
        Assert.Single(again.Document.Texts);
        Assert.Equal(saved.ZoomSegments.Count, again.Document.ZoomSegments.Count);
        again.Dispose();

        var service = TestApp.Host.Services.GetRequiredService<RecordingEditorService>();
        var open = service.OpenAsync(folder);
        Pump(() => open.IsCompleted, TimeSpan.FromSeconds(10));
        var window = Assert.Single(service.OpenEditors);
        window.ForceClose();
        Pump(() => !Directory.Exists(folder), TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(folder));
    }

    [AvaloniaFact]
    public void Keyboard_follows_the_windows_shortcuts()
    {
        var session = Load(NewTake(withAudio: false));
        var window = Open(session, ThemeVariant.Dark);
        var before = session.Document.ZoomSegments.Count;

        // Delete with a cut selection cuts it out; Ctrl+Z brings it back; Ctrl+Y redoes.
        session.SetCutSelection(new TimeRange(4, 5));
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Single(session.Document.Cuts);
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Empty(session.Document.Cuts);
        window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
        Assert.Single(session.Document.Cuts);

        // Delete with a zoom selected removes it; Esc deselects.
        var zoom = session.Document.ZoomSegments[0];
        session.Select(LaneKind.Zoom, zoom.Id);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Equal(before - 1, session.Document.ZoomSegments.Count);
        session.AddZoomAt(0.5);
        Assert.Equal(LaneKind.Zoom, session.SelectedKind);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(session.SelectedKind);

        // Space plays and pauses.
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.True(session.Playback.IsPlaying);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(session.Playback.IsPlaying);
        window.ForceClose();
    }

    [AvaloniaFact]
    public void Lanes_and_filmstrip_respond_to_the_mouse()
    {
        var session = Load(NewTake(withAudio: false));
        var window = Open(session, ThemeVariant.Light);
        Pump(() => false, TimeSpan.FromMilliseconds(100));
        var lane = window.GetVisualDescendants().OfType<LaneControl>().First(l => l.Kind == LaneKind.Zoom);
        var strip = window.GetVisualDescendants().OfType<Filmstrip>().Single();
        Point At(Control control, double time, double y) =>
            control.TranslatePoint(new Point(time / session.Duration * control.Bounds.Width, y), window)!.Value;

        // Press on the zoom block's body selects it; dragging moves it as one undo step.
        var zoom = session.Document.ZoomSegments[0];
        var undo = session.History.UndoCount;
        var grab = At(lane, (zoom.Start + zoom.End) / 2, 15);
        window.MouseDown(grab, MouseButton.Left);
        Assert.Equal(LaneKind.Zoom, session.SelectedKind);
        window.MouseMove(grab - new Point(60, 0));
        window.MouseMove(grab - new Point(120, 0));
        window.MouseUp(grab - new Point(120, 0), MouseButton.Left);
        var moved = session.Document.ZoomSegments[0];
        Assert.True(moved.Start < zoom.Start - 0.3, $"moved from {zoom.Start} to {moved.Start}");
        Assert.Equal(zoom.Length, moved.Length, 6);
        Assert.Equal(undo + 1, session.History.UndoCount);

        // First click on empty lane space puts the selection down; the next adds a zoom there.
        var empty = At(lane, 11.6, 15);
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        Assert.Null(session.SelectedKind);
        var count = session.Document.ZoomSegments.Count;
        var spot = At(lane, 10.8, 15);   // after the moved block: room for a new zoom to the end
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        Assert.Equal(count + 1, session.Document.ZoomSegments.Count);
        Assert.Equal(LaneKind.Zoom, session.SelectedKind);

        // Shift-drag on the filmstrip picks a stretch; Delete cuts it; clicking the seam restores it.
        var from = At(strip, 6.0, 27);
        var to = At(strip, 7.5, 27);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(from + new Point(10, 0), RawInputModifiers.Shift);
        window.MouseMove(to, RawInputModifiers.Shift);
        window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift);
        Assert.NotNull(session.CutSelection);
        Assert.InRange(session.CutSelection!.Value.Start, 5.8, 6.2);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        var cut = Assert.Single(session.Document.Cuts);
        Assert.Null(session.CutSelection);
        var seam = At(strip, cut.Start, 27);
        window.MouseDown(seam, MouseButton.Left);
        window.MouseUp(seam, MouseButton.Left);
        Assert.Empty(session.Document.Cuts);

        // A plain click on the strip seeks there.
        var click = At(strip, 9, 27);
        window.MouseDown(click, MouseButton.Left);
        window.MouseUp(click, MouseButton.Left);
        Assert.Equal(9, session.PlayheadSource, 1);

        // Dragging the start trim handle trims (the playhead follows to the new start).
        var handle = At(strip, 0, 27) + new Point(5, 0);
        window.MouseDown(handle, MouseButton.Left);
        window.MouseMove(handle + new Point(40, 0));
        window.MouseMove(At(strip, 1.5, 27));
        window.MouseUp(At(strip, 1.5, 27), MouseButton.Left);
        Assert.InRange(session.Document.TrimStart, 1.3, 1.7);
        Assert.Equal(0, session.Playback.OutputTime, 3);
        window.ForceClose();
    }

    [AvaloniaFact]
    public void Copy_as_gif_puts_a_verified_gif_file_on_the_clipboard()
    {
        var session = Load(NewTake(withAudio: false));
        session.Apply(session.Document with { TrimStart = 1, TrimEnd = 2.5, GifSize = GifSize.Small });
        var export = session.ExportAsync(ExportKind.CopyGif);
        Pump(() => export.IsCompleted, TimeSpan.FromSeconds(30));
        Assert.True(export.IsCompletedSuccessfully);
        var files = TestApp.Host.Services.GetRequiredService<IClipboardService>().GetFiles();
        var file = Assert.Single(files);
        Assert.EndsWith(".gif", file, StringComparison.Ordinal);
        Assert.True(GifDecoder.LooksLikeGif(File.ReadAllBytes(file), out var frames) && frames == 18);
        Assert.True(session.HasExported);
        Assert.False(session.IsExporting);
        // Nothing but the GIF itself was left in the copy folder by this export.
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(file)!), f => Path.GetFileName(f).StartsWith('.'));
        session.Dispose();
    }

    [AvaloniaFact]
    public void Saving_video_without_an_encoder_explains_instead_of_failing()
    {
        var session = Load(NewTake(withAudio: false));
        Assert.False(session.CanEncodeVideo);
        var export = session.ExportAsync(ExportKind.Save);
        Pump(() => export.IsCompleted, TimeSpan.FromSeconds(5));
        Assert.False(session.HasExported);
        Assert.Null(session.FinishedFile);
        session.Dispose();
    }

    [AvaloniaFact]
    public void An_undecodable_master_can_still_be_saved_as_the_raw_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "rivet-editor-tests", "raw-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Take-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
        var saveTo = Path.Combine(root, "Saved");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(saveTo);
        File.WriteAllBytes(Path.Combine(folder, "take.mp4"), [1, 2, 3, 4, 5]);

        var settings = Core.Settings.SettingsStore.InMemory();
        settings.Set(RecordingEditorSettings.SaveFolder, saveTo);
        var services = new ServiceCollection()
            .AddSingleton<Core.Settings.ISettingsStore>(settings)
            .AddSingleton(Core.App.AppPaths.ForTemporaryDirectory(root))
            .AddSingleton<IVideoFrameSourceFactory, BrokenDecoder>()
            .BuildServiceProvider();
        var task = EditorSession.LoadAsync(folder, services);
        Pump(() => task.IsCompleted, TimeSpan.FromSeconds(10));
        var session = task.Result;
        Assert.False(session.IsReady);
        Assert.NotNull(session.LoadError);

        var save = session.ExportAsync(ExportKind.Save);
        Pump(() => save.IsCompleted, TimeSpan.FromSeconds(10));
        Assert.True(session.HasExported);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(session.FinishedFile!));
        Assert.Equal(saveTo, Path.GetDirectoryName(session.FinishedFile));
        session.Dispose();
    }

    private sealed class BrokenDecoder : IVideoFrameSourceFactory
    {
        public IVideoFrameSource Open(string path, VideoOpenOptions? options = null) => throw new MediaUnavailableException("No decoder here.");
    }

    [AvaloniaFact]
    public void Feature_removal_closes_editors()
    {
        var folder = NewTake(withAudio: false);
        var service = TestApp.Host.Services.GetRequiredService<RecordingEditorService>();
        var open = service.OpenAsync(folder);
        Pump(() => open.IsCompleted, TimeSpan.FromSeconds(10));
        Assert.NotEmpty(service.OpenEditors);
        service.CloseAll();
        Pump(() => service.OpenEditors.Count == 0 && !Directory.Exists(folder), TimeSpan.FromSeconds(10));
        Assert.Empty(service.OpenEditors);
        Assert.False(Directory.Exists(folder));
    }

    [AvaloniaFact]
    public void Editor_contract_is_registered()
    {
        Assert.IsType<RecordingEditorService>(TestApp.Host.Services.GetRequiredService<IRecordingEditor>());
    }
}
