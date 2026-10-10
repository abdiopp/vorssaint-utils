// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.Core.Displays;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Displays;

/// <summary>
/// One display: icon, name and "NN%", the brightness slider, the dimming
/// choice ("Dim the picture" / "Extra dimming") and a red failure line.
/// Updates in place so a slider being dragged is never rebuilt.
/// </summary>
internal sealed class DisplayRow : UserControl
{
    private readonly BrightnessService _service;
    private readonly ISettingsStore _settings;
    private readonly bool _wide;
    private readonly SymbolIcon _icon = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name;
    private readonly TextBlock _value;
    private readonly TextBlock _route;
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, Margin = new Thickness(0, -6, 0, -6) };
    private readonly ToggleButton _forced;
    private readonly ToggleButton _extended;
    private readonly TextBlock _choiceCaption;
    private readonly TextBlock _error;
    private bool _updating;

    public DisplayRow(BrightnessService service, ISettingsStore settings, DisplayStatus status, int index, bool wide)
    {
        _service = service;
        _settings = settings;
        _wide = wide;
        Id = status.Id;
        _name = MonitorUi.Text(null, wide ? 14 : 12.5, FontWeight.SemiBold);
        _value = MonitorUi.Value(null, wide ? 13 : 12);
        _value.MinWidth = 38;
        _value.TextAlignment = TextAlignment.Right;
        _route = MonitorUi.Caption(null);
        _error = MonitorUi.Note(null);
        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("DangerBrush").ToBinding());
        _choiceCaption = MonitorUi.Note(null);
        _forced = Choice(L.Get("brightness.softwareDimming"));
        _extended = Choice(L.Get("brightness.extendedDimming"));
        _forced.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                _service.SetForcedSoftware(Id, _forced.IsChecked == true);
            }
        };
        _extended.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                _service.SetExtendedDimming(Id, _extended.IsChecked == true);
            }
        };
        _slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _value.Text = $"{Math.Round(e.NewValue):0}%";
            _service.SetLevel(Id, e.NewValue / 100, _settings.Get(DisplaySettings.OsdEnabled));
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(_icon);
        var titles = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _route } };
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(_value, 2);
        header.Children.Add(_value);

        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _forced, _extended } };
        Content = new StackPanel
        {
            Spacing = wide ? 6 : 4,
            Children = { header, _slider, choices, _choiceCaption, _error },
        };
        Update(status, index);
    }

    public string Id { get; }

    public static string DisplayName(DisplayStatus status, int index) =>
        !string.IsNullOrWhiteSpace(status.Name) ? status.Name
        : status.IsInternal ? L.Get("win.displays.builtIn")
        : L.Format("win.displays.genericFormat", index + 1);

    public void Update(DisplayStatus status, int index)
    {
        _updating = true;
        try
        {
            var name = DisplayName(status, index);
            _name.Text = name;
            _icon.Symbol = IconConverter.Parse(status.IsInternal ? "Laptop" : "Desktop");
            AutomationProperties.SetName(_slider, name);
            _slider.IsEnabled = status.CanAdjust;
            if (Math.Abs(_slider.Value - (status.Level * 100)) > 0.25)
            {
                _slider.Value = status.Level * 100;
            }

            _value.Text = status.CanAdjust ? $"{status.Percent}%" : L.Get("brightness.displayOff");
            _route.Text = status.Route switch
            {
                BrightnessRoute.System => L.Get("win.displays.routeSystem"),
                BrightnessRoute.Ddc => L.Get("win.displays.routeDdc"),
                BrightnessRoute.Software => L.Get("win.displays.routeSoftware"),
                _ => null,
            };
            _route.IsVisible = _route.Text is not null;
            ToolTip.SetTip(_route, status.Route == BrightnessRoute.Software && !status.ForcedSoftware ? L.Get("win.displays.softwareCaption") : null);

            _forced.IsVisible = status.OffersForcedSoftware;
            _forced.IsChecked = status.ForcedSoftware;
            _extended.IsVisible = status.OffersExtendedDimming;
            _extended.IsChecked = status.ExtendedDimming;
            ToolTip.SetTip(_forced, L.Get("win.displays.dimCaption"));
            ToolTip.SetTip(_extended, L.Get("win.displays.extraDimmingCaption"));
            StyleChoice(_forced);
            StyleChoice(_extended);
            ((Control)_forced.Parent!).IsVisible = _forced.IsVisible || _extended.IsVisible;

            // Settings explain the choice in words; the panel keeps it to a tooltip.
            _choiceCaption.Text = !_wide ? null
                : status.OffersForcedSoftware ? L.Get("win.displays.dimCaption")
                : status.OffersExtendedDimming ? L.Get("win.displays.extraDimmingCaption")
                : status.Route == BrightnessRoute.Software ? L.Get("win.displays.softwareCaption")
                : null;
            _choiceCaption.IsVisible = _choiceCaption.Text is not null;
            _error.Text = status.Error;
            _error.IsVisible = status.Error is not null;
        }
        finally
        {
            _updating = false;
        }
    }

    private static ToggleButton Choice(string title)
    {
        var check = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Checkmark, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var button = new ToggleButton
        {
            Padding = new Thickness(8, 3),
            MinHeight = 22,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { check, new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        AutomationProperties.SetName(button, title);
        button.IsCheckedChanged += (_, _) => check.IsVisible = button.IsChecked == true;
        check.IsVisible = false;
        return button;
    }

    private static void StyleChoice(ToggleButton button)
    {
        if (button.IsChecked == true)
        {
            button.ClearValue(TemplatedControl.BackgroundProperty);
        }
        else
        {
            button.Bind(TemplatedControl.BackgroundProperty, button.GetResourceObservable("ChipBrush").ToBinding());
        }
    }
}
