// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture;
using Rivet.App.Features.ScreenshotEditor;
using Rivet.App.Settings;
using Rivet.App.Tests.Capture;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.ScreenshotEditor;
using Rivet.Imaging.Skia;
using Rivet.Platform.Fake.Capture;
using SkiaSharp;
using Xunit;

namespace Rivet.App.Tests.ScreenshotEditor;

public class EditorWindowTests
{
    /// <summary>A synthetic capture: a light window with text lines and a gradient panel.</summary>
    internal static PixelBuffer SampleCapture(int width = 1200, int height = 700, double scale = 1)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(new SKColor(0xF7, 0xF7, 0xF9));
        using (var bar = new SKPaint { Color = new SKColor(0xE6, 0xE6, 0xEB) })
        {
            c.DrawRect(0, 0, width, 40, bar);
        }

        var fonts = AnnotationFonts.Shared;
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x22, 0x22, 0x28) };
        string[] lines = ["Quarterly report — draft", "Revenue grew 12 % over the last quarter.", "Contact: someone@example.com", "Account 1234 5678 9012 3456", "Next review: Monday 10:00"];
        var y = 70f;
        foreach (var line in lines)
        {
            fonts.DrawTopLeft(c, line, 32, y, 24, fonts.Semibold, ink);
            y += 44;
        }

        using var photo = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(width * 0.62f, 80), new SKPoint(width - 30, height - 30),
                [new SKColor(0xFF, 0x7E, 0x5F), new SKColor(0xFE, 0xB4, 0x7B)], SKShaderTileMode.Clamp),
        };
        c.DrawRoundRect(SKRect.Create(width * 0.62f, 80, (width * 0.38f) - 30, height - 110), 14, 14, photo);
        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image, scale);
    }

    private static List<Annotation> SampleMarks() =>
    [
        new() { Kind = AnnotationKind.Rect, Rect = new ImgRect(24, 104, 520, 40), Color = AnnotationColor.Red, Stroke = StrokeWidth.Medium },
        new() { Kind = AnnotationKind.Arrow, Start = new ImgPoint(700, 330), End = new ImgPoint(560, 250), Color = AnnotationColor.Red, Stroke = StrokeWidth.Large },
        new() { Kind = AnnotationKind.Highlight, Rect = new ImgRect(24, 192, 400, 36), Color = AnnotationColor.Yellow },
        new() { Kind = AnnotationKind.Blur, Rect = new ImgRect(24, 236, 460, 40), BlurLevel = 3 },
        new() { Kind = AnnotationKind.Counter, Rect = new ImgRect(600, 120, 0, 0), Color = AnnotationColor.Blue, Number = 1 },
        new() { Kind = AnnotationKind.Counter, Rect = new ImgRect(640, 160, 0, 0), Color = AnnotationColor.Blue, Number = 2 },
        new() { Kind = AnnotationKind.Text, Rect = new ImgRect(60, 520, 380, 40), Text = "Ship this on Monday", Color = AnnotationColor.Purple, TextSize = 28 },
        new() { Kind = AnnotationKind.Sticker, Rect = new ImgRect(1040, 560, 72, 72), Sticker = StickerKind.Party },
    ];

    private static EditorWindow NewEditor(double scale = 1)
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Screenshot, true);
        return new EditorWindow(host.Services, new ScreenshotEditRequest { Image = SampleCapture(scale: scale) });
    }

    private static void ResetEditorSettings()
    {
        var settings = TestApp.Host.Settings;
        foreach (var key in new[] { "screenshotBackdropStyle", "screenshotWatermarkStyle", "screenshotAnnotationShadows", "screenshotLastTool", "screenshotLastColor", "screenshotLastStroke", "screenshotLastTextSize", "screenshotToolOrder", "screenshotToolShortcuts", "screenshotBackdropPresets", "screenshotWatermarkPresets" })
        {
            settings.Reset(key);
        }
    }

    private static string Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(TestApp.SnapshotDirectory, name + ".png");
        frame!.Save(path);
        return path;
    }

    private static Point CanvasPoint(EditorWindow window, ImgPoint image) =>
        window.Canvas.TranslatePoint(window.Canvas.ImageToView(image), window) ?? default;

    /// <summary>The editor stays dark whatever the app theme; rendered in both to prove nothing light leaks in.</summary>
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Editor_window_with_marks_backdrop_and_watermark(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var settings = TestApp.Host.Settings;
        settings.Set(EditorSettings.BackdropStyleJson, BackdropCodec.Encode(new BackdropStyle { Kind = BackdropKind.Preset, PresetId = "ocean", Padding = 0.4, CornerRadius = 0.08 }));
        settings.Set(EditorSettings.WatermarkStyleJson, WatermarkCodec.Encode(new WatermarkStyle { Kind = WatermarkKind.Text, Text = "Confidential", Opacity = 0.5, Size = 0.35 }));
        settings.Set(EditorSettings.AnnotationShadows, true);
        var window = NewEditor();
        window.Width = 1296;
        window.Height = 840;
        var session = window.Controller.Session;
        session.LoadAnnotations(SampleMarks());
        session.SetTool(EditorTool.Select);
        session.Select(session.Annotations[1].Id);
        window.Show();
        var path = Save(window, $"editor-window-{theme}");
        Assert.True(File.Exists(path));

        // The stage is dark in both app themes.
        using var image = SKImage.FromEncodedData(path);
        using var bitmap = SKBitmap.FromImage(image);
        var corner = bitmap.GetPixel(bitmap.Width - 4, bitmap.Height / 2);
        Assert.True(corner.Red < 60 && corner.Green < 60 && corner.Blue < 60, $"stage colour {corner}");
        window.ForceClose();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        ResetEditorSettings();
    }

    [AvaloniaFact]
    public void Crop_mode_shows_the_crop_bar()
    {
        var window = NewEditor();
        window.Width = 1100;
        window.Height = 760;
        window.Controller.Session.LoadAnnotations(SampleMarks().Take(3));
        window.Show();
        window.Controller.Session.SetTool(EditorTool.Crop);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var from = CanvasPoint(window, new ImgPoint(20, 60));
        var to = CanvasPoint(window, new ImgPoint(900, 600));
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        Save(window, "editor-window-crop");
        window.MouseUp(to, MouseButton.Left);
        Assert.Equal(new ImgRect(20, 60, 880, 540), window.Controller.Session.CropDraft);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(880, window.Controller.BaseImage.Width);
        Assert.Equal(EditorTool.Select, window.Controller.Session.Tool);
        window.ForceClose();
        ResetEditorSettings();
    }

    [AvaloniaFact]
    public void Text_tool_style_bar_and_inline_editor()
    {
        var window = NewEditor(scale: 1.5);
        window.Width = 1100;
        window.Height = 760;
        window.Show();
        var session = window.Controller.Session;
        session.SetTool(EditorTool.Text);
        session.SetColor(AnnotationColor.Blue);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var at = CanvasPoint(window, new ImgPoint(120, 470));
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(session.EditingTextId);
        DispatcherTimerHelper.Flush();
        window.KeyTextInput("Hello Windows");
        Save(window, "editor-window-text");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Null(session.EditingTextId);
        var text = Assert.Single(session.Annotations);
        Assert.Equal("Hello Windows", text.Text);
        Assert.Equal(AnnotationColor.Blue, text.Color);
        window.ForceClose();
        ResetEditorSettings();
    }

    [AvaloniaFact]
    public void Drawing_with_the_mouse_then_undo_and_redo_from_the_keyboard()
    {
        var window = NewEditor();
        window.Width = 1200;
        window.Height = 800;
        window.Show();
        window.Controller.Session.SetTool(EditorTool.Rect);
        var from = CanvasPoint(window, new ImgPoint(100, 100));
        var to = CanvasPoint(window, new ImgPoint(400, 300));
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        var rect = Assert.Single(window.Controller.Session.Annotations);
        Assert.True(rect.Rect.NearlyEquals(new ImgRect(100, 100, 300, 200), 1.5), rect.Rect.ToString());
        Assert.True(window.Controller.IsDirty);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.Empty(window.Controller.Session.Annotations);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.Single(window.Controller.Session.Annotations);

        // Digit 2 picks the second tool in the rail (Arrow by default); Esc deselects.
        window.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, "2");
        Assert.Equal(EditorTool.Arrow, window.Controller.Session.Tool);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(window.Controller.Session.SelectedId);

        // A dirty editor asks before closing: Close() keeps it open.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        foreach (var dialog in Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                     ? desktop.Windows.Where(w => w != window).ToList()
                     : [])
        {
            dialog.Close();
        }

        window.ForceClose();
        Assert.False(window.IsVisible);
        ResetEditorSettings();
    }

    [AvaloniaFact]
    public void Ctrl_wheel_zooms_and_fit_returns()
    {
        var window = NewEditor();
        window.Width = 1100;
        window.Height = 760;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = window.Canvas;
        Assert.True(canvas.FitMode);
        var fit = canvas.Zoom;
        var at = CanvasPoint(window, new ImgPoint(600, 350));
        window.MouseWheel(at, new Vector(0, 3), RawInputModifiers.Control);
        Assert.False(canvas.FitMode);
        Assert.True(canvas.Zoom > fit);
        window.KeyPress(Key.D1, RawInputModifiers.Control, PhysicalKey.Digit1, "1");
        Assert.True(canvas.IsActualSize);
        Assert.Equal(100, canvas.ZoomPercent);
        window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.Digit0, "0");
        Assert.True(canvas.FitMode);
        window.ForceClose();
    }

    [AvaloniaFact]
    public async Task The_editor_service_is_registered_and_opens_windows()
    {
        var services = TestApp.Host.Services;
        var editor = Assert.IsType<ScreenshotEditorService>(services.GetRequiredService<IScreenshotEditor>());
        await editor.OpenAsync(new ScreenshotEditRequest { Image = SampleCapture(400, 300) });
        Assert.Equal(1, editor.OpenCount);
        editor.Sync(available: false);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, editor.OpenCount);
    }

    [AvaloniaFact]
    public void Edit_clipboard_image_opens_the_clipboard_picture()
    {
        // The action belongs to the capture module; it opens this editor when it is installed.
        var services = TestApp.Host.Services;
        var editor = services.GetRequiredService<ScreenshotEditorService>();
        var actions = services.GetRequiredService<ActionRegistry>();
        var clipboard = services.GetRequiredService<FakeCaptureClipboard>();
        clipboard.SetText("not an image");
        _ = actions.InvokeAsync(CaptureModule.EditClipboardActionId, ActionSource.Other);
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.Equal(0, editor.OpenCount);
        clipboard.SetImage(SampleCapture(300, 200), [], null);
        _ = actions.InvokeAsync(CaptureModule.EditClipboardActionId, ActionSource.Other);
        SelectorSessionTests.Pump(() => editor.OpenCount == 1);
        editor.CloseAll();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_page_renders(string theme)
    {
        var host = TestApp.Host;
        host.Services.GetRequiredService<FeatureRuntime>().SetAvailable(FeatureIds.Screenshot, true);
        var vm = new SettingsViewModel(host.Services);
        vm.Navigate(ScreenshotEditorModule.PageId);
        Assert.Equal(ScreenshotEditorModule.PageId, vm.CurrentPageId);
        var window = new SettingsWindow(vm) { Width = 1080, Height = 1000, RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        Save(window, $"settings-screenshotEditor-{theme}");
        window.Close();
    }

    [AvaloniaFact]
    public void Popovers_render_in_the_editor_theme()
    {
        var window = NewEditor();
        var controller = window.Controller;
        controller.Backdrop = new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [new RgbColor(0.35, 0.34, 0.84), new RgbColor(1, 0.45, 0.66)], Padding = 0.5 };
        controller.SaveBackdropPreset(controller.Backdrop);
        controller.Watermark = new WatermarkStyle { Kind = WatermarkKind.Text, Text = "Rivet", Color = AnnotationColor.Orange };
        controller.SaveWatermarkPreset(controller.Watermark);
        var backdrop = new BackdropPopover(controller);
        var watermark = new WatermarkPopover(controller);
        var tools = new ToolOrderEditor(TestApp.Host.Services, compact: true) { Width = 340 };
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 24,
            Margin = new Thickness(16),
            Children = { backdrop, watermark, tools },
        };
        TestApp.Snapshot(row, "editor-popovers", 1060, theme: ThemeVariant.Dark);
        window.ForceClose();
        ResetEditorSettings();
    }

    [AvaloniaFact]
    public void Tool_keys_follow_the_settings()
    {
        var settings = TestApp.Host.Settings;
        settings.Set(EditorSettings.ToolOrderCsv, "rect,arrow");
        settings.Set(EditorSettings.ToolShortcutsCsv, "ellipse=:0x45");
        var window = NewEditor();
        window.Show();
        window.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
        Assert.Equal(EditorTool.Rect, window.Controller.Session.Tool);
        window.KeyPress(Key.E, RawInputModifiers.None, PhysicalKey.E, "e");
        Assert.Equal(EditorTool.Ellipse, window.Controller.Session.Tool);
        settings.Set(EditorSettings.ToolShortcutsEnabled, false);
        window.KeyPress(Key.D2, RawInputModifiers.None, PhysicalKey.Digit2, "2");
        Assert.Equal(EditorTool.Ellipse, window.Controller.Session.Tool);
        settings.Reset(EditorSettings.ToolShortcutsEnabled.Key);
        window.ForceClose();
        ResetEditorSettings();
    }
}

/// <summary>Runs the dispatcher long enough for short UI timers (the 50 ms text-field focus) to fire.</summary>
internal static class DispatcherTimerHelper
{
    public static void Flush()
    {
        var until = DateTime.UtcNow.AddMilliseconds(120);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
