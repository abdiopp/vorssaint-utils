// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>
/// Sound: the volume mixer, the output switcher, audio device priority and
/// mute microphone (spec 04 part B). The shared device layer runs while any
/// of them needs it; sessions only while the mixer is installed.
/// </summary>
public sealed class SoundModule : IFeatureModule
{
    public const string SectionId = "mixer";
    public const string OutputSwitcherRoleId = "soundOutputSwitcher";
    public const string MicMuteRoleId = "micMute";
    public const string MicMuteActionId = "micMute.toggle";
    public const string OutputSwitcherActionId = "soundOutputSwitcher.next";
    public const string OpenMixerActionId = "sound.openMixer";

    public const string MixerPageId = "mixer";
    public const string OutputSwitcherPageId = "soundOutputSwitcher";
    public const string AudioPriorityPageId = "audioPriority";
    public const string MicMutePageId = "micMute";

    public string Id => "sound";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new AudioDeviceService(sp.GetRequiredService<IAudioPlatform>(), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton(sp => new MixerService(sp.GetRequiredService<IAudioPlatform>(), sp.GetRequiredService<AudioDeviceService>(), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton(sp => new OutputSwitcherService(sp.GetRequiredService<AudioDeviceService>(), sp.GetRequiredService<ISettingsStore>(), sp.GetService<IHud>()));
        services.AddSingleton(sp => new AudioPriorityService(sp.GetRequiredService<AudioDeviceService>(), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton(sp => new MicMuteService(sp.GetRequiredService<IAudioPlatform>(), sp.GetRequiredService<AudioDeviceService>(), sp.GetRequiredService<ISettingsStore>(), sp.GetService<IHud>()));
        services.AddSingleton(sp => new PreciseVolumeService(sp.GetRequiredService<IInputHooks>(), sp.GetRequiredService<AudioDeviceService>(), sp.GetRequiredService<ISettingsStore>(), sp.GetService<IHud>()));
    }

    public void Initialize(ModuleContext context)
    {
        var devices = context.Get<AudioDeviceService>();
        var mixer = context.Get<MixerService>();
        var switcher = context.Get<OutputSwitcherService>();
        var priority = context.Get<AudioPriorityService>();
        var micMute = context.Get<MicMuteService>();
        var precise = context.Get<PreciseVolumeService>();
        var settings = context.Settings;
        var runtime = context.Features;

        // ── Feature controllers (idempotent; run at launch, on install/uninstall and enable-key changes) ──
        runtime.RegisterController(FeatureIds.Mixer, new DelegateFeatureController(available =>
        {
            devices.SetDemand(FeatureIds.Mixer, available, sessions: available);
            devices.SetMixerActive(available);
            mixer.SetActive(available);
            precise.SetMixerInstalled(available);
        }));
        runtime.RegisterController(FeatureIds.SoundOutputSwitcher, new DelegateFeatureController(available =>
        {
            devices.SetDemand(FeatureIds.SoundOutputSwitcher, available);
            switcher.SetActive(available);
        }));
        runtime.RegisterController(FeatureIds.AudioPriority, new DelegateFeatureController(available =>
        {
            devices.SetDemand(FeatureIds.AudioPriority, available);
            priority.SetActive(available);
        }));
        runtime.RegisterController(FeatureIds.MicMute, new DelegateFeatureController(available =>
        {
            micMute.SetActive(available);
            UpdateTrayBadge(context, micMute);
        }));

        // ── Actions and shortcuts ──
        context.Actions.Register(new AppAction
        {
            Id = MicMuteActionId,
            FeatureId = FeatureIds.MicMute,
            TitleKey = "Strings.micMuteName",
            Icon = "MicOff",
            ClosesPanel = false,
            Keywords = ["microphone", "mic", "mute"],
            IsOn = () => micMute.IsMuted,
            Run = _ => micMute.ToggleAsync(),
        });
        context.Actions.Register(new AppAction
        {
            Id = OutputSwitcherActionId,
            FeatureId = FeatureIds.SoundOutputSwitcher,
            TitleKey = "Strings.soundOutputSwitcherTitle",
            Icon = "SpeakerBluetooth",
            ClosesPanel = false,
            Keywords = ["audio", "output", "speakers", "headphones"],
            Run = async _ => await switcher.NextAsync(),
        });
        var shell = context.Get<IAppShell>();
        context.Actions.Register(new AppAction
        {
            Id = OpenMixerActionId,
            FeatureId = FeatureIds.Mixer,
            TitleKey = "Strings.mixerSection",
            Icon = "Speaker2",
            ClosesPanel = false,
            Keywords = ["volume", "audio", "sound"],
            Run = _ =>
            {
                shell.ShowPanel(SectionId);
                return Task.CompletedTask;
            },
        });

        context.Shortcuts.Register(new ShortcutRole
        {
            Id = OutputSwitcherRoleId,
            FeatureId = FeatureIds.SoundOutputSwitcher,
            TitleKey = "Strings.soundOutputSwitcherTitle",
            Storage = SoundSettings.OutputSwitcherShortcut,
            Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('S')),
            RequiredEnableKeys = [FeatureKeys.SoundOutputSwitcherEnabled],
            ActionId = OutputSwitcherActionId,
        });
        context.Shortcuts.Register(new ShortcutRole
        {
            Id = MicMuteRoleId,
            FeatureId = FeatureIds.MicMute,
            TitleKey = "Strings.micMuteName",
            Storage = SoundSettings.MicMuteShortcut,
            Default = KeyChord.Of(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, VirtualKeys.Letter('M')),
            RequiredEnableKeys = [SoundSettings.MicMuteShortcutEnabled],
            ActionId = MicMuteActionId,
        });

        // ── Tray badge while muted ──
        micMute.Changed += (_, _) => UpdateTrayBadge(context, micMute);
        settings.Observe(() => UpdateTrayBadge(context, micMute), SoundSettings.MicMuteTrayIndicator, SoundSettings.MicMuteActive);

        // ── Panel tab ──
        context.Panel.AddSection(new PanelSectionDescriptor
        {
            Id = SectionId,
            TitleKey = "Strings.mixerSection",
            Icon = "Speaker2",
            FeatureIds = [FeatureIds.Mixer, FeatureIds.AudioPriority],
            Order = 7, // spec 05 §3.4.3: after Keep awake and Displays, before System
            SettingsPageId = MixerPageId,
            CreateView = sp => new MixerSectionView(sp),
        });

        // ── Settings pages ──
        var pages = context.SettingsPages;
        pages.Add(new SettingsPageDescriptor
        {
            Id = MixerPageId,
            TitleKey = "Strings.mixerSection",
            Icon = "Speaker2",
            Category = SettingsCategory.Sound,
            Order = 0,
            FeatureIds = [FeatureIds.Mixer],
            CreateView = sp => new MixerSettingsPage(sp),
            KeywordKeys = ["Strings.mixerSystemOutputTitle", "Strings.mixerInputTitle", "mixer.hideInactiveApps", "Strings.mixerLowerOnHeadphonesDisconnect", "Strings.preciseVolumeRollerEnable"],
            Keywords = ["volume", "audio", "sound", "mixer", "app volume"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = OutputSwitcherPageId,
            TitleKey = "Strings.soundOutputSwitcherTitle",
            Icon = "SpeakerBluetooth",
            Category = SettingsCategory.Sound,
            Order = 1,
            FeatureIds = [FeatureIds.SoundOutputSwitcher],
            CreateView = sp => new OutputSwitcherSettingsPage(sp),
            KeywordKeys = ["Strings.soundOutputSwitcherEnable", "Strings.soundOutputSwitcherDevices"],
            Keywords = ["audio", "output", "speakers", "headphones"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = AudioPriorityPageId,
            TitleKey = "hub.titleAudioPriority",
            Icon = "TextNumberList",
            Category = SettingsCategory.Sound,
            Order = 2,
            FeatureIds = [FeatureIds.AudioPriority],
            CreateView = sp => new AudioPrioritySettingsPage(sp),
            KeywordKeys = ["Strings.audioPriorityOutputList", "Strings.audioPriorityInputList"],
            Keywords = ["audio", "priority", "microphone", "output"],
        });
        pages.Add(new SettingsPageDescriptor
        {
            Id = MicMutePageId,
            TitleKey = "Strings.micMuteName",
            Icon = "MicOff",
            Category = SettingsCategory.Sound,
            Order = 3,
            FeatureIds = [FeatureIds.MicMute],
            CreateView = sp => new MicMuteSettingsPage(sp),
            KeywordKeys = ["Strings.micMuteMenuBarToggle", "Strings.globalHotkeySection"],
            Keywords = ["microphone", "mic", "mute"],
        });
    }

    private static void UpdateTrayBadge(ModuleContext context, MicMuteService micMute)
    {
        var tray = context.Services.GetService<ITrayPresence>();
        if (tray is null)
        {
            return;
        }

        var show = micMute.IsActive && micMute.IsMuted && context.Settings.Get(SoundSettings.MicMuteTrayIndicator);
        tray.SetIndicator(MicMuteRoleId, show ? new TrayIndicator { Badge = true, TooltipLine = L.Get("Strings.micMutedHUD") } : null);
    }
}
