// SPDX-License-Identifier: GPL-3.0-or-later
#pragma warning disable CS0618 // Bitmap.Save(string): the replacement overload is not needed for test snapshots.
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Capture.Selector;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Platform.Fake.Capture;
using Xunit;

namespace Rivet.App.Tests.Capture;

/// <summary>Drives real selector sessions on the fake desktop (one 1920×1080 display at 100 %).</summary>
public class SelectorSessionTests
{
    private static IServiceProvider Services => TestApp.Host.Services;

    internal static void Pump(Func<bool> done, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (SelectorSession Session, Task<SelectorOutcome?> Run) Start(CaptureTool tool, IReadOnlyList<CaptureTool>? tools = null)
    {
        SelectorModel.ResetLastRegion();
        var session = new SelectorSession(Services, new SelectorRequest
        {
            Tools = tools ?? [CaptureTool.Screenshot, CaptureTool.Recording, CaptureTool.Text, CaptureTool.Color],
            Tool = tool,
        });
        var run = session.RunAsync();
        Pump(() => session.Overlays.Count > 0 || run.IsCompleted);
        return (session, run);
    }

    private static SelectorOutcome? Finish(Task<SelectorOutcome?> run)
    {
        Pump(() => run.IsCompleted);
        return run.Result;
    }

    [AvaloniaFact]
    public void Drag_returns_the_frozen_pixels_of_the_area()
    {
        var (session, run) = Start(CaptureTool.Screenshot);
        session.OnPointerMoved(new PointD(100, 120), KeyModifiers.None);
        session.OnPointerPressed(new PointD(100, 120), KeyModifiers.None);
        session.OnPointerMoved(new PointD(300, 260), KeyModifiers.None);
        session.OnPointerMoved(new PointD(500, 420), KeyModifiers.None);
        session.OnPointerReleased(new PointD(500, 420), KeyModifiers.None);
        var outcome = Finish(run);

        Assert.NotNull(outcome);
        Assert.Equal(SelectorActionKind.ConfirmRegion, outcome!.Kind);
        Assert.Equal(new PixelRect(100, 120, 400, 300), outcome.Region);
        Assert.Equal((400, 300), (outcome.Image!.Image.Width, outcome.Image.Image.Height));
        Assert.Equal(1.0, outcome.Image.Scale);
    }

    [AvaloniaFact]
    public void Escape_cancels_without_a_result()
    {
        var (session, run) = Start(CaptureTool.Screenshot);
        session.OnKey(SelectorKey.Escape, KeyAction.Down, KeyModifiers.None);
        Assert.Null(Finish(run));
    }

    [AvaloniaFact]
    public void Enter_captures_the_display_under_the_pointer()
    {
        var (session, run) = Start(CaptureTool.Screenshot);
        session.OnPointerMoved(new PointD(800, 500), KeyModifiers.None);
        session.OnKey(SelectorKey.Enter, KeyAction.Down, KeyModifiers.None);
        var outcome = Finish(run);
        Assert.Equal(SelectorActionKind.ConfirmDisplay, outcome!.Kind);
        Assert.Equal((1920, 1080), (outcome.Image!.Image.Width, outcome.Image.Image.Height));
        Assert.Equal(CaptureTargetKind.Display, outcome.Image.Kind);
    }

    [AvaloniaFact]
    public void A_click_captures_the_frontmost_window_under_it()
    {
        var windows = Services.GetRequiredService<IWindowEnumerator>().EnumerateWindows();
        var front = windows[0];
        var (session, run) = Start(CaptureTool.Screenshot);
        var point = new PointD(front.Bounds.X + 30, front.Bounds.Y + 40);
        session.OnPointerMoved(point, KeyModifiers.None);
        Assert.Equal(front.Handle, session.Model!.HighlightedWindow?.Handle);
        session.OnPointerPressed(point, KeyModifiers.None);
        session.OnPointerReleased(point + new PointD(1, 1), KeyModifiers.None);
        var outcome = Finish(run);
        Assert.Equal(SelectorActionKind.ConfirmWindow, outcome!.Kind);
        Assert.Equal(front.Handle, outcome.Window!.Handle);
        Assert.Equal((front.Bounds.Width, front.Bounds.Height), (outcome.Image!.Image.Width, outcome.Image.Image.Height));
        Assert.Equal(front.Title, outcome.Image.WindowTitle);
    }

    [AvaloniaFact]
    public void Recording_returns_an_even_region_without_pixels()
    {
        var (session, run) = Start(CaptureTool.Recording);
        session.OnPointerPressed(new PointD(11, 21), KeyModifiers.None);
        session.OnPointerMoved(new PointD(212, 160), KeyModifiers.None);
        session.OnPointerReleased(new PointD(212, 160), KeyModifiers.None);
        var outcome = Finish(run);
        Assert.Equal(CaptureTool.Recording, outcome!.Tool);
        Assert.Null(outcome.Image);
        Assert.Equal(0, outcome.Region.Width % 2);
        Assert.Equal(0, outcome.Region.Height % 2);
        Assert.True(outcome.Region.Width >= 200 && outcome.Region.Height >= 138);
    }

    [AvaloniaFact]
    public void Color_mode_picks_the_pixel_under_the_pointer()
    {
        var desktop = CaptureSnapshotTests.Desktop();
        var (session, run) = Start(CaptureTool.Color);
        Assert.True(session.Model!.LoupeOn);
        session.OnPointerMoved(new PointD(37.6, 51.2), KeyModifiers.None);
        session.OnKey(SelectorKey.Enter, KeyAction.Down, KeyModifiers.None);
        var outcome = Finish(run);
        Assert.Equal(Rivet.Imaging.Capture.CaptureImaging.ReadPixel(desktop, 37, 51), outcome!.Color);
    }

    [AvaloniaFact]
    public void C_copies_a_colour_without_ending_the_session()
    {
        var clipboard = Services.GetRequiredService<FakeCaptureClipboard>();
        var (session, run) = Start(CaptureTool.Color);
        session.OnPointerMoved(new PointD(600, 300), KeyModifiers.None);
        session.OnKey(SelectorKey.C, KeyAction.Down, KeyModifiers.None);
        Assert.False(run.IsCompleted);
        Assert.StartsWith("#", clipboard.Text, StringComparison.Ordinal);
        Assert.True(session.Model!.IsCopiedFeedbackActive(DateTime.UtcNow));
        session.OnKey(SelectorKey.Escape, KeyAction.Down, KeyModifiers.None);
        Assert.Null(Finish(run));
    }

    [AvaloniaFact]
    public void Switching_to_a_tool_with_another_policy_takes_new_photographs()
    {
        var settings = Services.GetRequiredService<ISettingsStore>();
        var capturer = Services.GetRequiredService<FakeScreenCapturer>();
        settings.Set(CaptureSettings.Freeze, false);
        try
        {
            var (session, run) = Start(CaptureTool.Screenshot);
            var before = capturer.DisplayCaptureCount;
            Assert.False(session.Model!.Policy.Freeze);
            session.OnKey(SelectorKey.Digit3, KeyAction.Down, KeyModifiers.None);
            Assert.True(session.Model.RefreshPending);
            Pump(() => !session.Model.RefreshPending);
            Assert.Equal(CaptureTool.Text, session.Model.Tool);
            Assert.True(session.Model.Policy.Freeze);
            Assert.Equal(before + 1, capturer.DisplayCaptureCount);
            session.OnKey(SelectorKey.Escape, KeyAction.Down, KeyModifiers.None);
            Finish(run);
        }
        finally
        {
            settings.Reset(CaptureSettings.Freeze.Key);
        }
    }

    [AvaloniaFact]
    public void A_failed_photograph_ends_the_session()
    {
        var capturer = Services.GetRequiredService<FakeScreenCapturer>();
        var screens = Services.GetRequiredService<IScreenService>();
        capturer.FailingDisplays.Add(screens.Primary.Id);
        try
        {
            var (_, run) = Start(CaptureTool.Screenshot);
            Assert.Null(Finish(run));
        }
        finally
        {
            capturer.FailingDisplays.Clear();
        }
    }

    [AvaloniaFact]
    public void R_repeats_the_last_area()
    {
        var (session, run) = Start(CaptureTool.Screenshot);
        session.OnPointerPressed(new PointD(200, 200), KeyModifiers.None);
        session.OnPointerMoved(new PointD(420, 330), KeyModifiers.None);
        session.OnPointerReleased(new PointD(420, 330), KeyModifiers.None);
        var first = Finish(run);

        // A second session (the last region survives for the app session).
        var second = new SelectorSession(Services, new SelectorRequest { Tools = [CaptureTool.Screenshot], Tool = CaptureTool.Screenshot });
        var secondRun = second.RunAsync();
        Pump(() => second.Overlays.Count > 0);
        Assert.True(second.Model!.RepeatAvailable);
        second.OnKey(SelectorKey.R, KeyAction.Down, KeyModifiers.None);
        var repeated = Finish(secondRun);
        Assert.Equal(first!.Region, repeated!.Region);
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Overlay_renders_selection_badge_and_loupe(string theme)
    {
        var (session, run) = Start(CaptureTool.Screenshot);
        var overlay = session.Overlays.First();
        overlay.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        session.OnPointerMoved(new PointD(960, 600), KeyModifiers.None);
        session.OnKey(SelectorKey.Z, KeyAction.Down, KeyModifiers.None);
        Dispatcher.UIThread.RunJobs();
        using var hover = overlay.CaptureRenderedFrame();
        hover!.Save(Path.Combine(TestApp.SnapshotDirectory, $"capture-overlay-hover-{theme}.png"));

        session.OnPointerPressed(new PointD(700, 300), KeyModifiers.None);
        session.OnPointerMoved(new PointD(1180, 640), KeyModifiers.None);
        Dispatcher.UIThread.RunJobs();
        using var drag = overlay.CaptureRenderedFrame();
        drag!.Save(Path.Combine(TestApp.SnapshotDirectory, $"capture-overlay-drag-{theme}.png"));

        session.OnKey(SelectorKey.Escape, KeyAction.Down, KeyModifiers.None);
        Finish(run);
    }
}

/// <summary>Two displays with different DPI (spec 01 §3.4.11).</summary>
public class MultiDisplaySelectorTests
{
    private static readonly ScreenInfo Primary = new()
    {
        Id = "\\\\.\\DISPLAY1", FriendlyName = "Main", Bounds = new PixelRect(0, 0, 1920, 1080), WorkArea = new PixelRect(0, 0, 1920, 1032), Scale = 1.0, IsPrimary = true,
    };

    private static readonly ScreenInfo Laptop = new()
    {
        Id = "\\\\.\\DISPLAY2", FriendlyName = "Laptop", Bounds = new PixelRect(-2880, 200, 2880, 1800), WorkArea = new PixelRect(-2880, 200, 2880, 1728), Scale = 1.5, IsPrimary = false,
    };

    [AvaloniaFact]
    public void Each_display_gets_an_overlay_and_a_drag_keeps_its_display_scale()
    {
        var screens = TestApp.Host.Services.GetRequiredService<Rivet.Platform.Fake.Shell.FakeScreens>();
        var original = screens.Screens;
        screens.Screens = [Primary, Laptop];
        try
        {
            SelectorModel.ResetLastRegion();
            var session = new SelectorSession(TestApp.Host.Services, new SelectorRequest { Tools = [CaptureTool.Screenshot], Tool = CaptureTool.Screenshot });
            var run = session.RunAsync();
            SelectorSessionTests.Pump(() => session.Overlays.Count == 2);
            var laptopOverlay = session.Overlays.Single(o => o.Display.Id == Laptop.Id);
            Assert.Equal(new Avalonia.PixelPoint(-2880, 200), laptopOverlay.Position);
            Assert.Equal(1920, laptopOverlay.Width, 1);

            session.OnPointerMoved(new PointD(-2000, 500), KeyModifiers.None);
            Assert.Equal(Laptop.Id, session.Model!.PointerDisplayId);
            Assert.False(session.Model.HintBarVisible(Primary.Id));
            session.OnPointerPressed(new PointD(-2000, 500), KeyModifiers.None);
            session.OnPointerMoved(new PointD(400, 900), KeyModifiers.None); // past the display edge: clamped
            session.OnPointerReleased(new PointD(400, 900), KeyModifiers.None);
            SelectorSessionTests.Pump(() => run.IsCompleted);
            var outcome = run.Result!;
            Assert.Equal(Laptop.Id, outcome.Display.Id);
            Assert.Equal(new PixelRect(-2000, 500, 2000, 400), outcome.Region);
            Assert.Equal(1.5, outcome.Image!.Scale);
            Assert.Equal((2000, 400), (outcome.Image.Image.Width, outcome.Image.Image.Height));
        }
        finally
        {
            screens.Screens = original;
        }
    }
}
