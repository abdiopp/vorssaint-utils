// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Imaging.RadialMenu;

namespace Rivet.App.Features.RadialMenu;

/// <summary>
/// The visual editor of one wheel level (spec 07 §3.2.13): a 330 DIP dark
/// stage with a 250 DIP wheel. Hovering a slice highlights its chip and shows
/// its name in the hub; a click (≤ 6 DIP of movement) edits it; a drag (> 6
/// DIP) swaps it with the slot under the pointer, picked by angle; right-click
/// offers Edit, "Edit actions" for submenus and Remove; the hub goes back
/// inside a submenu, or adds an action while the level is empty.
/// </summary>
public sealed class RadialCanvasEditor : Border
{
    private const double Side = 300;
    private readonly RadialPresenter _presenter;
    private readonly RadialWheelView _wheel;
    private readonly Canvas _overlay = new() { Width = Side, Height = Side, Background = Brushes.Transparent };
    private readonly Border _dragGhost;
    private IReadOnlyList<RadialItem> _items = [];
    private bool _inSubmenu;
    private string? _submenuName;
    private Point? _press;
    private int? _pressIndex;
    private bool _dragging;
    private int? _hover;

    public RadialCanvasEditor(RadialPresenter presenter)
    {
        _presenter = presenter;
        Height = RadialGeometry.CanvasStageHeight;
        CornerRadius = new CornerRadius(12);
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromRgb(30, 33, 40), 0), new GradientStop(Color.FromRgb(18, 20, 25), 1) },
        };
        ClipToBounds = true;
        _wheel = new RadialWheelView(presenter, RadialWheelSize.Canvas, Side) { ReduceMotion = true, IsHitTestVisible = false };
        _dragGhost = new Border
        {
            Width = RadialGeometry.CanvasChip,
            Height = RadialGeometry.CanvasChip,
            CornerRadius = new CornerRadius(RadialGeometry.CanvasChip / 2),
            Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            IsVisible = false,
            IsHitTestVisible = false,
        };
        _overlay.Children.Add(_dragGhost);
        var stage = new Grid { Width = Side, Height = Side, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { _wheel, _overlay } };
        Child = stage;
        _overlay.PointerMoved += OnMoved;
        _overlay.PointerExited += (_, _) => SetHover(null);
        _overlay.PointerPressed += OnPressed;
        _overlay.PointerReleased += OnReleased;
        AutomationProperties.SetName(this, L.Get("radialMenu.actionsHeader"));
        ToolTip.SetTip(this, L.Get("radialMenu.canvasHint"));
        ToolTip.SetShowDelay(this, 1500);
    }

    /// <summary>A slot was clicked (edit it).</summary>
    public event EventHandler<int>? EditRequested;

    /// <summary>"Edit actions" on a submenu.</summary>
    public event EventHandler<int>? OpenSubmenuRequested;

    public event EventHandler<int>? RemoveRequested;

    /// <summary>Two slots swapped by a drag.</summary>
    public event EventHandler<(int From, int To)>? SwapRequested;

    /// <summary>The hub was clicked: back inside a submenu, add when empty.</summary>
    public event EventHandler? HubClicked;

    public RadialWheelView Wheel => _wheel;

    public void SetLevel(IReadOnlyList<RadialItem> items, uint color, bool inSubmenu, string? submenuName)
    {
        _items = items;
        _inSubmenu = inSubmenu;
        _submenuName = submenuName;
        _wheel.SetContent(items, color, dark: true, NowPlayingState.Loading);
        _hover = null;
        UpdateHub();
        _wheel.Settle();
    }

    private (double Dx, double Dy) Offset(Point p) => (p.X - (Side / 2), p.Y - (Side / 2));

    private int? SliceAt(Point p)
    {
        var (dx, dy) = Offset(p);
        return _items.Count == 0 ? null : RadialGeometry.Highlight(dx, -dy, _items.Count, RadialGeometry.CanvasDeadZone);
    }

    private bool OnHub(Point p)
    {
        var (dx, dy) = Offset(p);
        return Math.Sqrt((dx * dx) + (dy * dy)) <= RadialGeometry.CanvasHub / 2;
    }

    private void SetHover(int? index)
    {
        if (index == _hover)
        {
            return;
        }

        _hover = index;
        UpdateHub();
        _wheel.Settle();
    }

    private void UpdateHub()
    {
        var label = _hover is { } i && i < _items.Count ? _presenter.Label(_items[i], NowPlayingState.Loading) : null;
        if (label is null && _items.Count == 0 && !_inSubmenu)
        {
            label = L.Get("radialMenu.addButton");
        }

        _wheel.SetHighlight(_hover, label, _inSubmenu ? _submenuName ?? string.Empty : null);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_press is { } start && _pressIndex is not null)
        {
            var d = p - start;
            if (!_dragging && Math.Sqrt((d.X * d.X) + (d.Y * d.Y)) > RadialGeometry.CanvasDragThreshold)
            {
                _dragging = true;
                _dragGhost.IsVisible = true;
            }

            if (_dragging)
            {
                Canvas.SetLeft(_dragGhost, p.X - (RadialGeometry.CanvasChip / 2));
                Canvas.SetTop(_dragGhost, p.Y - (RadialGeometry.CanvasChip / 2));
                SetHover(SliceAt(p));
                return;
            }
        }

        SetHover(OnHub(p) ? null : SliceAt(p));
        Cursor = _hover is not null || (OnHub(p) && (_inSubmenu || _items.Count == 0)) ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        var point = e.GetCurrentPoint(_overlay);
        if (point.Properties.IsRightButtonPressed)
        {
            if (SliceAt(p) is { } index && !OnHub(p))
            {
                ShowMenu(index);
            }

            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _press = p;
        _pressIndex = OnHub(p) ? null : SliceAt(p);
        _dragging = false;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        var start = _press;
        var from = _pressIndex;
        var dragged = _dragging;
        _press = null;
        _pressIndex = null;
        _dragging = false;
        _dragGhost.IsVisible = false;
        if (start is null)
        {
            return;
        }

        if (dragged && from is { } source)
        {
            if (SliceAt(p) is { } target && target != source)
            {
                SwapRequested?.Invoke(this, (source, target));
            }

            return;
        }

        if (OnHub(start.Value))
        {
            HubClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (from is { } index)
        {
            EditRequested?.Invoke(this, index);
        }
    }

    private void ShowMenu(int index)
    {
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = L.Get("Strings.menuEdit"), Icon = new SymbolIcon { Symbol = Symbol.Edit, FontSize = 14 } };
        edit.Click += (_, _) => EditRequested?.Invoke(this, index);
        menu.Items.Add(edit);
        if (index < _items.Count && _items[index].IsSubmenu)
        {
            var open = new MenuItem { Header = L.Get("radialMenu.editActionsButton"), Icon = new SymbolIcon { Symbol = Symbol.Grid, FontSize = 14 } };
            open.Click += (_, _) => OpenSubmenuRequested?.Invoke(this, index);
            menu.Items.Add(open);
        }

        var remove = new MenuItem { Header = L.Get("radialMenu.deleteButton"), Icon = new SymbolIcon { Symbol = Symbol.Delete, FontSize = 14 } };
        remove.Click += (_, _) => RemoveRequested?.Invoke(this, index);
        menu.Items.Add(remove);
        ContextMenu = menu;
        menu.Open(this);
    }
}
