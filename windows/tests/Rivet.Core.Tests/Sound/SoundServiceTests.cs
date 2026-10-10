// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Sound;
using Xunit;

namespace Rivet.Core.Tests.Sound;

internal sealed class RecordingHud : IHud
{
    public List<(string Message, HudStyle Style)> Messages { get; } = [];

    public void Show(string message, HudStyle style = HudStyle.Info, string? icon = null, TimeSpan? duration = null) => Messages.Add((message, style));
}

/// <summary>A sample PC: speakers, headphones and a virtual cable; two microphones.</summary>
internal sealed class SoundRig : IDisposable
{
    public SoundRig(bool observe = true)
    {
        Platform = new TestAudioPlatform();
        Settings = SettingsStore.InMemory();
        Time = new ManualTimeProvider();
        Hud = new RecordingHud();
        Devices = new AudioDeviceService(Platform, Settings, Time);
        Platform.Add(TestAudioPlatform.Output("spk", "Speakers", AudioDeviceTier.BuiltIn), 0.8f, publish: false);
        Platform.Add(TestAudioPlatform.Output("hp", "Headphones", AudioDeviceTier.Hardware, headphones: true), 0.3f, publish: false);
        Platform.Add(TestAudioPlatform.Output("cable", "CABLE Input", AudioDeviceTier.Virtual), 1f, publish: false);
        Platform.Add(TestAudioPlatform.Input("mic", "Microphone Array", AudioDeviceTier.BuiltIn), 0.7f, publish: false);
        Platform.Add(TestAudioPlatform.Input("headset", "Headset Microphone"), 0.6f, publish: false);
        if (observe)
        {
            Devices.SetDemand("test", true);
        }
    }

    public TestAudioPlatform Platform { get; }

    public SettingsStore Settings { get; }

    public ManualTimeProvider Time { get; }

    public RecordingHud Hud { get; }

    public AudioDeviceService Devices { get; }

    public void Dispose() => Devices.Dispose();
}

public class AudioDeviceServiceTests
{
    [Fact]
    public void Demand_starts_observation_and_lists_put_the_default_first()
    {
        using var rig = new SoundRig();
        Assert.True(rig.Platform.ObservesDevices);
        Assert.False(rig.Platform.ObservesSessions);
        Assert.True(rig.Devices.IsLive);
        Assert.Equal(["spk", "cable", "hp"], rig.Devices.Outputs.Select(d => d.Id));
        Assert.Equal("mic", rig.Devices.DefaultInputId);

        rig.Devices.SetDemand("mixer", true, sessions: true);
        Assert.True(rig.Platform.ObservesSessions);
        rig.Devices.SetDemand("mixer", false);
        rig.Devices.SetDemand("test", false);
        Assert.False(rig.Platform.ObservesDevices);
        Assert.False(rig.Devices.IsLive);
        Assert.Empty(rig.Devices.Outputs);
    }

    [Fact]
    public async Task The_all_apps_switch_clears_routes_and_publishes_the_new_default_at_once()
    {
        using var rig = new SoundRig();
        rig.Settings.Set(SoundSettings.AppOutputDevices, new Dictionary<string, string> { ["spotify.exe"] = "cable" });
        var switched = 0;
        rig.Devices.UniversalSwitched += (_, _) => switched++;

        Assert.Equal(AudioSwitchStatus.Success, await rig.Devices.SwitchOutputForAllAppsAsync("hp"));
        Assert.Equal("hp", rig.Devices.DefaultOutputId);
        Assert.Equal(("hp", AudioFlow.Render), rig.Platform.DefaultWrites.Single());
        Assert.Empty(rig.Settings.Get(SoundSettings.AppOutputDevices));
        Assert.Equal("hp", rig.Settings.Get(SoundSettings.UniversalOutputDevice));
        Assert.Equal(1, rig.Platform.ClearRoutesCount);
        Assert.Equal(1, switched);
    }

    [Fact]
    public async Task A_refused_or_impossible_switch_keeps_the_routes()
    {
        using var rig = new SoundRig();
        rig.Settings.Set(SoundSettings.AppOutputDevices, new Dictionary<string, string> { ["spotify.exe"] = "cable" });
        Assert.Equal(AudioSwitchStatus.Unavailable, await rig.Devices.SwitchOutputForAllAppsAsync("gone"));
        rig.Platform.AcceptDefaultChanges = false;
        Assert.Equal(AudioSwitchStatus.Failed, await rig.Devices.SwitchOutputForAllAppsAsync("hp"));
        rig.Platform.Capabilities = rig.Platform.Capabilities with { CanSetDefaultDevice = false };
        Assert.Equal(AudioSwitchStatus.Unsupported, await rig.Devices.SwitchOutputForAllAppsAsync("hp"));
        Assert.Single(rig.Settings.Get(SoundSettings.AppOutputDevices));
        Assert.Equal(0, rig.Platform.ClearRoutesCount);
    }

    [Fact]
    public async Task Output_level_zero_mutes_and_a_positive_level_unmutes()
    {
        using var rig = new SoundRig();
        var speakers = rig.Platform.EndpointOf("spk");
        rig.Devices.SetOutputVolume(0);
        Assert.True(speakers.Muted);
        Assert.Equal(0f, speakers.Volume);
        Assert.Equal(0f, rig.Devices.OutputVolume!.Scalar);

        rig.Devices.SetOutputVolume(0.4);
        Assert.False(speakers.Muted);
        Assert.Equal(0.4f, speakers.Volume, 3);

        Assert.Equal(0.41, (await rig.Devices.StepOutputVolumeAsync(0.01))!.Value, 3);
        Assert.Equal(0.41f, speakers.Volume, 3);
    }

    [Fact]
    public void Headphones_disconnecting_lowers_the_speakers_and_their_return_restores_them()
    {
        using var rig = new SoundRig();
        rig.Settings.Set(SoundSettings.LowerVolumeOnHeadphonesDisconnect, true);
        rig.Devices.SetMixerActive(true);
        var speakers = rig.Platform.EndpointOf("spk");
        rig.Platform.SetDefault("hp", AudioFlow.Render);

        rig.Platform.Remove("hp", fallback: "spk");
        Assert.Equal(0.25f, speakers.Volume, 3);
        Assert.Equal(new HeadphoneGuardRestore("spk", 0.8, 0.25), Rounded(rig.Settings.Get(SoundSettings.HeadphoneGuardPending)));

        rig.Platform.Add(TestAudioPlatform.Output("hp", "Headphones", headphones: true), 0.3f, makeDefault: true);
        Assert.Equal(0.8f, speakers.Volume, 3);
        Assert.Null(rig.Settings.Get(SoundSettings.HeadphoneGuardPending));
    }

    [Fact]
    public void The_guard_gives_the_level_back_on_quit_while_the_speaker_still_holds_it()
    {
        var rig = new SoundRig();
        rig.Settings.Set(SoundSettings.LowerVolumeOnHeadphonesDisconnect, true);
        rig.Devices.SetMixerActive(true);
        rig.Platform.SetDefault("hp", AudioFlow.Render);
        rig.Platform.Remove("hp", fallback: "spk");
        Assert.Equal(0.25f, rig.Platform.EndpointOf("spk").Volume, 3);

        rig.Dispose();
        Assert.Equal(0.8f, rig.Platform.EndpointOf("spk").Volume, 3);
        Assert.Null(rig.Settings.Get(SoundSettings.HeadphoneGuardPending));
    }

    [Fact]
    public async Task The_preferred_microphone_is_applied_again_and_the_original_comes_back()
    {
        using var rig = new SoundRig();
        rig.Devices.SetMixerActive(true);
        Assert.Equal(AudioSwitchStatus.Success, await rig.Devices.ChooseInputAsync("headset"));
        Assert.Equal("headset", rig.Devices.DefaultInputId);
        Assert.Equal("headset", rig.Settings.Get(SoundSettings.PreferredInputDevice));
        Assert.Equal("mic", rig.Settings.Get(SoundSettings.PreferredInputOriginalDevice));

        rig.Platform.SetDefault("mic", AudioFlow.Capture); // Windows switched away
        Assert.Equal("headset", rig.Devices.DefaultInputId);

        await rig.Devices.ChooseInputAsync(null); // "Default": no preference
        Assert.Equal("mic", rig.Devices.DefaultInputId);
        Assert.Equal(string.Empty, rig.Settings.Get(SoundSettings.PreferredInputDevice));
    }

    [Fact]
    public async Task Audio_priority_owning_the_input_keeps_the_preference_dormant()
    {
        using var rig = new SoundRig();
        rig.Devices.SetMixerActive(true);
        await rig.Devices.ChooseInputAsync("headset");
        rig.Devices.PriorityOwnsInput = true;
        rig.Platform.SetDefault("mic", AudioFlow.Capture);
        Assert.Equal("mic", rig.Devices.DefaultInputId);

        Assert.Equal(AudioSwitchStatus.Success, await rig.Devices.ChooseInputAsync("headset"));
        Assert.Equal("headset", rig.Devices.DefaultInputId);
    }

    private static HeadphoneGuardRestore? Rounded(HeadphoneGuardRestore? record) =>
        record is null ? null : record with { PreviousLevel = Math.Round(record.PreviousLevel, 3), AppliedLevel = Math.Round(record.AppliedLevel, 3) };
}

public class MixerServiceTests
{
    private static (SoundRig Rig, MixerService Mixer) Start(Action<TestAudioPlatform>? sessions = null)
    {
        var rig = new SoundRig();
        rig.Devices.SetDemand("mixer", true, sessions: true);
        sessions?.Invoke(rig.Platform);
        var mixer = new MixerService(rig.Platform, rig.Devices, rig.Settings, rig.Time);
        mixer.SetActive(true);
        return (rig, mixer);
    }

    [Fact]
    public void A_saved_volume_is_applied_when_the_app_starts_playing()
    {
        var (rig, mixer) = Start();
        using var _ = rig;
        rig.Settings.Set(SoundSettings.AppVolumes, new Dictionary<string, double> { ["spotify.exe"] = 0.3 });

        rig.Platform.AddSession("s1", 100, "spotify.exe", "Spotify", volume: 1f);
        Assert.Equal(0.3f, rig.Platform.SessionOf("s1").Volume, 3);
        Assert.Equal(0.3, mixer.Rows.Single(r => r.RowId == "spotify.exe").Volume, 3);

        rig.Platform.AddSession("s2", 101, "spotify.exe", "Spotify", volume: 1f, device: "hp");
        Assert.Equal(0.3f, rig.Platform.SessionOf("s2").Volume, 3);
    }

    [Fact]
    public void Hidden_apps_are_never_touched()
    {
        var (rig, mixer) = Start();
        using var _ = rig;
        rig.Settings.Set(SoundSettings.AppVolumes, new Dictionary<string, double> { ["game.exe"] = 0.2 });
        rig.Settings.Set(SoundSettings.HiddenApps, new Dictionary<string, string> { ["game.exe"] = "Game" });
        rig.Platform.AddSession("g", 7, "game.exe", "Game");
        Assert.Equal(1f, rig.Platform.SessionOf("g").Volume);
        Assert.DoesNotContain(mixer.Rows, r => r.RowId == "game.exe");
        Assert.Empty(rig.Platform.SessionVolumeWrites);
    }

    [Fact]
    public void Setting_a_volume_writes_every_session_and_remembers_it()
    {
        var (rig, mixer) = Start(p =>
        {
            p.AddSession("e1", 10, "edge.exe", "Edge");
            p.AddSession("e2", 11, "edge.exe", "Edge");
        });
        using var _ = rig;

        mixer.SetVolume("edge.exe", 0.55);
        Assert.Equal(0.55f, rig.Platform.SessionOf("e1").Volume, 3);
        Assert.Equal(0.55f, rig.Platform.SessionOf("e2").Volume, 3);
        Assert.Equal(0.55, rig.Settings.Get(SoundSettings.AppVolumes)["edge.exe"], 3);

        mixer.SetVolume("edge.exe", 1.0);
        Assert.False(rig.Settings.Get(SoundSettings.AppVolumes).ContainsKey("edge.exe"));
    }

    [Fact]
    public void Changes_made_in_windows_own_mixer_are_followed_and_remembered()
    {
        var (rig, mixer) = Start(p =>
        {
            p.AddSession("a1", 10, "app.exe", "App");
            p.AddSession("a2", 11, "app.exe", "App");
        });
        using var _ = rig;
        rig.Time.Advance(TimeSpan.FromSeconds(5));

        rig.Platform.ChangeExternally("a1", 0.4f);
        Assert.Equal(0.4, mixer.Rows.Single(r => r.RowId == "app.exe").Volume, 3);
        Assert.Equal(0.4, rig.Settings.Get(SoundSettings.AppVolumes)["app.exe"], 3);
        Assert.Equal(0.4f, rig.Platform.SessionOf("a2").Volume, 3); // the app's other session follows

        rig.Platform.ChangeExternally("a1", 0.4f, muted: true);
        Assert.True(rig.Platform.SessionOf("a2").Muted);
    }

    [Fact]
    public void An_app_resetting_its_own_level_right_after_starting_gets_the_saved_one_back()
    {
        var (rig, _) = Start();
        using var __ = rig;
        rig.Settings.Set(SoundSettings.AppVolumes, new Dictionary<string, double> { ["game.exe"] = 0.3 });
        rig.Platform.AddSession("g", 7, "game.exe", "Game");
        rig.Platform.ChangeExternally("g", 1f); // the game restores 100 % on start
        Assert.Equal(0.3f, rig.Platform.SessionOf("g").Volume, 3);
        Assert.Equal(0.3, rig.Settings.Get(SoundSettings.AppVolumes)["game.exe"], 3);
    }

    [Fact]
    public void Mute_uses_windows_per_app_mute_and_unmuting_from_zero_restores_the_last_level()
    {
        var (rig, mixer) = Start(p => p.AddSession("a", 10, "app.exe", "App", volume: 0.6f));
        using var _ = rig;

        mixer.ToggleMute("app.exe");
        Assert.True(rig.Platform.SessionOf("a").Muted);
        Assert.Equal(0.6f, rig.Platform.SessionOf("a").Volume, 3); // the slider keeps its place
        Assert.True(mixer.Rows.Single(r => r.RowId == "app.exe").IsSilent);

        mixer.ToggleMute("app.exe");
        Assert.False(rig.Platform.SessionOf("a").Muted);

        mixer.SetVolume("app.exe", 0.7);
        mixer.SetVolume("app.exe", 0);
        mixer.ToggleMute("app.exe");
        Assert.Equal(0.7f, rig.Platform.SessionOf("a").Volume, 3);

        mixer.ToggleMute("app.exe");
        mixer.ResetVolume("app.exe");
        Assert.False(rig.Platform.SessionOf("a").Muted);
        Assert.Equal(1f, rig.Platform.SessionOf("a").Volume, 3);
    }

    [Fact]
    public async Task Per_app_outputs_are_remembered_routed_and_cleared_by_the_all_apps_switch()
    {
        var (rig, mixer) = Start(p => p.AddSession("a", 10, "app.exe", "App"));
        using var _ = rig;

        Assert.True(await mixer.SetOutputAsync("app.exe", "hp"));
        Assert.Equal("hp", rig.Platform.Routes[10]);
        Assert.Equal("hp", rig.Settings.Get(SoundSettings.AppOutputDevices)["app.exe"]);
        Assert.Equal("hp", mixer.Rows.Single(r => r.RowId == "app.exe").OutputDeviceId);

        rig.Platform.AddSession("a2", 12, "app.exe", "App");
        Assert.Equal("hp", rig.Platform.Routes[12]); // a new process of the app is routed too

        await rig.Devices.SwitchOutputForAllAppsAsync("cable");
        Assert.Null(mixer.Rows.Single(r => r.RowId == "app.exe").OutputDeviceId);
        Assert.Empty(rig.Platform.Routes);
    }

    [Fact]
    public async Task A_route_chosen_in_windows_settings_shows_until_the_user_picks_default()
    {
        var (rig, mixer) = Start(p => p.AddSession("a", 10, "app.exe", "App"));
        using var _ = rig;
        rig.Platform.SetWindowsRoute("a", "hp");
        Assert.Equal("hp", mixer.Rows.Single(r => r.RowId == "app.exe").OutputDeviceId);

        await mixer.SetOutputAsync("app.exe", null);
        Assert.Null(mixer.Rows.Single(r => r.RowId == "app.exe").OutputDeviceId);
        Assert.Null(rig.Platform.Routes[10]);
    }

    [Fact]
    public void Pins_order_and_hiding_go_through_the_arrangement()
    {
        var (rig, mixer) = Start(p =>
        {
            p.AddSession("a", 1, "a.exe", "Alpha");
            p.AddSession("b", 2, "b.exe", "Beta");
            p.AddSession("c", 3, "c.exe", "Gamma");
        });
        using var _ = rig;
        rig.Settings.Set(SoundSettings.ShowSystemSounds, false);
        Assert.Equal(["a.exe", "b.exe", "c.exe"], mixer.Rows.Select(r => r.RowId));

        mixer.Pin("c.exe");
        Assert.Equal(["c.exe", "a.exe", "b.exe"], mixer.Rows.Select(r => r.RowId));
        Assert.False(mixer.CanMoveBy("c.exe", 1)); // pinned group ends there
        mixer.MoveBy("b.exe", -1);
        Assert.Equal(["c.exe", "b.exe", "a.exe"], mixer.Rows.Select(r => r.RowId));
        mixer.Move("c.exe", "a.exe", after: true); // across groups: refused
        Assert.Equal(["c.exe", "b.exe", "a.exe"], mixer.Rows.Select(r => r.RowId));

        mixer.Hide("b.exe");
        Assert.Equal(["c.exe", "a.exe"], mixer.Rows.Select(r => r.RowId));
        Assert.Equal(2, mixer.HiddenCount); // b.exe and the System sounds row
        mixer.SetShown("b.exe", true);
        Assert.Contains(mixer.Rows, r => r.RowId == "b.exe");
        mixer.SetShown(MixerRow.SystemSoundsId, true);
        Assert.Contains(mixer.Rows, r => r.IsSystemSounds);
    }

    [Fact]
    public void Apps_without_an_identity_use_session_only_volumes()
    {
        var (rig, mixer) = Start(p => p.AddSession("x", 55, null, "pid 55"));
        using var _ = rig;
        mixer.SetVolume("process:55", 0.4);
        Assert.Equal(0.4f, rig.Platform.SessionOf("x").Volume, 3);
        Assert.Empty(rig.Settings.Get(SoundSettings.AppVolumes));
        Assert.Equal(0.4, mixer.Rows.Single(r => r.RowId == "process:55").Volume, 3);
    }

    [Fact]
    public void Inactive_mixer_lists_nothing()
    {
        var (rig, mixer) = Start(p => p.AddSession("a", 1, "a.exe", "A"));
        using var _ = rig;
        mixer.SetActive(false);
        Assert.Empty(mixer.Rows);
    }
}

public class OutputSwitcherServiceTests
{
    [Fact]
    public async Task The_shortcut_cycles_the_selected_outputs_and_names_each_one()
    {
        using var rig = new SoundRig();
        var switcher = new OutputSwitcherService(rig.Devices, rig.Settings, rig.Hud);
        switcher.SetActive(true);
        rig.Settings.Set(SoundSettings.OutputSwitcherDeviceIds, ["spk", "hp"]);

        var result = await switcher.NextAsync();
        Assert.Equal(OutputCycleOutcome.Switch, result.Outcome);
        Assert.Equal("hp", rig.Devices.DefaultOutputId);
        Assert.Equal("Headphones", rig.Hud.Messages.Last().Message);

        await switcher.NextAsync();
        Assert.Equal("spk", rig.Devices.DefaultOutputId);

        rig.Platform.Remove("hp", fallback: "spk");
        Assert.Equal(OutputCycleOutcome.Unchanged, (await switcher.NextAsync()).Outcome);
    }

    [Fact]
    public async Task Nothing_selected_and_available_says_so()
    {
        using var rig = new SoundRig();
        var switcher = new OutputSwitcherService(rig.Devices, rig.Settings, rig.Hud);
        switcher.SetActive(true);
        rig.Settings.Set(SoundSettings.OutputSwitcherDeviceIds, ["gone"]);
        Assert.Equal(OutputCycleOutcome.NoCandidates, (await switcher.NextAsync()).Outcome);
        Assert.Equal(HudStyle.Warning, rig.Hud.Messages.Single().Style);
    }

    [Fact]
    public void Turning_it_on_with_nothing_selected_seeds_the_current_output()
    {
        using var rig = new SoundRig();
        var switcher = new OutputSwitcherService(rig.Devices, rig.Settings, rig.Hud);
        switcher.SetActive(true);
        Assert.Empty(switcher.Selection);
        rig.Settings.Set(FeatureKeys.SoundOutputSwitcherEnabled, true);
        Assert.Equal(["spk"], switcher.Selection);

        switcher.SetSelected("cable", true);
        Assert.Equal(["spk", "cable"], switcher.Selection);
        switcher.SetSelected("spk", false);
        Assert.Equal(["cable"], switcher.Selection);
    }
}

public class AudioPriorityServiceTests
{
    private static (SoundRig Rig, AudioPriorityService Priority) Start(IReadOnlyList<string>? outputs = null)
    {
        var rig = new SoundRig();
        if (outputs is not null)
        {
            rig.Settings.Set(SoundSettings.PriorityOutputIds, outputs);
        }

        var priority = new AudioPriorityService(rig.Devices, rig.Settings, rig.Time);
        priority.SetActive(true);
        return (rig, priority);
    }

    [Fact]
    public void A_new_list_starts_from_the_current_device_then_tiers()
    {
        var (rig, priority) = Start();
        using var _ = rig;
        Assert.Equal(["spk", "hp", "cable"], rig.Settings.Get(SoundSettings.PriorityOutputIds));
        Assert.Equal(["mic", "headset"], rig.Settings.Get(SoundSettings.PriorityInputIds));
        var entries = priority.Entries(AudioFlow.Render);
        Assert.True(entries[0].IsCurrent);
        Assert.Equal(1, entries[0].Rank);
        Assert.True(rig.Devices.PriorityOwnsInput);
    }

    [Fact]
    public void Launch_is_a_baseline_not_a_reason_to_switch()
    {
        var (rig, _) = Start(["hp", "spk", "cable"]);
        using var __ = rig;
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("spk", rig.Devices.DefaultOutputId);
        Assert.Empty(rig.Platform.DefaultWrites);
    }

    [Fact]
    public void A_higher_ranked_device_connecting_becomes_the_default()
    {
        var (rig, _) = Start(["hp", "spk", "cable"]);
        using var __ = rig;
        rig.Platform.Remove("hp", fallback: "spk");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("spk", rig.Devices.DefaultOutputId);

        rig.Platform.Add(TestAudioPlatform.Output("hp", "Headphones", headphones: true));
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("spk", rig.Devices.DefaultOutputId); // debounced
        rig.Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal("hp", rig.Devices.DefaultOutputId);
    }

    [Fact]
    public void When_the_active_device_disconnects_the_next_ranked_one_takes_over()
    {
        var (rig, _) = Start(["hp", "cable", "spk"]);
        using var __ = rig;
        rig.Platform.SetDefault("hp", AudioFlow.Render);
        rig.Platform.Remove("hp", fallback: "spk"); // Windows picked the speakers
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("cable", rig.Devices.DefaultOutputId);
    }

    [Fact]
    public void A_manual_default_change_alone_is_kept()
    {
        var (rig, _) = Start(["spk", "hp", "cable"]);
        using var __ = rig;
        rig.Platform.SetDefault("cable", AudioFlow.Render);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("cable", rig.Devices.DefaultOutputId);
    }

    [Fact]
    public void A_new_device_waits_two_seconds_and_lands_above_the_virtual_ones()
    {
        var (rig, _) = Start(["spk", "hp", "cable"]);
        using var __ = rig;
        rig.Platform.Add(TestAudioPlatform.Output("usb", "USB DAC"));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("usb", rig.Settings.Get(SoundSettings.PriorityOutputIds));
        rig.Time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(["spk", "hp", "usb", "cable"], rig.Settings.Get(SoundSettings.PriorityOutputIds));
        Assert.Equal("spk", rig.Devices.DefaultOutputId);
        Assert.Equal("USB DAC", rig.Settings.Get(SoundSettings.PriorityDeviceNames)["usb"]);
    }

    [Fact]
    public void A_new_device_windows_made_current_is_ranked_first_and_kept()
    {
        var (rig, _) = Start(["spk", "hp", "cable"]);
        using var __ = rig;
        rig.Platform.Add(TestAudioPlatform.Output("usb", "USB Headset"), makeDefault: true);
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("usb", rig.Settings.Get(SoundSettings.PriorityOutputIds)[0]);
        Assert.Equal("usb", rig.Devices.DefaultOutputId);
    }

    [Fact]
    public void Reordering_triggers_enforcement_but_a_switched_off_list_does_not_enforce()
    {
        var (rig, priority) = Start(["spk", "hp", "cable"]);
        using var __ = rig;
        priority.Move(AudioFlow.Render, 1, 0);
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("hp", rig.Devices.DefaultOutputId);

        rig.Settings.Set(FeatureKeys.AudioPriorityOutputEnabled, false);
        priority.Move(AudioFlow.Render, 1, 0);
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("hp", rig.Devices.DefaultOutputId);

        rig.Settings.Set(FeatureKeys.AudioPriorityOutputEnabled, true); // switching it on enforces
        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal("spk", rig.Devices.DefaultOutputId);
    }

    [Fact]
    public void Disconnected_entries_keep_their_place_and_last_known_name()
    {
        var (rig, priority) = Start(["hp", "spk", "cable"]);
        using var __ = rig;
        rig.Platform.Remove("hp", fallback: "spk");
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var hp = priority.Entries(AudioFlow.Render)[0];
        Assert.Equal("hp", hp.Id);
        Assert.False(hp.IsAvailable);
        Assert.Equal("Headphones", hp.Name);
    }
}

public class MicMuteServiceTests
{
    [Fact]
    public async Task Mute_and_unmute_every_microphone_with_feedback()
    {
        using var rig = new SoundRig(observe: false);
        var service = new MicMuteService(rig.Platform, rig.Devices, rig.Settings, rig.Hud);
        service.SetActive(true);

        await service.ToggleAsync();
        Assert.True(service.IsMuted);
        Assert.True(rig.Platform.EndpointOf("mic").Muted);
        Assert.True(rig.Platform.EndpointOf("headset").Muted);
        Assert.Equal(["mic", "headset"], rig.Settings.Get(SoundSettings.MicMuteMutedDevices));
        Assert.True(rig.Platform.ObservesDevices); // listening while muted
        Assert.Single(rig.Hud.Messages);

        await service.ToggleAsync();
        Assert.False(service.IsMuted);
        Assert.False(rig.Platform.EndpointOf("mic").Muted);
        Assert.Empty(rig.Settings.Get(SoundSettings.MicMuteMutedDevices)!);
        Assert.False(rig.Platform.ObservesDevices);
    }

    [Fact]
    public async Task A_microphone_connected_while_muted_is_muted_on_arrival()
    {
        using var rig = new SoundRig(observe: false);
        var service = new MicMuteService(rig.Platform, rig.Devices, rig.Settings, rig.Hud);
        service.SetActive(true);
        await service.MuteAsync();

        var usb = rig.Platform.Add(TestAudioPlatform.Input("usb", "USB Microphone"), 0.9f);
        Assert.True(usb.Muted);
        Assert.Contains("usb", rig.Settings.Get(SoundSettings.MicMuteMutedDevices)!);
    }

    [Fact]
    public async Task The_mute_survives_a_relaunch_and_uninstalling_unmutes_first()
    {
        using var rig = new SoundRig(observe: false);
        var first = new MicMuteService(rig.Platform, rig.Devices, rig.Settings, rig.Hud);
        first.SetActive(true);
        await first.MuteAsync();
        first.Dispose();

        rig.Platform.EndpointOf("mic").Muted = false; // something reopened it while the app was closed
        var second = new MicMuteService(rig.Platform, rig.Devices, rig.Settings, rig.Hud);
        second.SetActive(true);
        Assert.True(second.IsMuted);
        Assert.True(rig.Platform.EndpointOf("mic").Muted);

        second.SetActive(false);
        Assert.False(second.IsMuted);
        Assert.False(rig.Platform.EndpointOf("mic").Muted);
        Assert.False(rig.Platform.EndpointOf("headset").Muted);
    }
}

public class PreciseVolumeServiceTests
{
    [Fact]
    public void Volume_keys_step_finely_unless_a_modifier_is_held()
    {
        using var rig = new SoundRig();
        var hooks = new TestInputHooks();
        var service = new PreciseVolumeService(hooks, rig.Devices, rig.Settings, rig.Hud);
        service.SetMixerInstalled(true);
        Assert.Equal(0, hooks.KeyboardSubscribers);

        rig.Settings.Set(SoundSettings.PreciseVolumeRollerEnabled, true);
        Assert.True(service.IsRunning);
        Assert.True(hooks.Raise(PreciseVolumeService.VolumeUpKey, KeyAction.Down));
        Assert.True(hooks.Raise(PreciseVolumeService.VolumeUpKey, KeyAction.Up));
        Assert.Equal(0.81f, rig.Platform.EndpointOf("spk").Volume, 3);
        Assert.Contains("81", rig.Hud.Messages.Last().Message);

        Assert.False(hooks.Raise(PreciseVolumeService.VolumeDownKey, KeyAction.Down, Core.Shortcuts.KeyModifiers.Control));
        Assert.False(hooks.Raise(PreciseVolumeService.VolumeDownKey, KeyAction.Down)); // its repeat goes to Windows too
        Assert.False(hooks.Raise(PreciseVolumeService.VolumeDownKey, KeyAction.Up));
        Assert.False(hooks.Raise(0x41, KeyAction.Down)); // other keys are never touched

        service.SetMixerInstalled(false);
        Assert.Equal(0, hooks.KeyboardSubscribers);
    }
}
