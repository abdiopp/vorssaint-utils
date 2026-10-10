// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Features;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Input;

/// <summary>
/// Settings › Super key: the key to hold, the modifier set (diagram of
/// keycaps), the tap action and the apps that pause it.
/// </summary>
public sealed class SuperKeySettingsPage : InputSettingsPage
{
    private static readonly (KeyModifiers Modifier, string Label)[] Keycaps =
    [
        (KeyModifiers.Shift, "Shift"),
        (KeyModifiers.Control, "Ctrl"),
        (KeyModifiers.Alt, "Alt"),
        (KeyModifiers.Win, "Win"),
    ];

    private readonly SuperKeyService? _service;
    private readonly List<(KeyModifiers Modifier, ToggleButton Button)> _keycapButtons = [];
    private readonly TextBlock _sourceKeycap = new() { FontWeight = FontWeight.SemiBold, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40, 0, 0, 0) };
    private readonly TextBlock _officeWarning;
    private readonly TextBlock _altGrNote;

    public SuperKeySettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _service = services.GetService<SuperKeyService>();
        var catalog = services.GetService<IAppCatalog>();
        _officeWarning = IndentedNote(L.Get("win.input.superOfficeKeyWarning"), "WarningBrush");
        _altGrNote = IndentedNote(L.Get("win.input.superAltGrNote"));

        var sources = SuperKeySources.All.Select(s => (s, SourceLabel(s))).ToList();
        var soloOptions = new[]
        {
            (SuperKeySoloActions.None, "superKey.soloNothing"),
            (SuperKeySoloActions.CapsLock, "superKey.soloCapsLock"),
            (SuperKeySoloActions.InputSource, "superKey.soloInputSource"),
            (SuperKeySoloActions.Escape, "superKey.soloEscape"),
        };
        var soloPanel = new StackPanel { Spacing = 6, Margin = new Thickness(40, 0, 0, 0) };
        var solo = Track(Settings.Bind(InputSettings.SuperKeySoloAction));
        foreach (var (value, key) in soloOptions)
        {
            var radio = new RadioButton { Content = L.Get(key), GroupName = "superKeySolo", IsChecked = solo.Value == value };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    solo.Value = value;
                }
            };
            soloPanel.Children.Add(radio);
        }

        Content = Stack(
            Header("superKey.pageTitle", "superKey.hubDescription"),
            LiveCard(null,
                Toggle(FeatureKeys.SuperKeyEnabled, "KeyboardShift", "superKey.enableToggle"),
                _status,
                ChoiceText(InputSettings.SuperKeySource, "Keyboard", "superKey.sourceKey", "superKey.enableCaption", sources),
                BuildDiagram(),
                _officeWarning,
                _altGrNote,
                new AppExclusionEditor(Settings, InputSettings.SuperKeyExceptions, catalog, "mouseExceptions.captionSuperKey")),
            Card("superKey.soloSection",
                IndentedNote(L.Get("superKey.soloCaption")),
                soloPanel),
            Note(L.Get("win.input.superElevatedNote")));

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(Refresh), InputSettings.SuperKeyModifiers, InputSettings.SuperKeySource, FeatureKeys.SuperKeyEnabled));
        if (_service is not null)
        {
            EventHandler handler = (_, _) => Dispatcher.UIThread.Post(Refresh);
            _service.StatusChanged += handler;
            Track(new Unsubscribe(() => _service.StatusChanged -= handler));
        }

        Refresh();
    }

    public static string SourceLabel(string source) => source switch
    {
        SuperKeySources.RightCommand => L.Format("superKey.rightKeyFormat", "Win"),
        SuperKeySources.RightOption => L.Format("superKey.rightKeyFormat", "Alt"),
        SuperKeySources.RightControl => L.Format("superKey.rightKeyFormat", "Ctrl"),
        SuperKeySources.RightShift => L.Format("superKey.rightKeyFormat", "Shift"),
        SuperKeySources.Apps => L.Get("win.input.superMenuKey"),
        _ => L.Get("superKey.capsLockKey"),
    };

    private Control ChoiceText(Setting<string> setting, string icon, string titleKey, string captionKey, IReadOnlyList<(string Value, string Label)> options)
    {
        var property = Track(Settings.Bind(setting));
        var combo = new ComboBox { MinWidth = 180, ItemsSource = options.Select(o => o.Label).ToList() };
        int IndexOf(string value) => options.Select(o => o.Value).ToList().IndexOf(value);
        combo.SelectedIndex = Math.Max(0, IndexOf(property.Value));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                property.Value = options[combo.SelectedIndex].Value;
            }
        };
        property.PropertyChanged += (_, _) => combo.SelectedIndex = Math.Max(0, IndexOf(property.Value));
        AutomationProperties.SetName(combo, L.Get(titleKey));
        return Row(icon, L.Get(titleKey), L.Get(captionKey), combo);
    }

    /// <summary>The source keycap "Hold" → four toggle keycaps.</summary>
    private Control BuildDiagram()
    {
        var source = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 6),
            MinWidth = 96,
            Child = new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    _sourceKeycap,
                    new TextBlock { Text = L.Get("superKey.holdHint"), Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        source.Bind(Border.BorderBrushProperty, source.GetResourceObservable("AccentBrush").ToBinding());
        source.Bind(Border.BackgroundProperty, source.GetResourceObservable("AccentFaintBrush").ToBinding());

        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (modifier, label) in Keycaps)
        {
            var button = new ToggleButton { Content = label, MinWidth = 52, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(button, label);
            button.IsCheckedChanged += (_, _) => OnKeycapChanged(modifier, button.IsChecked == true);
            _keycapButtons.Add((modifier, button));
            caps.Children.Add(button);
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(40, 4, 0, 4),
            Children =
            {
                source,
                new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ArrowRight, FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                caps,
            },
        };
    }

    private void OnKeycapChanged(KeyModifiers modifier, bool on)
    {
        var set = SuperKeyModifierSet.Parse(Settings.Get(InputSettings.SuperKeyModifiers));
        var updated = on ? set | modifier : set & ~modifier;
        if (updated == set)
        {
            return;
        }

        if (!SuperKeyModifierSet.IsValid(updated))
        {
            Refresh();
            return;
        }

        Settings.Set(InputSettings.SuperKeyModifiers, SuperKeyModifierSet.Format(updated));
    }

    private void Refresh()
    {
        var set = SuperKeyModifierSet.Parse(Settings.Get(InputSettings.SuperKeyModifiers));
        foreach (var (modifier, button) in _keycapButtons)
        {
            button.IsChecked = set.HasFlag(modifier);
            // A keycap cannot be switched off if no Ctrl, Alt or Win would be left.
            button.IsEnabled = SuperKeyModifierSet.CanToggle(set, modifier);
        }

        _sourceKeycap.Text = SourceLabel(Settings.Get(InputSettings.SuperKeySource));
        _officeWarning.IsVisible = SuperKeyModifierSet.IsOfficeKey(set);
        _altGrNote.IsVisible = !_officeWarning.IsVisible && SuperKeyModifierSet.IncludesAltGr(set);

        var status = _service?.Status ?? SuperKeyStatus.Off;
        _status.IsVisible = status != SuperKeyStatus.Off;
        if (status == SuperKeyStatus.Working)
        {
            _status.Text = L.Format("superKey.panelCaptionFormat", SourceLabel(Settings.Get(InputSettings.SuperKeySource)), SuperKeyModifierSet.Display(set))
                           + " " + L.Get("superKey.activeNow");
            _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("MetricGreenBrush").ToBinding());
        }
        else if (status == SuperKeyStatus.Paused)
        {
            _status.Text = L.Get("mouseExceptions.pausedSuperKey");
            _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("TextSecondaryBrush").ToBinding());
        }
    }
}
