// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Recording;
using Rivet.App.Tests.Capture;
using Avalonia;
using Avalonia.Controls;
using Rivet.App.Modules;
using Rivet.App.Shell;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Recording;
using Rivet.Platform.Fake.Shell;
using Xunit;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Tests.Recording;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecorderFlowCollection
{
    public const string Name = "recorder-flow";
}

/// <summary>End-to-end flows on the fake platform: the real controller, session, WAV writers and pointer track.</summary>
[Collection(RecorderFlowCollection.Name)]
public sealed class RecorderFlowTests : IDisposable
{
    private readonly string _saveFolder = Path.Combine(Path.GetTempPath(), "rivet-recordings-" + Guid.NewGuid());

    public RecorderFlowTests() => Directory.CreateDirectory(_saveFolder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_saveFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task Toggle_records_then_saves_straight_away_when_the_editor_is_off()
    {
        var (controller, settings) = Prepare();
        settings.Set(RecorderSettings.SystemAudio, true);
        await StartAsync(controller);
        Assert.Equal(ScreenRecorderState.Recording, controller.State);
        Assert.True(controller.Chrome.IsIndicatorVisible);
        Assert.True(controller.Chrome.IsGuideVisible); // display recording: the guide is shown
        Assert.Equal(RecordingTargetKind.Display, controller.Target!.Kind); // Enter in the chooser: the monitor under the pointer
        var take = controller.ActiveTakeFolder!;
        Assert.True(Directory.Exists(take));

        var tile = TestApp.Host.Services.GetRequiredService<PanelRegistry>().Tiles.Single(t => t.Id == RecordingModule.TileId);
        Assert.Equal("recorder.stopButton", tile.TitleKey);
        Assert.StartsWith("Recording 0:0", tile.LiveCaption!());
        var action = TestApp.Host.Services.GetRequiredService<ActionRegistry>().Get(RecordingModule.ToggleActionId)!;
        Assert.Equal("recorder.stopButton", action.TitleKey);
        var tray = TestApp.Host.Services.GetRequiredService<TrayMenuRegistry>().Items.Single(i => i.Id == "screenRecorder.stop");
        Assert.True(tray.IsVisible!());
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Recording", TestApp.Host.Services.GetRequiredService<FakeTrayIcon>().Tooltip, StringComparison.Ordinal);
        Assert.True(TestApp.Host.Services.GetRequiredService<FakeRecorderSystem>().SleepPreventions > 0);

        Assert.True(controller.TogglePause());
        Assert.Equal(ScreenRecorderState.Paused, controller.State);
        Assert.True(controller.Chrome.IndicatorView!.IsPaused);
        Assert.StartsWith("Paused", tile.LiveCaption!());
        Assert.True(controller.TogglePause());
        Assert.False(controller.Chrome.IndicatorView.IsPaused);

        await Task.Delay(400);
        var vm = new PanelViewModel(TestApp.Host.Services);
        vm.Rebuild();
        var surface = new Border { Child = new PanelView { DataContext = vm } };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(surface, "recording-panel-while-recording", 342);

        await controller.ToggleAsync();
        Assert.Equal(ScreenRecorderState.Finishing, controller.State);
        Assert.False(controller.Chrome.IsIndicatorVisible);
        await controller.Finishing!;

        Assert.Equal(ScreenRecorderState.Idle, controller.State);
        Assert.False(Directory.Exists(take));
        var saved = Assert.Single(Directory.GetFiles(_saveFolder, "Recording *.mp4"));
        Assert.Matches(@"Recording \d{4}-\d{2}-\d{2} at \d{2}\.\d{2}\.\d{2}\.mp4$", saved);
        Assert.Equal("recorder.pageTitle", TestApp.Host.Services.GetRequiredService<PanelRegistry>().Tiles.Single(t => t.Id == RecordingModule.TileId).TitleKey);
        Assert.False(tray.IsVisible!());
        Assert.Equal(0, TestApp.Host.Services.GetRequiredService<FakeRecorderSystem>().SleepPreventions);
    }

    [AvaloniaFact]
    public async Task The_fake_platform_produces_a_take_in_the_shared_format()
    {
        var services = TestApp.Host.Services;
        var screens = services.GetRequiredService<IScreenService>();
        var folder = TakeFolders.Create(TestApp.Host.Paths);
        var session = new RecordingSession(
            new RecordingSessionOptions
            {
                Target = ScreenRecorderController.MonitorUnderPointer(screens),
                TakeFolder = folder,
                FrameRate = 30,
                SystemAudio = true,
                Microphone = true,
                AppVersion = "test",
            },
            new RecordingServices(
                services.GetRequiredService<IVideoCaptureBackend>(),
                services.GetRequiredService<IAudioCaptureBackend>(),
                services.GetRequiredService<ICursorProbe>(),
                services.GetRequiredService<IRecorderSystem>(),
                services.GetRequiredService<IInputHooks>(),
                QpcClock.Instance));
        try
        {
            await Task.Run(() => session.StartAsync());
            await Task.Delay(300);
            var hooks = services.GetRequiredService<FakeInputHooks>();
            hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = VirtualKeys.A, Action = KeyAction.Down });
            hooks.RaiseKey(new KeyboardHookEvent { VirtualKey = VirtualKeys.A, Action = KeyAction.Up });
            hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftDown });
            hooks.RaiseMouse(new MouseHookEvent { Kind = MouseHookKind.LeftUp });
            Assert.True(session.Pause());
            await Task.Delay(250);
            Assert.True(session.Resume());
            await Task.Delay(300);
            var outcome = await Task.Run(() => session.StopAsync());

            Assert.True(outcome.Written);
            var manifest = TakeManifest.Read(folder)!;
            Assert.Equal(30, manifest.Capture.Fps);
            Assert.Equal(TakeCaptureKind.Display, manifest.Capture.Kind);
            Assert.Equal([0, 0, 1920, 1080], manifest.Capture.RegionPx);
            Assert.Equal((1920, 1080), (manifest.Video.Width, manifest.Video.Height));
            Assert.Equal([TakeAudioSource.System, TakeAudioSource.Microphone], manifest.Audio.Select(a => a.Source));
            Assert.InRange(manifest.Video.DurationSeconds, 0.45, 1.5); // the 250 ms pause is not in it
            foreach (var audio in manifest.Audio)
            {
                var bytes = new FileInfo(Path.Combine(folder, audio.File)).Length - 44;
                Assert.Equal(Math.Round(manifest.Video.DurationSeconds * 48_000), bytes / 4.0, 0);
            }

            var pointer = PointerTrack.Read(Path.Combine(folder, PointerTrack.FileName));
            Assert.InRange(pointer.Samples.Count, 20, 200);
            Assert.Equal([true, false], pointer.Clicks.Select(c => c.IsDown));
            Assert.Single(TypingTrack.Read(Path.Combine(folder, TypingTrack.FileName)).Times);
            Assert.Equal(TypingTrack.FileName, manifest.TypingTrack);
            Assert.NotEmpty(pointer.Shapes);
            Assert.All(pointer.Samples, s => Assert.InRange(s.Time, 0f, (float)manifest.Video.DurationSeconds + 0.01f));
        }
        finally
        {
            RecordingDelivery.TryDeleteTake(folder);
        }
    }

    [AvaloniaFact]
    public async Task Discard_deletes_the_take_and_saves_nothing()
    {
        var (controller, _) = Prepare();
        await StartAsync(controller);
        var take = controller.ActiveTakeFolder!;
        await Task.Delay(150);
        controller.Stop(new StopReason(StopKind.Discard));
        await controller.Finishing!;
        Assert.Equal(ScreenRecorderState.Idle, controller.State);
        Assert.False(Directory.Exists(take));
        Assert.Empty(Directory.GetFiles(_saveFolder));
    }

    [AvaloniaFact]
    public async Task A_closed_window_stops_the_recording_and_keeps_it()
    {
        var (controller, _) = Prepare();
        await StartAsync(controller);
        await Task.Delay(150);
        TestApp.Host.Services.GetRequiredService<FakeVideoCaptureBackend>().LastSession!.RaiseEnded(CaptureEndReason.WindowClosed);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ScreenRecorderState.Finishing, controller.State);
        await controller.Finishing!;
        Assert.Single(Directory.GetFiles(_saveFolder, "*.mp4"));
    }

    [AvaloniaFact]
    public async Task Uninstalling_the_feature_stops_and_still_saves()
    {
        var (controller, _) = Prepare();
        var runtime = TestApp.Host.Services.GetRequiredService<FeatureRuntime>();
        await StartAsync(controller);
        await Task.Delay(150);
        try
        {
            runtime.SetAvailable(FeatureIds.ScreenRecorder, false);
            Assert.Equal(ScreenRecorderState.Finishing, controller.State);
            await controller.Finishing!;
            Assert.Single(Directory.GetFiles(_saveFolder, "*.mp4"));
            await controller.ToggleAsync(); // uninstalled: nothing starts
            Assert.Equal(ScreenRecorderState.Idle, controller.State);
        }
        finally
        {
            runtime.SetAvailable(FeatureIds.ScreenRecorder, true);
        }
    }

    [AvaloniaFact]
    public async Task A_blocked_microphone_warns_and_records_without_it()
    {
        var (controller, settings) = Prepare();
        settings.Set(RecorderSettings.Microphone, true);
        var audio = TestApp.Host.Services.GetRequiredService<FakeAudioCaptureBackend>();
        audio.MicrophoneStatus = MicrophoneStatus.Denied;
        try
        {
            await StartAsync(controller);
            Assert.Equal(ScreenRecorderState.Recording, controller.State);
            Assert.False(File.Exists(Path.Combine(controller.ActiveTakeFolder!, RecordingSession.MicrophoneFile)));
            controller.Stop();
            await controller.Finishing!;
        }
        finally
        {
            audio.MicrophoneStatus = MicrophoneStatus.Available;
        }
    }

    [AvaloniaFact]
    public async Task A_countdown_can_be_cancelled_with_the_toggle()
    {
        var (controller, settings) = Prepare();
        settings.Set(RecorderSettings.Countdown, 3);
        var release = new TaskCompletionSource();
        controller.Delay = _ => release.Task;
        var start = controller.ToggleAsync();
        Assert.Equal(ScreenRecorderState.Preparing, controller.State);
        ChooserDriver.ConfirmDisplay();
        await Until(() => controller.Chrome.IsCountdownVisible);
        Assert.True(controller.Chrome.IsGuideVisible);
        await controller.ToggleAsync(); // cancel
        Assert.Equal(ScreenRecorderState.Idle, controller.State);
        Assert.False(controller.Chrome.IsCountdownVisible);
        Assert.False(controller.Chrome.IsGuideVisible);
        release.SetResult();
        await start;
        Assert.Equal(ScreenRecorderState.Idle, controller.State);
        Assert.Null(controller.ActiveTakeFolder);
    }

    [AvaloniaFact]
    public void Selections_resolve_to_snapped_regions_on_their_monitor()
    {
        var screens = new FakeScreens
        {
            Screens =
            [
                new ScreenInfo { Id = "A", FriendlyName = "A", Bounds = new PixelRect(0, 0, 1920, 1080), WorkArea = new PixelRect(0, 0, 1920, 1040), Scale = 1, IsPrimary = true },
                new ScreenInfo { Id = "B", FriendlyName = "B", Bounds = new PixelRect(1920, 0, 2880, 1800), WorkArea = new PixelRect(1920, 0, 2880, 1760), Scale = 1.5, IsPrimary = false },
            ],
        };
        var area = ScreenRecorderController.Resolve(new CaptureSelection { Tool = CaptureTool.Recording, Kind = CaptureTargetKind.Area, Bounds = new PixelRect(2000, 100, 641, 401), ScreenId = "B" }, screens);
        Assert.Equal(RecordingTargetKind.Area, area.Kind);
        Assert.Equal("B", area.Monitor.Id);
        Assert.Equal(new PixelRect(2000, 100, 640, 400), area.Region);

        var window = ScreenRecorderController.Resolve(new CaptureSelection { Tool = CaptureTool.Recording, Kind = CaptureTargetKind.Window, Bounds = new PixelRect(1800, -20, 600, 400), ScreenId = "A", WindowHandle = 77 }, screens);
        Assert.Equal(RecordingTargetKind.Window, window.Kind);
        Assert.Equal(77, window.Window);
        Assert.Equal(new PixelRect(1800, 0, 120, 380), window.Region);

        var display = ScreenRecorderController.Resolve(new CaptureSelection { Tool = CaptureTool.Recording, Kind = CaptureTargetKind.Display, Bounds = new PixelRect(1920, 0, 2880, 1800), ScreenId = "missing" }, screens);
        Assert.Equal("B", display.Monitor.Id);
        Assert.Equal(new PixelRect(1920, 0, 2880, 1800), display.Region);
    }

    [AvaloniaFact]
    public void The_dedicated_shortcut_is_registered_off_by_default()
    {
        var shortcuts = TestApp.Host.Services.GetRequiredService<ShortcutManager>();
        var role = shortcuts.Find(RecordingModule.ShortcutRoleId)!;
        Assert.Equal(KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Digit(5)), role.Default);
        Assert.Equal(RecordingModule.ToggleActionId, role.ActionId);
        Assert.Equal("recorder.toggle", role.ActionId);
        Assert.Equal(ShortcutState.Inactive, shortcuts.GetState(role));
    }

    /// <summary>Starts from the toggle and answers the shared chooser with Enter.</summary>
    private static async Task StartAsync(ScreenRecorderController controller)
    {
        var start = controller.ToggleAsync();
        ChooserDriver.ConfirmDisplay();
        await start;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private (ScreenRecorderController Controller, ISettingsStore Settings) Prepare()
    {
        var host = TestApp.Host;
        var settings = host.Settings;
        settings.Set(RecorderSettings.SaveFolder, _saveFolder);
        settings.Set(RecorderSettings.Countdown, 0);
        settings.Set(RecorderSettings.SystemAudio, false);
        settings.Set(RecorderSettings.Microphone, false);
        settings.Set(RecorderSettings.ShowIndicator, true);
        // The recording editor is installed in the shared host; these flows test the direct-save path.
        settings.Set(RecorderSettings.OpenEditor, false);
        var controller = host.Services.GetRequiredService<ScreenRecorderController>();
        controller.Delay = _ => Task.CompletedTask;
        Assert.Equal(ScreenRecorderState.Idle, controller.State);
        return (controller, settings);
    }
}
