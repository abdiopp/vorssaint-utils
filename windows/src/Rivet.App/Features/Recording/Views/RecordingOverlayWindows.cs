// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.App.Shell;
using Rivet.Core.Platform;
using AvaloniaPixelPoint = Avalonia.PixelPoint;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Recording.Views;

/// <summary>
/// Base for the recorder's floating windows: no frame, no taskbar button,
/// never activated, always on top and excluded from captures
/// (WDA_EXCLUDEFROMCAPTURE), so none of them appears in the video.
/// </summary>
public abstract class RecorderOverlayWindow : Window
{
    private readonly WindowChromeOptions _chrome;
    private bool _chromeApplied;

    protected RecorderOverlayWindow(WindowChromeOptions extra)
    {
        _chrome = WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.ExcludeFromCapture | WindowChromeOptions.Topmost | extra;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Focusable = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
    }

    /// <summary>Shows the window (first time: applies the native styles).</summary>
    public void ShowOverlay()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, _chrome);
        }

        Opacity = 1;
    }

    /// <summary>Physical pixels per DIP on the monitor this window ended up on.</summary>
    protected double Scale(ScreenInfo screen) => RenderScaling > 0 ? RenderScaling : Math.Max(0.5, screen.Scale);
}

/// <summary>The pill's window, centred near the top of the recorded monitor (10 DIPs below its work area's top).</summary>
public sealed class RecordingIndicatorWindow : RecorderOverlayWindow
{
    private ScreenInfo? _screen;

    public RecordingIndicatorWindow()
        : base(WindowChromeOptions.None)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = View;
        ScalingChanged += (_, _) => Place();
        Opened += (_, _) => Place();
    }

    public RecordingIndicatorView View { get; } = new();

    public void ShowOn(ScreenInfo screen)
    {
        _screen = screen;
        Opacity = 0;
        // Position first so Windows picks the right DPI for this monitor.
        Position = new AvaloniaPixelPoint(screen.WorkArea.X + (screen.WorkArea.Width / 2), screen.WorkArea.Y + 10);
        ShowOverlay();
        Place();
    }

    private void Place()
    {
        if (_screen is not { } screen)
        {
            return;
        }

        var scale = Scale(screen);
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var area = screen.WorkArea;
        // The view has 4 DIPs of shadow room above the capsule: the capsule's top lands 10 DIPs below the work area's top.
        Position = new AvaloniaPixelPoint(area.X + ((area.Width - width) / 2), area.Y + (int)Math.Round(6 * scale));
    }
}

/// <summary>
/// The region guide (area and display recordings): everything outside the
/// recorded rectangle dimmed 30 %, the rectangle outlined in blue. Covers the
/// recorded monitor exactly, click-through. Sized from the monitor's physical
/// pixels and the window's own scaling, so it lines up at any DPI.
/// </summary>
public sealed class RegionGuideWindow : RecorderOverlayWindow
{
    private readonly RegionGuideView _view = new();
    private ScreenInfo? _screen;

    public RegionGuideWindow()
        : base(WindowChromeOptions.ClickThrough)
    {
        SizeToContent = SizeToContent.Manual;
        IsHitTestVisible = false;
        Content = _view;
        ScalingChanged += (_, _) => Fit();
        Opened += (_, _) => Fit();
    }

    public void ShowFor(ScreenInfo screen, PixelRect region)
    {
        _screen = screen;
        _view.Monitor = screen.Bounds;
        _view.Region = region;
        Position = new AvaloniaPixelPoint(screen.Bounds.X, screen.Bounds.Y);
        Fit();
        ShowOverlay();
        Fit();
    }

    private void Fit()
    {
        if (_screen is not { } screen)
        {
            return;
        }

        var scale = Scale(screen);
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;
        Position = new AvaloniaPixelPoint(screen.Bounds.X, screen.Bounds.Y);
        _view.PixelScale = scale;
        _view.InvalidateVisual();
    }
}

/// <summary>Draws the guide: a 30 % black frame around the region (even-odd fill) and a 2 DIP blue outline inset 1 DIP.</summary>
public sealed class RegionGuideView : Control
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(77, 0, 0, 0));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(242, 46, 140, 255)), 2);

    public PixelRect Monitor { get; set; }

    public PixelRect Region { get; set; }

    /// <summary>Physical pixels per DIP.</summary>
    public double PixelScale { get; set; } = 1;

    public override void Render(DrawingContext context)
    {
        var scale = PixelScale > 0 ? PixelScale : 1;
        var bounds = new Rect(Bounds.Size);
        var hole = new Rect(
            (Region.X - Monitor.X) / scale,
            (Region.Y - Monitor.Y) / scale,
            Region.Width / scale,
            Region.Height / scale);
        var frame = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(bounds), new RectangleGeometry(hole) },
        };
        context.DrawGeometry(Dim, null, frame);
        context.DrawRectangle(null, Outline, hole.Deflate(1));
    }
}

/// <summary>
/// The countdown (spec 02 §3.5): an 82 DIP circle at the top centre of the
/// screen with the pointer, the number, and an accent ring draining over
/// 0.92 s; re-presented for each number.
/// </summary>
public sealed class CountdownWindow : RecorderOverlayWindow
{
    private ScreenInfo? _screen;

    public CountdownWindow()
        : base(WindowChromeOptions.ClickThrough)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = View;
        ScalingChanged += (_, _) => Place();
        Opened += (_, _) => Place();
    }

    public CountdownView View { get; } = new();

    public void ShowNumber(ScreenInfo screen, int number)
    {
        _screen = screen;
        View.Start(number);
        if (!IsVisible)
        {
            Opacity = 0;
            Position = new AvaloniaPixelPoint(screen.WorkArea.X + (screen.WorkArea.Width / 2), screen.WorkArea.Y + 24);
        }

        ShowOverlay();
        Place();
    }

    private void Place()
    {
        if (_screen is not { } screen)
        {
            return;
        }

        var scale = Scale(screen);
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var area = screen.WorkArea;
        Position = new AvaloniaPixelPoint(area.X + ((area.Width - width) / 2), area.Y + (int)Math.Round(24 * scale));
    }
}

/// <summary>The countdown circle; <see cref="Progress"/> drains from 1 to 0 over 0.92 s after <see cref="Start"/>.</summary>
public sealed class CountdownView : Control
{
    public const double Diameter = 82;
    public const double DrainSeconds = 0.92;

    private static readonly IBrush Plate = new SolidColorBrush(Color.FromArgb(225, 32, 32, 32));
    private static readonly IPen Rim = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _started;

    public CountdownView()
    {
        Width = Diameter + 16;
        Height = Diameter + 16;
        _timer.Tick += (_, _) =>
        {
            Progress = Math.Clamp(1 - ((DateTime.UtcNow - _started).TotalSeconds / DrainSeconds), 0, 1);
            if (Progress <= 0)
            {
                _timer.Stop();
            }

            InvalidateVisual();
        };
    }

    public int Number { get; private set; }

    /// <summary>1 = full ring, 0 = empty.</summary>
    public double Progress { get; set; } = 1;

    public void Start(int number)
    {
        Number = number;
        Progress = 1;
        _started = DateTime.UtcNow;
        _timer.Start();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Diameter / 2;
        context.DrawEllipse(Plate, Rim, center, radius, radius);

        // Accent ring trimmed from 4 % to 4 % + 92 % × progress, starting at 12 o'clock.
        var accent = this.FindResource("AccentBrush") as IBrush ?? new SolidColorBrush(Color.FromRgb(10, 132, 255));
        var pen = new Pen(accent, 3) { LineCap = PenLineCap.Round };
        var ringRadius = radius - 6;
        var start = 0.04;
        var sweep = 0.92 * Math.Clamp(Progress, 0, 1);
        if (sweep > 0.001)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var a0 = (start * 2 * Math.PI) - (Math.PI / 2);
                var a1 = ((start + sweep) * 2 * Math.PI) - (Math.PI / 2);
                g.BeginFigure(new Point(center.X + (ringRadius * Math.Cos(a0)), center.Y + (ringRadius * Math.Sin(a0))), false);
                g.ArcTo(
                    new Point(center.X + (ringRadius * Math.Cos(a1)), center.Y + (ringRadius * Math.Sin(a1))),
                    new Size(ringRadius, ringRadius),
                    0,
                    sweep > 0.5,
                    SweepDirection.Clockwise);
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }

        var text = new FormattedText(
            Number.ToString(System.Globalization.CultureInfo.CurrentCulture),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
            32,
            Brushes.White);
        context.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }
}
