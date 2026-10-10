// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Controls;
using Rivet.Core.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The editor's dark stage (spec 01 §3.10.2): colours and small builders for
/// the "material" capsules, icon buttons and colour dots. The editor window
/// is always dark, whatever the app theme.
/// </summary>
internal static class EditorChrome
{
    public static readonly Color Stage = Color.FromRgb(0x1D, 0x1D, 0x1D);
    public static readonly IBrush StageBrush = new SolidColorBrush(Stage);
    public static readonly IBrush StageHighlight = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(9, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
    };

    public static readonly IBrush Material = new SolidColorBrush(Color.FromArgb(0xF0, 0x2C, 0x2C, 0x2E));
    public static readonly IBrush MaterialBorder = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Divider = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Primary = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Secondary = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Hover = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush Pressed = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    public static readonly IBrush BrandMark = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    public static readonly BoxShadows CapsuleShadow = new(new BoxShadow { Blur = 14, OffsetY = 4, Color = Color.FromArgb(0x55, 0, 0, 0) });

    /// <summary>The system accent, falling back to the editor blue.</summary>
    public static Color Accent =>
        Application.Current?.TryFindResource("SystemAccentColor", out var value) == true && value is Color c ? c : Color.FromRgb(0x0A, 0x85, 0xFF);

    public static IBrush AccentBrush => new SolidColorBrush(Accent);

    public static IBrush AccentTint(double opacity) => new SolidColorBrush(Accent, opacity);

    /// <summary>A rounded "material" capsule.</summary>
    public static Border Capsule(Control child, double radius, Thickness padding) => new()
    {
        Background = Material,
        BorderBrush = MaterialBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        BoxShadow = CapsuleShadow,
        Child = child,
    };

    public static Border VerticalDivider() => new() { Width = 1, Height = 18, Background = Divider, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };

    public static Border HorizontalDivider() => new() { Height = 1, Background = Divider, Margin = new Thickness(4, 3) };

    public static SymbolIcon Icon(string name, double size = 16, IconVariant variant = IconVariant.Regular) =>
        new() { Symbol = IconConverter.Parse(name), FontSize = size, IconVariant = variant, Foreground = Primary };

    /// <summary>A borderless icon button with a tooltip and an accessible name (never takes focus from the text editor).</summary>
    public static Button IconButton(string icon, string tooltip, Action onClick, double size = 16, double width = 30, double height = 28)
    {
        var button = new Button
        {
            Content = Icon(icon, size),
            Width = width,
            Height = height,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Focusable = false,
            Classes = { "editorIcon" },
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A text button in the stage style (Fit, 1:1, menus).</summary>
    public static Button TextButton(string text, Action onClick, string? tooltip = null, double fontSize = 12)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeight.SemiBold, Foreground = Primary, VerticalAlignment = VerticalAlignment.Center },
            Padding = new Thickness(8, 3),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "editorIcon" },
        };
        if (tooltip is not null)
        {
            ToolTip.SetTip(button, tooltip);
        }

        AutomationProperties.SetName(button, tooltip ?? text);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static void SetActive(Button button, bool active)
    {
        button.Background = active ? AccentTint(0.22) : Brushes.Transparent;
        if (button.Content is SymbolIcon icon)
        {
            icon.Foreground = active ? AccentBrush : Primary;
        }
        else if (button.Content is TextBlock text)
        {
            text.Foreground = active ? AccentBrush : Primary;
        }
    }

    public static Color ToAvalonia(Rgb rgb, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Math.Round(rgb.R * 255), (byte)Math.Round(rgb.G * 255), (byte)Math.Round(rgb.B * 255));

    public static Color ToAvalonia(AnnotationColor color, double alpha = 1) => ToAvalonia(AnnotationColors.RgbOf(color), alpha);

    public static Color ToAvalonia(Rivet.Imaging.Backdrop.RgbColor c) => Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    /// <summary>
    /// A 96-DPI bitmap of <paramref name="image"/>; size the Image control in DIPs and it scales down
    /// crisply. (Bitmaps created with another DPI render cropped in Avalonia 12.1.)
    /// </summary>
    public static Bitmap ToBitmap(SKImage image) => ImageInterop.ToBitmap(image, 1.0);

    /// <summary>An icon (and optional label) button that shows an on/off state with the accent tint.</summary>
    public static Button ToggleButton(string icon, string name, Action onClick, string? label = null)
    {
        Control content = label is null
            ? Icon(icon, 16)
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Icon(icon, 14), new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Primary, VerticalAlignment = VerticalAlignment.Center } } };
        var button = new Button
        {
            Content = content,
            MinWidth = 30,
            Height = 26,
            Padding = new Thickness(label is null ? 0 : 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "editorIcon" },
        };
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Shows the on/off state of a <see cref="ToggleButton(string, string, Action, string?)"/>.</summary>
    public static void SetToggled(Button button, bool on)
    {
        button.Background = on ? AccentTint(0.22) : Brushes.Transparent;
        var foreground = on ? AccentBrush : Primary;
        IEnumerable<Control> children = button.Content switch
        {
            Panel panel => panel.Children,
            Control single => [single],
            _ => [],
        };
        foreach (var child in children)
        {
            switch (child)
            {
                case SymbolIcon icon:
                    icon.Foreground = foreground;
                    break;
                case TextBlock text:
                    text.Foreground = foreground;
                    break;
            }
        }
    }

    /// <summary>Styles shared by the editor's buttons (hover wash, pressed state).</summary>
    public static Avalonia.Styling.Styles ButtonStyles()
    {
        var styles = new Avalonia.Styling.Styles();
        styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("editorIcon").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Hover) },
        });
        styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("editorIcon").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Pressed) },
        });
        styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("editorIcon").Class(":disabled"))
        {
            Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.35) },
        });
        styles.Add(new Avalonia.Styling.Style(x => x.OfType<Button>().Class("editorIcon").Class(":disabled").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Avalonia.Styling.Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
        });
        return styles;
    }
}

/// <summary>A round colour swatch button (17-point dot, 23-point ring when selected).</summary>
internal sealed class ColorDot : Button
{
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 17, Height = 17 };
    private readonly Avalonia.Controls.Shapes.Ellipse _ring = new() { Width = 23, Height = 23, StrokeThickness = 1.5, IsVisible = false };

    public ColorDot(Color color, string name, Action onClick, double size = 17)
    {
        _dot.Width = _dot.Height = size;
        _ring.Width = _ring.Height = size + 6;
        _dot.Fill = new SolidColorBrush(color);
        _dot.Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        _dot.StrokeThickness = 1;
        _ring.Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        Content = new Grid { Width = size + 7, Height = size + 7, Children = { _ring, _dot } };
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        MinWidth = 0;
        MinHeight = 0;
        Focusable = false;
        ToolTip.SetTip(this, name);
        AutomationProperties.SetName(this, name);
        Click += (_, _) => onClick();
    }

    public bool IsSelected
    {
        get => _ring.IsVisible;
        set => _ring.IsVisible = value;
    }
}
