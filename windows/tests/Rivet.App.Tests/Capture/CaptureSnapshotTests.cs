// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture;
using Rivet.App.Features.Capture.Hud;
using Rivet.App.Features.Capture.Pins;
using Rivet.App.Features.Capture.Preview;
using Rivet.App.Features.Capture.Recent;
using Rivet.App.Features.Capture.Scrolling;
using Rivet.App.Features.Capture.Selector;
using Rivet.App.Features.Capture.Settings;
using Rivet.App.Features.Capture.Text;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Platform.Fake.Capture;
using Xunit;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Tests.Capture;

/// <summary>Renders the capture tools' views to tests/artifacts/snapshots (prefix "capture-").</summary>
public class CaptureSnapshotTests
{
    private static IServiceProvider Services => TestApp.Host.Services;

    internal static PixelBuffer Desktop()
    {
        var screens = Services.GetRequiredService<IScreenService>();
        return Services.GetRequiredService<FakeDesktop>().Render(screens.Primary, false, default);
    }

    private static void SnapshotWindow(Window window, string name, ThemeVariant theme)
    {
        window.RequestedThemeVariant = theme;
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(TestApp.SnapshotDirectory, name + ".png"));
        window.Close();
    }

    /// <summary>Puts a floating window over a neutral backdrop, like the desktop it floats on.</summary>
    private static void SnapshotFloating(Control content, string name, double width, double? height, ThemeVariant theme)
    {
        var backdrop = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#2F5D9E"), 0), new GradientStop(Color.Parse("#8A4F9E"), 1) },
            },
            Padding = new Thickness(16),
            Child = content,
        };
        TestApp.Snapshot(backdrop, name, width, height, theme);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Settings_pages_render(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var runtime = Services.GetRequiredService<FeatureRuntime>();
        runtime.SetAvailable(FeatureIds.Screenshot, true);
        runtime.SetAvailable(FeatureIds.ScreenOcr, true);
        runtime.SetAvailable(FeatureIds.ColorPicker, true);
        Services.GetRequiredService<ISettingsStore>().Set(CaptureSettings.FileNamePattern, "Shot %y-%mo-%d %##");
        try
        {
            // The page is taller than the headless screen: render it in two halves.
            var top = new ScreenshotSettingsPage(Services) { Margin = new Thickness(28, 22) };
            TestApp.Snapshot(top, $"capture-settings-screenshot-{theme}", 860, theme: variant);
            var bottom = new ScreenshotSettingsPage(Services) { Margin = new Thickness(28, -1040, 28, 22) };
            TestApp.Snapshot(new Border { ClipToBounds = true, Child = bottom }, $"capture-settings-screenshot2-{theme}", 860, theme: variant);
            TestApp.Snapshot(new ScreenOcrSettingsPage(Services) { Margin = new Thickness(28, 22) }, $"capture-settings-ocr-{theme}", 860, theme: variant);
            TestApp.Snapshot(new ColorPickerSettingsPage(Services) { Margin = new Thickness(28, 22) }, $"capture-settings-color-{theme}", 860, theme: variant);
        }
        finally
        {
            Services.GetRequiredService<ISettingsStore>().Reset(CaptureSettings.FileNamePattern.Key);
        }
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Hint_bar_renders_for_every_tool(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var settings = Services.GetRequiredService<ISettingsStore>();
        CaptureTool[] tools = [CaptureTool.Screenshot, CaptureTool.Recording, CaptureTool.Text, CaptureTool.Color];
        var stack = new StackPanel { Spacing = 18 };
        foreach (var tool in tools)
        {
            var bar = new SelectorHintBar(settings) { Width = 620 };
            var mode = tool switch { CaptureTool.Recording => SelectorMode.Geometry, CaptureTool.Color => SelectorMode.Color, _ => SelectorMode.Image };
            var state = new HintState(tools, tool, mode, ShowPalette: true, Standalone: false, RegionOnly: false, Purpose: null,
                Scrolling: false, LoupeOn: tool == CaptureTool.Color, RepeatAvailable: tool == CaptureTool.Screenshot, WindowClicks: mode != SelectorMode.Color);
            bar.Update(state);
            bar.Height = SelectorHintBar.HeightFor(state);
            stack.Children.Add(bar);
        }

        var single = new SelectorHintBar(settings) { Width = 620 };
        var singleState = new HintState([CaptureTool.Screenshot], CaptureTool.Screenshot, SelectorMode.Image, false, false, false, null, true, true, false, false);
        single.Update(singleState);
        stack.Children.Add(single);

        var standalone = new SelectorHintBar(settings) { Width = 680 };
        standalone.Update(new HintState([CaptureTool.Screenshot], CaptureTool.Screenshot, SelectorMode.Geometry, false, true, false, "Scrolling screenshot", false, false, false, true));
        stack.Children.Add(standalone);
        SnapshotFloating(stack, $"capture-hintbar-{theme}", 720, null, variant);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Quick_preview_renders(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var capture = new CapturedImage(Rivet.Imaging.Capture.CaptureImaging.Crop(Desktop(), new PixelRect(100, 80, 900, 560))!, new PixelRect(100, 80, 900, 560));
        var window = new QuickPreviewWindow(capture, persistent: true, editAvailable: true);
        window.SetCompleted(saved: true, copied: false);
        window.ShowQrButton();
        SnapshotWindow(window, $"capture-preview-{theme}", variant);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Recent_captures_render(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var store = Services.GetRequiredService<RecentCapturesStore>();
        if (store.Entries.Count == 0)
        {
            var desktop = Desktop();
            store.AddScreenshot(Rivet.Imaging.Capture.CaptureImaging.Crop(desktop, new PixelRect(120, 90, 700, 460))!, new PixelRect(120, 90, 700, 460));
            store.AddScreenshot(Rivet.Imaging.Capture.CaptureImaging.Crop(desktop, new PixelRect(1500, 560, 320, 300))!, new PixelRect(1500, 560, 320, 300));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.Entries.Count < 2 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }

        var palette = CaptureUi.Plate(new RecentCapturesView(Services, showHeader: true, close: () => { }), radius: 18, padding: new Thickness(14));
        SnapshotFloating(palette, $"capture-recent-palette-{theme}", 500, null, variant);

        var panel = new Border { Classes = { "card" }, Child = new RecentCapturesView(Services, showHeader: false) };
        panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        TestApp.Snapshot(new Border { Padding = new Thickness(12), Child = panel }, $"capture-recent-panel-{theme}", 340, theme: variant);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Floating_tools_render(string theme)
    {
        var variant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        SnapshotWindow(new QrResultWindow("https://example.com/rivet", new Uri("https://example.com/rivet")), $"capture-qr-link-{theme}", variant);
        SnapshotWindow(new QrResultWindow("WIFI:S:Office;T:WPA;P:correct horse battery staple;;\nSecond code payload", null), $"capture-qr-text-{theme}", variant);

        var bar = new ScrollingControlBar();
        bar.SetHeight(2480);
        SnapshotWindow(bar, $"capture-scrollbar-{theme}", variant);

        var countdown = new CountdownWindow();
        countdown.ShowValue(3);
        using var frame = countdown.CaptureRenderedFrame();
        frame?.Save(Path.Combine(TestApp.SnapshotDirectory, $"capture-countdown-{theme}.png"));
        countdown.Stop();
    }

    [AvaloniaFact]
    public void Pin_renders_aspect_locked()
    {
        var image = Rivet.Imaging.Capture.CaptureImaging.Crop(Desktop(), new PixelRect(400, 200, 800, 500))!;
        var pin = new PinWindow(image, new Size(800, 500)) { Width = 400, Height = 250 };
        SnapshotWindow(pin, "capture-pin", ThemeVariant.Light);
    }
}
