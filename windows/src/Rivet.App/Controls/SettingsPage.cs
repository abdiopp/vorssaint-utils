// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Controls;

/// <summary>
/// Base for Settings pages built in code. Tracks bindings and disposes them
/// when the page leaves the window, and offers small builders so every page
/// looks the same:
/// <code>
/// Content = Stack(
///     Header("screenshot.pageTitle", "screenshot.pageCaption"),
///     Card("screenshot.saveSection",
///         Toggle(MySettings.AutoCopy, "Copy", "screenshot.autoCopy", "screenshot.autoCopyCaption")));
/// </code>
/// </summary>
public abstract class SettingsPage : UserControl
{
    private readonly List<IDisposable> _tracked = [];

    protected SettingsPage(ISettingsStore settings)
    {
        Settings = settings;
    }

    protected ISettingsStore Settings { get; }

    protected T Track<T>(T disposable)
        where T : IDisposable
    {
        _tracked.Add(disposable);
        return disposable;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var item in _tracked)
        {
            item.Dispose();
        }

        _tracked.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected static StackPanel Stack(params Control?[] children)
    {
        var panel = new StackPanel { Spacing = 16 };
        foreach (var child in children.OfType<Control>())
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    protected static PageHeader Header(string titleKey, string? descriptionKey = null) =>
        new() { Title = L.Get(titleKey), Description = descriptionKey is null ? null : L.Get(descriptionKey) };

    /// <summary>A card; null rows are skipped, separators are inserted between rows.</summary>
    protected static SettingsCard Card(string? titleKey, params Control?[] rows) => CardText(titleKey is null ? null : L.Get(titleKey), rows);

    protected static SettingsCard CardText(string? title, params Control?[] rows)
    {
        var stack = new StackPanel { Spacing = 2 };
        var first = true;
        foreach (var row in rows.OfType<Control>())
        {
            if (!first)
            {
                stack.Children.Add(new Border { Classes = { "separator" }, Margin = new Thickness(40, 4, 0, 4) });
            }

            stack.Children.Add(row);
            first = false;
        }

        return new SettingsCard { Header = title, Content = stack };
    }

    protected static SettingsRow Row(string? icon, string title, string? description, Control? trailing = null) =>
        new() { Icon = icon, Title = title, Description = description, Content = trailing };

    /// <summary>A switch row bound two-way to a boolean setting.</summary>
    protected SettingsRow Toggle(Setting<bool> setting, string? icon, string titleKey, string? descriptionKey = null, Action<bool>? changed = null)
    {
        var property = Track(Settings.Bind(setting));
        var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = property.Value };
        toggle.IsCheckedChanged += (_, _) =>
        {
            var value = toggle.IsChecked == true;
            if (property.Value != value)
            {
                property.Value = value;
                changed?.Invoke(value);
            }
        };
        property.PropertyChanged += (_, _) => toggle.IsChecked = property.Value;
        AutomationProperties.SetName(toggle, L.Get(titleKey));
        return Row(icon, L.Get(titleKey), descriptionKey is null ? null : L.Get(descriptionKey), toggle);
    }

    /// <summary>A drop-down row bound to a setting; <paramref name="options"/> are (value, label key).</summary>
    protected SettingsRow Choice<T>(Setting<T> setting, string? icon, string titleKey, string? descriptionKey, IReadOnlyList<(T Value, string Label)> options)
    {
        var property = Track(Settings.Bind(setting));
        var combo = new ComboBox { MinWidth = 180, ItemsSource = options.Select(o => o.Label).ToList() };
        int IndexOf(T value) => options.Select(o => o.Value).ToList().IndexOf(value);
        combo.SelectedIndex = Math.Max(0, IndexOf(property.Value));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < options.Count)
            {
                property.Value = options[combo.SelectedIndex].Value;
            }
        };
        property.PropertyChanged += (_, _) => combo.SelectedIndex = Math.Max(0, IndexOf(property.Value));
        return Row(icon, L.Get(titleKey), descriptionKey is null ? null : L.Get(descriptionKey), combo);
    }

    /// <summary>A slider row bound to a numeric setting, with a value label.</summary>
    protected SettingsRow Slider(Setting<double> setting, string? icon, string titleKey, double min, double max, double step, Func<double, string> format)
    {
        var property = Track(Settings.Bind(setting));
        var slider = new Avalonia.Controls.Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step, TickFrequency = step, IsSnapToTickEnabled = step > 0, Width = 180, Value = property.Value };
        var label = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = format(property.Value) };
        slider.ValueChanged += (_, e) =>
        {
            property.Value = e.NewValue;
            label.Text = format(e.NewValue);
        };
        return Row(icon, L.Get(titleKey), null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, label } });
    }

    protected static Button ActionButton(string text, Action onClick, string? icon = null, bool accent = false)
    {
        var content = icon is null
            ? (object)text
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 15 }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
        var button = new Button { Content = content };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    protected static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption" } };

    protected static TextBlock Note(string text, string brushKey = "TextSecondaryBrush")
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brushKey).ToBinding());
        return block;
    }
}
