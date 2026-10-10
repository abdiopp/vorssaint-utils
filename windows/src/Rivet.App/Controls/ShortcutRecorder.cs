// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using CoreModifiers = Rivet.Core.Shortcuts.KeyModifiers;

namespace Rivet.App.Controls;

public sealed class ChordRecordedEventArgs(KeyChord chord) : RoutedEventArgs(ShortcutRecorder.ChordRecordedEvent)
{
    public KeyChord Chord { get; } = chord;
}

/// <summary>
/// A button that records a key combination. While recording, every app
/// hotkey is suspended and (on Windows) a low-level hook swallows the keys,
/// so shell shortcuts like Win+letter are captured instead of acted on.
/// Esc cancels; Backspace/Delete clears when <see cref="AllowClear"/> is set.
/// The owner validates and saves in <see cref="ChordRecorded"/>.
/// </summary>
public sealed class ShortcutRecorder : Button
{
    public static readonly RoutedEvent<ChordRecordedEventArgs> ChordRecordedEvent =
        RoutedEvent.Register<ShortcutRecorder, ChordRecordedEventArgs>(nameof(ChordRecorded), RoutingStrategies.Bubble);

    public static readonly StyledProperty<KeyChord> ChordProperty =
        AvaloniaProperty.Register<ShortcutRecorder, KeyChord>(nameof(Chord));

    public static readonly StyledProperty<bool> AllowClearProperty =
        AvaloniaProperty.Register<ShortcutRecorder, bool>(nameof(AllowClear));

    public static readonly StyledProperty<bool> IsRecordingProperty =
        AvaloniaProperty.Register<ShortcutRecorder, bool>(nameof(IsRecording));

    private readonly TextBlock _label = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private IDisposable? _hook;
    private bool _suspended;

    public ShortcutRecorder()
    {
        Content = _label;
        Classes.Add("recorder");
        MinWidth = 140;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        UpdateLabel();
    }

    public event EventHandler<ChordRecordedEventArgs>? ChordRecorded
    {
        add => AddHandler(ChordRecordedEvent, value);
        remove => RemoveHandler(ChordRecordedEvent, value);
    }

    /// <summary>Raised when recording starts or ends (pages show the recording caption).</summary>
    public event EventHandler<bool>? RecordingChanged;

    public KeyChord Chord
    {
        get => GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public bool AllowClear
    {
        get => GetValue(AllowClearProperty);
        set => SetValue(AllowClearProperty, value);
    }

    public bool IsRecording
    {
        get => GetValue(IsRecordingProperty);
        private set => SetValue(IsRecordingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChordProperty || change.Property == IsRecordingProperty)
        {
            UpdateLabel();
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (IsRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        StopRecording();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopRecording();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnKeyDown(e);
            return;
        }

        // Fallback path when no hook delivered the key first.
        e.Handled = true;
        HandleKey(AvaloniaKeyMap.ToVirtualKey(e.Key), AvaloniaKeyMap.ToModifiers(e.KeyModifiers));
    }

    private void StartRecording()
    {
        var services = AppHost.Current?.Services;
        var shortcuts = services?.GetService<ShortcutManager>();
        if (shortcuts is not null)
        {
            shortcuts.Suspend();
            _suspended = true;
        }

        var hooks = services?.GetService<IInputHooks>();
        _hook = hooks?.SubscribeKeyboard(OnHookKey, priority: 1000);
        IsRecording = true;
        Focus();
        RecordingChanged?.Invoke(this, true);
    }

    private void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        _hook?.Dispose();
        _hook = null;
        if (_suspended)
        {
            _suspended = false;
            AppHost.Current?.Services.GetService<ShortcutManager>()?.Resume();
        }

        IsRecording = false;
        RecordingChanged?.Invoke(this, false);
    }

    private bool OnHookKey(ref KeyboardHookEvent e)
    {
        if (e.Action == KeyAction.Down)
        {
            var vk = e.VirtualKey;
            var modifiers = e.Modifiers;
            Dispatcher.UIThread.Post(() => HandleKey(vk, modifiers));
        }

        // Swallow everything while recording, so nothing else acts on these keys.
        return true;
    }

    private void HandleKey(int virtualKey, CoreModifiers modifiers)
    {
        if (!IsRecording || virtualKey == 0 || VirtualKeys.IsModifier(virtualKey))
        {
            return;
        }

        if (virtualKey == VirtualKeys.Escape && modifiers == CoreModifiers.None)
        {
            StopRecording();
            return;
        }

        if (virtualKey is VirtualKeys.Back or VirtualKeys.Delete && modifiers == CoreModifiers.None)
        {
            if (AllowClear)
            {
                StopRecording();
                RaiseEvent(new ChordRecordedEventArgs(KeyChord.None));
            }

            return;
        }

        var chord = new KeyChord(modifiers, virtualKey);
        StopRecording();
        RaiseEvent(new ChordRecordedEventArgs(chord));
    }

    private void UpdateLabel()
    {
        var names = AppHost.Current?.Services.GetService<IKeyNameProvider>();
        _label.Text = IsRecording
            ? L.Get("win.shell.shortcutPressKeys")
            : Chord.IsEmpty ? L.Get("win.shell.shortcutNone") : Chord.ToDisplayString(names);
    }
}
