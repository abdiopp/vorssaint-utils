// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.RecordingEditor;
using Rivet.Core.ScreenshotEditor;
using Rivet.Core.Settings;
using Rivet.Imaging.Backdrop;
using Xunit;

namespace Rivet.App.Tests.Integration;

/// <summary>Contracts between modules that were built separately and must agree.</summary>
public class CrossModuleTests
{
    [Fact]
    public void Saved_custom_backgrounds_are_shared_by_both_editors()
    {
        var settings = SettingsStore.InMemory();
        var fromScreenshot = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [new RgbColor(0.2, 0.47, 0.96), new RgbColor(1, 0.75, 0.35)] };
        settings.Set(EditorSettings.BackdropPresetsJson, BackdropCodec.EncodePresets(BackdropCodec.AddPreset([], fromScreenshot)));

        var seenByRecorder = RecordingEditorSettings.ReadBackdropPresets(settings);
        var preset = Assert.Single(seenByRecorder);
        Assert.Equal(RecorderBackdropKind.Gradient, preset.Kind);
        Assert.Equal(2, preset.Colors!.Count);

        var fromRecorder = new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = "forest" };
        RecordingEditorSettings.WriteBackdropPresets(settings, RecordingEditorSettings.AddBackdropPreset(seenByRecorder, fromRecorder));

        var seenByScreenshot = BackdropCodec.DecodePresets(settings.Get(EditorSettings.BackdropPresetsJson));
        Assert.Equal(2, seenByScreenshot.Count);
        Assert.Contains(seenByScreenshot, p => p.Kind == BackdropKind.Preset && p.PresetId == "forest");
        Assert.Contains(seenByScreenshot, p => p.Kind == BackdropKind.Gradient);
    }

    [Fact]
    public void Backups_leave_out_image_backgrounds_and_watermarks()
    {
        _ = TestApp.Host; // modules register their backup sanitizers at start-up
        var settings = SettingsStore.InMemory();
        var image = new BackdropStyle { Kind = BackdropKind.Image, ImagePath = @"C:\Users\me\Pictures\wall.jpg" };
        var colour = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean" };
        settings.Set(EditorSettings.BackdropPresetsJson, BackdropCodec.EncodePresets([image, colour]));
        settings.Set(EditorSettings.BackdropStyleJson, BackdropCodec.Encode(image));

        var json = SettingsBackup.Export(settings);
        Assert.DoesNotContain("wall.jpg", json, StringComparison.Ordinal);
        Assert.Contains("ocean", json, StringComparison.Ordinal);
    }
}
