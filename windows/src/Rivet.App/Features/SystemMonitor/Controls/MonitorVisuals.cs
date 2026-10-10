// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.Core.Localization;
using Rivet.Core.SystemMonitor;

namespace Rivet.App.Features.SystemMonitor.Controls;

/// <summary>Theme resource lookups for custom-drawn monitor visuals.</summary>
internal static class MonitorBrushes
{
    public static IBrush Get(Control control, string key, IBrush? fallback = null) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback ?? Brushes.Gray;

    public static Color ColorOf(Control control, string key) =>
        Get(control, key) is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    /// <summary>The resource key of a semantic tone (accent for normal).</summary>
    public static string KeyFor(MetricTone tone) => tone switch
    {
        MetricTone.Good => "MetricGreenBrush",
        MetricTone.Elevated => "MetricYellowBrush",
        MetricTone.Critical => "MetricRedBrush",
        _ => "AccentBrush",
    };

    public static string PressureKey(MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Normal => "MetricGreenBrush",
        MemoryPressure.Warning => "MetricYellowBrush",
        MemoryPressure.Critical => "MetricRedBrush",
        _ => "TextSecondaryBrush",
    };

    public static Color WithAlpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * color.A), 0, 255), color.R, color.G, color.B);
}

/// <summary>
/// History graph (spec §3.14): a filled area under a polyline with a vertical
/// gradient (16 % → 0 %), an optional second series (fill 8 %) sharing the
/// ceiling, and an optional scale label (dashed top line plus the ceiling in
/// a small chip over the oldest samples). Renders only with two points or more.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<double>?> SecondValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(SecondValues));

    public static readonly StyledProperty<double> CeilingProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Ceiling), 1.0);

    public static readonly StyledProperty<string?> ScaleLabelProperty =
        AvaloniaProperty.Register<Sparkline, string?>(nameof(ScaleLabel));

    public static readonly StyledProperty<string> ColorKeyProperty =
        AvaloniaProperty.Register<Sparkline, string>(nameof(ColorKey), "AccentBrush");

    public static readonly StyledProperty<string> SecondColorKeyProperty =
        AvaloniaProperty.Register<Sparkline, string>(nameof(SecondColorKey), "MetricGreenBrush");

    public static readonly StyledProperty<bool> ShowBaselineProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(ShowBaseline), true);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, SecondValuesProperty, CeilingProperty, ScaleLabelProperty, ColorKeyProperty, SecondColorKeyProperty, ShowBaselineProperty);
    }

    public Sparkline()
    {
        Height = 30;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? SecondValues
    {
        get => GetValue(SecondValuesProperty);
        set => SetValue(SecondValuesProperty, value);
    }

    /// <summary>The value at the top of the graph (1.0 for fractions).</summary>
    public double Ceiling
    {
        get => GetValue(CeilingProperty);
        set => SetValue(CeilingProperty, value);
    }

    /// <summary>"100%", "2.0 KB/s"; null hides the scale.</summary>
    public string? ScaleLabel
    {
        get => GetValue(ScaleLabelProperty);
        set => SetValue(ScaleLabelProperty, value);
    }

    public string ColorKey
    {
        get => GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    public string SecondColorKey
    {
        get => GetValue(SecondColorKeyProperty);
        set => SetValue(SecondColorKeyProperty, value);
    }

    public bool ShowBaseline
    {
        get => GetValue(ShowBaselineProperty);
        set => SetValue(ShowBaselineProperty, value);
    }

    /// <summary>Whether there is anything to draw (two points or more).</summary>
    public static bool CanDraw(IReadOnlyList<double>? values) => values is { Count: >= 2 };

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var ceiling = Ceiling > 0 && double.IsFinite(Ceiling) ? Ceiling : 1;
        if (ShowBaseline)
        {
            var baseline = MonitorBrushes.WithAlpha(MonitorBrushes.ColorOf(this, "TextSecondaryBrush"), 0.28);
            context.DrawLine(new Pen(new SolidColorBrush(baseline), 1), new Point(0, height - 0.5), new Point(width, height - 0.5));
        }

        if (CanDraw(SecondValues))
        {
            DrawSeries(context, SecondValues!, ceiling, MonitorBrushes.ColorOf(this, SecondColorKey), 0.08, width, height);
        }

        if (CanDraw(Values))
        {
            DrawSeries(context, Values!, ceiling, MonitorBrushes.ColorOf(this, ColorKey), 0.16, width, height);
        }

        if (ScaleLabel is { Length: > 0 } label && (CanDraw(Values) || CanDraw(SecondValues)))
        {
            var secondary = MonitorBrushes.ColorOf(this, "TextSecondaryBrush");
            var dash = new Pen(new SolidColorBrush(MonitorBrushes.WithAlpha(secondary, 0.4)), 1, new DashStyle([2, 3], 0));
            context.DrawLine(dash, new Point(0, 0.5), new Point(width, 0.5));
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 9, new SolidColorBrush(secondary));
            var chip = new Rect(1, 1, text.Width + 8, text.Height + 2);
            var background = MonitorBrushes.ColorOf(this, "PanelBackgroundBrush");
            context.DrawRectangle(new SolidColorBrush(MonitorBrushes.WithAlpha(background, 0.82)), null, chip, 4, 4);
            context.DrawText(text, new Point(chip.X + 4, chip.Y + 1));
        }
    }

    private static void DrawSeries(DrawingContext context, IReadOnlyList<double> values, double ceiling, Color color, double fillAlpha, double width, double height)
    {
        var top = 1.5;
        var usable = height - top - 1;
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count == 1 ? 0 : width * i / (values.Count - 1);
            var v = double.IsFinite(values[i]) ? Math.Clamp(values[i] / ceiling, 0, 1) : 0;
            points[i] = new Point(x, top + (usable * (1 - v)));
        }

        var fill = new StreamGeometry();
        using (var g = fill.Open())
        {
            g.BeginFigure(new Point(0, height), true);
            foreach (var p in points)
            {
                g.LineTo(p);
            }

            g.LineTo(new Point(width, height));
            g.EndFigure(true);
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(MonitorBrushes.WithAlpha(color, fillAlpha), 0),
                new GradientStop(MonitorBrushes.WithAlpha(color, 0), 1),
            },
        };
        context.DrawGeometry(gradient, null, fill);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false);
            for (var i = 1; i < points.Length; i++)
            {
                g.LineTo(points[i]);
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}

/// <summary>A thin capsule bar filled to a fraction.</summary>
public sealed class UsageBar : Control
{
    public static readonly StyledProperty<double?> FractionProperty =
        AvaloniaProperty.Register<UsageBar, double?>(nameof(Fraction));

    public static readonly StyledProperty<string> FillKeyProperty =
        AvaloniaProperty.Register<UsageBar, string>(nameof(FillKey), "AccentBrush");

    static UsageBar()
    {
        AffectsRender<UsageBar>(FractionProperty, FillKeyProperty);
    }

    public UsageBar()
    {
        Height = 5;
        VerticalAlignment = VerticalAlignment.Center;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double? Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public string FillKey
    {
        get => GetValue(FillKeyProperty);
        set => SetValue(FillKeyProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var radius = bounds.Height / 2;
        var track = MonitorBrushes.WithAlpha(MonitorBrushes.ColorOf(this, "TextPrimaryBrush"), 0.09);
        context.DrawRectangle(new SolidColorBrush(track), null, bounds, radius, radius);
        if (Fraction is { } f && f > 0)
        {
            var width = Math.Max(bounds.Height, bounds.Width * Math.Clamp(f, 0, 1));
            context.DrawRectangle(MonitorBrushes.Get(this, FillKey), null, new Rect(0, 0, width, bounds.Height), radius, radius);
        }
    }
}

/// <summary>Memory pressure pill: a glowing dot and label in a capsule tinted 13 % of the colour.</summary>
public sealed class PressurePill : Border
{
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private MemoryPressure _pressure = MemoryPressure.Unknown;

    public PressurePill()
    {
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(7, 2, 8, 2);
        VerticalAlignment = VerticalAlignment.Center;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _dot, _label } };
        ActualThemeVariantChanged += (_, _) => Apply();
        AttachedToVisualTree += (_, _) => Apply();
    }

    public MemoryPressure Pressure
    {
        get => _pressure;
        set
        {
            _pressure = value;
            Apply();
        }
    }

    public static string Label(MemoryPressure pressure) => pressure switch
    {
        MemoryPressure.Normal => L.Get("Strings.pressureNormal"),
        MemoryPressure.Warning => L.Get("Strings.pressureWarning"),
        MemoryPressure.Critical => L.Get("Strings.pressureCritical"),
        _ => "-",
    };

    private void Apply()
    {
        var color = MonitorBrushes.ColorOf(this, MonitorBrushes.PressureKey(_pressure));
        _dot.Fill = new SolidColorBrush(color);
        _dot.Effect = _pressure == MemoryPressure.Unknown ? null : new DropShadowEffect { Color = color, BlurRadius = 6, OffsetX = 0, OffsetY = 0, Opacity = 0.8 };
        _label.Text = Label(_pressure);
        _label.Foreground = new SolidColorBrush(color);
        Background = new SolidColorBrush(MonitorBrushes.WithAlpha(color, 0.13));
    }
}

/// <summary>
/// Per-core bars grouped by class (spec §3.2): bars 60 high filled from the
/// bottom (primary at 78 %, empty track 3.5 %), "–" with a dashed outline for
/// cores without a fresh interval, and "Class ×N" under each group.
/// </summary>
public sealed class CoreMatrixView : Control
{
    public const double BarHeight = 60;
    public const double RowHeight = 88;
    public const double RowSpacing = 12;

    private IReadOnlyList<CoreGroup> _groups = [];
    private IReadOnlyList<double?> _usage = [];

    public CoreMatrixView()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void Update(IReadOnlyList<CoreGroup> groups, IReadOnlyList<double?> usage)
    {
        var relayout = groups.Count != _groups.Count || groups.Sum(g => g.Cores.Count) != _groups.Sum(g => g.Cores.Count);
        _groups = groups;
        _usage = usage;
        if (relayout)
        {
            InvalidateMeasure();
        }

        InvalidateVisual();
    }

    public static string GroupTitle(CoreGroup group) => group.Class switch
    {
        CoreClass.Performance => L.Get("cpuCores.performanceCores"),
        CoreClass.Efficiency => L.Get("cpuCores.efficiencyCores"),
        CoreClass.Super => L.Get("cpuCores.superCores"),
        _ => L.Get("Strings.cpuLabel"),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 292;
        var rows = CoreMatrixLayout.Layout(_groups, width).Count;
        return new Size(width, rows == 0 ? 0 : (rows * RowHeight) + ((rows - 1) * RowSpacing));
    }

    public override void Render(DrawingContext context)
    {
        var rows = CoreMatrixLayout.Layout(_groups, Bounds.Width);
        var primary = MonitorBrushes.ColorOf(this, "TextPrimaryBrush");
        var secondary = MonitorBrushes.ColorOf(this, "TextSecondaryBrush");
        var fill = new SolidColorBrush(MonitorBrushes.WithAlpha(primary, 0.78));
        var track = new SolidColorBrush(MonitorBrushes.WithAlpha(primary, 0.035));
        var dashed = new Pen(new SolidColorBrush(MonitorBrushes.WithAlpha(secondary, 0.5)), 1, new DashStyle([2, 2], 0));
        var y = 0.0;
        foreach (var row in rows)
        {
            foreach (var segment in row)
            {
                var n = segment.Cores.Count;
                var barsWidth = (n * segment.BarWidth) + ((n - 1) * CoreMatrixLayout.BarGap);
                var x = segment.X + ((segment.Width - barsWidth) / 2);
                foreach (var core in segment.Cores)
                {
                    var rect = new Rect(x, y, segment.BarWidth, BarHeight);
                    context.DrawRectangle(track, null, rect, 3, 3);
                    var value = core < _usage.Count ? _usage[core] : null;
                    if (value is { } v)
                    {
                        var h = Math.Max(1.5, BarHeight * Math.Clamp(v, 0, 1));
                        context.DrawRectangle(fill, null, new Rect(x, y + BarHeight - h, segment.BarWidth, h), 3, 3);
                    }
                    else
                    {
                        context.DrawRectangle(null, dashed, rect.Deflate(0.5), 3, 3);
                        var dash = Text("–", 10, secondary);
                        context.DrawText(dash, new Point(x + ((segment.BarWidth - dash.Width) / 2), y + ((BarHeight - dash.Height) / 2)));
                    }

                    x += segment.BarWidth + CoreMatrixLayout.BarGap;
                }

                var caption = Text($"{GroupTitle(segment.Group)} ×{n}", 10, secondary);
                context.DrawText(caption, new Point(segment.X + Math.Max(0, (segment.Width - caption.Width) / 2), y + BarHeight + 8));
            }

            y += RowHeight + RowSpacing;
        }
    }

    private static FormattedText Text(string text, double size, Color color) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), size, new SolidColorBrush(color));
}

/// <summary>Bars-mode gauge (spec §3.16.3): a rounded outline filled bottom-up, a dash when empty.</summary>
public sealed class UsageGauge : Control
{
    public static readonly StyledProperty<double?> FractionProperty =
        AvaloniaProperty.Register<UsageGauge, double?>(nameof(Fraction));

    public static readonly StyledProperty<Color> FillColorProperty =
        AvaloniaProperty.Register<UsageGauge, Color>(nameof(FillColor), Color.Parse("#64D2FF"));

    static UsageGauge()
    {
        AffectsRender<UsageGauge>(FractionProperty, FillColorProperty);
    }

    public UsageGauge()
    {
        Width = 10;
        Height = 22;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double? Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Color FillColor
    {
        get => GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var outline = new Rect(0.6, 0.6, Bounds.Width - 1.2, Bounds.Height - 1.2);
        var ink = MonitorBrushes.ColorOf(this, "TextPrimaryBrush");
        context.DrawRectangle(null, new Pen(new SolidColorBrush(MonitorBrushes.WithAlpha(ink, 0.85)), 1.15), outline, 2.5, 2.5);
        var inner = outline.Deflate(2);
        if (Fraction is { } f)
        {
            var h = inner.Height * Math.Clamp(f, 0, 1);
            if (h > 0)
            {
                context.DrawRectangle(new SolidColorBrush(FillColor), null, new Rect(inner.X, inner.Bottom - h, inner.Width, h), 1.2, 1.2);
            }
        }
        else
        {
            var mid = inner.Y + (inner.Height / 2);
            context.DrawLine(new Pen(new SolidColorBrush(MonitorBrushes.WithAlpha(ink, 0.7)), 1.2), new Point(inner.X + 1, mid), new Point(inner.Right - 1, mid));
        }
    }
}
