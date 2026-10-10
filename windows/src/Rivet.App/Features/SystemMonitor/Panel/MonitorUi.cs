// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Panel;

/// <summary>Small builders shared by the monitor sections, detail views and settings.</summary>
internal static class MonitorUi
{
    public static readonly FontFeatureCollection TabularDigits = [FontFeature.Parse("tnum")];

    public static TextBlock Text(string? text, double size = 12, FontWeight weight = FontWeight.Normal, string? brushKey = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (brushKey is not null)
        {
            block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brushKey).ToBinding());
        }

        return block;
    }

    /// <summary>A value with tabular digits so numbers do not wobble.</summary>
    public static TextBlock Value(string? text, double size = 12, FontWeight weight = FontWeight.SemiBold, string? brushKey = null)
    {
        var block = Text(text, size, weight, brushKey);
        block.FontFeatures = TabularDigits;
        return block;
    }

    public static TextBlock Caption(string? text) => Text(text, 11, FontWeight.Normal, "TextSecondaryBrush");

    /// <summary>A caption that wraps instead of being cut.</summary>
    public static TextBlock Note(string? text)
    {
        var block = Caption(text);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    public static TextBlock SectionTitle(string text)
    {
        var block = new TextBlock { Text = text.ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, VerticalAlignment = VerticalAlignment.Center };
        return block;
    }

    public static SymbolIcon Icon(string name, double size = 14, string? brushKey = null)
    {
        var icon = new SymbolIcon { Symbol = IconConverter.Parse(name), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (brushKey is not null)
        {
            icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable(brushKey).ToBinding());
        }

        return icon;
    }

    /// <summary>A small colour dot bound to a theme brush.</summary>
    public static Avalonia.Controls.Shapes.Ellipse Dot(string brushKey, double size = 7)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, dot.GetResourceObservable(brushKey).ToBinding());
        return dot;
    }

    public static Border Card(Control child, Thickness? padding = null) =>
        new() { Classes = { "card" }, Padding = padding ?? new Thickness(10, 9), Child = child };

    /// <summary>A block card: optional uppercase heading (with an optional trailing control) above the content.</summary>
    public static Border Block(string? title, Control content, Control? trailing = null)
    {
        var stack = new StackPanel { Spacing = 7 };
        if (title is not null || trailing is not null)
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 18 };
            if (title is not null)
            {
                header.Children.Add(SectionTitle(title));
            }

            if (trailing is not null)
            {
                Grid.SetColumn(trailing, 1);
                header.Children.Add(trailing);
            }

            stack.Children.Add(header);
        }

        stack.Children.Add(content);
        return Card(stack);
    }

    /// <summary>A small icon button with a tooltip and an accessible name.</summary>
    public static Button IconButton(string icon, string tooltip, Action onClick, double size = 13)
    {
        var button = new Button { Classes = { "icon" }, Padding = new Thickness(4), Content = Icon(icon, size, "TextSecondaryBrush") };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A label/value row: icon, title, flexible gap, value (and an optional caption under the title).</summary>
    public static Grid Row(string? icon, string? iconBrush, string title, Control value, string? caption = null, bool reserveIcon = false)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(icon is null && !reserveIcon ? "0,*,Auto" : "22,*,Auto"), ColumnSpacing = 6, MinHeight = 22 };
        if (icon is not null)
        {
            grid.Children.Add(Icon(icon, 14, iconBrush));
        }

        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(title, 12.5, FontWeight.Medium));
        if (caption is not null)
        {
            texts.Children.Add(Caption(caption));
        }

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    /// <summary>A graph sized for panel sections (30) or detail views (38).</summary>
    public static Sparkline Graph(string colorKey, double height = 30, string? secondColorKey = null) =>
        new() { ColorKey = colorKey, SecondColorKey = secondColorKey ?? "MetricGreenBrush", Height = height, Margin = new Thickness(0, 2, 0, 0) };

    public static void SetGraph(Sparkline graph, IReadOnlyList<double> values, double ceiling, string? label, bool showScale, IReadOnlyList<double>? second = null)
    {
        graph.Values = values;
        graph.SecondValues = second;
        graph.Ceiling = ceiling;
        graph.ScaleLabel = showScale ? label : null;
        graph.IsVisible = Sparkline.CanDraw(values) || Sparkline.CanDraw(second);
    }

    public static double Peak(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Max();

    /// <summary>Formats shared by every view, read from settings once per update.</summary>
    public sealed record Formats(TemperatureUnit Unit, bool Bits, bool AppMemory, bool Graphs, bool Scale, CultureInfo Culture)
    {
        public static Formats From(ISettingsStore s) => new(
            MetricFormat.ParseTemperatureUnit(s.Get(MonitorSettings.TemperatureUnit)),
            s.Get(MonitorSettings.NetworkSpeedUnit) == "bits",
            s.Get(MonitorSettings.MemoryMetric) == "app",
            true,
            s.Get(MonitorSettings.GraphScale),
            CultureInfo.CurrentCulture);

        public string Rate(double? bytesPerSecond) =>
            bytesPerSecond is { } b ? MetricFormat.NetworkRate(b, Bits, Culture) : L.Get("Strings.networkMeasuring");

        public string Temperature(double? celsius) => celsius is { } c ? MetricFormat.Temperature(c, Unit, Culture) : "-";
    }
}
