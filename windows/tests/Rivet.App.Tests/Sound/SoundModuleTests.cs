// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Sound;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Sound;
using Rivet.Platform.Fake.Shell;
using Rivet.Platform.Fake.Sound;
using Xunit;

namespace Rivet.App.Tests.Sound;

/// <summary>The sound module inside the real host: registrations, gating, actions, shortcuts and the tray badge.</summary>
public class SoundModuleTests
{
    private static readonly SettingDefinition[] TouchedSettings =
    [
        FeatureKeys.SoundOutputSwitcherEnabled, SoundSettings.OutputSwitcherDeviceIds, SoundSettings.MicMuteShortcutEnabled,
        SoundSettings.MicMuteActive, SoundSettings.MicMuteMutedDevices, SoundSettings.MicMuteSavedVolumes,
        SoundSettings.MicMuteSavedChannelVolumes, SoundSettings.UniversalOutputDevice, SoundSettings.AppOutputDevices,
    ];

    [AvaloniaFact]
    public void Panel_tab_and_settings_pages_are_registered_with_the_spec_gating()
    {
        var host = TestApp.Host;
        var section = host.Services.GetRequiredService<PanelRegistry>().Sections.Single(s => s.Id == SoundModule.SectionId);
        Assert.Equal([FeatureIds.Mixer, FeatureIds.AudioPriority], section.FeatureIds);
        Assert.Equal(7, section.Order); // spec 05 §3.4.3: after Keep awake (4) and Displays (5), before System (10)
        Assert.Equal("Strings.mixerSection", section.TitleKey);

        var pages = host.Services.GetRequiredService<SettingsPageRegistry>().Pages.Where(p => p.Category == SettingsCategory.Sound).OrderBy(p => p.Order).ToList();
        Assert.Equal([SoundModule.MixerPageId, SoundModule.OutputSwitcherPageId, SoundModule.AudioPriorityPageId, SoundModule.MicMutePageId], pages.Select(p => p.Id));
        Assert.Equal([FeatureIds.Mixer], pages[0].FeatureIds);
        Assert.Equal([FeatureIds.MicMute], pages[3].FeatureIds);
        Assert.All(new[] { SoundModule.MicMuteActionId, SoundModule.OutputSwitcherActionId, SoundModule.OpenMixerActionId },
            id => Assert.NotNull(host.Services.GetRequiredService<ActionRegistry>().Get(id)));
    }

    [AvaloniaFact]
    public void Observation_follows_the_installed_features()
    {
        using var scope = new SoundTestScope();
        var runtime = scope.Get<FeatureRuntime>();
        Assert.True(scope.Fake.ObservesSessions);
        Assert.NotEmpty(scope.Get<MixerService>().Rows);

        runtime.SetAvailable(FeatureIds.Mixer, false);
        Assert.False(scope.Fake.ObservesSessions);
        Assert.True(scope.Fake.ObservesDevices); // the output switcher still needs the devices
        Assert.Empty(scope.Get<MixerService>().Rows);

        runtime.SetAvailable(FeatureIds.SoundOutputSwitcher, false);
        Assert.False(scope.Fake.ObservesDevices);
    }

    [AvaloniaFact]
    public void Shortcut_roles_use_the_spec_defaults_and_drive_the_switcher()
    {
        using var scope = new SoundTestScope();
        Reset(scope); // the shared test host may carry the switcher state of an earlier test
        try
        {
            var shortcuts = scope.Get<ShortcutManager>();
            var switcherRole = shortcuts.Find(SoundModule.OutputSwitcherRoleId)!;
            var micRole = shortcuts.Find(SoundModule.MicMuteRoleId)!;
            var ctrlAltWin = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win;
            Assert.Equal(KeyChord.Of(ctrlAltWin, VirtualKeys.Letter('S')), switcherRole.Default);
            Assert.Equal(KeyChord.Of(ctrlAltWin, VirtualKeys.Letter('M')), micRole.Default);
            Assert.False(ReservedShortcuts.IsReserved(switcherRole.Default));
            Assert.False(ReservedShortcuts.IsReserved(micRole.Default));
            Assert.Equal(ShortcutState.Inactive, shortcuts.GetState(switcherRole));

            var settings = scope.Get<ISettingsStore>();
            settings.Set(FeatureKeys.SoundOutputSwitcherEnabled, true);
            Assert.Equal(ShortcutState.Active, shortcuts.GetState(switcherRole));
            Assert.Equal([FakeAudioPlatform.SpeakersId], settings.Get(SoundSettings.OutputSwitcherDeviceIds)); // seeded

            settings.Set(SoundSettings.OutputSwitcherDeviceIds, [FakeAudioPlatform.SpeakersId, FakeAudioPlatform.HeadphonesId]);
            Assert.True(scope.Get<FakeHotkeyService>().Press(switcherRole.Default));
            Assert.Equal(FakeAudioPlatform.HeadphonesId, scope.Get<AudioDeviceService>().DefaultOutputId);
        }
        finally
        {
            Reset(scope);
        }
    }

    [AvaloniaFact]
    public async Task Mic_mute_action_mutes_every_microphone_and_badges_the_tray()
    {
        using var scope = new SoundTestScope();
        try
        {
            var actions = scope.Get<ActionRegistry>();
            var micMute = scope.Get<MicMuteService>();
            var tray = scope.Get<FakeTrayIcon>();

            Assert.True(await actions.InvokeAsync(SoundModule.MicMuteActionId, ActionSource.Shortcut));
            Assert.True(micMute.IsMuted);
            Assert.True(await scope.Fake.InvokeAsync(a => a.ReadVolume(FakeAudioPlatform.MicrophoneId)!.Muted));
            Assert.True(await scope.Fake.InvokeAsync(a => a.ReadVolume(FakeAudioPlatform.HeadsetMicId)!.Muted));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(L.Get("Strings.micMutedHUD"), tray.Tooltip);

            Assert.True(await actions.InvokeAsync(SoundModule.MicMuteActionId, ActionSource.RadialMenu));
            Assert.False(micMute.IsMuted);
            Assert.False(await scope.Fake.InvokeAsync(a => a.ReadVolume(FakeAudioPlatform.MicrophoneId)!.Muted));
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(L.Get("Strings.micMutedHUD"), tray.Tooltip);
        }
        finally
        {
            Reset(scope);
        }
    }

    private static void Reset(SoundTestScope scope)
    {
        var settings = scope.Get<ISettingsStore>();
        foreach (var setting in TouchedSettings)
        {
            settings.Reset(setting.Key);
        }
    }
}
