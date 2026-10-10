// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Awake;
using Xunit;

namespace Rivet.Core.Tests.Awake;

/// <summary>Keep Awake behaviour contracts (spec §3.18, §8.4).</summary>
public class KeepAwakeManagerTests
{
    [Fact]
    public void Presets_start_timed_sessions_and_become_the_last_pick()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(30);
        Assert.True(manager.IsActive);
        Assert.Equal(rig.Timers.Now.AddMinutes(30), manager.State.EndTime);
        Assert.Equal(30, rig.Settings.Get(KeepAwakeSettings.DefaultDurationMinutes));
        Assert.False(rig.Settings.Get(KeepAwakeSettings.SwitchUsesUntil));
        Assert.True(rig.Requests.System);
        Assert.True(rig.Requests.Display);
    }

    [Fact]
    public void Unknown_presets_mean_indefinitely()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(45);
        Assert.True(manager.IsActive);
        Assert.Null(manager.State.EndTime);
        Assert.Equal(0, rig.Settings.Get(KeepAwakeSettings.DefaultDurationMinutes));
    }

    [Fact]
    public void Last_pick_prefers_a_future_until_time_and_never_rolls_a_passed_one_to_tomorrow()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        rig.Settings.Set(KeepAwakeSettings.DefaultDurationMinutes, 60);
        var until = rig.Timers.Now.AddHours(3);
        manager.ActivateUntil(until);
        Assert.True(rig.Settings.Get(KeepAwakeSettings.SwitchUsesUntil));
        manager.Toggle();
        Assert.False(manager.IsActive);

        manager.StartLastPick();
        Assert.Equal(until, manager.State.EndTime);
        Assert.Null(manager.State.SessionMinutes);
        manager.Toggle();

        rig.Timers.Advance(TimeSpan.FromHours(4));
        manager.StartLastPick();
        Assert.Equal(rig.Timers.Now.AddMinutes(60), manager.State.EndTime);
    }

    [Fact]
    public void Until_in_the_past_is_ignored()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.ActivateUntil(rig.Timers.Now.AddMinutes(-5));
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Extend_adds_to_the_later_of_end_and_now()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(15);
        var end = manager.State.EndTime!.Value;
        manager.Extend(30);
        Assert.Equal(end.AddMinutes(30), manager.State.EndTime);
        manager.Activate(0);
        manager.Extend(15);
        Assert.Null(manager.State.EndTime);
    }

    [Fact]
    public void Timer_end_notifies_and_restores_normal_sleep()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(15);
        rig.Timers.Advance(TimeSpan.FromMinutes(15));
        Assert.False(manager.IsActive);
        Assert.False(rig.Requests.System);
        Assert.Single(rig.Toasts.Shown);
    }

    [Fact]
    public void Timer_hands_over_to_automation_only_when_it_would_start_any()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationExternalDisplay, true);
        rig.Settings.Set(KeepAwakeSettings.AutomationPower, true);
        var manager = rig.Create();
        manager.Activate(15);
        rig.Displays.External = true;
        rig.Timers.Advance(TimeSpan.FromMinutes(15));
        Assert.True(manager.IsActive);
        Assert.Equal(KeepAwakeTrigger.Automation, manager.State.Trigger);
        Assert.Null(manager.State.EndTime);
        Assert.Empty(rig.Toasts.Shown);
    }

    [Fact]
    public void Timer_does_not_hand_over_when_all_conditions_are_required()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationExternalDisplay, true);
        rig.Settings.Set(KeepAwakeSettings.AutomationPower, true);
        rig.Settings.Set(KeepAwakeSettings.AutomationRequireAll, true);
        var manager = rig.Create();
        manager.Activate(15);
        rig.Displays.External = true;
        rig.Timers.Advance(TimeSpan.FromMinutes(15));
        Assert.False(manager.IsActive);
        Assert.Single(rig.Toasts.Shown);
    }

    [Fact]
    public void Battery_protection_outranks_the_hand_over()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationExternalDisplay, true);
        var manager = rig.Create();
        manager.Activate(15);
        rig.Displays.External = true;
        rig.Power.BatteryPercent = 9;
        rig.Timers.Advance(TimeSpan.FromMinutes(15));
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Battery_protection_blocks_a_start_and_ends_a_running_session()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        rig.Power.BatteryPercent = 8;
        manager.Activate(60);
        Assert.False(manager.IsActive);
        Assert.Equal(8, manager.State.BatteryBlockedPercent);
        Assert.Empty(rig.Toasts.Shown);

        rig.Power.OnExternalPower = true;
        rig.Power.Raise();
        Assert.Null(manager.State.BatteryBlockedPercent);
        manager.Activate(60);
        Assert.True(manager.IsActive);

        rig.Power.OnExternalPower = false;
        rig.Timers.Advance(KeepAwakeManager.BatteryCheckInterval);
        Assert.False(manager.IsActive);
        Assert.Single(rig.Toasts.Shown);
    }

    [Fact]
    public void Battery_limit_never_ignores_the_charge()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.BatteryLimitPercent, 0);
        rig.Power.BatteryPercent = 2;
        var manager = rig.Create();
        manager.Activate(15);
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Automation_starts_and_ends_automatic_sessions_but_never_ends_manual_ones()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationPower, true);
        var manager = rig.Create();
        rig.Power.OnExternalPower = true;
        rig.Power.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.True(manager.IsActive);
        Assert.Equal(KeepAwakeTrigger.Automation, manager.State.Trigger);
        Assert.Equal(AutomationCondition.Power, manager.State.ActiveConditions);

        rig.Power.OnExternalPower = false;
        rig.Power.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.False(manager.IsActive);

        manager.Activate(0);
        rig.Power.OnExternalPower = true;
        rig.Power.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        rig.Power.OnExternalPower = false;
        rig.Power.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.True(manager.IsActive);
        Assert.Equal(KeepAwakeTrigger.Manual, manager.State.Trigger);
    }

    [Fact]
    public void Power_condition_never_matches_on_a_desktop()
    {
        var rig = new KeepAwakeRig();
        rig.Power.HasBattery = false;
        rig.Power.OnExternalPower = true;
        rig.Settings.Set(KeepAwakeSettings.AutomationPower, true);
        var manager = rig.Create();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Manual_stop_suppresses_automation_until_its_conditions_clear()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationExternalDisplay, true);
        rig.Displays.External = true;
        var manager = rig.Create();
        Assert.True(manager.IsActive);
        manager.Toggle();
        rig.Displays.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.False(manager.IsActive);

        rig.Displays.External = false;
        rig.Displays.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        rig.Displays.External = true;
        rig.Displays.Raise();
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Changing_automation_preferences_clears_the_suppression()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.AutomationExternalDisplay, true);
        rig.Displays.External = true;
        var manager = rig.Create();
        manager.Toggle();
        Assert.False(manager.IsActive);
        rig.Settings.Set(KeepAwakeSettings.AutomationRequireAll, true);
        rig.Timers.Advance(TimeSpan.FromSeconds(1));
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Apps_condition_polls_only_while_enabled_with_apps()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        rig.Settings.Set(KeepAwakeSettings.AutomationApps, true);
        rig.Apps.Running.Add("zoom.exe");
        rig.Timers.Advance(TimeSpan.FromSeconds(5));
        Assert.False(manager.IsActive);
        rig.Settings.Set(KeepAwakeSettings.AutomationAppList, ["Zoom.exe"]);
        rig.Timers.Advance(TimeSpan.FromSeconds(5));
        Assert.True(manager.IsActive);
        Assert.Equal(AutomationCondition.Apps, manager.State.ActiveConditions);
        rig.Apps.Running.Clear();
        rig.Timers.Advance(TimeSpan.FromSeconds(5));
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Any_and_all_rules()
    {
        var both = AutomationCondition.ExternalDisplay | AutomationCondition.Power;
        Assert.True(KeepAwakeManager.IsSatisfied(both, AutomationCondition.Power, requireAll: false));
        Assert.False(KeepAwakeManager.IsSatisfied(both, AutomationCondition.Power, requireAll: true));
        Assert.True(KeepAwakeManager.IsSatisfied(both, both | AutomationCondition.Apps, requireAll: true));
        Assert.False(KeepAwakeManager.IsSatisfied(AutomationCondition.None, both, requireAll: false));
    }

    [Fact]
    public void Pause_while_locked_releases_and_resumes_the_session()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.PauseWhenLocked, true);
        var manager = rig.Create();
        manager.Activate(60);
        rig.Lock.Set(true);
        Assert.True(manager.State.IsPaused);
        Assert.False(rig.Requests.System);
        rig.Lock.Set(false);
        Assert.False(manager.State.IsPaused);
        Assert.True(rig.Requests.System);
    }

    [Fact]
    public void A_session_that_ended_while_locked_ends_on_unlock()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.PauseWhenLocked, true);
        var manager = rig.Create();
        manager.Activate(15);
        rig.Lock.Set(true);
        rig.Timers.Advance(TimeSpan.FromMinutes(20));
        Assert.True(manager.IsActive);
        rig.Lock.Set(false);
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Allow_display_sleep_applies_live()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(0);
        Assert.True(rig.Requests.Display);
        rig.Settings.Set(KeepAwakeSettings.AllowDisplaySleep, true);
        Assert.True(rig.Requests.System);
        Assert.False(rig.Requests.Display);
    }

    [Fact]
    public void Pointer_jiggle_runs_at_its_interval_while_active()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.MouseJiggle, true);
        rig.Settings.Set(KeepAwakeSettings.MouseJiggleIntervalMinutes, 2);
        var manager = rig.Create();
        manager.Activate(0);
        rig.Timers.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(3, rig.Jiggler.Nudges);
        manager.Toggle();
        rig.Timers.Advance(TimeSpan.FromMinutes(7));
        Assert.Equal(3, rig.Jiggler.Nudges);
    }

    [Fact]
    public void Lid_mode_journals_before_writing_and_restores_at_the_end()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidModePreferred, true);
        var manager = rig.Create();
        Assert.Equal(LidModeStatus.Ready, manager.State.LidMode);
        manager.Activate(0);
        Assert.Equal(["write 0/0"], rig.Lid.Log);
        Assert.NotNull(rig.Settings.Get(KeepAwakeSettings.LidRecoveryMarker));
        Assert.Equal(LidModeStatus.Active, manager.State.LidMode);
        manager.Toggle();
        Assert.Equal(1u, rig.Lid.Ac);
        Assert.Null(rig.Settings.Get(KeepAwakeSettings.LidRecoveryMarker));
        Assert.Equal(LidModeStatus.Ready, manager.State.LidMode);
    }

    [Fact]
    public void Lid_restore_keeps_a_value_the_user_changed_meanwhile()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidModePreferred, true);
        var manager = rig.Create();
        manager.Activate(0);
        rig.Lid.Dc = 2; // The user picked "Hibernate" on battery while the session ran.
        manager.Toggle();
        Assert.Equal(1u, rig.Lid.Ac);
        Assert.Equal(2u, rig.Lid.Dc);
    }

    [Fact]
    public void A_crash_is_undone_at_launch_before_auto_start()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidRecoveryMarker, new LidRecovery { Scheme = rig.Lid.Scheme, OriginalAc = 1, OriginalDc = 3 });
        rig.Lid.Ac = 0;
        rig.Lid.Dc = 0;
        rig.Settings.Set(KeepAwakeSettings.AutoStart, true);
        var manager = rig.Create();
        Assert.Equal("write 1/3", rig.Lid.Log[0]);
        Assert.Null(rig.Settings.Get(KeepAwakeSettings.LidRecoveryMarker));
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Restoring_with_the_lid_closed_requests_sleep_with_bounded_retries()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidModePreferred, true);
        var manager = rig.Create();
        manager.Activate(0);
        rig.Lid.IsLidClosed = true;
        manager.Toggle();
        rig.Timers.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(KeepAwakeManager.SleepRetries, rig.Lid.SleepRequests);
    }

    [Fact]
    public void A_refused_lid_change_turns_the_preference_off()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidModePreferred, true);
        rig.Lid.FailWrites = true;
        var manager = rig.Create();
        manager.Activate(0);
        Assert.False(rig.Settings.Get(KeepAwakeSettings.LidModePreferred));
        Assert.Null(rig.Settings.Get(KeepAwakeSettings.LidRecoveryMarker));
        Assert.Equal(LidModeStatus.Failed, manager.State.LidMode);
        Assert.True(manager.IsActive);
    }

    [Fact]
    public void Quitting_restores_everything_synchronously()
    {
        var rig = new KeepAwakeRig();
        rig.Settings.Set(KeepAwakeSettings.LidModePreferred, true);
        var manager = rig.Create();
        manager.Activate(0);
        rig.Lid.IsLidClosed = true;
        rig.Lid.SleepSucceeds = true;
        manager.Shutdown();
        Assert.False(rig.Requests.System);
        Assert.Equal(1u, rig.Lid.Ac);
        Assert.Equal(1, rig.Lid.SleepRequests);
    }

    [Fact]
    public void Uninstalling_ends_the_session_without_a_notification()
    {
        var rig = new KeepAwakeRig();
        var manager = rig.Create();
        manager.Activate(15);
        manager.Sync(false);
        Assert.False(manager.IsActive);
        Assert.Empty(rig.Toasts.Shown);
        manager.Activate(15);
        Assert.False(manager.IsActive);
    }

    [Fact]
    public void Until_resolves_to_the_next_occurrence()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 17, 30, 0, TimeSpan.Zero), KeepAwakeManager.ResolveUntil(new TimeOnly(17, 30), now, zone));
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero), KeepAwakeManager.ResolveUntil(new TimeOnly(8, 0), now, zone));
    }

    [Theory]
    [InlineData(3900, "1 h 05 min")]
    [InlineData(249, "4 min 09 s")]
    [InlineData(12, "12 s")]
    public void Remaining_format(int seconds, string expected) =>
        Assert.Equal(expected, KeepAwakeFormat.Remaining(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(3492, "58:12")]
    [InlineData(3912, "1:05:12")]
    public void Chip_countdown_format(int seconds, string expected) =>
        Assert.Equal(expected, KeepAwakeFormat.Chip(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Compact_countdown_format()
    {
        Assert.Equal("1:05", KeepAwakeFormat.Compact(TimeSpan.FromMinutes(65.9)));
        Assert.Equal("12 min", KeepAwakeFormat.Compact(TimeSpan.FromMinutes(12.4)));
        Assert.Equal("1 min", KeepAwakeFormat.Compact(TimeSpan.FromSeconds(20)));
        Assert.Equal("∞", KeepAwakeFormat.Compact(null));
    }
}
