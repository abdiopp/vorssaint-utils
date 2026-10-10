// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>
/// Settings › Mouse button shortcuts: extra buttons and side-wheel directions
/// mapped to key combinations, with the capture flow, plus the desktop drag.
/// </summary>
public sealed class MouseButtonsSettingsPage : InputSettingsPage
{
    private readonly MouseButtonShortcutsService? _service;
    private readonly IMouseButtonClaims? _claims;
    private readonly StackPanel _mappingRows = new() { Spacing = 2 };
    private readonly TextBlock _captureStatus = new() { Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _capturePanel = new() { Spacing = 6, Margin = new Thickness(40, 4, 0, 0), IsVisible = false };
    private readonly TextBlock _gestureButtonLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _gestureMessage = new() { Classes = { "caption" }, IsVisible = false, Margin = new Thickness(40, 0, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private IDisposable? _capture;
    private int? _pendingButton;

    public MouseButtonsSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetService<MouseButtonShortcutsService>();
        _claims = services.GetService<IMouseButtonClaims>();
        var catalog = services.GetService<IAppCatalog>();

        var add = ActionButton(L.Get("mouseButtons.addButton"), () => StartCapture(ButtonCaptureKind.Shortcut), "Add");
        var cancel = ActionButton(L.Get("mouseButtons.captureCancel"), StopCapture);
        _capturePanel.Children.Add(_captureStatus);
        _capturePanel.Children.Add(new TextBlock { Text = L.Get("mouseButtons.captureHint"), Classes = { "caption", "tertiary" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _capturePanel.Children.Add(cancel);
        var addRow = new StackPanel { Spacing = 6, Margin = new Thickness(40, 4, 0, 0), Children = { add } };

        var pickButton = ActionButton(L.Get("mouseButtons.spacesPickButton"), () => StartCapture(ButtonCaptureKind.DesktopGesture));
        var gestureButtonRow = Row("CursorClick", L.Get("win.input.desktopButtonLabel"), null,
            new StackPanel { Orientation = Orientation.Horizontal, Children = { _gestureButtonLabel, pickButton } });

        Content = Stack(
            Header("mouseButtons.pageTitle", "mouseButtons.hubDescription"),
            LiveCard(null,
                Toggle(FeatureKeys.MouseButtonShortcutsEnabled, "ControlButton", "mouseButtons.enableLabel", "mouseButtons.enableCaption", on =>
                {
                    if (!on)
                    {
                        StopCapture();
                    }
                }),
                ShowWhen(_mappingRows, FeatureKeys.MouseButtonShortcutsEnabled),
                ShowWhen(addRow, FeatureKeys.MouseButtonShortcutsEnabled),
                _capturePanel,
                IndentedNote(L.Get("win.input.extraButtonsHint")),
                new AppExclusionEditor(Settings, InputSettings.MouseButtonExceptions, catalog, "mouseExceptions.captionButtonShortcuts")),
            LiveCard(null,
                Toggle(InputSettings.DesktopGestureEnabled, "DesktopArrowRight", "mouseButtons.spacesEnableLabel", "mouseButtons.spacesEnableCaption", on =>
                {
                    if (!on)
                    {
                        // Turning the switch off clears the bound button.
                        Settings.Set(InputSettings.DesktopGestureButton, 0);
                        StopCapture();
                    }
                }),
                ShowWhen(gestureButtonRow, InputSettings.DesktopGestureEnabled),
                _gestureMessage,
                ShowWhen(Toggle(InputSettings.DesktopGestureFollowsDrag, "ArrowSwap", "mouseButtons.spacesFollowsDragLabel", "mouseButtons.spacesFollowsDragCaption"), InputSettings.DesktopGestureEnabled)),
            Note(L.Get("win.input.elevatedNote")));

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(Rebuild), InputSettings.MouseButtonShortcuts, InputSettings.DesktopGestureButton, FeatureKeys.MouseButtonShortcutsEnabled));
        Track(new Unsubscribe(StopCapture));
        Rebuild();
    }

    private void Rebuild()
    {
        var mappings = MouseButtonMappings.Decode(Settings.Get(InputSettings.MouseButtonShortcuts));
        _mappingRows.Children.Clear();
        var ids = mappings.Keys.ToList();
        if (_pendingButton is { } pending && !mappings.ContainsKey(pending))
        {
            ids.Add(pending);
        }

        if (ids.Count == 0)
        {
            _mappingRows.Children.Add(new TextBlock { Text = L.Get("mouseButtons.emptyCaption"), Classes = { "caption" }, Margin = new Thickness(40, 0, 0, 0) });
        }

        foreach (var id in MouseButtonIds.Sorted(ids))
        {
            _mappingRows.Children.Add(BuildMappingRow(id, mappings.TryGetValue(id, out var chord) ? chord : KeyChord.None));
        }

        var gesture = Settings.Get(InputSettings.DesktopGestureButton);
        _gestureButtonLabel.Text = gesture == 0 ? L.Get("win.shell.shortcutNone") : ButtonName(gesture);
    }

    private Control BuildMappingRow(int id, KeyChord chord)
    {
        var recorder = new ShortcutRecorder { Chord = chord };
        var message = new TextBlock { Classes = { "caption" }, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        recorder.ChordRecorded += (_, e) =>
        {
            if (e.Chord.IsEmpty)
            {
                return;
            }

            if (!MouseButtonMappings.IsBindable(e.Chord))
            {
                message.Text = L.Get("win.shell.shortcutInvalid");
                message.IsVisible = true;
                return;
            }

            _pendingButton = null;
            Settings.Set(InputSettings.MouseButtonShortcuts, MouseButtonMappings.With(Settings.Get(InputSettings.MouseButtonShortcuts), id, e.Chord));
        };
        recorder.RecordingChanged += (_, recording) =>
        {
            message.Text = recording ? L.Get("win.shell.shortcutRecordingCaption") : null;
            message.IsVisible = recording;
        };
        var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Delete, FontSize = 15 } };
        ToolTip.SetTip(remove, L.Get("mouseButtons.removeButton"));
        AutomationProperties.SetName(remove, L.Get("mouseButtons.removeButton"));
        remove.Click += (_, _) =>
        {
            if (_pendingButton == id)
            {
                _pendingButton = null;
            }

            Settings.Set(InputSettings.MouseButtonShortcuts, MouseButtonMappings.With(Settings.Get(InputSettings.MouseButtonShortcuts), id, null));
            Rebuild();
        };

        var description = _claims?.IsClaimed(id) == true ? L.Get("mouseButtons.rowWheelNote")
            : chord.IsEmpty ? L.Get("win.input.pendingShortcutHint")
            : null;
        var row = Row(MouseButtonIds.IsSideWheel(id) ? "ArrowBidirectionalLeftRight" : "ControlButton", ButtonName(id), description,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { recorder, remove } });
        return new StackPanel { Margin = new Thickness(0), Children = { row, message } };
    }

    private void StartCapture(ButtonCaptureKind kind)
    {
        StopCapture();
        _gestureMessage.IsVisible = false;
        var capture = _service?.BeginCapture(kind, id => OnCaptured(kind, id));
        _capturePanel.IsVisible = true;
        if (capture is null)
        {
            _captureStatus.Text = L.Get("mouseButtons.captureBlind");
            return;
        }

        _capture = capture;
        _captureStatus.Text = L.Get(kind == ButtonCaptureKind.Shortcut ? "mouseButtons.captureWaiting" : "mouseButtons.spacesCaptureWaiting");
    }

    private void StopCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _capturePanel.IsVisible = false;
    }

    private void OnCaptured(ButtonCaptureKind kind, int id)
    {
        if (_capture is null)
        {
            return;
        }

        var mappings = MouseButtonMappings.Decode(Settings.Get(InputSettings.MouseButtonShortcuts));
        if (kind == ButtonCaptureKind.Shortcut)
        {
            string? refusal = id == MouseButtonIds.Middle || !MouseButtonIds.IsMappable(id) ? "mouseButtons.captureUnsupported"
                : _claims?.IsClaimed(id) == true ? "mouseButtons.captureWheel"
                : mappings.ContainsKey(id) || _pendingButton == id ? "mouseButtons.captureExists"
                : null;
            if (refusal is not null)
            {
                _captureStatus.Text = L.Get(refusal);
                return;
            }

            StopCapture();
            _pendingButton = id;
            Rebuild();
            return;
        }

        string? gestureRefusal = id is MouseButtonIds.Middle or < MouseButtonIds.FirstExtra ? "mouseButtons.spacesCaptureUnsupported"
            : _claims?.IsClaimed(id) == true ? "mouseButtons.captureWheel"
            : mappings.ContainsKey(id) && Settings.Get(FeatureKeys.MouseButtonShortcutsEnabled) ? "mouseButtons.spacesCaptureExists"
            : null;
        if (gestureRefusal is not null)
        {
            _captureStatus.Text = L.Get(gestureRefusal);
            return;
        }

        StopCapture();
        Settings.Set(InputSettings.DesktopGestureButton, id);
    }

    public static string ButtonName(int id)
    {
        var (key, number) = MouseButtonIds.NameKey(id);
        return number is { } n ? L.Format(key, n) : L.Get(key);
    }
}
