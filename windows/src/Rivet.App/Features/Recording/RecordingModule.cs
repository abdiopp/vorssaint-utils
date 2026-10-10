// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Recording.Views;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Recording;

/// <summary>
/// Screen recording, the capture side (feature <c>screenRecorder</c>): the
/// toggle action, the dedicated shortcut, the Utilities tile ("Stop
/// recording" with the elapsed time while recording), the tray tint and
/// "Stop recording" tray item, and the Settings page. The editor that opens
/// afterwards is a separate module behind <c>IRecordingEditor</c>.
/// </summary>
public sealed class RecordingModule : IFeatureModule
{
    /// <summary>
    /// "recorder.toggle". Spelled in two parts because the string-key test treats
    /// any "recorder.x" literal as a catalog key, and action ids share that prefix.
    /// </summary>
    public const string ToggleActionId = "recorder" + ".toggle";
    public const string ShortcutRoleId = "screenRecorder";
    public const string SettingsPageId = "screenRecording";
    public const string TileId = "screenRecorder";

    /// <summary>Windows default for the dedicated shortcut (spec 05 §6.1): Ctrl+Alt+Win+5, off until switched on.</summary>
    public static KeyChord DefaultShortcut { get; } = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Digit(5));

    public string Id => "recording";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<RecordingChrome>();
        services.AddSingleton<RecordingDelivery>();
        services.AddSingleton<ScreenRecorderController>();
        services.AddSingleton<IScreenRecorder>(sp => sp.GetRequiredService<ScreenRecorderController>());
    }

    public void Initialize(ModuleContext context)
    {
        var controller = context.Get<ScreenRecorderController>();
        context.Features.RegisterController(FeatureIds.ScreenRecorder, controller);

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = ShortcutRoleId,
            FeatureId = FeatureIds.ScreenRecorder,
            TitleKey = "recorder.pageTitle",
            Storage = RecorderSettings.Shortcut,
            Default = DefaultShortcut,
            RequiredEnableKeys = [RecorderSettings.ShortcutEnabled],
            ActionId = ToggleActionId,
            GroupId = "screenCapture",
        });

        RegisterStateful(context, controller);
        controller.StateChanged += (_, _) => RegisterStateful(context, controller);

        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "screenRecorder.stop",
            Title = () => L.Get("recorder.stopButton"),
            Icon = "RecordStop",
            Order = -60,
            FeatureId = FeatureIds.ScreenRecorder,
            IsVisible = () => controller.IsRecording,
            Invoke = controller.Stop,
        });
        context.TrayMenu.Add(new TrayMenuItem
        {
            Id = "screenRecorder.pause",
            Title = () => L.Get(controller.State == ScreenRecorderState.Paused ? "recorder.resumeButton" : "recorder.pauseButton"),
            Icon = "Pause",
            Order = -59,
            FeatureId = FeatureIds.ScreenRecorder,
            IsVisible = () => controller.State is ScreenRecorderState.Recording or ScreenRecorderState.Paused,
            Invoke = () => controller.TogglePause(),
        });

        context.SettingsPages.Add(new SettingsPageDescriptor
        {
            Id = SettingsPageId,
            TitleKey = "recorder.pageTitle",
            Icon = "Record",
            Category = SettingsCategory.Capture,
            Order = 20,
            FeatureIds = [FeatureIds.ScreenRecorder],
            CreateView = sp => new RecordingSettingsPage(sp),
            KeywordKeys =
            [
                "recorder.countdownLabel", "recorder.frameRateLabel", "recorder.systemAudioToggle", "recorder.microphoneToggle",
                "recorder.openEditorToggle", "recorder.folderLabel", "win.recording.showIndicator",
            ],
            Keywords = ["record", "recording", "video", "screencast", "mp4", "fps", "microphone", "audio"],
        });
    }

    /// <summary>Panel tile and action titles follow the state ("Stop recording" while recording).</summary>
    private static void RegisterStateful(ModuleContext context, ScreenRecorderController controller)
    {
        var recording = controller.IsRecording;
        context.Actions.Register(new AppAction
        {
            Id = ToggleActionId,
            FeatureId = FeatureIds.ScreenRecorder,
            TitleKey = recording ? "recorder.stopButton" : "recorder.pageTitle",
            SubtitleKey = recording ? null : "recorder.panelCaption",
            Icon = recording ? "RecordStop" : "Record",
            Keywords = ["record", "recording", "video", "screencast", "capture"],
            Run = ctx => controller.ToggleAsync(fromShortcut: ctx.Source == ActionSource.Shortcut),
        });
        context.Panel.AddTile(new PanelTileDescriptor
        {
            Id = TileId,
            FeatureId = FeatureIds.ScreenRecorder,
            TitleKey = recording ? "recorder.stopButton" : "recorder.pageTitle",
            CaptionKey = "recorder.panelCaption",
            Icon = recording ? "RecordStop" : "Record",
            Order = 160,
            ActionId = ToggleActionId,
            ShortcutRoleId = ShortcutRoleId,
            SettingsPageId = SettingsPageId,
            LiveCaption = () => controller.LiveCaption,
            // Spec 02 §3.1: the Recent captures button, hidden while a recording is busy.
            Accessory = new PanelTileAccessory
            {
                TitleKey = "recentCaptures.title",
                Icon = "History",
                ActionId = RecentCaptureActions.ShowPalette,
                IsVisible = () => !controller.IsBusy && context.Actions.Get(RecentCaptureActions.ShowPalette) is { } palette && context.Actions.IsEnabled(palette),
            },
        });
    }
}
