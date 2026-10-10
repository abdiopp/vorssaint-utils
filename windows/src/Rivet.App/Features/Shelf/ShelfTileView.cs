// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Shelf;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// One 78×88 tile (spec 07 §3.1.13): icon well 64×50 at (7,6), two-line label
/// at (3,59); piles draw two back plates, a count badge and an expand
/// chevron; a check badge when selected; a pin badge that becomes a ✕ on
/// hover. Click selects, Shift-click extends, a double click on a pile
/// expands it, moving more than 4 DIP starts a drag-out, right-click opens
/// the context menu. Tooltips after 1 s.
/// </summary>
public sealed class ShelfTileView : Border
{
    private static readonly IBrush WellBrush = new ImmutableSolidColorBrush(Color.FromArgb(18, 128, 128, 128));
    private static readonly IBrush PlateNear = new ImmutableSolidColorBrush(Color.FromArgb(26, 128, 128, 128));
    private static readonly IBrush PlateFar = new ImmutableSolidColorBrush(Color.FromArgb(16, 128, 128, 128));

    private readonly ShelfService _service;
    private readonly ShelfCardView _card;
    private readonly Button _remove;
    private readonly Border? _pinBadge;
    private PointerPressedEventArgs? _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private bool _mergeHover;

    public ShelfTileView(ShelfService service, ShelfCardView card, ShelfRow row, double scale)
    {
        _service = service;
        _card = card;
        Row = row;
        var item = row.Item;
        var selected = service.Selection.Contains(item.Id);
        Width = ShelfConstants.TileWidth;
        Height = ShelfConstants.TileHeight;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(2);
        Background = Brushes.Transparent;
        ApplySelection(selected);

        var canvas = new Canvas { Width = ShelfConstants.TileWidth - 4, Height = ShelfConstants.TileHeight - 4 };

        // Pile back plates (+3 / +6).
        if (item.IsPile)
        {
            canvas.Children.Add(At(new Border { Width = 64, Height = 50, CornerRadius = new CornerRadius(8), Background = PlateFar }, 5 + 6, 4 - 6 + 6));
            canvas.Children.Add(At(new Border { Width = 64, Height = 50, CornerRadius = new CornerRadius(8), Background = PlateNear }, 5 + 3, 4 - 3 + 3));
        }

        var well = new Border { Width = 64, Height = 50, CornerRadius = new CornerRadius(8), Background = WellBrush, ClipToBounds = true };
        well.Child = BuildIcon(item, scale);
        canvas.Children.Add(At(well, 5, 4));

        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = 10,
            Width = 72,
            MaxHeight = 26,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };
        canvas.Children.Add(At(label, 1, 57));

        if (item.IsPile)
        {
            var count = new Border
            {
                Height = 15,
                MinWidth = 22,
                CornerRadius = new CornerRadius(7.5),
                Padding = new Thickness(4, 0),
                Child = new TextBlock { Text = ShelfTree.LeafCount(item).ToString(CultureInfo.CurrentCulture), FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            count.Bind(BackgroundProperty, count.GetResourceObservable("AccentBrush"));
            canvas.Children.Add(At(count, 48, 37));
            var expanded = service.Expanded.Contains(item.Id);
            var chevron = new Button
            {
                Classes = { "icon" },
                Width = 17,
                Height = 17,
                Padding = new Thickness(0),
                Content = new SymbolIcon { Symbol = expanded ? Symbol.ChevronDown : Symbol.ChevronRight, FontSize = 10 },
            };
            var tip = L.Get(expanded ? "win.shelf.collapsePile" : "win.shelf.expandPile");
            ToolTip.SetTip(chevron, tip);
            AutomationProperties.SetName(chevron, tip);
            chevron.Click += (_, _) => service.ToggleExpanded(item.Id);
            canvas.Children.Add(At(chevron, 2, 2));
        }

        if (selected)
        {
            var check = new SymbolIcon { Symbol = Symbol.CheckmarkCircle, IconVariant = IconVariant.Filled, FontSize = 15 };
            check.Bind(SymbolIcon.ForegroundProperty, check.GetResourceObservable("AccentBrush"));
            canvas.Children.Add(At(check, 2, item.IsPile ? 20 : 2));
        }

        if (item.Pinned)
        {
            var pin = new SymbolIcon { Symbol = Symbol.Pin, IconVariant = IconVariant.Filled, FontSize = 12 };
            pin.Bind(SymbolIcon.ForegroundProperty, pin.GetResourceObservable("AccentBrush"));
            _pinBadge = new Border { Child = pin };
            canvas.Children.Add(At(_pinBadge, 56, 2));
        }

        _remove = new Button
        {
            Classes = { "icon" },
            Width = 17,
            Height = 17,
            Padding = new Thickness(0),
            IsVisible = false,
            Content = new SymbolIcon { Symbol = Symbol.DismissCircle, IconVariant = IconVariant.Filled, FontSize = 14 },
        };
        ToolTip.SetTip(_remove, L.Get("Strings.actionRemove"));
        AutomationProperties.SetName(_remove, L.Get("Strings.actionRemove"));
        _remove.Click += (_, _) => service.RemoveItem(item.Id);
        canvas.Children.Add(At(_remove, 55, 1));
        Child = canvas;

        ToolTip.SetTip(this, new TextBlock { Text = ShelfTooltips.Text(item, service.FileKind), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = ShelfConstants.TooltipMaxWidth });
        ToolTip.SetShowDelay(this, (int)ShelfConstants.TooltipDelay.TotalMilliseconds);
        AutomationProperties.SetName(this, item.Title);

        PointerEntered += (_, _) => SetHover(true);
        PointerExited += (_, _) => SetHover(false);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        ContextRequested += OnContextRequested;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnTileDragOver);
        AddHandler(DragDrop.DragOverEvent, OnTileDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetMergeHover(false));
        AddHandler(DragDrop.DropEvent, OnTileDrop);
    }

    public ShelfRow Row { get; }

    private static Control At(Control control, double x, double y)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        return control;
    }

    private Control BuildIcon(ShelfItem item, double scale)
    {
        var (image, isThumbnail) = _service.Thumbnails.Get(item, scale);
        if (image is not null)
        {
            return new Image
            {
                Source = image,
                Stretch = isThumbnail ? Stretch.UniformToFill : Stretch.Uniform,
                Margin = isThumbnail ? new Thickness(0) : new Thickness(13, 8),
            };
        }

        var symbol = item.Kind switch
        {
            ShelfItemKind.Text => Symbol.TextT,
            ShelfItemKind.Link => Symbol.Link,
            ShelfItemKind.Batch => Symbol.Stack,
            _ when item.Path is { } p && Directory.Exists(p) => Symbol.Folder,
            _ when item.IsImage => Symbol.Image,
            _ => Symbol.Document,
        };
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("TextSecondaryBrush"));
        return icon;
    }

    private void ApplySelection(bool selected)
    {
        if (selected || _mergeHover)
        {
            this.Bind(BorderBrushProperty, this.GetResourceObservable("AccentBrush"));
            if (selected)
            {
                this.Bind(BackgroundProperty, this.GetResourceObservable("AccentSoftBrush"));
            }
        }
        else
        {
            BorderBrush = Brushes.Transparent;
            Background = Brushes.Transparent;
        }
    }

    private void SetHover(bool hover)
    {
        _remove.IsVisible = hover;
        if (_pinBadge is not null)
        {
            _pinBadge.IsVisible = !hover;
        }
    }

    private void SetMergeHover(bool hover)
    {
        _mergeHover = hover;
        ApplySelection(_service.Selection.Contains(Row.Item.Id));
    }

    // ── Mouse ───────────────────────────────────────────────────────────

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        e.Handled = true;
        _pressed = e;
        _pressPoint = e.GetPosition(this);
        _dragging = false;
        _card.Focus();
        ToolTip.SetIsOpen(this, false);
        if (e.ClickCount >= 2 && Row.Item.IsPile)
        {
            _pressed = null;
            _service.ToggleExpanded(Row.Item.Id);
        }
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _dragging)
        {
            return;
        }

        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y)) <= ShelfConstants.DragStartThreshold)
        {
            return;
        }

        _dragging = true;
        _pressed = null;
        await StartDragAsync(pressed).ConfigureAwait(true);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressed is null || _dragging)
        {
            _pressed = null;
            return;
        }

        _pressed = null;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _service.Selection.ShiftClick(Row.Item.Id, _card.VisibleOrder());
        }
        else
        {
            _service.Selection.Click(Row.Item.Id);
        }
    }

    /// <summary>Drag-out of the tile or the selection (spec 07 §3.1.14).</summary>
    private async Task StartDragAsync(PointerPressedEventArgs pressed)
    {
        var leaves = _service.PrepareDragOut(Row.Item.Id);
        if (leaves is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var data = new DataTransfer();
        foreach (var leaf in leaves)
        {
            switch (leaf.Kind)
            {
                case ShelfItemKind.File when leaf.Path is { } path:
                    IStorageItem? storage = Directory.Exists(path)
                        ? await top.StorageProvider.TryGetFolderFromPathAsync(path).ConfigureAwait(true)
                        : await top.StorageProvider.TryGetFileFromPathAsync(path).ConfigureAwait(true);
                    if (storage is not null)
                    {
                        data.Add(DataTransferItem.CreateFile(storage));
                    }

                    break;
                case ShelfItemKind.Text when leaf.Text is { } text:
                    data.Add(DataTransferItem.CreateText(text));
                    break;
                case ShelfItemKind.Link when leaf.Url is { } url:
                    data.Add(DataTransferItem.CreateText(url));
                    break;
            }
        }

        // The ids of the grabbed tiles, for merges inside the shelf (in-process only).
        var grabbed = _service.Selection.Contains(Row.Item.Id) ? _service.Selection.Selected.ToList() : [Row.Item.Id];
        var marker = new DataTransferItem();
        marker.Set(ShelfCardView.InternalFormat, string.Join(',', grabbed));
        data.Add(marker);

        var effects = DragDropEffects.Copy | (_service.AllowsMove(leaves) ? DragDropEffects.Move : DragDropEffects.None);
        var surfacePinned = TopLevel.GetTopLevel(this) is ShelfCardWindow { IsPinned: true };
        _card.BeginInteraction();
        _service.BeginInternalDrag();
        DragDropEffects result;
        LastDropWasMerge = false;
        try
        {
            result = await DragDrop.DoDragDropAsync(pressed, data, effects).ConfigureAwait(true);
        }
        finally
        {
            _card.EndInteraction();
        }

        // Explorer's optimized move reports "none" although the file moved: a vanished source counts as accepted.
        var accepted = result != DragDropEffects.None
                       || leaves.Any(l => l.Kind == ShelfItemKind.File && l.Path is { } p && !File.Exists(p) && !Directory.Exists(p));
        _service.CompleteDragOut(leaves, accepted, LastDropWasMerge, _card.Surface, surfacePinned);
        LastDropWasMerge = false;
    }

    /// <summary>Set by the tile that received an internal drop, read by the drag source.</summary>
    internal static bool LastDropWasMerge { get; set; }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        ToolTip.SetIsOpen(this, false);
        var scope = ShelfTree.Scope(_service.Store.Items, Row.Item.Id, _service.Selection.Selected);
        var ids = _service.Selection.Contains(Row.Item.Id) ? _service.Selection.Selected.ToList() : [Row.Item.Id];
        var pinned = ids.Select(id => ShelfTree.Find(_service.Store.Items, id)).OfType<ShelfItem>().All(i => i.Pinned);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(pinned ? "Strings.shelfActionUnpin" : "Strings.shelfActionPin", Symbol.Pin, () => _service.TogglePin(ids)));
        if (scope.Any(l => l.Kind == ShelfItemKind.File))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Strings.shelfActionOpen", Symbol.Open, () => _service.Open(scope)));
            menu.Items.Add(MenuItem("Strings.shelfActionOpenWith", Symbol.AppGeneric, () => _service.OpenWith(scope)));
            if (_service.CanEdit(scope))
            {
                menu.Items.Add(MenuItem("Strings.menuEdit", Symbol.Edit, () => _service.Edit(scope)));
            }

            menu.Items.Add(MenuItem("Strings.shelfActionShare", Symbol.Share, () => _service.Share(scope, Rivet.App.Shell.WindowInterop.Handle(TopLevel.GetTopLevel(this)!))));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("win.shelf.showInExplorer", Symbol.FolderOpen, () => _service.Reveal(scope)));
        }

        ContextMenu = menu;
        menu.Open(this);
    }

    private static MenuItem MenuItem(string key, Symbol icon, Action action)
    {
        var item = new MenuItem { Header = L.Get(key), Icon = new SymbolIcon { Symbol = icon, FontSize = 14 } };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Drops onto the tile ─────────────────────────────────────────────

    private IReadOnlyList<Guid> InternalIds(IDataTransfer data) =>
        data.TryGetValue(ShelfCardView.InternalFormat) is { } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList()
            : [];

    private bool IsOwnOrDescendant(IReadOnlyList<Guid> sources)
    {
        var self = Row.Item;
        var family = ShelfTree.AllItems([self]).Select(i => i.Id).ToHashSet();
        return sources.Any(family.Contains);
    }

    private void OnTileDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var data = e.DataTransfer;
        if (data.Contains(ShelfCardView.InternalFormat))
        {
            var sources = InternalIds(data);
            var ok = sources.Count > 0 && !IsOwnOrDescendant(sources);
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            SetMergeHover(ok);
            return;
        }

        var accept = _service.IsAvailable && ShelfDropReader.CanAccept(data);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetMergeHover(accept);
    }

    private void OnTileDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        SetMergeHover(false);
        var data = e.DataTransfer;
        if (data.Contains(ShelfCardView.InternalFormat))
        {
            var sources = InternalIds(data);
            if (sources.Count > 0 && !IsOwnOrDescendant(sources) && _service.MoveInto(Row.Item.Id, sources))
            {
                LastDropWasMerge = true;
                e.DragEffects = DragDropEffects.Move;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }

            return;
        }

        e.DragEffects = _service.Drop(ShelfDropReader.Read(data), Row.Item.Id, _card.Surface) ? DragDropEffects.Copy : DragDropEffects.None;
    }
}
