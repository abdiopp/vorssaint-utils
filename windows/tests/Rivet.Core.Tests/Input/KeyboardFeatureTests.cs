// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Input;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Xunit;

namespace Rivet.Core.Tests.Input;

public class SuperKeyStateTests
{
    private static long Ms(double ms) => InputTime.FromMs(ms);

    [Fact]
    public void A_lone_press_is_a_tap_under_500ms_and_a_hold_from_500ms()
    {
        var state = new SuperKeyState();
        Assert.True(state.OnTriggerDown(false, Ms(0)));
        Assert.Equal(SuperKeyRelease.SoloTap, state.OnTriggerUp(Ms(499)));
        Assert.True(state.OnTriggerDown(false, Ms(1000)));
        Assert.Equal(SuperKeyRelease.SoloHold, state.OnTriggerUp(Ms(1500)));
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void Repeats_change_nothing_and_any_other_input_makes_a_chord()
    {
        var state = new SuperKeyState();
        Assert.True(state.OnTriggerDown(false, Ms(0)));
        Assert.False(state.OnTriggerDown(false, Ms(600)));
        Assert.Equal(Ms(0), state.DownTimestamp);
        Assert.False(state.DidRepeat);
        state.OnOtherInput();
        Assert.Equal(SuperKeyRelease.Chord, state.OnTriggerUp(Ms(700)));

        Assert.True(state.OnTriggerDown(otherModifiersHeld: true, Ms(1000)));
        Assert.Equal(SuperKeyRelease.Chord, state.OnTriggerUp(Ms(1100)));

        Assert.True(state.OnTriggerDown(false, Ms(2000)));
        state.OnTriggerDown(false, Ms(2600), countRepeats: true);
        Assert.True(state.DidRepeat);
    }

    [Theory]
    [InlineData(SuperKeySoloActions.None, SuperKeyRelease.SoloTap, false, SuperKeyEffect.None)]
    [InlineData(SuperKeySoloActions.Escape, SuperKeyRelease.SoloTap, false, SuperKeyEffect.Escape)]
    [InlineData(SuperKeySoloActions.Escape, SuperKeyRelease.SoloHold, false, SuperKeyEffect.Escape)]
    [InlineData(SuperKeySoloActions.Escape, SuperKeyRelease.SoloHold, true, SuperKeyEffect.None)]
    [InlineData(SuperKeySoloActions.CapsLock, SuperKeyRelease.SoloTap, false, SuperKeyEffect.ToggleCapsLock)]
    [InlineData(SuperKeySoloActions.CapsLock, SuperKeyRelease.SoloHold, false, SuperKeyEffect.ToggleCapsLock)]
    [InlineData(SuperKeySoloActions.CapsLock, SuperKeyRelease.SoloHold, true, SuperKeyEffect.None)]
    [InlineData(SuperKeySoloActions.InputSource, SuperKeyRelease.SoloTap, false, SuperKeyEffect.NextInputSource)]
    [InlineData(SuperKeySoloActions.InputSource, SuperKeyRelease.SoloHold, false, SuperKeyEffect.ToggleCapsLock)]
    [InlineData(SuperKeySoloActions.InputSource, SuperKeyRelease.SoloHold, true, SuperKeyEffect.ToggleCapsLock)]
    [InlineData(SuperKeySoloActions.Escape, SuperKeyRelease.Chord, false, SuperKeyEffect.None)]
    public void Solo_effect_table(string action, SuperKeyRelease release, bool didRepeat, SuperKeyEffect expected) =>
        Assert.Equal(expected, SuperKeyState.Effect(action, release, didRepeat));

    [Fact]
    public void Modifier_sets_need_ctrl_alt_or_win()
    {
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, SuperKeyModifierSet.Parse(null));
        Assert.Equal(SuperKeyModifierSet.Default, SuperKeyModifierSet.Parse("shift"));
        Assert.Equal(KeyModifiers.Win | KeyModifiers.Shift, SuperKeyModifierSet.Parse("shift+command"));
        Assert.Equal("control+option+shift+command", SuperKeyModifierSet.SanitizeStorage("command+shift+option+control"));
        Assert.Equal("control+option+shift", SuperKeyModifierSet.SanitizeStorage("bogus"));
        Assert.True(SuperKeyModifierSet.IsOfficeKey(SuperKeyModifierSet.Parse("control+option+shift+command")));
        Assert.False(SuperKeyModifierSet.CanToggle(KeyModifiers.Control | KeyModifiers.Shift, KeyModifiers.Control));
        Assert.True(SuperKeyModifierSet.CanToggle(KeyModifiers.Control | KeyModifiers.Shift, KeyModifiers.Shift));
        Assert.True(SuperKeyModifierSet.CanToggle(KeyModifiers.Control, KeyModifiers.Win));
    }

    [Fact]
    public void Sources_map_to_windows_keys_and_unknown_values_fall_back_to_caps_lock()
    {
        Assert.Equal(VirtualKeys.Capital, SuperKeySources.VirtualKey(SuperKeySources.Sanitize("leftFoot")));
        Assert.Equal(VirtualKeys.RWin, SuperKeySources.VirtualKey(SuperKeySources.RightCommand));
        Assert.Equal(VirtualKeys.RMenu, SuperKeySources.VirtualKey(SuperKeySources.RightOption));
        Assert.Equal(VirtualKeys.Apps, SuperKeySources.VirtualKey(SuperKeySources.Apps));
        Assert.Equal(SuperKeySoloActions.None, SuperKeySoloActions.Sanitize("dance"));
    }

    [Fact]
    public void Input_sources_cycle_and_wrap()
    {
        IReadOnlyList<nint> layouts = [10, 20, 30];
        Assert.Equal((nint)20, InputSourceCycle.Next(layouts, (nint)10));
        Assert.Equal((nint)10, InputSourceCycle.Next(layouts, (nint)30));
        Assert.Equal((nint)10, InputSourceCycle.Next(layouts, (nint)99));
        Assert.Equal((nint)10, InputSourceCycle.Next(layouts, null));
        Assert.Null(InputSourceCycle.Next<nint>([10], 10));
    }

    [Fact]
    public void The_watchdog_waits_twice_the_repeat_delay_between_3_and_30_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), SuperKeyState.WatchdogDelay(250));
        Assert.Equal(TimeSpan.FromSeconds(3), SuperKeyState.WatchdogDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(4), SuperKeyState.WatchdogDelay(2000));
        Assert.Equal(TimeSpan.FromSeconds(30), SuperKeyState.WatchdogDelay(60000));
    }
}

public class ChordStrokeTests
{
    private static string Describe(IEnumerable<(int Vk, KeyAction Action)> strokes) =>
        string.Join(",", strokes.Select(s => $"{s.Vk:X2}{(s.Action == KeyAction.Down ? "v" : "^")}"));

    [Fact]
    public void Missing_modifiers_are_pressed_around_the_key()
    {
        var strokes = ChordStrokes.Exact(new KeyChord(KeyModifiers.Control | KeyModifiers.Win, VirtualKeys.Left), _ => false);
        Assert.Equal("A2v,5Bv,25v,25^,5B^,A2^", Describe(strokes));
    }

    [Fact]
    public void Held_chord_modifiers_are_reused_and_extra_ones_released_behind_a_mask()
    {
        var held = new HashSet<int> { VirtualKeys.RControl, VirtualKeys.LShift };
        var strokes = ChordStrokes.Exact(new KeyChord(KeyModifiers.Control, VirtualKeys.Letter('W')), held.Contains);
        Assert.Equal("E8v,E8^,A0^,57v,57^", Describe(strokes));
    }
}

public class QuitProtectionMachineTests
{
    private const int F4 = 0x73;
    private const int W = 0x57;
    private const int Q = 0x51;

    private static long Ms(double ms) => InputTime.FromMs(ms);

    private static QuitProtectionSlotConfig QuitSlot(string mode, string scope = QuitProtectionScopes.All, params string[] apps) => new()
    {
        Slot = Core.Input.QuitSlot.Quit,
        Enabled = true,
        Mode = mode,
        HoldMs = 800,
        DoubleIntervalMs = 1500,
        Scope = scope,
        Apps = new AppExclusionList(apps),
        Chords = [QuitProtectionChord.AltF4, QuitProtectionChord.CtrlQ],
    };

    private static QuitProtectionSlotConfig CloseSlot(string mode, string extra = QuitProtectionModes.ShiftModifier) => new()
    {
        Slot = Core.Input.QuitSlot.Close,
        Enabled = true,
        Mode = mode,
        ExtraModifier = extra,
        Chords = [QuitProtectionChord.CtrlW],
    };

    [Fact]
    public void Hold_confirms_after_the_duration_and_eats_the_physical_repeats_and_release()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.Hold) };
        machine.OnKeyDown(0xA4, KeyModifiers.None, Ms(0));
        Assert.True(machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(10)));
        Assert.Equal(1, host.Masks);
        Assert.NotNull(host.Hud);
        Assert.Equal(Ms(810), host.Hud!.ProgressEndNs);
        Assert.Equal(Ms(810), host.Wake);

        Assert.True(machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(500)));
        machine.OnWake(Ms(700));
        Assert.Empty(host.Sent);
        machine.OnWake(Ms(810));
        Assert.Equal([new KeyChord(KeyModifiers.Alt, F4)], host.Sent);
        Assert.Null(host.Hud);
        Assert.True(machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(900)));
        Assert.True(machine.OnKeyUp(F4, Ms(950)));
        Assert.False(machine.OnKeyUp(0xA4, Ms(960)));
    }

    [Fact]
    public void Releasing_early_releasing_alt_or_pressing_esc_cancels_a_hold()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.Hold) };
        machine.OnKeyDown(0xA4, KeyModifiers.None, Ms(0));
        machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(10));
        Assert.True(machine.OnKeyUp(F4, Ms(200)));
        Assert.False(machine.IsPending);

        machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(300));
        Assert.False(machine.OnKeyUp(0xA4, Ms(400)));
        Assert.False(machine.IsPending);
        machine.OnKeyUp(F4, Ms(450));

        machine.OnKeyDown(0xA4, KeyModifiers.None, Ms(500));
        machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(510));
        Assert.True(machine.OnKeyDown(VirtualKeys.Escape, KeyModifiers.Alt, Ms(600)));
        Assert.False(machine.IsPending);
        machine.OnWake(Ms(2000));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void A_hold_never_confirms_into_another_window()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.Hold) };
        machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(0));
        host.Window = 0x2222;
        machine.OnWake(Ms(900));
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void Double_press_confirms_inclusively_at_the_interval()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.DoublePress) };
        Assert.True(machine.OnKeyDown(Q, KeyModifiers.Control, Ms(0)));
        Assert.True(machine.OnKeyUp(Q, Ms(80)));
        Assert.True(machine.IsPending);
        Assert.Equal(Ms(1600), host.Wake);
        // Exactly 1.5 s later: the press passes through (it is the confirmation).
        Assert.False(machine.OnKeyDown(Q, KeyModifiers.Control, Ms(1500)));
        Assert.True(machine.OnKeyDown(Q, KeyModifiers.Control, Ms(1600)));
        Assert.False(machine.OnKeyUp(Q, Ms(1650)));

        var late = new QuitProtectionMachine(new FakeHost()) { Quit = QuitSlot(QuitProtectionModes.DoublePress) };
        late.OnKeyDown(Q, KeyModifiers.Control, 0);
        late.OnKeyUp(Q, Ms(10));
        Assert.True(late.OnKeyDown(Q, KeyModifiers.Control, Ms(1500) + 1));
        Assert.True(late.IsPending);
    }

    [Fact]
    public void Extra_modifier_confirms_without_the_extra_modifier_and_hints_on_the_plain_chord()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Close = CloseSlot(QuitProtectionModes.ExtraModifier) };
        Assert.True(machine.OnKeyDown(W, KeyModifiers.Control, Ms(0)));
        Assert.Equal(new KeyChord(KeyModifiers.Control | KeyModifiers.Shift, W), host.Hud!.Shown);
        Assert.Equal(Ms(1500), host.Wake);
        Assert.True(machine.OnKeyUp(W, Ms(50)));
        Assert.False(machine.IsPending);

        Assert.True(machine.OnKeyDown(W, KeyModifiers.Control | KeyModifiers.Shift, Ms(100)));
        Assert.Equal([new KeyChord(KeyModifiers.Control, W)], host.Sent);
        Assert.True(machine.OnKeyUp(W, Ms(150)));
        Assert.False(machine.OnKeyDown(W, KeyModifiers.Control | KeyModifiers.Alt, Ms(200)));
    }

    [Fact]
    public void Other_keys_and_chords_pass_and_cancel_what_is_pending()
    {
        var host = new FakeHost();
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.Hold), Close = CloseSlot(QuitProtectionModes.Hold) };
        Assert.False(machine.OnKeyDown(0x41, KeyModifiers.None, Ms(0)));
        Assert.False(machine.OnKeyDown(Q, KeyModifiers.Control | KeyModifiers.Shift, Ms(10)));
        machine.OnKeyUp(Q, Ms(15));
        Assert.True(machine.OnKeyDown(Q, KeyModifiers.Control, Ms(20)));
        Assert.Equal(QuitProtectionChord.CtrlQ, machine.PendingChord);
        Assert.True(machine.OnKeyDown(W, KeyModifiers.Control, Ms(30)));
        Assert.Equal(QuitProtectionChord.CtrlW, machine.PendingChord);
        Assert.False(machine.OnKeyDown(0x41, KeyModifiers.Control, Ms(40)));
        Assert.False(machine.IsPending);
    }

    [Fact]
    public void A_disabled_slot_passes()
    {
        var machine = new QuitProtectionMachine(new FakeHost()) { Quit = QuitSlot(QuitProtectionModes.Hold) with { Enabled = false } };
        Assert.False(machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(0)));
    }

    [Theory]
    [InlineData(QuitProtectionScopes.All, @"C:\Apps\Game.exe", true)]
    [InlineData(QuitProtectionScopes.SelectedOnly, @"C:\Apps\Game.exe", true)]
    [InlineData(QuitProtectionScopes.SelectedOnly, @"C:\Apps\Other.exe", false)]
    [InlineData(QuitProtectionScopes.SelectedOnly, null, false)]
    [InlineData(QuitProtectionScopes.AllExceptSelected, @"C:\Apps\Game.exe", false)]
    [InlineData(QuitProtectionScopes.AllExceptSelected, @"C:\Apps\Other.exe", true)]
    [InlineData(QuitProtectionScopes.AllExceptSelected, null, true)]
    public void Scope_truth_table(string scope, string? app, bool protectedHere)
    {
        var host = new FakeHost { AppPath = app };
        var machine = new QuitProtectionMachine(host) { Quit = QuitSlot(QuitProtectionModes.Hold, scope, @"c:\apps\game.exe") };
        Assert.Equal(protectedHere, machine.OnKeyDown(F4, KeyModifiers.Alt, Ms(0)));
    }

    [Fact]
    public void Durations_are_clamped_and_extra_modifiers_never_equal_the_base()
    {
        var store = SettingsStore.InMemory();
        var slot = QuitProtectionSlotSettings.Quit;
        store.Set(slot.HoldDurationMs, 100);
        Assert.Equal(250, store.Get(slot.HoldDurationMs));
        store.Set(slot.HoldDurationMs, 3000);
        Assert.Equal(2000, store.Get(slot.HoldDurationMs));
        store.Set(slot.HoldDurationMs, double.NaN);
        Assert.Equal(800, store.Get(slot.HoldDurationMs));
        store.Set(slot.DoubleIntervalMs, 100);
        Assert.Equal(200, store.Get(slot.DoubleIntervalMs));
        store.Set(slot.DoubleIntervalMs, 3000);
        Assert.Equal(1500, store.Get(slot.DoubleIntervalMs));
        store.Set(slot.Mode, "sometimes");
        Assert.Equal(QuitProtectionModes.Hold, store.Get(slot.Mode));
        Assert.Equal(KeyModifiers.Shift, QuitProtectionModes.EffectiveExtra(QuitProtectionModes.OptionModifier, KeyModifiers.Alt));
        Assert.Equal(KeyModifiers.Alt, QuitProtectionModes.EffectiveExtra(QuitProtectionModes.OptionModifier, KeyModifiers.Control));
        Assert.Equal(["a.exe", "B.exe"], AppExclusionList.SanitizeSorted(["B.exe", " a.exe", "b.EXE", ""]));
    }

    private sealed class FakeHost : IQuitProtectionHost
    {
        public List<KeyChord> Sent { get; } = [];

        public QuitHudRequest? Hud { get; private set; }

        public int Masks { get; private set; }

        public long? Wake { get; private set; }

        public nint Window { get; set; } = 0x1111;

        public string? AppPath { get; set; } = @"C:\Apps\Editor.exe";

        public void ShowHud(QuitHudRequest request) => Hud = request;

        public void HideHud() => Hud = null;

        public void SendChord(KeyChord chord) => Sent.Add(chord);

        public void SendMask() => Masks++;

        public void ScheduleWake(long? deadlineNs) => Wake = deadlineNs;

        public nint ForegroundWindow() => Window;

        public string? ForegroundAppPath() => AppPath;
    }
}

public class AppExclusionListTests
{
    [Fact]
    public void Entries_are_trimmed_and_deduplicated_case_insensitively_in_order()
    {
        Assert.Equal([@"C:\A\b.exe", "blender.exe"], AppExclusionList.Sanitize([@" C:\A\b.exe ", "", @"c:\a\B.EXE", "blender.exe", "  "]));
    }

    [Fact]
    public void Paths_match_case_insensitively_with_either_separator()
    {
        var list = new AppExclusionList([@"C:\Program Files\Mozilla Firefox\firefox.exe"]);
        Assert.True(list.Matches(@"c:\program files\mozilla firefox\FIREFOX.EXE"));
        Assert.True(list.Matches("C:/Program Files/Mozilla Firefox/firefox.exe"));
        Assert.True(list.Matches(@"\\?\C:\Program Files\Mozilla Firefox\firefox.exe"));
        Assert.False(list.Matches(@"C:\Other\firefox.exe"));
        Assert.False(list.Matches(null));
    }

    [Fact]
    public void Version_folders_keep_matching_after_an_update()
    {
        var list = new AppExclusionList([@"C:\Users\me\AppData\Local\Discord\app-1.0.9005\Discord.exe", @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe"]);
        Assert.True(list.Matches(@"C:\Users\me\AppData\Local\Discord\app-1.0.9010\Discord.exe"));
        Assert.True(list.Matches(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe"));
        Assert.False(list.Matches(@"C:\Program Files\Blender Foundation\Blender 4.2\other.exe"));
    }

    [Fact]
    public void Bare_executable_names_match_anywhere_and_bundle_ids_never_match()
    {
        var list = new AppExclusionList(["blender.exe", "com.apple.Safari"]);
        Assert.True(list.Matches(@"D:\Tools\Blender\blender.exe"));
        Assert.False(list.Matches(@"C:\Apps\com.apple.Safari"));
        Assert.Equal("blender", new AppIdentityInfo(@"D:\Tools\Blender\blender.exe").DisplayName);
    }
}
