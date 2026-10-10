// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Modules.CleaningMode;
using Rivet.Core.Platform;
using Xunit;

namespace Rivet.Core.Tests.CleaningMode;

public class CleaningUnlockCounterTests
{
    private const int Esc = 0x1B;
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Five_escapes_in_a_row_unlock()
    {
        var counter = new CleaningUnlockCounter();
        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(UnlockCounterResult.Advanced, counter.KeyDown(Esc, false, S(i)));
            Assert.Equal(i, counter.Progress);
        }

        Assert.Equal(UnlockCounterResult.Unlock, counter.KeyDown(Esc, false, S(5)));
    }

    [Fact]
    public void The_window_is_six_seconds_inclusive()
    {
        var counter = new CleaningUnlockCounter();
        Assert.Equal(6.0, counter.Window.TotalSeconds);
        counter.KeyDown(Esc, false, S(0));
        counter.KeyDown(Esc, false, S(6.0));
        Assert.Equal(2, counter.Progress);
        counter.KeyDown(Esc, false, S(12.001));
        Assert.Equal(1, counter.Progress);
    }

    [Fact]
    public void Other_keys_and_modifiers_reset_but_repeats_are_ignored()
    {
        var counter = new CleaningUnlockCounter();
        counter.KeyDown(Esc, false, S(0));
        counter.KeyDown(Esc, false, S(1));
        Assert.Equal(UnlockCounterResult.Ignored, counter.KeyDown(Esc, true, S(1.5)));
        Assert.Equal(2, counter.Progress);
        Assert.Equal(UnlockCounterResult.Reset, counter.KeyDown(0x41, false, S(2)));
        Assert.Equal(0, counter.Progress);
        counter.KeyDown(Esc, false, S(3));
        Assert.Equal(UnlockCounterResult.Reset, counter.ModifierChanged());
        Assert.Equal(0, counter.Progress);
    }
}

public class CleaningReleaseGateTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Tears_down_at_once_when_no_button_is_held()
    {
        var gate = new CleaningMouseReleaseGate();
        Assert.Equal(ReleaseGateDecision.TearDown, gate.RequestUnlock(S(0)));
    }

    [Fact]
    public void Waits_for_buttons_pressed_during_the_lock_including_presses_during_the_wait()
    {
        var gate = new CleaningMouseReleaseGate();
        gate.ButtonDown(CleaningMouseButton.Left);
        Assert.Equal(ReleaseGateDecision.Waiting, gate.RequestUnlock(S(0)));
        gate.ButtonDown(CleaningMouseButton.Right);
        Assert.Equal(ReleaseGateDecision.Waiting, gate.ButtonUp(CleaningMouseButton.Left));
        Assert.Equal(ReleaseGateDecision.TearDown, gate.ButtonUp(CleaningMouseButton.Right));
    }

    [Fact]
    public void The_five_second_deadline_forces_the_teardown()
    {
        var gate = new CleaningMouseReleaseGate();
        gate.ButtonDown(CleaningMouseButton.Middle);
        gate.RequestUnlock(S(10));
        Assert.Equal(ReleaseGateDecision.Waiting, gate.Poll(S(14.9)));
        Assert.Equal(ReleaseGateDecision.TearDown, gate.Poll(S(15)));
    }

    [Fact]
    public void A_release_of_a_button_held_before_the_lock_is_ignored()
    {
        var gate = new CleaningMouseReleaseGate();
        Assert.Equal(ReleaseGateDecision.Idle, gate.ButtonUp(CleaningMouseButton.Left));
        Assert.Equal(0, gate.HeldCount);
    }
}

public class CleaningInputFilterTests
{
    private const int Esc = 0x1B;
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    private CleaningInputFilter NewFilter(out CleaningMouseReleaseGate gate)
    {
        gate = new CleaningMouseReleaseGate();
        var filter = new CleaningInputFilter(gate, () => _now);
        filter.Activate();
        return filter;
    }

    [Fact]
    public void Swallows_every_key_including_escape()
    {
        var filter = NewFilter(out _);
        Assert.True(filter.OnKey(0x41, KeyAction.Down));
        Assert.True(filter.OnKey(0x41, KeyAction.Up));
        Assert.True(filter.OnKey(Esc, KeyAction.Down));
        Assert.True(filter.OnKey(0x5B, KeyAction.Down)); // Win
        Assert.True(filter.OnKey(0xAD, KeyAction.Down)); // volume mute
    }

    [Fact]
    public void Swallows_wheels_and_side_buttons_but_passes_clicks_and_moves()
    {
        var filter = NewFilter(out var gate);
        Assert.True(filter.OnMouse(MouseHookKind.Wheel));
        Assert.True(filter.OnMouse(MouseHookKind.HorizontalWheel));
        Assert.True(filter.OnMouse(MouseHookKind.XDown));
        Assert.False(filter.OnMouse(MouseHookKind.Move));
        Assert.False(filter.OnMouse(MouseHookKind.LeftDown));
        Assert.Equal(1, gate.HeldCount);
        Assert.False(filter.OnMouse(MouseHookKind.LeftUp));
        Assert.Equal(0, gate.HeldCount);
    }

    [Fact]
    public void A_held_key_is_auto_repeat_and_neither_counts_nor_resets()
    {
        var filter = NewFilter(out _);
        filter.OnKey(Esc, KeyAction.Down);
        filter.OnKey(Esc, KeyAction.Down); // repeat
        filter.OnKey(Esc, KeyAction.Down); // repeat
        Assert.Equal(1, filter.Progress);
        filter.OnKey(Esc, KeyAction.Up);
        filter.OnKey(Esc, KeyAction.Down);
        Assert.Equal(2, filter.Progress);
    }

    [Fact]
    public void Modifier_press_and_release_reset_the_counter()
    {
        var filter = NewFilter(out _);
        filter.OnKey(Esc, KeyAction.Down);
        filter.OnKey(Esc, KeyAction.Up);
        filter.OnKey(0xA0, KeyAction.Down); // left shift
        Assert.Equal(0, filter.Progress);
        filter.OnKey(Esc, KeyAction.Down);
        filter.OnKey(Esc, KeyAction.Up);
        Assert.Equal(1, filter.Progress);
        filter.OnKey(0xA0, KeyAction.Up);
        Assert.Equal(0, filter.Progress);
    }

    [Fact]
    public void Five_escapes_raise_the_unlock_request()
    {
        var filter = NewFilter(out _);
        var unlocked = 0;
        filter.UnlockRequested += () => unlocked++;
        for (var i = 0; i < 5; i++)
        {
            filter.OnKey(Esc, KeyAction.Down);
            filter.OnKey(Esc, KeyAction.Up);
            _now += TimeSpan.FromSeconds(1);
        }

        Assert.Equal(1, unlocked);
    }

    [Fact]
    public void A_hung_ui_releases_the_keyboard()
    {
        var filter = NewFilter(out _);
        CleaningFailOpenReason? reason = null;
        filter.FailedOpen += r => reason = r;
        _now += TimeSpan.FromSeconds(16);
        Assert.False(filter.OnKey(0x41, KeyAction.Down));
        Assert.Equal(CleaningFailOpenReason.UiNotResponding, reason);
        Assert.False(filter.IsActive);
        Assert.False(filter.OnKey(0x42, KeyAction.Down));
    }

    [Fact]
    public void A_stalled_teardown_releases_the_keyboard()
    {
        var filter = NewFilter(out _);
        CleaningFailOpenReason? reason = null;
        filter.FailedOpen += r => reason = r;
        filter.NoteUnlockRequested();
        _now += TimeSpan.FromSeconds(4);
        filter.Heartbeat();
        Assert.True(filter.OnKey(0x41, KeyAction.Down));
        _now += TimeSpan.FromSeconds(4);
        filter.Heartbeat();
        Assert.False(filter.OnKey(0x41, KeyAction.Down));
        Assert.Equal(CleaningFailOpenReason.TeardownStalled, reason);
    }

    [Fact]
    public void Nothing_is_swallowed_when_inactive()
    {
        var gate = new CleaningMouseReleaseGate();
        var filter = new CleaningInputFilter(gate, () => _now);
        Assert.False(filter.OnKey(0x41, KeyAction.Down));
        Assert.False(filter.OnMouse(MouseHookKind.Wheel));
    }
}

public class CleaningModeControllerTests
{
    private const int Esc = 0x1B;

    [Fact]
    public void Activation_installs_the_filter_first_and_teardown_removes_it()
    {
        var hooks = new TestInputHooks();
        var controller = new CleaningModeController(hooks);
        Assert.True(controller.TryActivate());
        Assert.Equal(CleaningModeState.Active, controller.State);
        Assert.Equal(1, hooks.KeyboardCount);
        Assert.True(hooks.Press(0x41));
        CleaningEndReason? ended = null;
        controller.Ended += (_, r) => ended = r;
        for (var i = 0; i < 5; i++)
        {
            hooks.Press(Esc);
        }

        Assert.Equal(CleaningEndReason.Unlocked, ended);
        Assert.Equal(CleaningModeState.Inactive, controller.State);
        Assert.Equal(0, hooks.KeyboardCount);
        Assert.Equal(0, hooks.MouseCount);
        Assert.False(hooks.Press(0x41));
    }

    [Fact]
    public void The_lock_has_priority_over_every_other_subscriber()
    {
        var hooks = new TestInputHooks();
        var seenByOther = 0;
        hooks.SubscribeKeyboard((ref KeyboardHookEvent _) => { seenByOther++; return false; }, priority: 1000);
        var controller = new CleaningModeController(hooks);
        controller.TryActivate();
        hooks.Press(0x41);
        Assert.Equal(0, seenByOther);
        controller.ForceEnd(CleaningEndReason.SessionChanged);
        hooks.Press(0x41);
        Assert.Equal(2, seenByOther);
    }

    [Fact]
    public void Without_hooks_nothing_locks()
    {
        var hooks = new TestInputHooks { ThrowOnSubscribe = true };
        var controller = new CleaningModeController(hooks);
        Assert.False(controller.TryActivate());
        Assert.Equal(CleaningModeState.Inactive, controller.State);
    }

    [Fact]
    public void Unlock_waits_for_a_button_held_during_the_lock()
    {
        var hooks = new TestInputHooks();
        var controller = new CleaningModeController(hooks);
        controller.TryActivate();
        hooks.Mouse(MouseHookKind.RightDown);
        controller.RequestUnlock();
        Assert.Equal(CleaningModeState.Pending, controller.State);
        Assert.True(hooks.Press(0x41));
        hooks.Mouse(MouseHookKind.RightUp);
        Assert.Equal(CleaningModeState.Inactive, controller.State);
    }

    [Fact]
    public void Dispose_always_restores_input()
    {
        var hooks = new TestInputHooks();
        var controller = new CleaningModeController(hooks);
        controller.TryActivate();
        controller.Dispose();
        Assert.Equal(0, hooks.KeyboardCount);
        Assert.False(hooks.Press(0x41));
    }

    [Fact]
    public void A_second_activation_is_ignored()
    {
        var hooks = new TestInputHooks();
        var controller = new CleaningModeController(hooks);
        Assert.True(controller.TryActivate());
        Assert.False(controller.TryActivate());
        Assert.Equal(1, hooks.KeyboardCount);
    }
}
