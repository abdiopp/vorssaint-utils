// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Modules;
using Rivet.App.Settings;
using Rivet.Core.Diagnostics;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Recording.Views;

/// <summary>
/// Settings → Capture → Screen recording (spec 02 §4, capture side): record
/// button with live elapsed time, the dedicated shortcut, countdown, frame
/// rate, the floating controls, system sound and microphone (with the
/// device), and what happens after recording (editor or save folder).
/// </summary>
public sealed class RecordingSettingsPage : SettingsPage
{
    private readonly IServiceProvider _services;
    private readonly ScreenRecorderController _controller;
    private readonly Button _recordButton;
    private readonly TextBlock _recordCaption = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _folderLabel = new() { Classes = { "caption" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _folderReset;

    public RecordingSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _services = services;
        _controller = services.GetRequiredService<ScreenRecorderController>();
        _recordButton = ActionButton(L.Get("recorder.startButton"), () => _ = _controller.ToggleAsync(), "Record", accent: true);
        _folderReset = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = Symbol.Dismiss, FontSize = 14 } };
        ToolTip.SetTip(_folderReset, L.Get("win.shell.reset"));
        AutomationProperties.SetName(_folderReset, L.Get("win.shell.reset"));
        _folderReset.Click += (_, _) =>
        {
            Settings.Reset(RecorderSettings.SaveFolder.Key);
            RefreshFolder();
        };

        Content = Stack(
            Header("recorder.pageTitle", "recorder.panelCaption"),
            CardText(null, Row("Record", L.Get("recorder.pageTitle"), null, new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { _recordButton, _recordCaption },
            })),
            ShortcutCard(),
            Card(
                "win.recording.recordingSection",
                Choice(RecorderSettings.Countdown, "Timer", "recorder.countdownLabel", null,
                    RecorderSettings.CountdownChoices.Select(s => (s, s == 0 ? L.Get("recorder.countdownOff") : L.Format("recorder.countdownSecondsFormat", s))).ToList()),
                Choice(RecorderSettings.FrameRate, "VideoClip", "recorder.frameRateLabel", "win.recording.frameRateCaption",
                    RecorderSettings.FrameRateChoices.Select(f => (f, L.Format("recorder.frameRateFormat", f))).ToList()),
                Toggle(RecorderSettings.ShowIndicator, "Record", "win.recording.showIndicator", "win.recording.showIndicatorCaption")),
            SoundCard(),
            AfterRecordingCard());

        RefreshFolder();
        RefreshRecordState();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _controller.StateChanged += OnRecorderChanged;
        _controller.Tick += OnRecorderChanged;
        RefreshRecordState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _controller.StateChanged -= OnRecorderChanged;
        _controller.Tick -= OnRecorderChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRecorderChanged(object? sender, EventArgs e) => RefreshRecordState();

    private void RefreshRecordState()
    {
        var recording = _controller.IsRecording;
        var label = recording ? L.Get("recorder.stopButton") : L.Get("recorder.startButton");
        if (_recordButton.Content is StackPanel { Children: [SymbolIcon icon, TextBlock text] })
        {
            icon.Symbol = recording ? Symbol.RecordStop : Symbol.Record;
            text.Text = label;
        }

        AutomationProperties.SetName(_recordButton, label);
        _recordButton.IsEnabled = _controller.State is not (ScreenRecorderState.Finishing or ScreenRecorderState.Preparing);
        _recordCaption.Text = _controller.LiveCaption ?? string.Empty;
        _recordCaption.IsVisible = recording;
    }

    private SettingsCard ShortcutCard()
    {
        var shortcuts = _services.GetRequiredService<ShortcutManager>();
        var role = shortcuts.Find(RecordingModule.ShortcutRoleId);
        var menuToggle = Toggle(RecorderSettings.ShowCaptureMenuOnShortcut, null, "screenshot.showCaptureMenuOnShortcut");
        var enabled = Track(Settings.Bind(RecorderSettings.ShortcutEnabled));
        menuToggle.IsEnabled = enabled.Value;
        enabled.PropertyChanged += (_, _) => menuToggle.IsEnabled = enabled.Value;
        return Card(
            "recorder.shortcutLabel",
            Toggle(RecorderSettings.ShortcutEnabled, "Keyboard", "win.recording.shortcutToggle", "win.recording.shortcutCaption"),
            role is null ? null : ShortcutEditor(shortcuts, role),
            menuToggle);
    }

    /// <summary>
    /// The shared shortcut row, or — while that control cannot be built before
    /// it is attached (it casts an unresolved theme resource; see the module
    /// doc's requests) — the current chord with a link to Keyboard shortcuts.
    /// </summary>
    private Control ShortcutEditor(ShortcutManager shortcuts, ShortcutRole role)
    {
        try
        {
            return new ShortcutRoleRow(role) { Margin = new Thickness(40, 0, 0, 0) };
        }
        catch (InvalidCastException ex)
        {
            Log.Warn("recorder", "The shared shortcut row could not be built; showing the chord read-only.", ex);
            var chord = shortcuts.GetChord(role).ToDisplayString(_services.GetService<IKeyNameProvider>());
            var shell = _services.GetService<IAppShell>();
            return Row(null, L.Get("recorder.pageTitle"), null, new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new Border { Classes = { "pill" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = chord } },
                    ActionButton(L.Get("Strings.shortcutsPageTitle"), () => shell?.OpenSettings(SettingsPageIds.Shortcuts)),
                },
            });
        }
    }

    private SettingsCard SoundCard()
    {
        var audio = _services.GetService<IAudioCaptureBackend>();
        var rows = new List<Control?>
        {
            Toggle(RecorderSettings.SystemAudio, "Speaker2", "recorder.systemAudioToggle", "recorder.systemAudioCaption"),
            Toggle(RecorderSettings.Microphone, "Mic", "recorder.microphoneToggle", "recorder.microphoneCaption"),
        };

        if (audio is not null)
        {
            rows.Add(MicrophoneDeviceRow(audio));
            var shell = _services.GetService<IShellService>();
            var blocked = Row("MicOff", L.Get("win.recording.microphoneBlocked"), L.Get("win.recording.microphoneBlockedCaption"),
                ActionButton(L.Get("win.recording.openPrivacySettings"), () => shell?.OpenSystemSettings("ms-settings:privacy-microphone")));
            blocked.IsVisible = false;
            rows.Add(blocked);
            var device = Settings.Get(RecorderSettings.MicrophoneDevice);
            _ = Task.Run(() => audio.ProbeMicrophone(device)).ContinueWith(
                t => blocked.IsVisible = t.IsCompletedSuccessfully && t.Result == MicrophoneStatus.Denied,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        return Card("win.recording.soundSection", rows.ToArray());
    }

    /// <summary>"Windows default" plus the active capture endpoints, listed off the UI thread.</summary>
    private SettingsRow MicrophoneDeviceRow(IAudioCaptureBackend audio)
    {
        var property = Track(Settings.Bind(RecorderSettings.MicrophoneDevice));
        var combo = new ComboBox { MinWidth = 220, MaxWidth = 340 };
        AutomationProperties.SetName(combo, L.Get("win.recording.microphoneDevice"));
        var options = new List<(string Id, string Label)>();
        var filling = false;

        void Fill(IReadOnlyList<AudioDeviceInfo>? devices)
        {
            filling = true;
            var saved = property.Value;
            options = [(string.Empty, L.Get("win.recording.defaultMicrophone"))];
            options.AddRange((devices ?? []).Select(d => (d.Id, d.Name)));
            if (saved.Length > 0 && options.All(o => o.Id != saved))
            {
                options.Add((saved, devices is null ? "…" : L.Get("win.recording.disconnectedMicrophone")));
            }

            combo.ItemsSource = options.Select(o => o.Label).ToList();
            combo.SelectedIndex = Math.Max(0, options.FindIndex(o => o.Id == saved));
            filling = false;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (!filling && combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Count)
            {
                property.Value = options[combo.SelectedIndex].Id;
            }
        };
        Fill(null);
        _ = Task.Run(audio.ListMicrophones).ContinueWith(
            t => Fill(t.IsCompletedSuccessfully ? t.Result : []),
            TaskScheduler.FromCurrentSynchronizationContext());

        var row = Row(null, L.Get("win.recording.microphoneDevice"), null, combo);
        var microphone = Track(Settings.Bind(RecorderSettings.Microphone));
        row.IsEnabled = microphone.Value;
        microphone.PropertyChanged += (_, _) => row.IsEnabled = microphone.Value;
        return row;
    }

    private SettingsCard AfterRecordingCard()
    {
        var editorAvailable = _services.GetService<IRecordingEditor>() is not null;
        var choose = ActionButton(L.Get("recorder.folderChoose"), () => _ = ChooseFolderAsync());
        var folderRow = Row("FolderOpen", L.Get("recorder.folderLabel"), null, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _folderLabel, _folderReset, choose },
        });
        return Card(
            "win.recording.afterSection",
            Toggle(RecorderSettings.OpenEditor, "Edit", "recorder.openEditorToggle", "recorder.openEditorCaption"),
            editorAvailable ? null : Note(L.Get("win.recording.editorMissing"), "WarningBrush"),
            folderRow,
            Note(L.Get("win.recording.directSaveNote")));
    }

    private void RefreshFolder()
    {
        var delivery = _services.GetRequiredService<RecordingDelivery>();
        var folder = delivery.SaveFolder;
        _folderLabel.Text = RecordingDelivery.FolderDisplayName(folder);
        ToolTip.SetTip(_folderLabel, folder);
        _folderReset.IsVisible = Settings.IsSaved(RecorderSettings.SaveFolder.Key);
    }

    private async Task ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var start = await storage.TryGetFolderFromPathAsync(_services.GetRequiredService<RecordingDelivery>().SaveFolder);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Get("recorder.folderLabel"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            Settings.Set(RecorderSettings.SaveFolder, path);
            RefreshFolder();
        }
    }
}
