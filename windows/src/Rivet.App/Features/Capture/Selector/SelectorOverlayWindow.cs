// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Capture;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Selector;

/// <summary>
/// One full-screen overlay per display (spec 01 §3.4.1): borderless,
/// top-most, kept out of every capture, exactly covering its monitor in
/// physical pixels. The selection chrome and loupe are drawn with Skia in
/// physical pixels; the hint bar and the Full screen pill are controls on top.
/// </summary>
internal sealed class SelectorOverlayWindow : Window
{
    private readonly SelectorSession _session;
    private readonly SkiaView _canvas = new();
    private readonly Canvas _chrome = new();
    private readonly SelectorHintBar _hintBar;
    private readonly Button _pill;

    /// <summary>
    /// The frame to draw, built on the UI thread: Avalonia renders custom draw
    /// operations on its render thread, which must never read the live session state.
    /// </summary>
    private volatile OverlayScene? _scene;

    public SelectorOverlayWindow(SelectorSession session, ScreenInfo display, ISettingsStore settings)
    {
        _session = session;
        Display = display;
        CaptureUi.MakeFloating(this);
        ShowActivated = true;
        Focusable = true;
        Width = display.Bounds.Width / display.Scale;
        Height = display.Bounds.Height / display.Scale;
        Position = new Avalonia.PixelPoint(display.Bounds.X, display.Bounds.Y);

        _canvas.Cursor = new Cursor(StandardCursorType.Cross);
        _canvas.Draw += OnDraw;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerWheelChanged += OnPointerWheel;
        _canvas.PointerCaptureLost += (_, _) => _session.OnPointerCaptureLost();
        PointerMoved += (_, e) =>
        {
            if (e.Source is not SkiaView)
            {
                _session.OnPointerMoved(ToGlobal(e.GetPosition(this)), Modifiers(e.KeyModifiers));
            }
        };

        _hintBar = new SelectorHintBar(settings);
        _hintBar.ToolRequested += (_, tool) => _session.RequestTool(tool);
        _hintBar.CancelRequested += (_, _) => _session.Cancel();

        _pill = new Button
        {
            Focusable = false,
            MinHeight = 32,
            Padding = new Thickness(14, 4),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    new SymbolIcon { Symbol = FluentIcons.Common.Symbol.FullScreenMaximize, FontSize = 15, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.Get("screenshot.fullScreenCaptureButton"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        _pill.Bind(Button.BackgroundProperty, _pill.GetResourceObservable("HudBackgroundBrush").ToBinding());
        _pill.Bind(Button.BorderBrushProperty, _pill.GetResourceObservable("PanelBorderBrush").ToBinding());
        ToolTip.SetTip(_pill, L.Get("screenshot.fullScreenShortcutTitle"));
        AutomationProperties.SetName(_pill, L.Get("screenshot.fullScreenShortcutTitle"));
        _pill.Click += (_, _) => _session.OnFullScreenPill(Display);
        _pill.PointerEntered += (_, _) => _session.SetPointerOverPill(true);
        _pill.PointerExited += (_, _) => _session.SetPointerOverPill(false);

        _chrome.Children.Add(_hintBar);
        _chrome.Children.Add(_pill);
        Content = new Panel { Children = { _canvas, _chrome } };
        Opened += (_, _) =>
        {
            CaptureWindows.ApplyWorkflowChrome(this);
            CaptureWindows.PlaceExactly(this, Display.Bounds, Display.Scale);
            Refresh();
        };
        ScalingChanged += (_, _) => Refresh();
    }

    public ScreenInfo Display { get; }

    /// <summary>Redraws the canvas and updates the controls from the session state.</summary>
    public void Refresh()
    {
        _scene = _session.SceneFor(Display);
        _canvas.InvalidateVisual();
        var scaling = RenderScaling > 0 ? RenderScaling : Display.Scale;
        var bounds = Display.Bounds;
        var work = Display.WorkArea.Intersect(bounds);
        if (work.IsEmpty)
        {
            work = bounds;
        }

        var state = _session.HintStateFor(Display);
        _hintBar.IsVisible = state is not null;
        if (state is not null)
        {
            _hintBar.Update(state);
            var widthDip = SelectorHintBar.WidthFor(state, bounds.Width / scaling);
            var heightDip = SelectorHintBar.HeightFor(state);
            _hintBar.Width = widthDip;
            _hintBar.Height = heightDip;
            Canvas.SetLeft(_hintBar, ((work.X - bounds.X) / scaling) + (((work.Width / scaling) - widthDip) / 2));
            Canvas.SetTop(_hintBar, ((work.Bottom - bounds.Y) / scaling) - 24 - heightDip);
        }

        _pill.IsVisible = _session.PillVisible(Display);
        if (_pill.IsVisible)
        {
            _pill.Measure(Size.Infinity);
            var pillWidth = _pill.DesiredSize.Width;
            Canvas.SetLeft(_pill, ((work.X - bounds.X) / scaling) + (((work.Width / scaling) - pillWidth) / 2));
            Canvas.SetTop(_pill, ((work.Y - bounds.Y) / scaling) + 12);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Only reached without the low-level hook (development build): the hook swallows these keys on Windows.
        var vk = AvaloniaKeyMap.ToVirtualKey(e.Key);
        if (SelectorKeys.Map(vk) is { } key)
        {
            e.Handled = true;
            _session.OnKey(key, Rivet.Core.Platform.KeyAction.Down, AvaloniaKeyMap.ToModifiers(e.KeyModifiers));
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        var vk = AvaloniaKeyMap.ToVirtualKey(e.Key);
        if (SelectorKeys.Map(vk) is { } key)
        {
            e.Handled = true;
            _session.OnKey(key, Rivet.Core.Platform.KeyAction.Up, AvaloniaKeyMap.ToModifiers(e.KeyModifiers));
            return;
        }

        base.OnKeyUp(e);
    }

    private void OnDraw(object? sender, SkiaDrawEventArgs e)
    {
        var scene = _scene;
        if (scene is null)
        {
            return;
        }

        var canvas = e.Canvas;
        canvas.Save();
        var scaling = (float)e.Scaling;
        canvas.Scale(1 / scaling, 1 / scaling);
        OverlayRenderer.Draw(canvas, scene);
        canvas.Restore();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
        {
            if (point.Properties.IsRightButtonPressed)
            {
                // A right click cancels, as in most Windows capture tools.
                _session.OnKey(SelectorKey.Escape, Rivet.Core.Platform.KeyAction.Down, Rivet.Core.Shortcuts.KeyModifiers.None);
            }

            return;
        }

        e.Pointer.Capture(_canvas);
        _session.OnPointerPressed(ToGlobal(point.Position), Modifiers(e.KeyModifiers));
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) =>
        _session.OnPointerMoved(ToGlobal(e.GetPosition(_canvas)), Modifiers(e.KeyModifiers));

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        e.Pointer.Capture(null);
        _session.OnPointerReleased(ToGlobal(e.GetPosition(_canvas)), Modifiers(e.KeyModifiers));
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        var delta = e.Delta.Y;
        // A notched wheel reports whole steps; anything fractional is a touchpad or a free-spinning wheel.
        var continuous = Math.Abs(delta - Math.Round(delta)) > 0.001;
        _session.OnWheel(delta, continuous, Modifiers(e.KeyModifiers));
        e.Handled = true;
    }

    private PointD ToGlobal(Point dip)
    {
        var scaling = RenderScaling > 0 ? RenderScaling : Display.Scale;
        return new PointD(Display.Bounds.X + (dip.X * scaling), Display.Bounds.Y + (dip.Y * scaling));
    }

    private Rivet.Core.Shortcuts.KeyModifiers Modifiers(Avalonia.Input.KeyModifiers modifiers) =>
        AvaloniaKeyMap.ToModifiers(modifiers) | _session.HeldModifiers();
}
