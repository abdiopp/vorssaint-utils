// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.App.Controls;
using Rivet.App.Features.SystemMonitor.Controls;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Readouts;

/// <summary>
/// Renders readout blocks side by side (spec §3.16.2–3.16.4): a small label
/// over a value, two-line rate blocks, usage gauges in bars mode, the memory
/// pressure dot and the battery glyph. Values reserve their widest text so
/// the strip never wobbles.
/// </summary>
public sealed class ReadoutStrip : UserControl
{
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public ReadoutStrip()
    {
        Content = _panel;
    }

    /// <summary>The block under each child (for click → detail).</summary>
    public IReadOnlyList<(Control View, ReadoutBlock Block)> Items { get; private set; } = [];

    public void Update(IReadOnlyList<ReadoutBlock> blocks, string? countdown, ISettingsStore settings)
    {
        _panel.Spacing = settings.Get(MonitorSettings.ReadoutSpacing) == "standard" ? 12 : 8;
        _panel.Children.Clear();
        var items = new List<(Control, ReadoutBlock)>();
        if (countdown is not null)
        {
            _panel.Children.Add(Single(null, countdown, countdown, MonitorUi.Value(countdown, 13, FontWeight.SemiBold, "MetricOrangeBrush")));
        }

        var colors = BarColors(settings);
        foreach (var block in blocks)
        {
            var view = Build(block, colors);
            ToolTip.SetTip(view, ReadoutTokens.Title(block.Token));
            AutomationProperties.SetName(view, $"{ReadoutTokens.Title(block.Token)} {block.Summary}");
            _panel.Children.Add(view);
            items.Add((view, block));
        }

        Items = items;
    }

    private static (Color Normal, Color Elevated, Color Critical) BarColors(ISettingsStore s) =>
        (Color.Parse(s.Get(MonitorSettings.BarNormalColor)), Color.Parse(s.Get(MonitorSettings.BarElevatedColor)), Color.Parse(s.Get(MonitorSettings.BarCriticalColor)));

    private static Control Build(ReadoutBlock block, (Color Normal, Color Elevated, Color Critical) colors)
    {
        if (block.IsGauge)
        {
            var letters = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in block.Label ?? string.Empty)
            {
                letters.Children.Add(MonitorUi.Text(c.ToString(), 7.5, FontWeight.SemiBold, "TextSecondaryBrush"));
            }

            var gauge = new UsageGauge
            {
                Fraction = block.Gauge,
                FillColor = block.Tone switch
                {
                    MetricTone.Critical => colors.Critical,
                    MetricTone.Elevated => colors.Elevated,
                    _ => colors.Normal,
                },
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { letters } };
            if (block.PressureDot is { } dot)
            {
                row.Children.Add(MonitorUi.Dot(MonitorBrushes.PressureKey(dot), 5));
            }

            row.Children.Add(gauge);
            return row;
        }

        if (block.SecondLine is { } second)
        {
            var first = Line(block.Value);
            var next = Line(second);
            return new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, MinWidth = ReserveWidth(block.Reserve, 10), Children = { first, next } };
        }

        var value = MonitorUi.Value(block.Value, 12.5, FontWeight.SemiBold);
        value.HorizontalAlignment = HorizontalAlignment.Center;
        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        if (block.PressureDot is { } pressure)
        {
            valueRow.Children.Add(MonitorUi.Dot(MonitorBrushes.PressureKey(pressure), 5));
        }

        if (block.BatteryLevel is { } level)
        {
            valueRow.Children.Add(MonitorUi.Icon(block.BatteryBolt ? "BatteryCharge" : $"Battery{level / 10}", 12, "TextSecondaryBrush"));
        }

        if (block.Value.Length > 0)
        {
            valueRow.Children.Add(value);
        }

        return Single(block.Label, block.Value, block.Reserve, valueRow);
    }

    private static Control Single(string? label, string value, string reserve, Control valueView)
    {
        var stack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, MinWidth = ReserveWidth(reserve, 12.5) };
        if (label is not null)
        {
            var labelText = MonitorUi.Text(label, 8, FontWeight.SemiBold, "TextSecondaryBrush");
            labelText.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(labelText);
        }

        valueView.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(valueView);
        return stack;
    }

    private static TextBlock Line(string text)
    {
        var line = MonitorUi.Value(text, 10, FontWeight.SemiBold);
        line.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
        line.HorizontalAlignment = HorizontalAlignment.Right;
        return line;
    }

    /// <summary>Approximate width of the reserved text (each digit counted as "8").</summary>
    private static double ReserveWidth(string reserve, double size)
    {
        if (reserve.Length == 0)
        {
            return 0;
        }

        var normalized = new string(reserve.Select(c => char.IsDigit(c) ? '8' : c).ToArray());
        var text = new FormattedText(normalized, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size, Brushes.Black);
        return Math.Ceiling(text.Width);
    }
}
