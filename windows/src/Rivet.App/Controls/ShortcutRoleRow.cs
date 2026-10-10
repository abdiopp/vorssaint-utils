// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Controls;

/// <summary>
/// Label + recorder + Reset for one <see cref="ShortcutRole"/>, with the
/// standard checks: invalid combination, reserved by Windows, used by another
/// role, and "Windows or another app owns it" after registration fails.
/// </summary>
public sealed class ShortcutRoleRow : UserControl
{
    private readonly ShortcutRole _role;
    private readonly ShortcutManager _shortcuts;
    private readonly ShortcutRecorder _recorder = new();
    private readonly Button _reset = new() { Classes = { "icon" } };
    private readonly TextBlock _message = new() { Classes = { "caption" }, IsVisible = false };
    private readonly Ellipse _status = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };

    public ShortcutRoleRow(ShortcutRole role, bool showFeatureName = false, string? title = null)
    {
        _role = role;
        _shortcuts = AppHost.Current!.Services.GetRequiredService<ShortcutManager>();

        var titleText = new TextBlock { Text = title ?? L.Get(role.TitleKey), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _status, titleText } };
        if (showFeatureName && FeatureCatalog.Find(role.FeatureId) is { } feature && L.Get(feature.TitleKey) != titleText.Text)
        {
            titleStack.Children.Add(new TextBlock { Text = L.Get(feature.TitleKey), Classes = { "caption", "tertiary" }, VerticalAlignment = VerticalAlignment.Center });
        }

        _reset.Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ArrowCounterclockwise, FontSize = 16 };
        ToolTip.SetTip(_reset, L.Get("win.shell.reset"));
        _reset.Click += (_, _) =>
        {
            _shortcuts.ResetChord(_role);
            ShowMessage(null);
            Refresh();
        };

        _recorder.ChordRecorded += (_, e) => Save(e.Chord);
        _recorder.RecordingChanged += (_, recording) => ShowMessage(recording ? L.Get("win.shell.shortcutRecordingCaption") : null, isError: false);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(titleStack);
        Grid.SetColumn(_recorder, 1);
        grid.Children.Add(_recorder);
        Grid.SetColumn(_reset, 2);
        grid.Children.Add(_reset);

        Content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4), Children = { grid, _message } };
        Refresh();
    }

    // Subscribed only while on screen, so cached pages that are shown again stay live
    // and rows that are never shown do not leak a handler.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _shortcuts.StatesChanged += OnStatesChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _shortcuts.StatesChanged -= OnStatesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStatesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Save(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return;
        }

        if (!chord.IsValidGlobalShortcut)
        {
            ShowMessage(L.Get("win.shell.shortcutInvalid"));
            return;
        }

        if (ReservedShortcuts.IsReserved(chord))
        {
            ShowMessage(L.Get("win.shell.shortcutReserved"));
            return;
        }

        if (_shortcuts.FindConflict(chord, _role, includeInactive: true) is { } other)
        {
            ShowMessage(L.Format("win.shell.shortcutUsedByFormat", L.Get(other.TitleKey)));
            return;
        }

        _shortcuts.SetChord(_role, chord);
        ShowMessage(null);
        Refresh();
    }

    private void Refresh()
    {
        var chord = _shortcuts.GetChord(_role);
        _recorder.Chord = chord;
        _reset.IsEnabled = chord != _role.Default;
        var state = _shortcuts.GetState(_role);
        _status.Fill = state switch
        {
            ShortcutState.Active => Brush("SuccessBrush", Brushes.Green),
            ShortcutState.RegistrationFailed => Brush("WarningBrush", Brushes.Orange),
            _ => Brush("TextTertiaryBrush", Brushes.Gray),
        };
        ToolTip.SetTip(_status, state == ShortcutState.Active ? L.Get("win.shell.shortcutActive") : L.Get("win.shell.shortcutInactive"));
        if (state == ShortcutState.RegistrationFailed && !_message.IsVisible)
        {
            ShowMessage(L.Get("win.shell.shortcutRegistrationFailed"));
        }
    }

    private void ShowMessage(string? text, bool isError = true)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
        _message.Foreground = isError ? Brush("WarningBrush", Brushes.Orange) : Brush("TextSecondaryBrush", Brushes.Gray);
    }

    /// <summary>Resource lookup that works before the row is attached (falls back to the app's resources).</summary>
    private IBrush Brush(string key, IBrush fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var local) && local is IBrush brush)
        {
            return brush;
        }

        if (Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var global) && global is IBrush appBrush)
        {
            return appBrush;
        }

        return fallback;
    }
}
