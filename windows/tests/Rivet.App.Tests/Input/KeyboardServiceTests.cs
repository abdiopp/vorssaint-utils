// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.App.Features.Input;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.App.Tests.Input;

public class SuperKeyServiceTests
{
    private const int K = 0x4B;

    private static InputRig Started(out SuperKeyService service, string solo = SuperKeySoloActions.None, string source = SuperKeySources.CapsLock)
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.SuperKeyEnabled, true);
        rig.Settings.Set(InputSettings.SuperKeySoloAction, solo);
        rig.Settings.Set(InputSettings.SuperKeySource, source);
        service = rig.SuperKey();
        service.Sync(true);
        return rig;
    }

    [Fact]
    public void Holding_caps_lock_presses_ctrl_alt_shift_until_it_is_released()
    {
        var rig = Started(out var service);
        using var _ = service;
        Assert.Equal(SuperKeyStatus.Working, service.Status);
        Assert.True(rig.Press(VirtualKeys.Capital, 0x3A));
        Assert.Equal(["keys:A2↓,A4↓,A0↓"], rig.TakeSent());
        rig.Clock.AdvanceMs(600);
        Assert.True(rig.Press(VirtualKeys.Capital, 0x3A));
        Assert.Empty(rig.TakeSent());
        Assert.False(rig.Press(K, 0x25));
        Assert.False(rig.Release(K, 0x25));
        Assert.True(rig.Release(VirtualKeys.Capital, 0x3A));
        // A chord: no mask needed except for Alt.
        Assert.Equal(["keys:E8↓,E8↑,A0↑,A4↑,A2↑"], rig.TakeSent());
    }

    [Fact]
    public void A_lone_tap_releases_the_modifiers_behind_a_mask_then_runs_the_tap_action()
    {
        var rig = Started(out var service, SuperKeySoloActions.Escape);
        using var _ = service;
        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.TakeSent();
        rig.Clock.AdvanceMs(120);
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.Equal(["keys:E8↓,E8↑,A0↑,A4↑,A2↑", "keys:1B↓,1B↑"], rig.TakeSent());
    }

    [Fact]
    public void Input_source_switches_on_a_tap_and_a_long_lone_hold_toggles_capitals()
    {
        var rig = Started(out var service, SuperKeySoloActions.InputSource);
        using var _ = service;
        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.Clock.AdvanceMs(100);
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.Equal(1, rig.Keyboard.InputSourceSwitches);
        rig.TakeSent();

        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.Clock.AdvanceMs(500);
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.Equal(1, rig.Keyboard.InputSourceSwitches);
        Assert.Equal("keys:14↓,14↑", rig.TakeSent().Last());
    }

    [Fact]
    public void A_click_while_held_is_a_chord_and_clicks_carry_the_modifiers()
    {
        var rig = Started(out var service, SuperKeySoloActions.Escape);
        using var _ = service;
        rig.Press(VirtualKeys.Capital, 0x3A);
        Assert.False(rig.Raise(Rivet.Core.Platform.MouseHookKind.LeftDown));
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.DoesNotContain("keys:1B↓,1B↑", rig.TakeSent());
    }

    [Fact]
    public void Modifiers_the_user_holds_are_not_pressed_again_and_their_release_waits_for_super()
    {
        var rig = Started(out var service);
        using var _ = service;
        Assert.False(rig.Press(VirtualKeys.LShift, 0x2A));
        rig.Press(VirtualKeys.Capital, 0x3A, KeyModifiers.Shift);
        Assert.Equal(["keys:A2↓,A4↓"], rig.TakeSent());
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.Equal(["keys:E8↓,E8↑,A4↑,A2↑"], rig.TakeSent());
        rig.Release(VirtualKeys.LShift, 0x2A);

        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.TakeSent();
        Assert.False(rig.Press(VirtualKeys.LShift, 0x2A));
        Assert.True(rig.Release(VirtualKeys.LShift, 0x2A));
        rig.Release(VirtualKeys.Capital, 0x3A);
        Assert.Contains("A0↑", rig.TakeSent().Single());
    }

    [Fact]
    public void Starting_on_caps_lock_turns_capitals_off()
    {
        var rig = new InputRig { Keyboard = { IsCapsLockOn = true } };
        rig.Settings.Set(FeatureKeys.SuperKeyEnabled, true);
        using var service = rig.SuperKey();
        service.Sync(true);
        Assert.Equal(["keys:14↓,14↑"], rig.TakeSent());
    }

    [Fact]
    public void Stopping_while_held_never_leaves_modifiers_down()
    {
        var rig = Started(out var service);
        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.TakeSent();
        service.Sync(false);
        Assert.Equal(["keys:E8↓,E8↑,A0↑,A4↑,A2↑"], rig.TakeSent());
        Assert.Equal(0, rig.Hooks.KeyboardSubscriberCount);
        Assert.False(rig.Press(VirtualKeys.Capital, 0x3A));
        service.Dispose();
    }

    [Fact]
    public void The_watchdog_lets_go_when_the_key_is_physically_up()
    {
        var rig = Started(out var service);
        using var _ = service;
        rig.Press(VirtualKeys.Capital, 0x3A);
        rig.TakeSent();
        rig.Keyboard.SetPhysical(VirtualKeys.Capital, null);
        service.RunWatchdogNow();
        Assert.Empty(rig.TakeSent());
        rig.Keyboard.SetPhysical(VirtualKeys.Capital, true);
        service.RunWatchdogNow();
        Assert.Empty(rig.TakeSent());
        rig.Keyboard.SetPhysical(VirtualKeys.Capital, false);
        service.RunWatchdogNow();
        Assert.Equal(["keys:E8↓,E8↑,A0↑,A4↑,A2↑"], rig.TakeSent());
        // Its release, arriving late, stays quiet.
        Assert.True(rig.Release(VirtualKeys.Capital, 0x3A));
        Assert.Empty(rig.TakeSent());
    }

    [Fact]
    public void A_running_app_from_the_list_pauses_the_super_key()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.SuperKeyEnabled, true);
        rig.Settings.Set(InputSettings.SuperKeyExceptions, ["game.exe"]);
        using var service = rig.SuperKey();
        service.Sync(true);
        Assert.True(service.IsRunning);
        Assert.Equal(1, rig.Running.WatcherCount);
        rig.Running.Running.Add(@"C:\Games\game.exe");
        rig.Running.NotifyChanged();
        Assert.False(service.IsRunning);
        Assert.Equal(SuperKeyStatus.Paused, service.Status);
        Assert.False(rig.Press(VirtualKeys.Capital, 0x3A));
        rig.Running.Running.Clear();
        rig.Running.NotifyChanged();
        Assert.True(service.IsRunning);
    }

    [Fact]
    public void Right_alt_as_source_also_swallows_the_altgr_fake_ctrl()
    {
        var rig = Started(out var service, source: SuperKeySources.RightOption);
        using var _ = service;
        Assert.True(rig.Press(VirtualKeys.LControl, SuperKeyService.AltGrFakeControlScan));
        Assert.True(rig.Press(VirtualKeys.RMenu, 0x38));
        Assert.False(rig.Press(VirtualKeys.Capital, 0x3A));
        Assert.True(rig.Release(VirtualKeys.LControl, SuperKeyService.AltGrFakeControlScan));
        Assert.True(rig.Release(VirtualKeys.RMenu, 0x38));
    }

    [Fact]
    public void The_modifier_set_follows_the_setting()
    {
        var rig = Started(out var service);
        using var _ = service;
        rig.Settings.Set(InputSettings.SuperKeyModifiers, "control+option+command");
        rig.Press(VirtualKeys.Capital, 0x3A);
        Assert.Equal(["keys:A2↓,A4↓,5B↓"], rig.TakeSent());
        rig.Release(VirtualKeys.Capital, 0x3A);
        rig.Settings.Set(InputSettings.SuperKeySource, SuperKeySources.RightControl);
        rig.TakeSent();
        Assert.False(rig.Press(VirtualKeys.Capital, 0x3A));
        Assert.True(rig.Press(VirtualKeys.RControl, 0x1D));
    }
}

public class QuitProtectionServiceTests
{
    private const int F4 = 0x73;
    private const int W = 0x57;

    [Fact]
    public void Alt_F4_must_be_held_and_shows_a_hud_with_progress()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.QuitProtectionQuitEnabled, true);
        var hud = new RecordingHud();
        using var service = rig.QuitProtection(hud);
        service.Sync(true);
        Assert.Equal(1, rig.Hooks.KeyboardSubscriberCount);

        rig.Press(VirtualKeys.LMenu, 0x38);
        Assert.True(rig.Press(F4, 0x3E, KeyModifiers.Alt));
        Assert.Equal(["keys:E8↓,E8↑"], rig.TakeSent());
        Assert.Equal("Hold Alt+F4 to quit", hud.Current?.Title);
        Assert.Equal("Esc cancels", hud.Current?.Detail);
        Assert.Equal(TimeSpan.FromMilliseconds(800), hud.Current?.ProgressRemaining);

        rig.Clock.AdvanceMs(800);
        service.Machine.OnWake(rig.Clock.NowNs);
        Assert.Null(hud.Current);
        Assert.Equal(["keys:73↓,73↑"], rig.TakeSent());
        Assert.True(rig.Release(F4, 0x3E, KeyModifiers.Alt));
    }

    [Fact]
    public void Ctrl_W_double_press_passes_the_second_press()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.QuitProtectionCloseEnabled, true);
        rig.Settings.Set(QuitProtectionSlotSettings.Close.Mode, QuitProtectionModes.DoublePress);
        var hud = new RecordingHud();
        using var service = rig.QuitProtection(hud);
        service.Sync(true);
        Assert.True(rig.Press(W, 0x11, KeyModifiers.Control));
        Assert.Equal("Press Ctrl+W again to close", hud.Current?.Title);
        Assert.True(rig.Release(W, 0x11, KeyModifiers.Control));
        rig.Clock.AdvanceMs(300);
        Assert.False(rig.Press(W, 0x11, KeyModifiers.Control));
        Assert.Null(hud.Current);
        Assert.Empty(rig.TakeSent());
    }

    [Fact]
    public void Ctrl_Q_is_part_of_the_quit_slot_unless_switched_off_and_feedback_can_be_hidden()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.QuitProtectionQuitEnabled, true);
        rig.Settings.Set(QuitProtectionSlotSettings.Quit.ShowFeedback, false);
        var hud = new RecordingHud();
        using var service = rig.QuitProtection(hud);
        service.Sync(true);
        Assert.True(rig.Press(0x51, 0x10, KeyModifiers.Control));
        Assert.Empty(hud.Shown);
        rig.Release(0x51, 0x10, KeyModifiers.Control);
        rig.Settings.Set(QuitProtectionSlotSettings.Quit.SecondChord, false);
        Assert.False(rig.Press(0x51, 0x10, KeyModifiers.Control));
    }

    [Fact]
    public void Apps_outside_the_scope_are_not_protected()
    {
        var rig = new InputRig();
        rig.Settings.Set(FeatureKeys.QuitProtectionQuitEnabled, true);
        rig.Settings.Set(QuitProtectionSlotSettings.Quit.Scope, QuitProtectionScopes.SelectedOnly);
        rig.Settings.Set(QuitProtectionSlotSettings.Quit.Exceptions, [@"C:\Apps\Editor.exe"]);
        using var service = rig.QuitProtection(new RecordingHud());
        service.Sync(true);
        Assert.False(rig.Press(F4, 0x3E, KeyModifiers.Alt));
        rig.Release(F4, 0x3E, KeyModifiers.Alt);
        rig.Resolver.ForegroundAppPath = @"C:\Apps\Editor.exe";
        Assert.True(rig.Press(F4, 0x3E, KeyModifiers.Alt));
    }

    [Fact]
    public void The_hud_text_matches_the_mode_and_slot()
    {
        var request = new QuitHudRequest(QuitSlot.Close, QuitProtectionModes.ExtraModifier, new KeyChord(KeyModifiers.Control | KeyModifiers.Shift, W), null, null);
        var content = QuitProtectionService.HudContent(request, null, 0);
        Assert.Equal("Use Ctrl+Shift+W to close", content.Title);
        Assert.Null(content.Detail);
        Assert.Null(content.ProgressRemaining);
    }
}
