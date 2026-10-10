// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Platform;
using PixelPoint = Rivet.Core.Platform.PixelPoint;

namespace Rivet.App.Shell;

public sealed record TrayMenuEntry(string? Title, string? Icon, Action? Invoke)
{
    public static TrayMenuEntry Separator { get; } = new(null, null, null);

    public bool IsSeparator => Title is null;
}

/// <summary>The tray icon's right-click menu, styled like a Windows 11 context menu.</summary>
public sealed class TrayMenuWindow : Window
{
    private readonly StackPanel _items = new() { Spacing = 1 };
    private bool _chromeApplied;

    public TrayMenuWindow()
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            MinWidth = 220,
            Child = _items,
        };
        border.Bind(Border.BackgroundProperty, this.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PanelBorderBrush").ToBinding());
        Content = border;
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Hide();
            }
        };
    }

    public void ShowAt(PixelPoint point, IReadOnlyList<TrayMenuEntry> entries)
    {
        _items.Children.Clear();
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                _items.Children.Add(new Border { Classes = { "separator" }, Margin = new Thickness(8, 4) });
                continue;
            }

            var row = new Button
            {
                Classes = { "row" },
                Padding = new Thickness(10, 7),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new SymbolIcon { Symbol = IconConverter.Parse(entry.Icon), FontSize = 16, Opacity = entry.Icon is null ? 0 : 1 },
                        new TextBlock { Text = entry.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            var action = entry.Invoke;
            row.Click += (_, _) =>
            {
                Hide();
                action?.Invoke();
            };
            _items.Children.Add(row);
        }

        Show();
        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
            WindowInterop.SetRoundedCorners(this);
        }

        Activate();
        var screen = WindowInterop.ScreenAt(this, new Avalonia.PixelPoint(point.X, point.Y));
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var area = screen.WorkingArea;
        var x = Math.Clamp(point.X - width, area.X, Math.Max(area.X, area.Right - width));
        var y = point.Y - height;
        if (y < area.Y)
        {
            y = point.Y;
        }

        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        Position = new Avalonia.PixelPoint(x, y);
    }
}
