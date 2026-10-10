// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;

namespace Rivet.App.Features.Capture;

/// <summary>Small building blocks shared by the capture tools' windows.</summary>
internal static class CaptureUi
{
    /// <summary>An icon-only button with a tooltip and an accessible name.</summary>
    public static Button IconButton(string icon, string tooltip, Action onClick, double iconSize = 16, string classes = "icon")
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = iconSize },
            Padding = new Thickness(7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            button.Classes.Add(c);
        }

        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A round 24-DIP button on a translucent dark plate (over images).</summary>
    public static Button RoundOverlayButton(string icon, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(150, 20, 20, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 13, Foreground = Brushes.White },
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A text button with an optional leading icon.</summary>
    public static Button TextButton(string text, Action onClick, string? icon = null, bool accent = false, string? tooltip = null)
    {
        object content = icon is null
            ? text
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        var button = new Button { Content = content, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 5) };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        if (tooltip is not null)
        {
            ToolTip.SetTip(button, tooltip);
        }

        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A key chip ("Esc", "1–4") for the selector's hint bar.</summary>
    public static Border KeyChip(string text, string? icon = null, bool active = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null)
        {
            content.Children.Add(new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }

        content.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2),
            BorderThickness = new Thickness(1),
            Child = content,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (active)
        {
            chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("AccentSoftBrush").ToBinding());
            chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("AccentBrush").ToBinding());
        }
        else
        {
            chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("ChipBrush").ToBinding());
            chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("PanelCardBorderBrush").ToBinding());
        }

        return chip;
    }

    /// <summary>The floating-surface plate (material look) used by the preview, palettes and bars.</summary>
    public static Border Plate(Control child, double radius = 12, Thickness? padding = null)
    {
        var plate = new Border
        {
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(10),
            Child = child,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 6, Color = Color.FromArgb(70, 0, 0, 0) }),
        };
        plate.Bind(Border.BackgroundProperty, plate.GetResourceObservable("HudBackgroundBrush").ToBinding());
        plate.Bind(Border.BorderBrushProperty, plate.GetResourceObservable("PanelBorderBrush").ToBinding());
        return plate;
    }

    /// <summary>A floating, borderless, transparent window for the capture tools.</summary>
    public static void MakeFloating(Window window)
    {
        window.WindowDecorations = WindowDecorations.None;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        window.CanResize = false;
        window.Background = Brushes.Transparent;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Title = Rivet.Core.App.AppIdentity.DisplayName;
    }
}
