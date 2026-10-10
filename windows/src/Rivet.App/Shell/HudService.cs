// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;

namespace Rivet.App.Shell;

/// <summary>Short floating messages near the bottom centre of the screen under the pointer.</summary>
public sealed class HudService : IHud
{
    private HudWindow? _window;
    private DispatcherTimer? _timer;

    public void Show(string message, HudStyle style = HudStyle.Info, string? icon = null, TimeSpan? duration = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window ??= new HudWindow();
            _window.Update(message, style, icon);
            _window.ShowAtCursorScreen();
            _timer?.Stop();
            _timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(1.8) };
            _timer.Tick += (_, _) =>
            {
                _timer?.Stop();
                _window?.Hide();
            };
            _timer.Start();
        });
    }
}

internal sealed class HudWindow : Window
{
    private readonly TextBlock _text = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center };
    private readonly SymbolIcon _icon = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
    private bool _chromeApplied;

    public HudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10),
            Margin = new Thickness(8),
            [!Border.BackgroundProperty] = this.GetResourceObservable("HudBackgroundBrush").ToBinding(),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("PanelBorderBrush").ToBinding(),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(60, 0, 0, 0) }),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _icon, _text } },
        };
    }

    public void Update(string message, HudStyle style, string? icon)
    {
        _text.Text = message;
        _icon.Symbol = IconConverter.Parse(icon ?? style switch
        {
            HudStyle.Success => "CheckmarkCircle",
            HudStyle.Warning => "Warning",
            HudStyle.Error => "ErrorCircle",
            _ => "Info",
        });
        _icon.Foreground = style switch
        {
            HudStyle.Success => (IBrush?)Application.Current?.FindResource("SuccessBrush"),
            HudStyle.Warning => (IBrush?)Application.Current?.FindResource("WarningBrush"),
            HudStyle.Error => (IBrush?)Application.Current?.FindResource("DangerBrush"),
            _ => (IBrush?)Application.Current?.FindResource("AccentBrush"),
        };
    }

    public void ShowAtCursorScreen()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate
                                              | WindowChromeOptions.ClickThrough | WindowChromeOptions.ExcludeFromCapture
                                              | WindowChromeOptions.Topmost);
        }

        var screen = WindowInterop.ScreenAtCursor(this);
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var area = screen.WorkingArea;
        Position = new Avalonia.PixelPoint(area.X + ((area.Width - width) / 2), area.Bottom - height - (int)(72 * scale));
    }
}
