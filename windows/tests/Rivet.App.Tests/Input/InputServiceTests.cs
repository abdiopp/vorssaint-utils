// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.App.Features.Input;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.App.Tests.Input;

public class ClickFilterServiceTests
{
    [Fact]
    public void Hooks_only_while_installed_switched_on_and_not_suspended()
    {
        var rig = new InputRig();
        using var service = rig.ClickFilter();
        service.Sync(true);
        Assert.Equal(0, rig.Hooks.MouseSubscriberCount);
        rig.Settings.Set(FeatureKeys.MouseClickDebounceEnabled, true);
        service.Sync(true);
        Assert.Equal(1, rig.Hooks.MouseSubscriberCount);
        using (rig.Control.Suspend("cleaning"))
        {
            Assert.Equal(0, rig.Hooks.MouseSubscriberCount);
        }

        Assert.Equal(1, rig.Hooks.MouseSubscriberCount);
        service.Sync(false);
        Assert.Equal(0, rig.Hooks.MouseSubscriberCount);
    }

    [Fact]
    public void A_bounce_is_swallowed_with_its_up_while_moves_and_side_buttons_pass()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.MouseClickDebounceEnabled, true);
        using var service = rig.ClickFilter();
        service.Sync(true);

        Assert.False(rig.Raise(MouseHookKind.LeftDown));
        rig.Clock.AdvanceMs(60);
        Assert.False(rig.Raise(MouseHookKind.LeftUp));
        rig.Clock.AdvanceMs(8);
        Assert.True(rig.Raise(MouseHookKind.LeftDown));
        Assert.False(rig.Raise(MouseHookKind.Move));
        rig.Clock.AdvanceMs(3);
        Assert.True(rig.Raise(MouseHookKind.LeftUp));
        rig.Clock.AdvanceMs(100);
        Assert.False(rig.Raise(MouseHookKind.LeftDown));
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 1));
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 1));
        Assert.Empty(rig.Hooks.Sent);
    }

    [Fact]
    public void The_window_setting_applies_live()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.MouseClickDebounceEnabled, true);
        using var service = rig.ClickFilter();
        service.Sync(true);
        rig.Settings.Set(InputSettings.ClickDebounceWindowMs, 100);
        rig.Raise(MouseHookKind.RightDown);
        rig.Clock.AdvanceMs(10);
        rig.Raise(MouseHookKind.RightUp);
        rig.Clock.AdvanceMs(80);
        Assert.True(rig.Raise(MouseHookKind.RightDown));
    }
}

public class KeyDebounceServiceTests
{
    [Fact]
    public void A_chattering_key_is_swallowed_but_modifiers_and_repeats_pass()
    {
        var rig = new InputRig { Keyboard = { RepeatDelayMs = 250 } };
        rig.Settings.Set(FeatureKeys.KeyboardDebounceEnabled, true);
        rig.Settings.Set(InputSettings.KeyDebounceWindowMs, 30);
        using var service = rig.KeyDebounce();
        service.Sync(true);
        Assert.Equal(1, rig.Hooks.KeyboardSubscriberCount);

        Assert.False(rig.Press(0x41, 0x1E));
        rig.Clock.AdvanceMs(10);
        Assert.False(rig.Release(0x41, 0x1E));
        rig.Clock.AdvanceMs(5);
        Assert.True(rig.Press(0x41, 0x1E));
        Assert.False(rig.Release(0x41, 0x1E));

        Assert.False(rig.Press(VirtualKeys.LShift, 0x2A));
        Assert.False(rig.Release(VirtualKeys.LShift, 0x2A));
        Assert.False(rig.Press(VirtualKeys.LShift, 0x2A));

        rig.Clock.AdvanceMs(100);
        Assert.False(rig.Press(0x42, 0x30));
        rig.Clock.AdvanceMs(260);
        Assert.False(rig.Press(0x42, 0x30));
        rig.Clock.AdvanceMs(33);
        Assert.False(rig.Press(0x42, 0x30));
    }

    [Fact]
    public void Per_key_overrides_come_from_settings()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.KeyboardDebounceEnabled, true);
        rig.Settings.Set(InputSettings.KeyDebounceWindowMs, 5);
        rig.Settings.Set(InputSettings.KeyDebounceKeyWindows, "sc1E:80");
        using var service = rig.KeyDebounce();
        service.Sync(true);
        rig.Press(0x41, 0x1E);
        rig.Release(0x41, 0x1E);
        rig.Clock.AdvanceMs(50);
        Assert.True(rig.Press(0x41, 0x1E));
        rig.Settings.Set(InputSettings.KeyDebounceKeyWindows, "sc1E:0");
        rig.Clock.AdvanceMs(1);
        Assert.False(rig.Press(0x41, 0x1E));
    }
}

public class MouseButtonShortcutsServiceTests
{
    private static InputRig Mapped(out MouseButtonShortcutsService service, string chord = "ctrl+alt:0x4B")
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.MouseButtonShortcutsEnabled, true);
        rig.Settings.Set(InputSettings.MouseButtonShortcuts, new Dictionary<string, string> { ["3"] = chord, ["-1"] = "ctrl+shift:0x09" });
        service = rig.MouseButtons();
        service.Sync(true);
        return rig;
    }

    [Fact]
    public void A_mapped_side_button_presses_its_chord_once_and_swallows_down_and_up()
    {
        var rig = Mapped(out var service);
        using var _ = service;
        Assert.True(rig.Raise(MouseHookKind.XDown, xButton: 1));
        Assert.Equal(["keys:A2↓,A4↓,4B↓,4B↑,A4↑,A2↑"], rig.TakeSent());
        Assert.False(rig.Raise(MouseHookKind.Move, x: 600));
        Assert.True(rig.Raise(MouseHookKind.XUp, xButton: 1));
        Assert.Empty(rig.TakeSent());
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 2));
        Assert.False(rig.Raise(MouseHookKind.XUp, xButton: 2));
    }

    [Fact]
    public void Apps_to_leave_alone_get_the_whole_gesture_and_unknown_apps_are_left_alone()
    {
        var rig = Mapped(out var service);
        using var _ = service;
        rig.Settings.Set(InputSettings.MouseButtonExceptions, [@"C:\Games\Game.exe"]);
        rig.Resolver.PointerAppPath = @"C:\Games\Game.exe";
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 1));
        rig.Resolver.PointerAppPath = @"C:\Program Files\Example\Example.exe";
        Assert.False(rig.Raise(MouseHookKind.XUp, xButton: 1));

        rig.Resolver.ForegroundAppPath = @"C:\Games\Game.exe";
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 1));
        rig.Raise(MouseHookKind.XUp, xButton: 1);

        rig.Resolver.ForegroundAppPath = @"C:\Program Files\Example\Example.exe";
        rig.Resolver.PointerAppPath = null;
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 1));
        rig.Raise(MouseHookKind.XUp, xButton: 1);
        Assert.Empty(rig.Hooks.Sent);
    }

    [Fact]
    public void A_side_wheel_burst_fires_once_and_touchpad_or_unknown_scrolling_passes()
    {
        var rig = Mapped(out var service);
        using var _ = service;
        Assert.True(rig.Raise(MouseHookKind.HorizontalWheel, delta: 120));
        rig.Clock.AdvanceMs(100);
        Assert.True(rig.Raise(MouseHookKind.HorizontalWheel, delta: 120));
        Assert.Equal(["keys:A2↓,A0↓,09↓,09↑,A0↑,A2↑"], rig.TakeSent());
        Assert.False(rig.Raise(MouseHookKind.HorizontalWheel, delta: -120));
        Assert.False(rig.Raise(MouseHookKind.HorizontalWheel, delta: 15));
        rig.Classifier.TouchpadActive = true;
        rig.Clock.AdvanceMs(400);
        Assert.False(rig.Raise(MouseHookKind.HorizontalWheel, delta: 120));
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
        Assert.Empty(rig.Hooks.Sent);
    }

    [Fact]
    public void Dragging_the_gesture_button_switches_desktops_and_a_short_click_is_replayed()
    {
        var rig = new InputRig();
        rig.Settings.Set(InputSettings.DesktopGestureEnabled, true);
        rig.Settings.Set(InputSettings.DesktopGestureButton, 4);
        using var service = rig.MouseButtons();
        service.Sync(true);
        Assert.True(service.IsRunning);

        Assert.True(rig.Raise(MouseHookKind.XDown, 500, 500, xButton: 2));
        Assert.False(rig.Raise(MouseHookKind.Move, 650, 500));
        Assert.Empty(rig.Hooks.Sent);
        Assert.False(rig.Raise(MouseHookKind.Move, 721, 505));
        Assert.Equal(["keys:A2↓,5B↓,27↓,27↑,5B↑,A2↑"], rig.TakeSent());
        Assert.True(rig.Raise(MouseHookKind.XUp, 721, 505, xButton: 2));
        Assert.Empty(rig.TakeSent());

        Assert.True(rig.Raise(MouseHookKind.XDown, 500, 500, xButton: 2));
        rig.Raise(MouseHookKind.Move, 510, 505);
        Assert.True(rig.Raise(MouseHookKind.XUp, 510, 505, xButton: 2));
        Assert.Equal(["mouse:XDown2", "mouse:XUp2"], rig.TakeSent());

        rig.Settings.Set(InputSettings.DesktopGestureFollowsDrag, true);
        rig.Raise(MouseHookKind.XDown, 500, 500, xButton: 2);
        rig.Raise(MouseHookKind.Move, 500, 340);
        Assert.Equal(["keys:5B↓,09↓,09↑,5B↑"], rig.TakeSent());
        rig.Raise(MouseHookKind.XUp, 500, 340, xButton: 2);
        rig.Raise(MouseHookKind.XDown, 500, 500, xButton: 2);
        rig.Raise(MouseHookKind.Move, 280, 500);
        Assert.Equal(["keys:A2↓,5B↓,27↓,27↑,5B↑,A2↑"], rig.TakeSent());
    }

    [Fact]
    public void A_button_with_a_shortcut_cannot_be_the_drag_button()
    {
        var rig = Mapped(out var service);
        using var _ = service;
        rig.Settings.Set(InputSettings.DesktopGestureEnabled, true);
        rig.Settings.Set(InputSettings.DesktopGestureButton, 3);
        Assert.True(rig.Raise(MouseHookKind.XDown, xButton: 1));
        Assert.Single(rig.TakeSent());
    }

    [Fact]
    public void Capture_reports_and_consumes_side_buttons_and_reports_but_passes_the_middle_button()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.MouseButtonShortcutsEnabled, true);
        using var service = rig.MouseButtons();
        service.Sync(true);
        var reported = new List<int>();
        using (var capture = service.BeginCapture(ButtonCaptureKind.Shortcut, reported.Add))
        {
            Assert.NotNull(capture);
            Assert.True(rig.Raise(MouseHookKind.XDown, xButton: 2));
            Assert.True(rig.Raise(MouseHookKind.XUp, xButton: 2));
            Assert.False(rig.Raise(MouseHookKind.MiddleDown));
            Assert.True(rig.Raise(MouseHookKind.HorizontalWheel, delta: -120));
        }

        Assert.Equal([4, 2, -2], reported);
        Assert.False(rig.Raise(MouseHookKind.XDown, xButton: 2));
    }

    [Fact]
    public void Capture_needs_the_feature_installed()
    {
        var rig = new InputRig();
        using var service = rig.MouseButtons();
        service.Sync(false);
        Assert.Null(service.BeginCapture(ButtonCaptureKind.Shortcut, _ => { }));
    }

    [Fact]
    public void Switching_off_mid_press_keeps_swallowing_that_press_s_release()
    {
        var rig = Mapped(out var service);
        using var _ = service;
        Assert.True(rig.Raise(MouseHookKind.XDown, xButton: 1));
        rig.Settings.Set(FeatureKeys.MouseButtonShortcutsEnabled, false);
        service.Sync(true);
        Assert.False(service.IsRunning);
        Assert.True(rig.Raise(MouseHookKind.XUp, xButton: 1));
        Assert.Equal(0, rig.Hooks.MouseSubscriberCount);
        Assert.False(rig.Raise(MouseHookKind.XUp, xButton: 1));
    }
}

public class ScrollServiceTests
{
    [Fact]
    public void A_notch_becomes_an_eased_stream_summing_to_the_step()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.SmoothScrollEnabled, true);
        using var service = rig.SmoothScroll();
        service.Sync(true);
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: -120));
        Assert.True(rig.Timer.IsRunning);
        var frames = rig.Timer.RunToEnd();
        var deltas = rig.TakeSent().Select(s => int.Parse(s["wheel:v".Length..])).ToList();
        Assert.Equal(-120, deltas.Sum());
        Assert.True(deltas.Count > 5, $"{deltas.Count} frames");
        Assert.True(Math.Abs(deltas[0]) < 60);
        Assert.InRange(frames, 10, 40);
    }

    [Fact]
    public void Ctrl_zoom_touchpads_fractions_and_excluded_apps_pass_untouched()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.SmoothScrollEnabled, true);
        using var service = rig.SmoothScroll();
        service.Sync(true);
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120, modifiers: KeyModifiers.Control));
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 30));
        rig.Classifier.TouchpadActive = true;
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
        rig.Classifier.TouchpadActive = false;
        rig.Settings.Set(InputSettings.SmoothScrollExceptions, ["blender.exe"]);
        rig.Resolver.PointerAppPath = @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe";
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
        rig.Resolver.PointerAppPath = null;
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
        Assert.False(rig.Timer.IsRunning);
        Assert.Empty(rig.Hooks.Sent);
    }

    [Fact]
    public void Smooth_scrolling_inverts_the_notches_it_glides_when_the_inverter_is_on()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.SmoothScrollEnabled, true);
        rig.Settings.Set(FeatureKeys.ScrollInverterEnabled, true);
        using var inverter = rig.Inverter();
        using var smooth = rig.SmoothScroll();
        inverter.Sync(true);
        smooth.Sync(true);
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 120));
        rig.Timer.RunToEnd();
        Assert.Equal(-120, rig.TakeSent().Sum(s => int.Parse(s["wheel:v".Length..])));
    }

    [Fact]
    public void The_inverter_flips_mouse_wheels_only()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.ScrollInverterEnabled, true);
        using var service = rig.Inverter();
        service.Sync(true);
        Assert.True(service.IsRunning);
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 240));
        Assert.Equal(["wheel:v-240"], rig.TakeSent());
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: -120, modifiers: KeyModifiers.Control));
        Assert.Equal(["wheel:v120"], rig.TakeSent());
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 40));
        rig.Classifier.FractionsAreMouse = true;
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 40));
        Assert.Equal(["wheel:v-40"], rig.TakeSent());
        rig.Classifier.TouchpadActive = true;
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
    }

    [Fact]
    public void Horizontal_inversion_follows_vertical_until_saved_and_governs_shift_scrolling()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.ScrollInverterEnabled, true);
        using var service = rig.Inverter();
        service.Sync(true);
        Assert.True(rig.Raise(MouseHookKind.HorizontalWheel, delta: 120));
        Assert.Equal(["wheel:h-120"], rig.TakeSent());
        rig.Settings.Set(InputSettings.ScrollInverterHorizontal, false);
        Assert.False(rig.Raise(MouseHookKind.HorizontalWheel, delta: 120));
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120, modifiers: KeyModifiers.Shift));
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 120));
        rig.TakeSent();

        rig.Settings.Set(FeatureKeys.ScrollInverterEnabled, false);
        rig.Settings.Set(InputSettings.ScrollInverterHorizontal, true);
        service.Sync(true);
        Assert.True(service.IsRunning);
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 120, modifiers: KeyModifiers.Shift));
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
    }

    [Fact]
    public void Inverter_exceptions_are_honoured()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.ScrollInverterEnabled, true);
        rig.Settings.Set(InputSettings.ScrollInverterExceptions, [@"C:\Program Files\Example\Example.exe"]);
        using var service = rig.Inverter();
        service.Sync(true);
        Assert.False(rig.Raise(MouseHookKind.Wheel, delta: 120));
        rig.Resolver.PointerAppPath = @"C:\Other\Other.exe";
        Assert.True(rig.Raise(MouseHookKind.Wheel, delta: 120));
    }
}
