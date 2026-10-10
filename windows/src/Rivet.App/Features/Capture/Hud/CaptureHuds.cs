// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Hosting;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Platform;
using SkiaSharp;

namespace Rivet.App.Features.Capture.Hud;

/// <summary>
/// "Copied" toast with a colour swatch for the colour picker (the shared HUD
/// shows icons only). Same place and look as the app's HUD.
/// </summary>
internal sealed class ColorToastWindow : Window
{
    private readonly Border _swatch = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)) };
    private readonly TextBlock _text = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis };
    private DispatcherTimer? _timer;

    public ColorToastWindow()
    {
        CaptureUi.MakeFloating(this);
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        IsHitTestVisible = false;
        var plate = CaptureUi.Plate(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _swatch, _text } }, radius: 10, padding: new Thickness(14, 9));
        plate.Margin = new Thickness(10);
        Content = plate;
    }

    public void Show(uint argb, string value)
    {
        _swatch.Background = new SolidColorBrush(Color.FromUInt32(argb | 0xFF000000));
        _text.Text = value;
        if (!IsVisible)
        {
            Opened += OnFirstOpen;
            Show();
        }

        Place();
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            Hide();
        };
        _timer.Start();
    }

    private void OnFirstOpen(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpen;
        CaptureWindows.ApplyWorkflowChrome(this, noActivate: true);
        var handle = WindowInterop.Handle(this);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<ICapturePlatform>()?.SetClickThrough(handle, true);
        }

        Place();
    }

    private void Place()
    {
        var screens = AppHost.Current?.Services.GetService<IScreenService>();
        if (screens is null)
        {
            return;
        }

        var display = screens.ScreenFromPoint(screens.CursorPosition);
        var s = display.Scale;
        var w = (int)Math.Ceiling(Bounds.Width * s);
        var h = (int)Math.Ceiling(Bounds.Height * s);
        var work = display.WorkArea;
        Position = new Avalonia.PixelPoint(work.X + ((work.Width - w) / 2), work.Bottom - h - (int)(72 * s));
    }
}

/// <summary>
/// The capture countdown (spec 01 §3.19, §6.24): an 82-DIP circle with the
/// remaining seconds and an accent ring that drains over 0.92 s each second,
/// driven by time so a late frame catches up.
/// </summary>
internal sealed class CountdownWindow : Window
{
    private const double Size = 82;
    private const double Inset = 8;
    private readonly SkiaView _view = new();
    private readonly DispatcherTimer _frames = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private DateTime _secondStarted = DateTime.UtcNow;
    private int _value;
    private SKColor _accent = new(0, 103, 192);
    private SKColor _plate = new(250, 250, 250, 242);
    private SKColor _ink = new(20, 20, 20);

    public CountdownWindow()
    {
        CaptureUi.MakeFloating(this);
        Width = Size + (2 * Inset);
        Height = Size + (2 * Inset);
        ShowActivated = false;
        IsHitTestVisible = false;
        _view.Draw += OnDraw;
        Content = _view;
        _frames.Tick += (_, _) => _view.InvalidateVisual();
    }

    public void ShowValue(int seconds)
    {
        _value = seconds;
        _secondStarted = DateTime.UtcNow;
        ResolveColors();
        if (!IsVisible)
        {
            Opened += OnFirstOpen;
            Show();
        }

        _frames.Start();
        _view.InvalidateVisual();
    }

    public void Stop()
    {
        _frames.Stop();
        Close();
    }

    private void OnFirstOpen(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpen;
        CaptureWindows.ApplyWorkflowChrome(this, noActivate: true);
        var services = AppHost.Current?.Services;
        var handle = WindowInterop.Handle(this);
        if (handle != 0)
        {
            services?.GetService<ICapturePlatform>()?.SetClickThrough(handle, true);
        }

        if (services?.GetService<IScreenService>() is { } screens)
        {
            // Top centre of the pointer display's work area, 24 DIPs below its top.
            var display = screens.ScreenFromPoint(screens.CursorPosition);
            var s = display.Scale;
            var side = (int)Math.Round(Width * s);
            var x = display.WorkArea.X + ((display.WorkArea.Width - side) / 2);
            var y = display.WorkArea.Y + (int)Math.Round((24 - Inset) * s);
            CaptureWindows.PlaceExactly(this, new Rivet.Core.Platform.PixelRect(x, y, side, side), s);
        }
    }

    private void ResolveColors()
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        _plate = dark ? new SKColor(43, 43, 43, 240) : new SKColor(251, 251, 251, 242);
        _ink = dark ? SKColors.White : new SKColor(20, 20, 20);
        if (Application.Current?.TryGetResource("SystemAccentColor", ActualThemeVariant, out var accent) == true && accent is Color c)
        {
            _accent = new SKColor(c.R, c.G, c.B, c.A);
        }
    }

    private void OnDraw(object? sender, SkiaDrawEventArgs e)
    {
        var canvas = e.Canvas;
        var center = new SKPoint((float)(e.Size.Width / 2), (float)(e.Size.Height / 2));
        var radius = (float)(Size / 2) - 1;
        using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(60), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(4, 4) })
        {
            canvas.DrawCircle(center.X, center.Y + 2, radius, shadow);
        }

        using (var plate = new SKPaint { Color = _plate, IsAntialias = true })
        {
            canvas.DrawCircle(center, radius, plate);
        }

        using (var ring = new SKPaint { Color = _ink.WithAlpha(40), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
        {
            canvas.DrawCircle(center, radius - 5, ring);
        }

        var progress = CountdownRing.Progress((DateTime.UtcNow - _secondStarted).TotalSeconds);
        var (from, to) = CountdownRing.Arc(progress);
        if (to > from)
        {
            using var arc = new SKPaint { Color = _accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeCap = SKStrokeCap.Round };
            var box = new SKRect(center.X - radius + 5, center.Y - radius + 5, center.X + radius - 5, center.Y + radius - 5);
            canvas.DrawArc(box, (float)((from * 360) - 90), (float)((to - from) * 360), false, arc);
        }

        using var font = Rivet.Imaging.Capture.CaptureFonts.Ui(32, bold: true);
        using var text = new SKPaint { Color = _ink, IsAntialias = true };
        var label = _value.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var width = font.MeasureText(label);
        var metrics = font.Metrics;
        canvas.DrawText(label, center.X - (width / 2), center.Y - ((metrics.Ascent + metrics.Descent) / 2), font, text);
    }
}
