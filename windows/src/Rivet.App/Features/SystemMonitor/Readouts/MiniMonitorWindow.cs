// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.SystemMonitor;
using PixelPoint = Avalonia.PixelPoint;

namespace Rivet.App.Features.SystemMonitor.Readouts;

/// <summary>
/// The Windows substitute for the macOS menu bar readouts: a small,
/// always-on-top strip of the pinned readouts. Drag it anywhere (the position
/// is remembered per PC); click a readout to open its details in the panel;
/// right-click for options. With click-through on, input passes to the window
/// below and the strip can only be moved after switching that off.
/// </summary>
public sealed class MiniMonitorWindow : Window
{
    private const double DragThreshold = 4;

    private readonly ISettingsStore _settings;
    private readonly ReadoutStrip _strip = new();
    private readonly Border _surface;
    private PixelPoint _pressScreen;
    private PixelPoint _pressWindow;
    private bool _pressed;
    private bool _dragging;
    private bool _chromeApplied;
    private bool _clickThrough;

    public MiniMonitorWindow(ISettingsStore settings)
    {
        _settings = settings;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("win.systemMonitor.miniMonitorTitle");
        _surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 4),
            Child = _strip,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        _surface.Bind(Border.BackgroundProperty, this.GetResourceObservable("HudBackgroundBrush").ToBinding());
        _surface.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PanelBorderBrush").ToBinding());
        Content = _surface;
        _surface.ContextMenu = BuildMenu();
    }

    /// <summary>A readout was clicked (not dragged).</summary>
    public event EventHandler<ReadoutBlock>? BlockClicked;

    /// <summary>The user chose "Open the panel" from the context menu.</summary>
    public event EventHandler? OpenPanelRequested;

    public event EventHandler? OpenSettingsRequested;

    public void Update(IReadOnlyList<ReadoutBlock> blocks, string? countdown) => _strip.Update(blocks, countdown, _settings);

    /// <summary>Shows the strip at its remembered place (or above the taskbar, bottom right).</summary>
    public void ShowAtSavedPosition()
    {
        if (!IsVisible)
        {
            Show();
        }

        ApplyChrome();
        Position = SavedOrDefaultPosition();
    }

    public void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        if (!_chromeApplied)
        {
            return;
        }

        var chrome = AppHost.Current?.Services.GetService<IWindowChrome>();
        var handle = WindowInterop.Handle(this);
        if (chrome is null || handle == 0)
        {
            return;
        }

        if (clickThrough)
        {
            chrome.Apply(handle, WindowChromeOptions.ClickThrough);
        }
        else
        {
            chrome.Remove(handle, WindowChromeOptions.ClickThrough);
        }
    }

    private void ApplyChrome()
    {
        if (_chromeApplied)
        {
            return;
        }

        _chromeApplied = true;
        var options = WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.Topmost;
        if (_clickThrough)
        {
            options |= WindowChromeOptions.ClickThrough;
        }

        WindowInterop.ApplyChrome(this, options);
        WindowInterop.SetRoundedCorners(this);
    }

    private PixelPoint SavedOrDefaultPosition()
    {
        var x = _settings.Get(MonitorSettings.MiniMonitorX);
        var y = _settings.Get(MonitorSettings.MiniMonitorY);
        var width = (int)Math.Ceiling(Math.Max(Bounds.Width, 120) * (Screens.Primary?.Scaling ?? 1));
        var height = (int)Math.Ceiling(Math.Max(Bounds.Height, 30) * (Screens.Primary?.Scaling ?? 1));
        if (x != int.MinValue && y != int.MinValue)
        {
            // Keep it reachable: the saved point must still be on a screen (monitors change).
            var screen = Screens.ScreenFromPoint(new PixelPoint(x + (width / 2), y + (height / 2)));
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                return new PixelPoint(Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)), Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
            }
        }

        var primary = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (primary is null)
        {
            return new PixelPoint(40, 40);
        }

        var margin = (int)(12 * primary.Scaling);
        var work = primary.WorkingArea;
        return new PixelPoint(work.Right - width - margin, work.Bottom - height - margin);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = true;
        _dragging = false;
        _pressScreen = this.PointToScreen(point.Position);
        _pressWindow = Position;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
        {
            return;
        }

        var current = this.PointToScreen(e.GetPosition(this));
        var dx = current.X - _pressScreen.X;
        var dy = current.Y - _pressScreen.Y;
        var scale = RenderScaling;
        if (!_dragging && Math.Sqrt((dx * dx) + (dy * dy)) < DragThreshold * scale)
        {
            return;
        }

        _dragging = true;
        Position = new PixelPoint(_pressWindow.X + dx, _pressWindow.Y + dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        e.Pointer.Capture(null);
        if (_dragging)
        {
            _dragging = false;
            _settings.Set(MonitorSettings.MiniMonitorX, Position.X);
            _settings.Set(MonitorSettings.MiniMonitorY, Position.Y);
            return;
        }

        var hit = _strip.Items.FirstOrDefault(i => i.View.IsPointerOver);
        if (hit.Block is not null)
        {
            BlockClicked?.Invoke(this, hit.Block);
        }
        else
        {
            OpenPanelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = L.Get("win.systemMonitor.miniMonitorOpenPanel") };
        open.Click += (_, _) => OpenPanelRequested?.Invoke(this, EventArgs.Empty);
        var settings = new MenuItem { Header = L.Get("Strings.menuSettings") };
        settings.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        var clickThrough = new MenuItem { Header = L.Get("win.systemMonitor.miniMonitorClickThrough") };
        clickThrough.Click += (_, _) => _settings.Set(MonitorSettings.MiniMonitorClickThrough, true);
        var hide = new MenuItem { Header = L.Get("win.systemMonitor.miniMonitorHide") };
        hide.Click += (_, _) => _settings.Set(MonitorSettings.MiniMonitorEnabled, false);
        return new ContextMenu { Items = { open, settings, new Separator(), clickThrough, hide } };
    }
}
