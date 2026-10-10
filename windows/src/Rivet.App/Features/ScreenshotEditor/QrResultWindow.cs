// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Shell;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Imaging.ScreenshotEditor;
using PixelPoint = Avalonia.PixelPoint;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The QR result panel (spec 01 §3.17): the decoded payload with Copy, plus
/// Open link for a single http(s) URL. Esc or a click elsewhere closes it; a
/// new result replaces an open panel.
/// </summary>
internal sealed class QrResultWindow : Window
{
    private static QrResultWindow? _current;
    private readonly IServiceProvider _services;
    private readonly QrResult _result;

    public QrResultWindow(IServiceProvider services, QrResult result)
    {
        _services = services;
        _result = result;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = 320;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("Strings.qrResultTitle");

        var payload = new TextBox
        {
            Text = result.Payload,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            MaxHeight = 132,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };
        payload.Bind(TextBox.BackgroundProperty, payload.GetResourceObservable("ChipBrush").ToBinding());

        var copy = new Button { Content = L.Get("Strings.qrResultCopy"), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        copy.Click += (_, _) => Copy();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copy);
        if (result.Url is not null)
        {
            var open = new Button { Content = L.Get("Strings.qrResultOpen"), Classes = { "accent" }, IsDefault = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            open.Click += (_, _) => OpenLink();
            buttons.Children.Add(open);
        }
        else
        {
            copy.Classes.Add("accent");
            copy.IsDefault = true;
        }

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new SymbolIcon { Symbol = FluentIcons.Common.Symbol.QrCode, FontSize = 18, [!SymbolIcon.ForegroundProperty] = this.GetResourceObservable("AccentBrush").ToBinding() },
                new TextBlock { Text = L.Get("Strings.qrResultTitle"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Margin = new Thickness(10),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 6, Color = Color.FromArgb(0x50, 0, 0, 0) }),
            Child = new StackPanel { Spacing = 12, Children = { header, payload, buttons } },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("HudBackgroundBrush").ToBinding());
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("PanelBorderBrush").ToBinding());
        Content = card;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Close();
    }

    /// <summary>Opens the panel 18 points below the pointer, clamped inside the display; replaces an open panel.</summary>
    public static void ShowFor(IServiceProvider services, QrResult result, Window? owner)
    {
        _current?.Close();
        var window = _current = new QrResultWindow(services, result) { RequestedThemeVariant = owner?.ActualThemeVariant };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }
        };
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Opened += (_, _) => window.Place();
        window.Show();
        WindowInterop.ApplyChrome(window, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
        window.Activate();
    }

    private void Place()
    {
        var screens = _services.GetService<IScreenService>();
        if (screens is null)
        {
            return;
        }

        var cursor = screens.CursorPosition;
        var screen = screens.ScreenFromPoint(cursor);
        var scale = screen.Scale;
        var w = (int)Math.Ceiling(Bounds.Width * scale);
        var h = (int)Math.Ceiling(Bounds.Height * scale);
        var inset = (int)(12 * scale);
        var area = screen.WorkArea;
        var x = Math.Clamp(cursor.X - (w / 2), area.X + inset, Math.Max(area.X + inset, area.Right - w - inset));
        var y = Math.Clamp(cursor.Y + (int)(18 * scale), area.Y + inset, Math.Max(area.Y + inset, area.Bottom - h - inset));
        Position = new PixelPoint(x, y);
    }

    private void Copy()
    {
        _services.GetRequiredService<IClipboardService>().SetText(_result.Payload);
        Close();
        _services.GetService<IHud>()?.Show(L.Get("Strings.ocrQRCopied"), HudStyle.Success, "Copy");
    }

    private void OpenLink()
    {
        Close();
        if (_result.Url is { } url)
        {
            _services.GetService<IShellService>()?.OpenUrl(url.ToString());
        }
    }
}
