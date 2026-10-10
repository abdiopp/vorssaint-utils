// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.App.Shell;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Clipboard;

/// <summary>Why a floating panel closed: Esc and explicit closes give focus back, clicks elsewhere do not.</summary>
public enum FloatingCloseReason
{
    Escape,
    OutsideClick,
    Action,
}

/// <summary>
/// Base of the app's floating panels (clipboard history, snippet library,
/// Command Bar, quick panel; spec 06 §3.1.6): borderless, top-most, no
/// taskbar button, a rounded HUD surface, kept alive between openings for
/// instant reuse. Windows has no non-activating key panels, so the panel
/// takes focus on show and hands it back to the app that was in front (§7.1).
/// </summary>
public abstract class FloatingPanel : Window
{
    private bool _chromeApplied;
    private bool _closingSilently;

    protected FloatingPanel()
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Surface = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Margin = new Thickness(10),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.FromArgb(70, 0, 0, 0) }),
        };
        Surface.Bind(Border.BackgroundProperty, this.GetResourceObservable("PanelBackgroundBrush").ToBinding());
        Surface.Bind(Border.BorderBrushProperty, this.GetResourceObservable("PanelBorderBrush").ToBinding());
        base.Content = Surface;
        Deactivated += (_, _) => OnDeactivatedCore();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The rounded surface; subclasses put their layout in <see cref="Border.Child"/>.</summary>
    protected Border Surface { get; }

    /// <summary>Raised after the panel hid, with the reason.</summary>
    public event EventHandler<FloatingCloseReason>? Dismissed;

    /// <summary>Whether a click in another app closes the panel (off while a dialog or editor needs it).</summary>
    protected virtual bool DismissOnDeactivate => true;

    public bool IsShowing => IsVisible;

    /// <summary>Shows, applies the tool-window chrome once, takes focus and positions the panel.</summary>
    public void Present()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
            WindowInterop.SetRoundedCorners(this);
        }

        Place();
        Activate();
        AppHost.Current?.Services.GetService<IWindowChrome>()?.BringToFront(WindowInterop.Handle(this));
        Dispatcher.UIThread.Post(() =>
        {
            Place();
            OnPresented();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the panel for a reason (the subclass decides about focus).</summary>
    public void Dismiss(FloatingCloseReason reason)
    {
        if (!IsVisible)
        {
            return;
        }

        _closingSilently = true;
        try
        {
            Hide();
        }
        finally
        {
            _closingSilently = false;
        }

        OnDismissed(reason);
        Dismissed?.Invoke(this, reason);
    }

    /// <summary>Positions the panel (called on show and when the content size changes).</summary>
    protected abstract void Place();

    protected virtual void OnPresented()
    {
    }

    protected virtual void OnDismissed(FloatingCloseReason reason)
    {
    }

    /// <summary>Keys before the focused control sees them (arrows, Enter, Esc go to the panel first).</summary>
    protected virtual void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 hides the panel instead of closing the app.
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            Dismiss(FloatingCloseReason.Escape);
        }

        base.OnClosing(e);
    }

    private void OnDeactivatedCore()
    {
        if (_closingSilently || !IsVisible || !DismissOnDeactivate)
        {
            return;
        }

        // Let a dialog the panel opened (which deactivates it) settle first.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && !IsActive && DismissOnDeactivate && !OwnsActiveWindow())
            {
                Dismiss(FloatingCloseReason.OutsideClick);
            }
        }, DispatcherPriority.Background);
    }

    private bool OwnsActiveWindow() =>
        OwnedWindows.Any(w => w.IsActive) ||
        (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
         && desktop.Windows.Any(w => w.IsActive && w.Owner == this));

    // ── Placement helpers (Position is in physical pixels, sizes in DIPs) ──

    /// <summary>The work area of the screen under the pointer, in physical pixels, with its scale.</summary>
    protected (Avalonia.PixelRect Area, double Scale) PointerScreen()
    {
        var screen = WindowInterop.ScreenAtCursor(this);
        return screen is null ? (new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0) : (screen.WorkingArea, screen.Scaling);
    }

    /// <summary>Centered horizontally, 38 % of the free space above it (the library and quick panel rule).</summary>
    protected void PlaceUpperCenter()
    {
        var (area, scale) = PointerScreen();
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var x = area.X + ((area.Width - width) / 2);
        var y = area.Y + (int)((area.Height - height) * 0.38);
        Position = Clamp(new Avalonia.PixelPoint(x, y), width, height, area, scale);
    }

    protected static Avalonia.PixelPoint Clamp(Avalonia.PixelPoint point, int width, int height, Avalonia.PixelRect area, double scale)
    {
        var margin = (int)(16 * scale);
        var x = Math.Clamp(point.X, area.X + margin - (int)(10 * scale), Math.Max(area.X + margin, area.Right - width - margin + (int)(10 * scale)));
        var y = Math.Clamp(point.Y, area.Y + margin - (int)(10 * scale), Math.Max(area.Y + margin, area.Bottom - height - margin + (int)(10 * scale)));
        return new Avalonia.PixelPoint(x, y);
    }
}
