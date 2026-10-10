// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Settings;
using Rivet.Core.Sound;
using Xunit;

namespace Rivet.Core.Tests.Sound;

public class MixerRowTests
{
    private static AudioSessionInfo Session(string key, int pid, string? id, string name, float volume = 1f, bool active = false, bool muted = false) => new()
    {
        Key = key,
        DeviceId = "out",
        ProcessId = pid,
        IsActive = active,
        Volume = volume,
        Muted = muted,
        App = new AudioAppInfo { PersistenceId = id, GroupId = id ?? $"process:{pid}", DisplayName = name },
    };

    private static AudioSessionInfo SystemSession(float volume = 0.8f) => new()
    {
        Key = "sys",
        DeviceId = "out",
        IsSystemSounds = true,
        Volume = volume,
        App = new AudioAppInfo { GroupId = MixerRow.SystemSoundsId, PersistenceId = MixerRow.SystemSoundsId, DisplayName = "System sounds" },
    };

    [Fact]
    public void Sessions_of_one_app_on_every_endpoint_become_one_row()
    {
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions =
            [
                Session("e1", 10, @"c:\edge\msedge.exe", "Microsoft Edge", volume: 0.4f, active: false),
                Session("e2", 11, @"c:\edge\msedge.exe", "Microsoft Edge", volume: 0.6f, active: true) with { DeviceId = "headphones" },
                Session("s1", 20, @"c:\spotify.exe", "Spotify", muted: true),
            ],
            ShowSystemSounds = false,
        });

        Assert.Equal(["Microsoft Edge", "Spotify"], rows.Select(r => r.DisplayName));
        var edge = rows[0];
        Assert.Equal(["e1", "e2"], edge.SessionKeys);
        Assert.Equal([10, 11], edge.ProcessIds);
        Assert.True(edge.IsPlaying);
        Assert.Equal(0.6, edge.Volume, 5); // the playing session represents the app
        Assert.False(edge.Muted);
        Assert.True(rows[1].Muted);
    }

    [Fact]
    public void Saved_volumes_routes_and_pins_apply_by_persistence_id()
    {
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions = [Session("a", 1, "app", "App", volume: 1f), Session("b", 2, null, "Bare", volume: 1f)],
            SavedVolumes = new Dictionary<string, double> { ["app"] = 0.3 },
            SessionVolumes = new Dictionary<string, double> { ["process:2"] = 0.2 },
            SavedRoutes = new Dictionary<string, string> { ["app"] = "headphones" },
            Arrangement = new MixerArrangement([], ["app"]),
            ShowSystemSounds = false,
        });

        var app = rows.Single(r => r.RowId == "app");
        Assert.Equal(0.3, app.Volume, 5);
        Assert.Equal("headphones", app.OutputDeviceId);
        Assert.True(app.IsPinned);
        var bare = rows.Single(r => r.RowId == "process:2");
        Assert.Null(bare.PersistenceId);
        Assert.Equal(0.2, bare.Volume, 5);
        Assert.False(bare.CanHide);
        Assert.False(bare.CanArrange);
    }

    [Fact]
    public void Own_process_is_never_listed_and_system_sounds_has_a_permanent_row()
    {
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions = [Session("me", 99, "me.exe", "Me"), Session("x", 5, "x.exe", "X")],
            OwnProcessId = 99,
        });

        Assert.DoesNotContain(rows, r => r.RowId == "me.exe");
        var system = rows.Single(r => r.IsSystemSounds);
        Assert.Empty(system.SessionKeys);
        Assert.Equal(1.0, system.Volume);

        var withSession = MixerRowBuilder.Build(new MixerRowInputs { Sessions = [SystemSession()] });
        Assert.Equal(0.8, withSession.Single().Volume, 5);

        var hidden = MixerRowBuilder.Build(new MixerRowInputs { Sessions = [SystemSession()], ShowSystemSounds = false });
        Assert.True(hidden.Single().IsHidden);
        Assert.Empty(MixerRowBuilder.Build(new MixerRowInputs { ShowSystemSounds = false }));
    }

    [Fact]
    public void Rows_sort_by_name_case_insensitively_then_by_id()
    {
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions = [Session("1", 1, "b2", "beta"), Session("2", 2, "a", "Alpha"), Session("3", 3, "b1", "Beta")],
            ShowSystemSounds = false,
            Culture = CultureInfo.GetCultureInfo("en-US"),
        });
        Assert.Equal(["a", "b1", "b2"], rows.Select(r => r.RowId));
    }

    [Fact]
    public void Hidden_apps_and_inactive_rows_leave_the_visible_list()
    {
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions =
            [
                Session("p", 1, "playing", "Playing", active: true),
                Session("i", 2, "idle", "Idle"),
                Session("c", 3, "custom", "Custom", volume: 0.5f),
                Session("m", 4, "muted", "Muted", muted: true),
                Session("r", 5, "routed", "Routed"),
                Session("h", 6, "hidden", "Hidden", active: true),
            ],
            SavedRoutes = new Dictionary<string, string> { ["routed"] = "dev" },
            HiddenApps = new Dictionary<string, string> { ["hidden"] = "Hidden" },
            ShowSystemSounds = false,
        });

        Assert.Equal(["Custom", "Idle", "Muted", "Playing", "Routed"], MixerRowBuilder.Visible(rows, MixerArrangement.Empty, hideInactive: false).Select(r => r.DisplayName));
        Assert.Equal(["Custom", "Muted", "Playing", "Routed"], MixerRowBuilder.Visible(rows, MixerArrangement.Empty, hideInactive: true).Select(r => r.DisplayName));
    }

    [Fact]
    public void Apps_in_the_list_shows_hidden_and_running_apps()
    {
        var hiddenApps = new Dictionary<string, string> { ["closed.exe"] = "Closed app", ["hidden.exe"] = "Hidden" };
        var rows = MixerRowBuilder.Build(new MixerRowInputs
        {
            Sessions = [Session("a", 1, "app.exe", "App"), Session("h", 2, "hidden.exe", "Hidden"), Session("b", 3, null, "Bare")],
            HiddenApps = hiddenApps,
        });

        var entries = MixerRowBuilder.ListEntries(rows, hiddenApps, showSystemSounds: false, "System sounds", CultureInfo.InvariantCulture);
        Assert.Equal(["App", "Bare", "Closed app", "Hidden", "System sounds"], entries.Select(e => e.DisplayName));
        Assert.True(entries.Single(e => e.Id == "app.exe").IsShown);
        Assert.False(entries.Single(e => e.Id == "closed.exe").IsShown);
        Assert.False(entries.Single(e => e.Id == "hidden.exe").IsShown);
        Assert.False(entries.Single(e => e.DisplayName == "Bare").CanToggle);
        var system = entries.Single(e => e.IsSystemSounds);
        Assert.False(system.IsShown);
        Assert.True(system.CanToggle);
    }
}

public class HeadphoneGuardTests
{
    private const double Level = 0.25;

    private static AudioSnapshot Snapshot(string? defaultId, params AudioDevice[] outputs) => new()
    {
        Outputs = outputs,
        DefaultOutputId = defaultId,
        IsLive = true,
    };

    private static readonly AudioDevice Speakers = TestAudioPlatform.Output("spk", "Speakers");
    private static readonly AudioDevice Headphones = TestAudioPlatform.Output("hp", "Headphones", headphones: true);
    private static readonly AudioDevice OtherHeadphones = TestAudioPlatform.Output("hp2", "Earbuds", headphones: true);

    [Fact]
    public void Disconnecting_headphones_lowers_the_new_default_once()
    {
        var guard = new HeadphoneGuard();
        Assert.Equal(HeadphoneGuardStepKind.None, guard.Observe(Snapshot("hp", Headphones, Speakers), true, Level, null).Kind);
        var step = guard.Observe(Snapshot("spk", Speakers), true, Level, null);
        Assert.Equal(HeadphoneGuardStepKind.Lower, step.Kind);
        Assert.Equal("spk", step.DeviceId);
        Assert.Equal(Level, step.Level);

        guard.Lowered("spk");
        Assert.Equal(HeadphoneGuardStepKind.None, guard.Observe(Snapshot("spk", Speakers), true, Level, null).Kind);
    }

    [Fact]
    public void Nothing_happens_when_off_when_the_new_default_is_headphones_or_when_headphones_stay()
    {
        Assert.Equal(HeadphoneGuardStepKind.None, Transition(enabled: false, Snapshot("spk", Speakers)));
        Assert.Equal(HeadphoneGuardStepKind.None, Transition(enabled: true, Snapshot("hp2", OtherHeadphones)));
        // The user picked the speakers while the headphones stay connected: not a disconnect.
        Assert.Equal(HeadphoneGuardStepKind.None, Transition(enabled: true, Snapshot("spk", Headphones, Speakers)));
        Assert.Equal(HeadphoneGuardStepKind.None, new HeadphoneGuard().Observe(AudioSnapshot.Empty, true, Level, null).Kind);

        static HeadphoneGuardStepKind Transition(bool enabled, AudioSnapshot after)
        {
            var guard = new HeadphoneGuard();
            guard.Observe(Snapshot("hp", Headphones, Speakers), enabled, Level, null);
            return guard.Observe(after, enabled, Level, null).Kind;
        }
    }

    [Fact]
    public void Headphones_returning_restore_the_remembered_level_when_the_speaker_is_present()
    {
        var record = new HeadphoneGuardRestore("spk", 0.8, Level);
        var guard = new HeadphoneGuard();
        guard.Observe(Snapshot("spk", Speakers), true, Level, record);
        var step = guard.Observe(Snapshot("hp", Headphones, Speakers), true, Level, record);
        Assert.Equal(HeadphoneGuardStepKind.Restore, step.Kind);
        Assert.Equal("spk", step.DeviceId);

        // Speaker away: keep the record for its return.
        Assert.Equal(HeadphoneGuardStepKind.None, new HeadphoneGuard().Observe(Snapshot("hp", Headphones), true, Level, record).Kind);
        // Restoring happens even when the option was turned off meanwhile.
        Assert.Equal(HeadphoneGuardStepKind.Restore, new HeadphoneGuard().Observe(Snapshot("hp", Headphones, Speakers), false, Level, record).Kind);
    }

    [Fact]
    public void Lowering_never_raises_and_restoring_respects_a_manual_change()
    {
        var platform = new TestAudioPlatform();
        platform.SetObservation(true, false);
        var speaker = platform.Add(Speakers, volume: 0.8f);

        var record = HeadphoneGuard.ApplyLower(platform, "spk", Level);
        Assert.Equal(new HeadphoneGuardRestore("spk", 0.8, Level), record! with { PreviousLevel = Math.Round(record.PreviousLevel, 3) });
        Assert.Equal(0.25f, speaker.Volume, 3);

        speaker.Volume = 0.1f;
        Assert.Null(HeadphoneGuard.ApplyLower(platform, "spk", Level)); // already quiet: never raised
        Assert.Equal(0.1f, speaker.Volume, 3);

        speaker.Volume = 0.25f;
        Assert.True(HeadphoneGuard.ApplyRestore(platform, new HeadphoneGuardRestore("spk", 0.8, Level)));
        Assert.Equal(0.8f, speaker.Volume, 3);

        speaker.Volume = 0.6f; // changed by hand since
        Assert.True(HeadphoneGuard.ApplyRestore(platform, new HeadphoneGuardRestore("spk", 0.8, Level)));
        Assert.Equal(0.6f, speaker.Volume, 3);

        speaker.Unreadable = true;
        Assert.False(HeadphoneGuard.ApplyRestore(platform, new HeadphoneGuardRestore("spk", 0.8, Level)));
    }
}

public class MicMuteEngineTests
{
    private static (TestAudioPlatform Platform, TestAudioPlatform.Endpoint A, TestAudioPlatform.Endpoint B) TwoMics()
    {
        var platform = new TestAudioPlatform();
        platform.SetObservation(true, false);
        var a = platform.Add(TestAudioPlatform.Input("a", "Mic A"), 0.8f);
        var b = platform.Add(TestAudioPlatform.Input("b", "Mic B"), 0.6f);
        return (platform, a, b);
    }

    [Fact]
    public void Mute_claims_every_microphone_it_silenced()
    {
        var (platform, a, b) = TwoMics();
        var sweep = MicMuteEngine.Mute(platform, new MicMuteRecord());
        Assert.True(a.Muted && b.Muted);
        Assert.Equal(["a", "b"], sweep.Record.Claimed);
        Assert.False(sweep.Partial);
        Assert.Equal(2, sweep.Reached);
    }

    [Fact]
    public void Already_silent_microphones_are_left_alone_and_not_claimed()
    {
        var (platform, a, b) = TwoMics();
        b.Muted = true; // the user muted it before
        var sweep = MicMuteEngine.Mute(platform, new MicMuteRecord());
        Assert.Equal(["a"], sweep.Record.Claimed);

        var unmute = MicMuteEngine.Unmute(platform, sweep.Record);
        Assert.False(a.Muted);
        Assert.True(b.Muted); // never claimed, never opened
        Assert.Empty(unmute.Record.Claimed!);
    }

    [Fact]
    public void A_mute_switch_that_ignores_writes_falls_back_to_level_zero_and_back()
    {
        var (platform, a, _) = TwoMics();
        a.MuteBroken = true;
        a.Channels = [0.8f, 0.7f];
        var sweep = MicMuteEngine.Mute(platform, new MicMuteRecord());
        Assert.Contains("a", sweep.Record.Claimed!);
        Assert.Equal(0f, a.Volume);
        Assert.Equal(0.8, sweep.Record.SavedVolumes["a"], 3);
        Assert.Equal(0.7, sweep.Record.SavedChannels["a"]["1"], 3);

        var unmute = MicMuteEngine.Unmute(platform, sweep.Record);
        Assert.Equal(0.8f, a.Volume, 3);
        Assert.Equal(0.7f, a.Channels[1], 3);
        Assert.False(unmute.Record.SavedVolumes.ContainsKey("a"));
    }

    [Fact]
    public void A_microphone_that_cannot_be_silenced_while_recording_is_partial()
    {
        var (platform, a, _) = TwoMics();
        a.MuteBroken = true;
        a.VolumeIgnored = true;
        a.Capturing = true;
        var sweep = MicMuteEngine.Mute(platform, new MicMuteRecord());
        Assert.True(sweep.Partial);
        Assert.DoesNotContain("a", sweep.Record.Claimed!);
        Assert.False(sweep.Record.SavedVolumes.ContainsKey("a"));

        a.Capturing = false;
        Assert.False(MicMuteEngine.Mute(platform, new MicMuteRecord()).Partial); // nobody records from it: not reported
    }

    [Fact]
    public void Claims_of_absent_devices_are_carried_forward_and_released_on_return()
    {
        var (platform, a, b) = TwoMics();
        var muted = MicMuteEngine.Mute(platform, new MicMuteRecord()).Record;
        platform.Remove("b");

        var unmuted = MicMuteEngine.Unmute(platform, muted);
        Assert.False(a.Muted);
        Assert.Equal(["b"], unmuted.Record.Claimed);

        platform.Add(TestAudioPlatform.Input("b", "Mic B"), 0.6f);
        var returning = platform.EndpointOf("b");
        returning.Muted = true; // it kept its hardware state while away
        var released = MicMuteEngine.Unmute(platform, unmuted.Record);
        Assert.False(returning.Muted);
        Assert.Empty(released.Record.Claimed!);
    }

    [Fact]
    public void A_missing_claim_list_restores_every_device_but_an_empty_one_restores_none()
    {
        var (platform, a, b) = TwoMics();
        a.Muted = true;
        b.Volume = 0f;
        MicMuteEngine.Unmute(platform, new MicMuteRecord { Claimed = [] });
        Assert.True(a.Muted);
        Assert.Equal(0f, b.Volume);

        var restored = MicMuteEngine.Unmute(platform, new MicMuteRecord { Claimed = null, LegacySaved = 0.5 });
        Assert.False(a.Muted);
        Assert.Equal(0.5f, b.Volume, 3); // legacy saved level when nothing per device was saved
        Assert.Empty(restored.Record.Claimed!);

        b.Volume = 0f;
        MicMuteEngine.Unmute(platform, new MicMuteRecord { Claimed = ["b"], LegacySaved = 0 });
        Assert.Equal((float)MicMuteEngine.FallbackRestoreLevel, b.Volume, 3);
    }

    [Fact]
    public void An_unreadable_device_keeps_its_claim()
    {
        var (platform, a, _) = TwoMics();
        var muted = MicMuteEngine.Mute(platform, new MicMuteRecord()).Record;
        a.Unreadable = true;
        var unmuted = MicMuteEngine.Unmute(platform, muted);
        Assert.Equal(["a"], unmuted.Record.Claimed);
        Assert.Equal(1, unmuted.Reached);
    }
}

public class SoundSettingsTests
{
    [Fact]
    public void App_volumes_drop_unity_invalid_ids_and_boost()
    {
        var store = SettingsStore.InMemory();
        store.Set(SoundSettings.AppVolumes, new Dictionary<string, double>
        {
            ["spotify.exe"] = 0.42,
            ["full.exe"] = 1.0,
            ["boosted.exe"] = 1.6, // a macOS backup: capped to 100 %, i.e. removed
            [""] = 0.3,
            ["bad\nid"] = 0.3,
            ["nan.exe"] = double.NaN,
        });

        var saved = store.Get(SoundSettings.AppVolumes);
        Assert.Equal(["spotify.exe"], saved.Keys);
        Assert.Equal(0.42, saved["spotify.exe"]);
    }

    [Fact]
    public void Headphone_level_migrates_values_below_ten()
    {
        var store = SettingsStore.InMemory();
        Assert.Equal(25, store.Get(SoundSettings.HeadphonesDisconnectVolumePercent));
        store.Set(SoundSettings.HeadphonesDisconnectVolumePercent, 5);
        Assert.Equal(25, store.Get(SoundSettings.HeadphonesDisconnectVolumePercent));
        store.Set(SoundSettings.HeadphonesDisconnectVolumePercent, 150);
        Assert.Equal(100, store.Get(SoundSettings.HeadphonesDisconnectVolumePercent));
        store.Set(SoundSettings.HeadphonesDisconnectVolumePercent, 40);
        Assert.Equal(40, store.Get(SoundSettings.HeadphonesDisconnectVolumePercent));
        store.Set(SoundSettings.PreciseVolumeRollerStepPercent, 3.0);
        Assert.Equal(1.0, store.Get(SoundSettings.PreciseVolumeRollerStepPercent));
    }

    [Fact]
    public void Muted_devices_keep_absent_and_empty_apart_across_a_file_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), "rivet-sound-" + Guid.NewGuid() + ".json");
        try
        {
            using (var store = SettingsStore.Load(path))
            {
                Assert.Null(store.Get(SoundSettings.MicMuteMutedDevices));
                store.Set(SoundSettings.MicMuteMutedDevices, []);
                store.Set(SoundSettings.AppOutputDevices, new Dictionary<string, string> { ["spotify.exe"] = "{0.0.0.00000000}.{abc}" });
                store.Set(SoundSettings.MicMuteSavedChannelVolumes, new Dictionary<string, IReadOnlyDictionary<string, double>> { ["mic"] = new Dictionary<string, double> { ["0"] = 0.5, ["x"] = 0.3 } });
                store.Set(SoundSettings.HeadphoneGuardPending, new HeadphoneGuardRestore("spk", 0.8, 0.25));
            }

            using (var reloaded = SettingsStore.Load(path))
            {
                Assert.NotNull(reloaded.Get(SoundSettings.MicMuteMutedDevices));
                Assert.Empty(reloaded.Get(SoundSettings.MicMuteMutedDevices)!);
                Assert.Equal("{0.0.0.00000000}.{abc}", reloaded.Get(SoundSettings.AppOutputDevices)["spotify.exe"]);
                Assert.Equal(["0"], reloaded.Get(SoundSettings.MicMuteSavedChannelVolumes)["mic"].Keys);
                Assert.Equal(new HeadphoneGuardRestore("spk", 0.8, 0.25), reloaded.Get(SoundSettings.HeadphoneGuardPending));
                reloaded.Reset(SoundSettings.MicMuteMutedDevices.Key);
                Assert.Null(reloaded.Get(SoundSettings.MicMuteMutedDevices));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Lists_are_sanitized_capped_and_machine_state_is_marked()
    {
        var store = SettingsStore.InMemory();
        store.Set(SoundSettings.PriorityOutputIds, Enumerable.Range(0, 80).Select(i => $"dev{i % 70}").Append(" padded ").ToList());
        var list = store.Get(SoundSettings.PriorityOutputIds);
        Assert.Equal(SoundSettings.MaxPriorityEntries, list.Count);
        Assert.Equal(list.Count, list.Distinct().Count());
        Assert.False(SoundSettings.IsValidId(new string('x', 513)));
        Assert.True(SoundSettings.MicMuteActive.IsMachineState);
        Assert.True(SoundSettings.MicMuteMutedDevices.IsMachineState);
        Assert.False(SoundSettings.AppVolumes.IsMachineState);
        Assert.Equal("appVolumes", SoundSettings.AppVolumes.Key);
        Assert.Equal("micMuteMenuBarIndicator", SoundSettings.MicMuteTrayIndicator.Key);
    }
}
