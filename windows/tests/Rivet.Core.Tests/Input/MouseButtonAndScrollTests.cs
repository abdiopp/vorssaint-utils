// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Input;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.Input;

public class SideWheelGateTests
{
    private static long Ms(double ms) => InputTime.FromMs(ms);

    [Fact]
    public void A_burst_fires_each_direction_once_and_a_quiet_gap_starts_a_new_burst()
    {
        var gate = new SideWheelGate();
        Assert.True(gate.ShouldFire(MouseButtonIds.SideWheelLeft, Ms(0)));
        Assert.False(gate.ShouldFire(MouseButtonIds.SideWheelLeft, Ms(100)));
        Assert.False(gate.ShouldFire(MouseButtonIds.SideWheelLeft, Ms(350)));
        Assert.True(gate.ShouldFire(MouseButtonIds.SideWheelRight, Ms(400)));
        Assert.False(gate.ShouldFire(MouseButtonIds.SideWheelRight, Ms(600)));
        // 250 ms is still the same burst; 251 ms is not.
        Assert.False(gate.ShouldFire(MouseButtonIds.SideWheelLeft, Ms(850)));
        Assert.True(gate.ShouldFire(MouseButtonIds.SideWheelLeft, Ms(1101)));
        Assert.True(gate.ShouldFire(MouseButtonIds.SideWheelRight, Ms(1000)));
    }
}

public class DesktopDragTrackerTests
{
    private static long Ms(double ms) => InputTime.FromMs(ms);

    [Fact]
    public void Exactly_220_fires_and_219_does_not()
    {
        var tracker = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.None, tracker.Feed(219, 0, Ms(0)));
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(1, 0, Ms(10)));
        var left = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.PreviousDesktop, left.Feed(-220, 0, Ms(0)));
    }

    [Fact]
    public void Up_150_opens_the_overview_once_per_press_and_down_is_app_windows()
    {
        var up = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.Overview, up.Feed(0, -150, Ms(0)));
        Assert.Equal(DesktopDragAction.None, up.Feed(0, -400, Ms(1000)));
        Assert.Equal(DesktopDragAction.None, up.Feed(500, 0, Ms(2000)));
        Assert.Equal(DesktopDragAction.AppWindows, new DesktopDragTracker().Feed(0, 150, Ms(0)));
    }

    [Fact]
    public void Repeats_one_step_at_a_time_never_within_the_cooldown_banking_one_step()
    {
        var tracker = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(220, 0, Ms(0)));
        // A fast flick: lots of travel inside 0.35 s banks at most one extra step.
        Assert.Equal(DesktopDragAction.None, tracker.Feed(900, 0, Ms(100)));
        Assert.Equal(DesktopDragAction.None, tracker.Feed(10, 0, Ms(349)));
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(0, 0, Ms(351)));
        Assert.Equal(DesktopDragAction.None, tracker.Feed(0, 0, Ms(800)));
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(220, 0, Ms(900)));
    }

    [Fact]
    public void The_axis_locks_at_the_first_firing_and_ties_go_horizontal()
    {
        var tracker = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(220, -150, Ms(0)));
        Assert.Equal(DesktopDragAction.None, tracker.Feed(0, -600, Ms(1000)));
        var vertical = new DesktopDragTracker();
        Assert.Equal(DesktopDragAction.Overview, vertical.Feed(200, -300, Ms(0)));
        Assert.Equal(DesktopDragAction.None, vertical.Feed(600, 0, Ms(1000)));
    }

    [Fact]
    public void Thresholds_scale_with_the_monitor()
    {
        var tracker = new DesktopDragTracker(scale: 2);
        Assert.Equal(DesktopDragAction.None, tracker.Feed(439, 0, Ms(0)));
        Assert.Equal(DesktopDragAction.NextDesktop, tracker.Feed(1, 0, Ms(1)));
    }
}

public class MouseButtonMappingTests
{
    [Fact]
    public void Invalid_entries_are_dropped_on_read()
    {
        var stored = new Dictionary<string, string>
        {
            ["3"] = "ctrl+alt:0x4B",
            ["4"] = "control+option+command:40", // macOS format
            ["-2"] = "ctrl+shift:0x09",
            ["-1"] = ":0x41",                    // no Ctrl/Alt/Win and not a function key
            ["2"] = "ctrl:0x41",                 // the middle button cannot be mapped
            ["32"] = "ctrl:0x41",
            ["7"] = "shift:0x74",                // Shift+F5 is valid
            ["x"] = "ctrl:0x41",
        };
        var sanitized = MouseButtonMappings.Sanitize(stored);
        Assert.Equal(["-2", "3", "7"], sanitized.Keys.OrderBy(k => int.Parse(k)).ToArray());
        Assert.Equal(new KeyChord(KeyModifiers.Control | KeyModifiers.Alt, 0x4B), MouseButtonMappings.Decode(sanitized)[3]);
    }

    [Fact]
    public void Names_follow_mouse_software_numbering()
    {
        Assert.Equal(("mouseButtons.backButtonName", null), MouseButtonIds.NameKey(3));
        Assert.Equal(("mouseButtons.sideWheelRightName", null), MouseButtonIds.NameKey(-1));
        Assert.Equal(("mouseButtons.otherButtonFormat", 6), MouseButtonIds.NameKey(5));
        Assert.Equal([-2, -1, 3, 5, 12], MouseButtonIds.Sorted([12, 3, -1, 5, -2]).ToArray());
        Assert.Equal(1, MouseButtonIds.ToXButton(3));
        Assert.Equal(0, MouseButtonIds.ToXButton(5));
    }
}

public class SmoothScrollEngineTests
{
    [Fact]
    public void First_frame_of_a_100px_glide_at_the_default_response_is_18px()
    {
        var axis = new SmoothScrollAxis();
        axis.Add(100);
        var first = axis.Step(1.0 / 60, 65, 0);
        Assert.InRange(first, 17.5, 18.5);
    }

    [Fact]
    public void Three_pixels_left_emit_one_pixel_at_60Hz_and_half_at_120Hz()
    {
        var sixty = new SmoothScrollAxis();
        sixty.Add(3);
        Assert.Equal(1.0, sixty.Step(1.0 / 60, 65, 0), 6);
        var oneTwenty = new SmoothScrollAxis();
        oneTwenty.Add(3);
        Assert.Equal(0.5, oneTwenty.Step(1.0 / 120, 65, 0), 6);
    }

    [Fact]
    public void Equal_elapsed_time_gives_identical_results_at_60_and_120Hz()
    {
        var a = new SmoothScrollAxis();
        var b = new SmoothScrollAxis();
        a.Add(400);
        b.Add(400);
        double sumA = 0;
        double sumB = 0;
        for (var i = 0; i < 6; i++)
        {
            sumA += a.Step(1.0 / 60, 65, 0);
        }

        for (var i = 0; i < 12; i++)
        {
            sumB += b.Step(1.0 / 120, 65, 0);
        }

        Assert.Equal(sumA, sumB, 6);
        Assert.Equal(a.Remaining, b.Remaining, 6);
    }

    [Fact]
    public void A_40px_notch_glides_in_about_16_frames_and_coast_stretches_the_landing()
    {
        int Frames(int coast, out double first)
        {
            var axis = new SmoothScrollAxis();
            axis.Add(40);
            first = axis.Step(1.0 / 60, 65, coast);
            var frames = 1;
            while (!axis.IsIdle && frames < 1000)
            {
                axis.Step(1.0 / 60, 65, coast);
                frames++;
            }

            return frames;
        }

        var plain = Frames(0, out var firstPlain);
        Assert.InRange(firstPlain, 7.3, 7.4);
        Assert.InRange(plain, 15, 17);
        var coasting = Frames(100, out _);
        Assert.True(coasting > plain + 8, $"coast 100 lasted {coasting} frames, coast 0 {plain}");
    }

    [Fact]
    public void Response_time_follows_the_spec_formula()
    {
        Assert.Equal(0.160, SmoothScrollAxis.ResponseTime(0), 6);
        Assert.Equal(0.100, SmoothScrollAxis.ResponseTime(50), 6);
        Assert.Equal(0.082, SmoothScrollAxis.ResponseTime(65), 6);
        Assert.Equal(0.040, SmoothScrollAxis.ResponseTime(100), 6);
    }

    [Fact]
    public void A_reversal_replaces_the_tail_and_a_stalled_frame_is_clamped()
    {
        var axis = new SmoothScrollAxis();
        axis.Add(120);
        axis.Step(1.0 / 60, 65, 0);
        Assert.True(axis.Add(-40));
        Assert.Equal(-40, axis.Remaining, 6);

        var stalled = new SmoothScrollAxis();
        stalled.Add(100);
        var emitted = stalled.Step(5.0, 65, 0);
        var clamped = new SmoothScrollAxis();
        clamped.Add(100);
        Assert.Equal(clamped.Step(1.0 / 20, 65, 0), emitted, 6);
    }

    [Fact]
    public void A_default_notch_sends_exactly_120_wheel_units()
    {
        var engine = new SmoothScrollEngine();
        engine.Add(40, horizontal: false, shift: false);
        var total = 0;
        var frames = 0;
        while (frames < 500)
        {
            var (v, h, active) = engine.Frame(1.0 / 60);
            Assert.Equal(0, h);
            total += v;
            frames++;
            if (!active)
            {
                break;
            }
        }

        Assert.Equal(120, total);
        Assert.True(engine.IsIdle);
    }

    [Fact]
    public void A_change_of_Shift_restarts_the_glide()
    {
        var engine = new SmoothScrollEngine();
        engine.Add(100, false, shift: false);
        engine.Frame(1.0 / 60);
        engine.Add(40, false, shift: true);
        var total = 0;
        for (var i = 0; i < 200; i++)
        {
            total += engine.Frame(1.0 / 60).Vertical;
        }

        Assert.Equal(120, total);
    }

    [Fact]
    public void Shift_and_horizontal_events_follow_the_horizontal_direction_setting()
    {
        Assert.True(ScrollDirection.ShouldInvert(false, false, invertVertical: true, invertHorizontal: false));
        Assert.False(ScrollDirection.ShouldInvert(false, true, invertVertical: true, invertHorizontal: false));
        Assert.True(ScrollDirection.ShouldInvert(true, false, invertVertical: false, invertHorizontal: true));
    }

    [Fact]
    public void Only_whole_notches_count_as_a_mouse_wheel_without_device_information()
    {
        Assert.Equal(WheelSource.MouseNotched, NotchWheelClassifier.ClassifyDelta(120));
        Assert.Equal(WheelSource.MouseNotched, NotchWheelClassifier.ClassifyDelta(-240));
        Assert.Equal(WheelSource.Unknown, NotchWheelClassifier.ClassifyDelta(30));
        Assert.Equal(WheelSource.Unknown, NotchWheelClassifier.ClassifyDelta(0));
    }
}
