// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.Capture;

/// <summary>The selector state machine against the spec's acceptance list (Appendix B: Selection, Chooser, Loupe).</summary>
public class SelectorModelTests
{
    private static readonly ScreenInfo Left = new() { Id = "L", FriendlyName = "Left", Bounds = new PixelRect(-1920, 0, 1920, 1080), WorkArea = new PixelRect(-1920, 0, 1920, 1040), Scale = 1, IsPrimary = false };
    private static readonly ScreenInfo Main = new() { Id = "M", FriendlyName = "Main", Bounds = new PixelRect(0, 0, 2880, 1800), WorkArea = new PixelRect(0, 0, 2880, 1728), Scale = 1.5, IsPrimary = true };

    private static readonly CaptureTool[] AllTools = [CaptureTool.Screenshot, CaptureTool.Recording, CaptureTool.Text, CaptureTool.Color];

    private static SelectorModel Model(CaptureTool tool = CaptureTool.Screenshot, IReadOnlyList<CaptureTool>? tools = null, bool regionOnly = false, bool freeze = true)
    {
        SelectorModel.ResetLastRegion();
        var config = new SelectorConfig
        {
            Tools = tools ?? AllTools,
            InitialTool = tool,
            RegionOnly = regionOnly,
            ScreenshotPolicy = new SelectorSourcePolicy(freeze, false, true, true),
        };
        var model = new SelectorModel(config, [Left, Main], new PointD(100, 100))
        {
            Windows =
            [
                new CaptureWindowInfo { Handle = 1, Bounds = new PixelRect(100, 100, 600, 400), Title = "Front", ProcessId = 1, Scale = 1.5 },
                new CaptureWindowInfo { Handle = 2, Bounds = new PixelRect(0, 0, 1500, 1000), Title = "Back", ProcessId = 2, Scale = 1.5 },
            ],
        };
        return model;
    }

    private static SelectorAction? Drag(SelectorModel model, PointD from, PointD to, KeyModifiers modifiers = KeyModifiers.None)
    {
        model.PointerMoved(from, modifiers);
        model.PointerPressed(from, modifiers);
        model.PointerMoved(new PointD((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers);
        model.PointerMoved(to, modifiers);
        return model.PointerReleased(to, modifiers);
    }

    [Fact]
    public void A_drag_confirms_a_positive_region_on_its_display()
    {
        var model = Model();
        var action = Drag(model, new PointD(900, 700), new PointD(300, 200));
        Assert.Equal(SelectorActionKind.ConfirmRegion, action!.Kind);
        Assert.Equal("M", action.DisplayId);
        Assert.Equal(new RectD(300, 200, 600, 500), action.Region);
        Assert.True(model.CapturePending);
    }

    [Fact]
    public void Shift_and_alt_shape_the_drag()
    {
        var square = Drag(Model(), new PointD(500, 500), new PointD(700, 560), KeyModifiers.Shift);
        Assert.Equal(new RectD(500, 500, 200, 200), square!.Region);
        var centred = Drag(Model(), new PointD(500, 500), new PointD(600, 550), KeyModifiers.Alt);
        Assert.Equal(new RectD(400, 450, 200, 100), centred!.Region);
    }

    [Fact]
    public void A_selection_never_spans_displays()
    {
        var action = Drag(Model(), new PointD(200, 200), new PointD(-400, 300));
        Assert.Equal(new RectD(0, 200, 200, 100), action!.Region);
    }

    [Fact]
    public void Under_four_dips_is_a_click_that_picks_the_frontmost_window()
    {
        var model = Model();
        Assert.Equal(1, (int)model.HighlightedWindow!.Handle);
        var action = Drag(model, new PointD(150, 150), new PointD(155, 154)); // 5 px < 4 DIP × 1.5
        Assert.Equal(SelectorActionKind.ConfirmWindow, action!.Kind);
        Assert.Equal(1, (int)action.Window!.Handle);
    }

    [Fact]
    public void A_click_outside_every_window_picks_nothing()
    {
        var model = Model();
        Assert.Null(Drag(model, new PointD(2000, 1500), new PointD(2001, 1500)));
        Assert.False(model.CapturePending);
    }

    [Fact]
    public void A_tiny_selection_is_ignored()
    {
        var model = Model();
        Assert.Null(Drag(model, new PointD(2000, 1500), new PointD(2007, 1502))); // 2 px tall < 2 DIP × 1.5
    }

    [Fact]
    public void A_pending_capture_and_a_finished_session_ignore_input()
    {
        var model = Model();
        Drag(model, new PointD(300, 300), new PointD(600, 600));
        Assert.Null(Drag(model, new PointD(300, 300), new PointD(700, 700)));
        Assert.Null(model.KeyDown(SelectorKey.Enter, KeyModifiers.None));
        model.End();
        Assert.Null(model.KeyDown(SelectorKey.Escape, KeyModifiers.None));
    }

    [Fact]
    public void Escape_cancels()
    {
        Assert.Equal(SelectorActionKind.Cancel, Model().KeyDown(SelectorKey.Escape, KeyModifiers.Control)!.Kind);
    }

    [Fact]
    public void Enter_confirms_the_display_or_the_colour()
    {
        var model = Model();
        model.PointerMoved(new PointD(-500, 400), KeyModifiers.None);
        var action = model.KeyDown(SelectorKey.Enter, KeyModifiers.None);
        Assert.Equal(SelectorActionKind.ConfirmDisplay, action!.Kind);
        Assert.Equal("L", action.DisplayId);

        var recording = Model(CaptureTool.Recording);
        Assert.Equal(SelectorActionKind.ConfirmDisplay, recording.KeyDown(SelectorKey.Enter, KeyModifiers.None)!.Kind);

        var color = Model(CaptureTool.Color);
        Assert.Equal(SelectorActionKind.PickColor, color.KeyDown(SelectorKey.Enter, KeyModifiers.None)!.Kind);
    }

    [Fact]
    public void Digits_switch_tools_and_unavailable_ones_are_ignored()
    {
        var model = Model(tools: [CaptureTool.Screenshot, CaptureTool.Text]);
        Assert.Null(model.KeyDown(SelectorKey.Digit2, KeyModifiers.None));
        Assert.Null(model.KeyDown(SelectorKey.Digit4, KeyModifiers.None));
        var action = model.KeyDown(SelectorKey.Digit3, KeyModifiers.None);
        Assert.Equal(SelectorActionKind.SwitchTool, action!.Kind);
        Assert.Equal(CaptureTool.Text, model.Tool);
        Assert.Null(model.KeyDown(SelectorKey.Digit1, KeyModifiers.Control));
    }

    [Fact]
    public void A_different_source_policy_needs_new_photographs()
    {
        var frozen = Model();
        Assert.False(frozen.KeyDown(SelectorKey.Digit3, KeyModifiers.None)!.NeedsRefresh); // same policy
        Assert.True(frozen.KeyDown(SelectorKey.Digit2, KeyModifiers.None)!.NeedsRefresh);  // recording keeps own windows

        var live = Model(freeze: false);
        var action = live.KeyDown(SelectorKey.Digit4, KeyModifiers.None);
        Assert.True(action!.NeedsRefresh);
        Assert.True(live.RefreshPending);
        Assert.Null(live.KeyDown(SelectorKey.Enter, KeyModifiers.None)); // nothing confirms on stale pixels
        live.CompleteRefresh();
        Assert.Equal(SelectorActionKind.PickColor, live.KeyDown(SelectorKey.Enter, KeyModifiers.None)!.Kind);
    }

    [Fact]
    public void Switching_tools_resets_scrolling_and_sets_the_loupe()
    {
        var model = Model();
        model.KeyDown(SelectorKey.S, KeyModifiers.None);
        Assert.True(model.Scrolling);
        model.KeyDown(SelectorKey.Digit4, KeyModifiers.None);
        Assert.False(model.Scrolling);
        Assert.True(model.LoupeOn);
        model.KeyDown(SelectorKey.Digit1, KeyModifiers.None);
        Assert.False(model.LoupeOn);
    }

    [Fact]
    public void The_full_screen_pill_shows_only_for_screenshots_on_the_pointer_display()
    {
        var model = Model();
        Assert.True(model.FullScreenPillVisible("M"));
        Assert.False(model.FullScreenPillVisible("L"));
        model.PointerPressed(new PointD(300, 300), KeyModifiers.None);
        Assert.False(model.FullScreenPillVisible("M"));
        Assert.False(model.HintBarVisible("M"));
        Assert.False(Model(CaptureTool.Text).FullScreenPillAvailable);
        var scrolling = Model();
        scrolling.KeyDown(SelectorKey.S, KeyModifiers.None);
        Assert.False(scrolling.FullScreenPillAvailable);
    }

    [Fact]
    public void Repeat_is_offered_for_an_existing_display_and_never_for_colour()
    {
        var model = Model();
        Assert.False(model.RepeatAvailable);
        Drag(model, new PointD(-1500, 100), new PointD(-1000, 500));

        // The last region survives into the next session (app session memory).
        var config = new SelectorConfig { Tools = AllTools, InitialTool = CaptureTool.Screenshot };
        var next = new SelectorModel(config, [Left, Main], new PointD(500, 500));
        Assert.True(next.RepeatAvailable);
        Assert.Equal(new RectD(-1500, 100, 500, 400), next.GhostRect("L"));
        Assert.Null(next.GhostRect("M"));
        var repeated = next.KeyDown(SelectorKey.R, KeyModifiers.None);
        Assert.Equal(SelectorActionKind.ConfirmRegion, repeated!.Kind);
        Assert.Equal("L", repeated.DisplayId);
        Assert.Equal(new RectD(-1500, 100, 500, 400), repeated.Region);

        var colour = new SelectorModel(config with { InitialTool = CaptureTool.Color }, [Left, Main], new PointD(500, 500));
        Assert.False(colour.RepeatAvailable);

        var gone = new SelectorModel(config, [Main], new PointD(500, 500));
        Assert.False(gone.RepeatAvailable);
    }

    [Fact]
    public void Scrolling_mode_allows_only_drags()
    {
        var model = Model();
        model.KeyDown(SelectorKey.S, KeyModifiers.None);
        Assert.False(model.AllowsWindowClicks);
        Assert.Null(model.HighlightedWindow);
        Assert.Null(model.KeyDown(SelectorKey.Enter, KeyModifiers.None));
        var action = Drag(model, new PointD(300, 300), new PointD(900, 1200));
        Assert.True(action!.Scrolling);
        Assert.False(Model(CaptureTool.Text).KeyDown(SelectorKey.S, KeyModifiers.None) is not null);
    }

    [Fact]
    public void Colour_mode_forces_the_loupe_and_picks_on_release()
    {
        var model = Model(CaptureTool.Color);
        Assert.True(model.LoupeOn);
        model.KeyDown(SelectorKey.Z, KeyModifiers.None);
        Assert.True(model.LoupeOn);
        var action = Drag(model, new PointD(300, 300), new PointD(500, 520));
        Assert.Equal(SelectorActionKind.PickColor, action!.Kind);
        Assert.Equal(new PointD(500, 520), action.Point);
        Assert.Null(model.Selection);
    }

    [Fact]
    public void Z_toggles_the_loupe_and_C_copies_without_ending()
    {
        var model = Model();
        Assert.Null(model.KeyDown(SelectorKey.C, KeyModifiers.None));
        model.KeyDown(SelectorKey.Z, KeyModifiers.None);
        Assert.True(model.LoupeOn);
        var copy = model.KeyDown(SelectorKey.C, KeyModifiers.None);
        Assert.Equal(SelectorActionKind.CopyColor, copy!.Kind);
        Assert.False(model.CapturePending);
        model.ShowCopied("#FFFFFF", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(model.IsCopiedFeedbackActive(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)));
        Assert.False(model.IsCopiedFeedbackActive(new DateTime(2026, 1, 1, 0, 0, 2, DateTimeKind.Utc)));
    }

    [Fact]
    public void Arrow_nudges_move_one_device_pixel_and_cross_displays()
    {
        var model = Model();
        model.KeyDown(SelectorKey.Z, KeyModifiers.None);
        model.PointerMoved(new PointD(0.4, 500.7), KeyModifiers.None);
        var step = model.KeyDown(SelectorKey.Right, KeyModifiers.None);
        Assert.Equal(new PointD(1, 500), step!.Point);
        var back = model.KeyDown(SelectorKey.Left, KeyModifiers.Shift);
        Assert.Equal(new PointD(-9, 500), back!.Point);
        Assert.Equal("L", model.PointerDisplayId);

        // Beyond every display: stop at the edge.
        model.PointerMoved(new PointD(-1920, 1075), KeyModifiers.None);
        var clamped = model.KeyDown(SelectorKey.Down, KeyModifiers.Shift);
        Assert.Equal(new PointD(-1920, 1079), clamped!.Point);
    }

    [Fact]
    public void Space_moves_the_selection_instead_of_resizing()
    {
        var model = Model();
        model.PointerMoved(new PointD(300, 300), KeyModifiers.None);
        model.PointerPressed(new PointD(300, 300), KeyModifiers.None);
        model.PointerMoved(new PointD(500, 400), KeyModifiers.None);
        model.KeyDown(SelectorKey.Space, KeyModifiers.None);
        model.PointerMoved(new PointD(600, 450), KeyModifiers.None);
        Assert.Equal(new RectD(400, 350, 200, 100), model.Selection);
        model.KeyUp(SelectorKey.Space);
        model.PointerMoved(new PointD(700, 500), KeyModifiers.None);
        var action = model.PointerReleased(new PointD(700, 500), KeyModifiers.None);
        Assert.Equal(new RectD(400, 350, 300, 150), action!.Region);
    }

    [Fact]
    public void Wheel_zooms_only_while_the_loupe_is_on()
    {
        var model = Model();
        Assert.False(model.Wheel(1, continuous: false, altHeld: false));
        model.KeyDown(SelectorKey.Z, KeyModifiers.None);
        Assert.True(model.Wheel(1, continuous: false, altHeld: false));
        Assert.True(model.Zoom > 1);
    }

    [Fact]
    public void Region_only_selectors_ignore_window_clicks_and_enter()
    {
        var model = Model(regionOnly: true);
        Assert.False(model.AllowsWindowClicks);
        Assert.Null(model.KeyDown(SelectorKey.Enter, KeyModifiers.None));
        Assert.False(model.FullScreenPillAvailable);
    }
}
