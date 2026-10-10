// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Rivet.Core.Platform;
using PixelPoint = Rivet.Core.Platform.PixelPoint;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Shell;

public enum PanelCloseReason
{
    /// <summary>Esc: focus goes back to the app that was in front.</summary>
    Escape,

    /// <summary>The tray icon was clicked again: focus goes back too.</summary>
    TrayIcon,

    /// <summary>A click in another app: that app already has focus.</summary>
    OutsideClick,

    /// <summary>An action from the panel (a tool opened): no hand-back.</summary>
    Action,
}

/// <summary>
/// The tray flyout: borderless, top-most, no taskbar button, Acrylic when
/// available. Placed against the taskbar edge next to the tray icon and kept
/// inside the work area; it grows away from the taskbar as content changes.
/// </summary>
public sealed class PanelWindow : Window
{
    private const double EdgeMargin = 12;

    private readonly PanelViewModel _viewModel;
    private readonly Border _surface;
    private PixelRect? _anchorRect;
    private PixelPoint _anchorPoint;
    private bool _chromeApplied;

    public PanelWindow(PanelViewModel viewModel)
    {
        _viewModel = viewModel;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        Title = Core.App.AppIdentity.DisplayName;

        _surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new PanelView { DataContext = viewModel },
        };
        _surface.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PanelBorderBrush").ToBinding());
        Content = _surface;

        _viewModel.SectionChanged += (_, _) => Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Background);
        SizeChanged += (_, _) => Reposition();
        Opened += (_, _) => OnOpened();
    }

    /// <summary>Raised when the window wants to close (Esc, deactivation); the controller decides.</summary>
    public event EventHandler<PanelCloseReason>? CloseRequested;

    public bool UseTranslucency { get; set; } = true;

    public void PlaceNear(PixelRect? trayRect, PixelPoint clickPoint)
    {
        _anchorRect = trayRect;
        _anchorPoint = clickPoint;
        Reposition();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, PanelCloseReason.Escape);
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Keep the window for instant reopening; only app shutdown really closes it.
        if (!e.IsProgrammatic || !ReallyClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Set before Close() during app shutdown.</summary>
    public bool ReallyClose { get; set; }

    private void OnOpened()
    {
        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
            WindowInterop.SetRoundedCorners(this);
        }

        ApplySurface();
        Reposition();
    }

    /// <summary>Acrylic tint when the OS blurs behind us, otherwise an opaque surface.</summary>
    public void ApplySurface()
    {
        TransparencyLevelHint = UseTranslucency ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None] : [WindowTransparencyLevel.None];
        var translucent = UseTranslucency && ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        _surface.Bind(Border.BackgroundProperty, this.GetResourceObservable(translucent ? "PanelTintBrush" : "PanelBackgroundBrush").ToBinding());
    }

    private void Reposition()
    {
        var reference = _anchorRect is { } r ? new PixelPoint(r.X + (r.Width / 2), r.Y + (r.Height / 2)) : _anchorPoint;
        var screen = WindowInterop.ScreenAt(this, new Avalonia.PixelPoint(reference.X, reference.Y));
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        var margin = (int)Math.Round(EdgeMargin * scale);

        // Limit the scrolling content so the panel always fits the work area.
        var maxContent = Math.Max(240, (area.Height / scale) - (2 * EdgeMargin) - 150);
        if (Math.Abs(_viewModel.ContentMaxHeight - maxContent) > 0.5)
        {
            _viewModel.ContentMaxHeight = maxContent;
        }

        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int x;
        int y;
        if (area.Bottom < bounds.Bottom || (area.Y == bounds.Y && area.X == bounds.X && area.Right == bounds.Right))
        {
            // Taskbar at the bottom (or auto-hidden): above it, centred on the icon.
            x = reference.X - (width / 2);
            y = area.Bottom - height - margin;
        }
        else if (area.Y > bounds.Y)
        {
            x = reference.X - (width / 2);
            y = area.Y + margin;
        }
        else if (area.X > bounds.X)
        {
            x = area.X + margin;
            y = reference.Y - (height / 2);
        }
        else
        {
            x = area.Right - width - margin;
            y = reference.Y - (height / 2);
        }

        x = Math.Clamp(x, area.X + margin, Math.Max(area.X + margin, area.Right - width - margin));
        y = Math.Clamp(y, area.Y + margin, Math.Max(area.Y + margin, area.Bottom - height - margin));
        Position = new Avalonia.PixelPoint(x, y);
    }
}
