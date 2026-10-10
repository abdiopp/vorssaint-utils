// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Sound;

namespace Rivet.App.Features.Sound;

/// <summary>Settings › Volume mixer: devices, apps and options.</summary>
public sealed class MixerSettingsPage : SettingsPage
{
    public MixerSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var mixer = services.GetRequiredService<MixerService>();
        var devices = services.GetRequiredService<AudioDeviceService>();
        Content = Stack(
            Header("Strings.mixerSection", "win.sound.mixerPageCaption"),
            CardText(L.Get("win.sound.devicesTitle"), new DevicesBlock(services),
                devices.Capabilities.CanSetDefaultDevice || !devices.IsLive ? null : Note(L.Get("win.sound.switchUnavailableNote"), "WarningBrush")),
            CardText(L.Get("win.sound.appsTitle"),
                new MixerAppsView(services),
                Note(L.Get("win.sound.noBoostNote")),
                mixer.CanRouteApps || !devices.IsLive ? null : Note(L.Get("win.sound.routingUnavailableNote"))),
            Card("Strings.keepAwakeOptions", new MixerOptionsView(services, inPanel: false)));
    }
}

/// <summary>Settings › Output switcher: the switch, the shortcut and the outputs in the cycle.</summary>
public sealed class OutputSwitcherSettingsPage : SettingsPage
{
    public OutputSwitcherSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var devices = services.GetRequiredService<AudioDeviceService>();
        var role = shortcuts.Find(SoundModule.OutputSwitcherRoleId);
        Content = Stack(
            Header("Strings.soundOutputSwitcherTitle", "hub.descSoundOutputSwitcher"),
            Card(null,
                Toggle(Core.Features.FeatureKeys.SoundOutputSwitcherEnabled, "SpeakerBluetooth", "Strings.soundOutputSwitcherEnable", "Strings.soundOutputSwitcherCaption"),
                role is null ? null : new ShortcutRoleRow(role)),
            Card("Strings.soundOutputSwitcherDevices", new OutputCycleView(services, showShortcut: true, showToggle: false)),
            devices.Capabilities.CanSetDefaultDevice || !devices.IsLive ? null : Note(L.Get("win.sound.switchUnavailableNote"), "WarningBrush"));
    }
}

/// <summary>Settings › Audio device priority: the output and microphone lists.</summary>
public sealed class AudioPrioritySettingsPage : SettingsPage
{
    public AudioPrioritySettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        Content = Stack(
            Header("hub.titleAudioPriority", "Strings.audioPriorityCaption"),
            CardText(null, new PriorityListView(services, AudioFlow.Render)),
            CardText(null, new PriorityListView(services, AudioFlow.Capture)));
    }
}

/// <summary>Settings › Mute microphone: the switch itself, the global shortcut and the tray badge.</summary>
public sealed class MicMuteSettingsPage : SettingsPage
{
    private readonly MicMuteService _micMute;
    private readonly TextBlock _status = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _statusIcon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _toggle = new() { Classes = { "accent" }, MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Center };

    public MicMuteSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _micMute = services.GetRequiredService<MicMuteService>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();
        var role = shortcuts.Find(SoundModule.MicMuteRoleId);
        _toggle.Click += async (_, _) => await _micMute.ToggleAsync();

        var statusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        statusRow.Children.Add(_statusIcon);
        Grid.SetColumn(_status, 1);
        statusRow.Children.Add(_status);
        Grid.SetColumn(_toggle, 2);
        statusRow.Children.Add(_toggle);

        Content = Stack(
            Header("Strings.micMuteName", "Strings.micMuteCaption"),
            CardText(null, statusRow),
            Card(null,
                Toggle(SoundSettings.MicMuteShortcutEnabled, "Keyboard", "Strings.quickToolShortcutToggle"),
                role is null ? null : new ShortcutRoleRow(role)),
            Card(null, Toggle(SoundSettings.MicMuteTrayIndicator, "MicOff", "Strings.micMuteMenuBarToggle", "Strings.micMuteMenuBarCaption")));
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _micMute.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _micMute.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var muted = _micMute.IsMuted;
        _status.Text = L.Get(muted ? "Strings.micMutedHUD" : "win.sound.micLive");
        _statusIcon.Content = SoundUi.Icon(muted ? "MicOff" : "Mic", 22, muted ? "WarningBrush" : "AccentBrush");
        _toggle.Content = L.Get(muted ? "Strings.micUnmuteName" : "Strings.micMuteName");
    }
}
