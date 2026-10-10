// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Rivet.App.Features.SystemMonitor.Panel;
using Rivet.App.Shell;
using Rivet.Core.Diagnostics;
using Rivet.Core.Displays;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Displays;

/// <summary>
/// "Show brightness when adjusting" (spec 03 §3.19.5): a 196 × 154 dark
/// panel centred on the display being changed, with a sun, the percentage and
/// 16 segments. Fades in over 0.1 s, stays 1 s after the last change and
/// fades out over 0.2 s; click-through and kept out of screen captures.
/// </summary>
internal sealed class BrightnessOsd : IDisposable
{
    public const double Width = 196;
    public const double Height = 154;
    private static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(0.10);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(0.20);

    private readonly BrightnessService _service;
    private readonly IScreenService? _screens;
    private OsdWindow? _window;
    private DispatcherTimer? _hold;
    private DispatcherTimer? _close;
    private bool _active;

    public BrightnessOsd(BrightnessService service, IScreenService? screens)
    {
        _service = service;
        _screens = screens;
    }

    public void Sync(bool enabled)
    {
        if (enabled == _active)
        {
            return;
        }

        _active = enabled;
        if (enabled)
        {
            _service.Adjusted += OnAdjusted;
        }
        else
        {
            _service.Adjusted -= OnAdjusted;
            HideNow();
        }
    }

    private void OnAdjusted(object? sender, BrightnessFeedback feedback) => Show(feedback.Display, feedback.Level);

    public void Show(DisplayStatus display, double level)
    {
        try
        {
            _window ??= new OsdWindow();
            _window.View.Level = level;
            var screen = _screens?.Screens.FirstOrDefault(s => string.Equals(s.Id, display.Id, StringComparison.OrdinalIgnoreCase));
            var bounds = screen?.Bounds ?? display.Bounds;
            var scale = screen?.Scale ?? 1;
            var position = new Avalonia.PixelPoint(
                bounds.X + (int)((bounds.Width - (Width * scale)) / 2),
                bounds.Y + (int)((bounds.Height - (Height * scale)) / 2));
            _window.Position = position;
            if (!_window.IsVisible)
            {
                _window.View.Opacity = 0;
                _window.Show();
            }

            Fade(1, FadeIn);
            _close?.Stop();
            _hold ??= new DispatcherTimer();
            _hold.Stop();
            _hold.Interval = FadeIn + Hold;
            _hold.Tick -= OnHoldElapsed;
            _hold.Tick += OnHoldElapsed;
            _hold.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Could not show the brightness OSD.", ex);
        }
    }

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _hold?.Stop();
        Fade(0, FadeOut);
        _close ??= new DispatcherTimer { Interval = FadeOut };
        _close.Tick -= OnCloseElapsed;
        _close.Tick += OnCloseElapsed;
        _close.Start();
    }

    private void OnCloseElapsed(object? sender, EventArgs e) => HideNow();

    private void Fade(double opacity, TimeSpan duration)
    {
        if (_window is null)
        {
            return;
        }

        _window.View.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration }];
        _window.View.Opacity = opacity;
    }

    private void HideNow()
    {
        _hold?.Stop();
        _close?.Stop();
        try
        {
            _window?.Hide();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Could not hide the brightness OSD.", ex);
        }
    }

    public void Dispose()
    {
        Sync(false);
        try
        {
            _window?.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Could not close the brightness OSD.", ex);
        }

        _window = null;
    }

    private sealed class OsdWindow : Window
    {
        public OsdWindow()
        {
            Title = "Brightness";
            WindowDecorations = WindowDecorations.None;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            CanResize = false;
            Focusable = false;
            IsHitTestVisible = false;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Width = BrightnessOsd.Width;
            Height = BrightnessOsd.Height;
            Content = View;
            Opened += (_, _) => WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate
                | WindowChromeOptions.ClickThrough | WindowChromeOptions.ExcludeFromCapture | WindowChromeOptions.Topmost);
        }

        public BrightnessOsdView View { get; } = new();
    }
}

/// <summary>The OSD's content (also rendered on its own by the snapshot tests).</summary>
internal sealed class BrightnessOsdView : Border
{
    private readonly TextBlock _percent = new() { FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, FontFeatures = MonitorUi.TabularDigits, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Segments _segments = new() { Width = 152, Height = 7, HorizontalAlignment = HorizontalAlignment.Center };

    public BrightnessOsdView()
    {
        Width = BrightnessOsd.Width;
        Height = BrightnessOsd.Height;
        CornerRadius = new CornerRadius(24);
        Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        var sun = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.BrightnessHigh, FontSize = 39, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var percentSign = new TextBlock { Text = "%", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 0, 5) };
        Child = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                sun,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { _percent, percentSign } },
                _segments,
            },
        };
        Level = 0.5;
    }

    public double Level
    {
        get => _segments.Level;
        set
        {
            _segments.Level = Math.Clamp(value, 0, 1);
            _percent.Text = BrightnessMath.ToPercent(value).ToString(System.Globalization.CultureInfo.CurrentCulture);
            _segments.InvalidateVisual();
        }
    }

    private sealed class Segments : Control
    {
        private static readonly IBrush Filled = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush Empty = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

        public double Level { get; set; }

        public override void Render(DrawingContext context)
        {
            const double gap = 2;
            var count = BrightnessMath.OsdSegments;
            var width = (Bounds.Width - (gap * (count - 1))) / count;
            var filled = BrightnessMath.FilledSegments(Level);
            for (var i = 0; i < count; i++)
            {
                var rect = new Rect(i * (width + gap), 0, width, Bounds.Height);
                context.DrawRectangle(i < filled ? Filled : Empty, null, rect, 1.5, 1.5);
            }
        }
    }
}
