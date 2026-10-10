// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Small builders shared by the editor's panels.</summary>
internal static class EditorUi
{
    public static SymbolIcon Icon(string name, double size = 15) => new() { Symbol = IconConverter.Parse(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A toolbar button with an icon and/or text, a tooltip and an accessible name.</summary>
    public static Button Button(string? text, string? icon, string? tooltip, Action onClick, params string[] classes)
    {
        object? content = (text, icon) switch
        {
            (null, { } i) => Icon(i),
            ({ } t, null) => new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center },
            ({ } t, { } i) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icon(i, 14), new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center } } },
            _ => null,
        };
        var button = new Button { Content = content, Classes = { "editor" } };
        foreach (var c in classes)
        {
            button.Classes.Add(c);
        }

        if (text is null)
        {
            button.Classes.Add("icon");
        }

        if (tooltip is not null)
        {
            ToolTip.SetTip(button, tooltip);
        }

        AutomationProperties.SetName(button, text ?? tooltip ?? string.Empty);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Section(string text) => new() { Text = text.ToUpper(System.Globalization.CultureInfo.CurrentUICulture), Classes = { "inspectorSection" } };

    public static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption" } };

    public static TextBlock Label(string text, double size = 12, FontWeight? weight = null, string brush = "EditorTextBrush")
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brush).ToBinding());
        return block;
    }

    public static Border Divider(bool vertical = false)
    {
        var border = new Border
        {
            Width = vertical ? 1 : double.NaN,
            Height = vertical ? 18 : 1,
            Margin = vertical ? new Thickness(4, 0) : default,
            VerticalAlignment = VerticalAlignment.Center,
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("EditorHairlineBrush").ToBinding());
        return border;
    }

    /// <summary>Binds a property to a theme resource of the editor.</summary>
    public static T Themed<T>(this T control, AvaloniaProperty property, string resource)
        where T : Control
    {
        control.Bind(property, control.GetResourceObservable(resource).ToBinding());
        return control;
    }

    /// <summary>A switch row: title left, compact toggle right.</summary>
    public static Grid SwitchRow(string title, bool value, Action<bool> changed, out ToggleSwitch toggle)
    {
        var sw = new ToggleSwitch { Classes = { "compact" }, IsChecked = value };
        AutomationProperties.SetName(sw, title);
        sw.IsCheckedChanged += (_, _) => changed(sw.IsChecked == true);
        toggle = sw;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { Label(title, 12.5), sw } };
        Grid.SetColumn(sw, 1);
        return grid;
    }

    /// <summary>A row of mutually exclusive toggle buttons.</summary>
    public static Border Segmented(IReadOnlyList<string> labels, int selected, Action<int> picked, out ToggleButton[] buttons)
    {
        var grid = new Grid { ColumnSpacing = 2 };
        var list = new ToggleButton[labels.Count];
        for (var i = 0; i < labels.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var index = i;
            var button = new ToggleButton
            {
                Content = new TextBlock { Text = labels[i], TextTrimming = TextTrimming.CharacterEllipsis },
                Classes = { "segment" },
                IsChecked = i == selected,
            };
            ToolTip.SetTip(button, labels[i]);
            AutomationProperties.SetName(button, labels[i]);
            button.Click += (_, _) =>
            {
                foreach (var other in list)
                {
                    other.IsChecked = other == button;
                }

                picked(index);
            };
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            list[i] = button;
        }

        buttons = list;
        return new Border
        {
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        }.Themed(Border.BackgroundProperty, "EditorSubpanelBrush");
    }
}

/// <summary>
/// A titled value slider (spec 02 §3.27): title left, current value right,
/// slider below; dragging updates live and commits one undo step on release;
/// a double-click resets to the default.
/// </summary>
internal sealed class ValueSlider : StackPanel
{
    private readonly Slider _slider;
    private readonly TextBlock _value;
    private readonly Func<double, string> _format;
    private readonly Action<double> _live;
    private readonly Action _begin;
    private readonly Action _commit;
    private bool _dragging;
    private bool _updating;

    public ValueSlider(string title, double min, double max, double step, double value, double resetValue,
        Func<double, string> format, Action begin, Action<double> live, Action commit)
    {
        _format = format;
        _live = live;
        _begin = begin;
        _commit = commit;
        Spacing = 2;
        _value = new TextBlock { Classes = { "sliderValue" }, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = title, Classes = { "sliderTitle" } }, _value } };
        Grid.SetColumn(_value, 1);
        _slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            TickFrequency = step,
            IsSnapToTickEnabled = step > 0,
            Value = value,
            Classes = { "inspector" },
        };
        AutomationProperties.SetName(_slider, title);
        Children.Add(header);
        Children.Add(_slider);
        _value.Text = format(value);

        _slider.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!_dragging)
            {
                _dragging = true;
                _begin();
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _slider.AddHandler(PointerReleasedEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _slider.ValueChanged += (_, e) =>
        {
            _value.Text = _format(e.NewValue);
            if (_updating)
            {
                return;
            }

            if (_dragging)
            {
                _live(e.NewValue);
            }
            else
            {
                // Keyboard or a single click: one step.
                _begin();
                _live(e.NewValue);
                _commit();
            }
        };
        _slider.DoubleTapped += (_, _) =>
        {
            _dragging = false;
            _begin();
            _live(resetValue);
            _commit();
            Update(resetValue);
        };
    }

    public void Update(double value)
    {
        if (_dragging)
        {
            return;
        }

        _updating = true;
        _slider.Value = value;
        _value.Text = _format(value);
        _updating = false;
    }

    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            _commit();
        }
    }

    public new bool IsEnabled
    {
        get => _slider.IsEnabled;
        set => _slider.IsEnabled = value;
    }
}
