// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.App.Shell;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;
using PixelPoint = Rivet.Core.Platform.PixelPoint;
using PixelRect = Rivet.Core.Platform.PixelRect;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// The floating card window (classic, or the docked shelf expanded): borderless,
/// transparent, topmost, no taskbar button, shown without taking focus (a click
/// inside makes it key so Esc and Ctrl+A work). It sizes to its content with
/// the top-left corner fixed. The classic card auto-hides only while nothing
/// holds it open (pin, items, pointer inside, drop hover, interaction, peek):
/// after 5 s it fades out over 0.22 s.
/// </summary>
public sealed class ShelfCardWindow : Window
{
    /// <summary>Room around the card for its shadow.</summary>
    public const double ShadowMargin = 12;

    private readonly ShelfService _service;
    private readonly DispatcherTimer _autoHide;
    private DateTime? _idleSince;
    private DispatcherTimer? _fade;
    private DispatcherTimer? _slide;
    private bool _chromeApplied;

    public ShelfCardWindow(ShelfService service, ShelfSurface surface)
    {
        _service = service;
        Surface = surface;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("Strings.shelfName");
        Card = new ShelfCardView(service, surface) { Margin = new Thickness(ShadowMargin) };
        Card.PinToggled += (_, _) =>
        {
            IsPinned = !IsPinned;
            Card.SetPinned(IsPinned);
        };
        Content = Card;
        service.StateChanged += OnStateChanged;
        service.Thumbnails.Loaded += (_, _) => Card.Refresh();
        _autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        _autoHide.Tick += (_, _) => EvaluateAutoHide();
    }

    public ShelfCardView Card { get; }

    public ShelfSurface Surface { get; }

    /// <summary>Keep-open pin (session only; hiding resets it).</summary>
    public bool IsPinned { get; private set; }

    /// <summary>Shown partly off-screen at an edge during a drag (no auto-hide).</summary>
    public bool IsPeeking { get; set; }

    /// <summary>Opened by a gesture during a drag that has not dropped anything yet.</summary>
    public bool IsSpeculative { get; private set; }

    public void MarkSpeculative() => IsSpeculative = true;

    public void MarkSettled() => IsSpeculative = false;

    /// <summary>Shows the card at the position <paramref name="place"/> computes from its pixel size.</summary>
    public void ShowAt(Func<(int Width, int Height), PixelPoint> place)
    {
        CancelFade();
        Card.Refresh();
        var scale = ScaleHint();
        Card.Measure(Size.Infinity);
        var desired = Card.DesiredSize;
        var size = ((int)Math.Ceiling((desired.Width + (2 * ShadowMargin)) * scale), (int)Math.Ceiling((desired.Height + (2 * ShadowMargin)) * scale));
        var point = place(size);

        // The card hangs inside the shadow margin; offset so the card itself lands on the computed point.
        var margin = (int)Math.Round(ShadowMargin * scale);
        Position = new Avalonia.PixelPoint(point.X - margin, point.Y - margin);
        Opacity = 1;
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
        }

        _idleSince = null;
        if (Surface == ShelfSurface.Classic)
        {
            _autoHide.Start();
        }
    }

    /// <summary>Hide: reset auto-hide, unpin, order out.</summary>
    public void HideCard()
    {
        _autoHide.Stop();
        CancelFade();
        _slide?.Stop();
        IsPinned = false;
        IsPeeking = false;
        IsSpeculative = false;
        Card.SetPinned(false);
        ToolTip.SetIsOpen(Card, false);
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>The card's frame (without the shadow margin) in physical pixels, or null while hidden.</summary>
    public PixelRect? FrameInPixels()
    {
        if (!IsVisible)
        {
            return null;
        }

        var scale = RenderScaling;
        var margin = (int)Math.Round(ShadowMargin * scale);
        return new PixelRect(Position.X + margin, Position.Y + margin, (int)Math.Round(Card.Bounds.Width * scale), (int)Math.Round(Card.Bounds.Height * scale));
    }

    /// <summary>Slides the card to <paramref name="target"/> (card coordinates) over 0.2 s.</summary>
    public void SlideTo(PixelPoint target)
    {
        var margin = (int)Math.Round(ShadowMargin * RenderScaling);
        var from = Position;
        var to = new Avalonia.PixelPoint(target.X - margin, target.Y - margin);
        var started = DateTime.UtcNow;
        _slide?.Stop();
        _slide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slide.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalSeconds / 0.2);
            var eased = 1 - Math.Pow(1 - t, 3);
            Position = new Avalonia.PixelPoint((int)Math.Round(from.X + ((to.X - from.X) * eased)), (int)Math.Round(from.Y + ((to.Y - from.Y) * eased)));
            if (t >= 1)
            {
                _slide?.Stop();
            }
        };
        _slide.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // The first click makes the card key so Esc and Ctrl+A reach it.
        Activate();
        Card.Focus();
        base.OnPointerPressed(e);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        Card.Refresh();
        if (!_service.Store.IsEmpty)
        {
            CancelFade();
        }
    }

    /// <summary>Held open while pinned, non-empty, hovered, hovered by a drop, during an interaction or a peek.</summary>
    private bool IsHeld() =>
        IsPinned || !_service.Store.IsEmpty || Card.IsPointerOver || Card.DropHover || Card.InteractionActive || IsPeeking || _service.InternalDragActive;

    private void EvaluateAutoHide()
    {
        if (!IsVisible || Surface != ShelfSurface.Classic)
        {
            _autoHide.Stop();
            return;
        }

        if (IsHeld())
        {
            _idleSince = null;
            CancelFade();
            return;
        }

        _idleSince ??= DateTime.UtcNow;
        if (_fade is null && DateTime.UtcNow - _idleSince.Value >= ShelfConstants.AutoHideDelay)
        {
            StartFade();
        }
    }

    private void StartFade()
    {
        var started = DateTime.UtcNow;
        _fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        _fade.Tick += (_, _) =>
        {
            if (IsHeld())
            {
                CancelFade();
                return;
            }

            var t = (DateTime.UtcNow - started).TotalSeconds / ShelfConstants.FadeDuration.TotalSeconds;
            if (t >= 1)
            {
                CancelFade();
                _service.HideClassic();
                return;
            }

            Opacity = 1 - t;
        };
        _fade.Start();
    }

    private void CancelFade()
    {
        _fade?.Stop();
        _fade = null;
        Opacity = 1;
    }

    private double ScaleHint()
    {
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        return screen?.Scaling ?? (RenderScaling > 0 ? RenderScaling : 1);
    }
}

/// <summary>
/// The collapsed docked shelf (spec 07 §3.1.8): near the tray icon a pill
/// (brand mark, count, down chevron; a green check for 0.9 s after a drop),
/// at the top centre a badge (mark, "Shelf", count capsule, chevron). Click
/// expands. A drag hovering it expands it at once (a Windows addition: the
/// pointer dwell needs the drag-image hint, a hovering OLE drag is proof).
/// </summary>
public sealed class ShelfPillWindow : Window
{
    private const double Margin8 = 8;
    private readonly ShelfService _service;
    private readonly Border _plate;
    private readonly SymbolIcon _mark = new() { Symbol = Symbol.Archive, FontSize = 15, Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { Text = L.Get("Strings.shelfName"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, FontFeatures = FontFeatureCollection.Parse("tnum"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _countCapsule;
    private readonly SymbolIcon _chevron = new() { Symbol = Symbol.ChevronDown, FontSize = 9, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
    private bool _chromeApplied;
    private bool _dropHover;

    public ShelfPillWindow(ShelfService service)
    {
        _service = service;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = L.Get("Strings.shelfName");

        _countCapsule = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(6, 0), Child = _count, VerticalAlignment = VerticalAlignment.Center };
        _plate = new Border
        {
            Margin = new Thickness(Margin8),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = BoxShadows.Parse("0 3 7 0 #29000000"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { _mark, _label, _countCapsule, _chevron } },
        };
        _plate.Bind(Border.BackgroundProperty, _plate.GetResourceObservable("PanelBackgroundBrush"));
        _plate.Bind(Border.BorderBrushProperty, _plate.GetResourceObservable("PanelBorderBrush"));
        ToolTip.SetTip(_plate, L.Get("Strings.shelfOpenNow"));
        AutomationProperties.SetName(_plate, L.Get("Strings.shelfName"));
        _plate.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_plate).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _service.ExpandDocked();
            }
        };
        _plate.PointerEntered += (_, _) => _chevron.Opacity = 1;
        _plate.PointerExited += (_, _) => _chevron.Opacity = _dropHover ? 1 : 0.55;
        Content = _plate;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHover(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public void Refresh(ShelfDockPlacement placement, int count, bool caught)
    {
        var badge = placement == ShelfDockPlacement.TopCenter;
        _label.IsVisible = badge;
        _mark.Symbol = caught ? Symbol.CheckmarkCircle : Symbol.Archive;
        _mark.IconVariant = caught ? IconVariant.Filled : IconVariant.Regular;
        _mark.FontSize = badge ? 18 : 15;
        if (caught)
        {
            _mark.Bind(SymbolIcon.ForegroundProperty, _mark.GetResourceObservable("SuccessBrush"));
        }
        else
        {
            _mark.ClearValue(SymbolIcon.ForegroundProperty);
        }

        _count.Text = count.ToString(CultureInfo.CurrentCulture);
        _countCapsule.IsVisible = count > 0;
        if (badge)
        {
            _countCapsule.Bind(Border.BackgroundProperty, _countCapsule.GetResourceObservable("ChipBrush"));
        }
        else
        {
            _countCapsule.Background = Brushes.Transparent;
        }

        _plate.Padding = new Thickness(count > 0 ? 12 : 11, 8);
        AutomationProperties.SetHelpText(_plate, count.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>Shows the pill at the docked frame computed for its pixel size (the 8 DIP margin included).</summary>
    public void ShowDocked(Func<(int Width, int Height), PixelRect> frame)
    {
        var scale = Screens.Primary?.Scaling ?? 1;
        _plate.Measure(Size.Infinity);
        var size = _plate.DesiredSize;
        var rect = frame(((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)));
        var margin = (int)Math.Round(Margin8 * scale);
        Position = new Avalonia.PixelPoint(rect.X - margin, rect.Y - margin);
        if (!IsVisible)
        {
            Show();
        }

        if (!_chromeApplied)
        {
            _chromeApplied = true;
            WindowInterop.ApplyChrome(this, WindowChromeOptions.ToolWindow | WindowChromeOptions.NoActivate | WindowChromeOptions.Topmost);
        }
    }

    public PixelRect? FrameInPixels()
    {
        if (!IsVisible)
        {
            return null;
        }

        var scale = RenderScaling;
        var margin = (int)Math.Round(Margin8 * scale);
        return new PixelRect(Position.X + margin, Position.Y + margin, (int)Math.Round(_plate.Bounds.Width * scale), (int)Math.Round(_plate.Bounds.Height * scale));
    }

    public Control Plate => _plate;

    private void SetDropHover(bool hover)
    {
        _dropHover = hover;
        _plate.BorderThickness = new Thickness(hover ? 2 : 1);
        _plate.Bind(Border.BorderBrushProperty, _plate.GetResourceObservable(hover ? "AccentBrush" : "PanelBorderBrush"));
        _plate.RenderTransform = hover ? new ScaleTransform(1.06, 1.06) : null;
        _chevron.Opacity = hover ? 1 : 0.55;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accept = _service.IsAvailable && !e.DataTransfer.Contains(ShelfCardView.InternalFormat) && ShelfDropReader.CanAccept(e.DataTransfer);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHover(accept);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHover(false);
        if (e.DataTransfer.Contains(ShelfCardView.InternalFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = _service.Drop(ShelfDropReader.Read(e.DataTransfer), null, ShelfSurface.Docked) ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
