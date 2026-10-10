// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.App.Shell;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Input;

/// <summary>
/// The quit-protection pill (spec 07 §3.7.7 "HUD"): near-black regardless of
/// the theme (white 9 % at 95 % opacity, 1 DIP white 16 % stroke), title 13
/// semibold, detail 10.5 at 68 %, 48 DIP tall or 56 with the hold progress
/// track (inset 24 DIP, 3 DIP tall, 7 DIP above the bottom). Width is
/// max(300, text + 24). No fades.
/// </summary>
public sealed class QuitProtectionHudView : UserControl
{
    public const double MinPillWidth = 300;
    public const double Height48 = 48;
    public const double Height56 = 56;

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0x17, 0x17, 0x17));
    private static readonly IBrush Stroke = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush TitleBrush = Brushes.White;
    private static readonly IBrush DetailBrush = new SolidColorBrush(Color.FromArgb(0xAD, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush ProgressBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    private readonly Border _pill;
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = TitleBrush, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 10.5, Foreground = DetailBrush, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _track = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Background = TrackBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24, 0, 24, 7) };
    private readonly Border _fill = new() { Height = 3, CornerRadius = new CornerRadius(1.5), Background = ProgressBrush, HorizontalAlignment = HorizontalAlignment.Left };

    public QuitProtectionHudView()
    {
        _track.Child = _fill;
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        var layers = new Grid();
        layers.Children.Add(text);
        layers.Children.Add(_track);
        _pill = new Border
        {
            Background = Fill,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            MinWidth = MinPillWidth,
            Padding = new Thickness(12, 0),
            Child = layers,
        };
        Content = _pill;
    }

    public bool HasProgress => _track.IsVisible;

    public void Update(QuitHudContent content, double progress)
    {
        _title.Text = content.Title;
        _detail.Text = content.Detail;
        _detail.IsVisible = !string.IsNullOrEmpty(content.Detail);
        _track.IsVisible = content.ProgressRemaining is not null;
        var height = _track.IsVisible ? Height56 : Height48;
        _pill.Height = height;
        _pill.CornerRadius = new CornerRadius(height / 2);
        // Keep the text clear of the track.
        UpdateTextMargin(height);
        SetProgress(progress);
    }

    public void SetProgress(double progress)
    {
        var width = Math.Max(0, _track.Bounds.Width);
        _fill.Width = width * Math.Clamp(progress, 0, 1);
        if (width <= 0)
        {
            // Before the first layout: size from the pill's minimum.
            _fill.Width = (MinPillWidth - 48) * Math.Clamp(progress, 0, 1);
        }
    }

    private void UpdateTextMargin(double height)
    {
        if (_pill.Child is Grid grid && grid.Children[0] is StackPanel text)
        {
            text.Margin = height > Height48 ? new Thickness(0, 0, 0, 8) : default;
        }
    }
}

/// <summary>The non-activating, click-through overlay window that hosts the pill near the bottom of the pointer's monitor.</summary>
public sealed class QuitProtectionHudWindow : Window
{
    private readonly QuitProtectionHudView _view = new();
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _chromeApplied;
    private DateTime _progressStart;
    private TimeSpan _progressLength;

    public QuitProtectionHudWindow()
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = _view;
        _ticker.Tick += (_, _) => Tick();
    }

    public void ShowContent(QuitHudContent content)
    {
        _progressStart = DateTime.UtcNow;
        _progressLength = content.ProgressRemaining ?? TimeSpan.Zero;
        _view.Update(content, 0);
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate
                                              | WindowChromeOptions.ClickThrough | WindowChromeOptions.Topmost
                                              | WindowChromeOptions.ExcludeFromCapture);
        }

        Place();
        if (content.ProgressRemaining is not null)
        {
            _ticker.Start();
        }
        else
        {
            _ticker.Stop();
        }
    }

    public void HideNow()
    {
        _ticker.Stop();
        if (IsVisible)
        {
            Hide();
        }
    }

    private void Tick()
    {
        if (_progressLength <= TimeSpan.Zero)
        {
            _ticker.Stop();
            return;
        }

        var progress = (DateTime.UtcNow - _progressStart) / _progressLength;
        _view.SetProgress(progress);
        if (progress >= 1)
        {
            _ticker.Stop();
        }
    }

    /// <summary>Bottom centre of the work area of the pointer's monitor, 18 DIP above its bottom.</summary>
    private void Place()
    {
        var screen = WindowInterop.ScreenAtCursor(this);
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var size = _view.DesiredSize.Width > 0 ? _view.DesiredSize : new Size(QuitProtectionHudView.MinPillWidth, QuitProtectionHudView.Height56);
        var width = (int)Math.Ceiling(Math.Max(size.Width, Bounds.Width) * scale);
        var height = (int)Math.Ceiling(Math.Max(size.Height, Bounds.Height) * scale);
        var area = screen.WorkingArea;
        Position = new Avalonia.PixelPoint(area.X + ((area.Width - width) / 2), area.Bottom - height - (int)Math.Round(18 * scale));
    }
}

/// <summary>Posts HUD requests from the hook/timer threads to the UI thread.</summary>
public sealed class QuitProtectionHudHost : IQuitProtectionHud, IDisposable
{
    private QuitProtectionHudWindow? _window;

    public void Show(QuitHudContent content) => Dispatcher.UIThread.Post(() =>
    {
        _window ??= new QuitProtectionHudWindow();
        _window.ShowContent(content);
    });

    public void Hide() => Dispatcher.UIThread.Post(() => _window?.HideNow());

    public void Dispose()
    {
        var window = _window;
        _window = null;
        if (window is not null)
        {
            Dispatcher.UIThread.Post(window.Close);
        }
    }
}
