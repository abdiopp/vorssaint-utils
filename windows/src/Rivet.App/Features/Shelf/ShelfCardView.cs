// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Rivet.Core.Localization;
using Rivet.Core.Modules.Shelf;
using Path = System.IO.Path;

namespace Rivet.App.Features.Shelf;

/// <summary>
/// The shelf card (spec 07 §3.1.7, §3.1.8): header (glyph, title or
/// "%d selected", leaf-count badge, keep-open pin, close or collapse), a
/// fixed-height scrolling tile grid (3 columns of 78×88 tiles in the 304 DIP
/// card) or the dashed empty state, and the footer (hint, Share, Clear all /
/// Remove selected). The whole card accepts drops; tiles accept merges.
/// </summary>
public sealed class ShelfCardView : UserControl
{
    /// <summary>In-process format carrying the ids of a tile drag (never leaves the app).</summary>
    public static readonly DataFormat<string> InternalFormat = DataFormat.CreateInProcessFormat<string>("rivet.shelf.items");

    private readonly ShelfService _service;
    private readonly ShelfSurface _surface;
    private readonly Border _card;
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge;
    private readonly TextBlock _badgeText = new() { FontSize = 11, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pin = new();
    private readonly Button _close = new();
    private readonly ScrollViewer _scroll = new() { Height = ShelfConstants.TileAreaHeight, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal, ItemSpacing = ShelfConstants.TileSpacing, LineSpacing = ShelfConstants.TileSpacing, Margin = new Thickness(ShelfConstants.TileInset) };
    private readonly Border _empty;
    private readonly Grid _footer;
    private readonly Button _share = new();
    private readonly Button _trash = new();
    private readonly TextBlock _trashLabel = new() { FontSize = 11 };
    private bool _dropHover;

    public ShelfCardView(ShelfService service, ShelfSurface surface)
    {
        _service = service;
        _surface = surface;

        // Header
        var glyph = new SymbolIcon { Symbol = Symbol.Archive, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(SymbolIcon.ForegroundProperty, glyph.GetResourceObservable("TextSecondaryBrush"));
        _badge = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 1), VerticalAlignment = VerticalAlignment.Center, Child = _badgeText };
        _badge.Bind(Border.BackgroundProperty, _badge.GetResourceObservable("ChipBrush"));
        ConfigureRound(_pin, Symbol.Pin, L.Get("Strings.shelfPin"));
        _pin.Click += (_, _) => PinToggled?.Invoke(this, EventArgs.Empty);
        if (surface == ShelfSurface.Classic)
        {
            ConfigureRound(_close, Symbol.Dismiss, L.Get("Strings.menuClose"));
            _close.Click += (_, _) => _service.HideClassic(viaCloseButton: true);
        }
        else
        {
            ConfigureRound(_close, Symbol.ChevronUp, L.Get("Strings.shelfCollapse"));
            _close.Click += (_, _) => _service.CollapseDocked();
            _pin.IsVisible = false;
        }

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, _title, _badge } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4, Background = Brushes.Transparent, Height = 30 };
        header.Children.Add(titleRow);
        Grid.SetColumn(_pin, 1);
        header.Children.Add(_pin);
        Grid.SetColumn(_close, 2);
        header.Children.Add(_close);
        header.PointerPressed += OnMoveRegionPressed;

        // Empty state: dashed rounded rectangle, down arrow and "Drag items here".
        var dashes = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3.33],
        };
        dashes.Bind(Shape.StrokeProperty, dashes.GetResourceObservable("TextTertiaryBrush"));
        var arrow = new SymbolIcon { Symbol = Symbol.ArrowDown, FontSize = 21, HorizontalAlignment = HorizontalAlignment.Center };
        arrow.Bind(SymbolIcon.ForegroundProperty, arrow.GetResourceObservable("TextSecondaryBrush"));
        var emptyText = new TextBlock { Text = L.Get("Strings.shelfEmpty"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
        emptyText.Bind(TextBlock.ForegroundProperty, emptyText.GetResourceObservable("TextSecondaryBrush"));
        _empty = new Border
        {
            Height = ShelfConstants.TileAreaHeight,
            Background = Brushes.Transparent,
            Child = new Grid
            {
                Children =
                {
                    dashes,
                    new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { arrow, emptyText } },
                },
            },
        };
        _empty.PointerPressed += OnMoveRegionPressed;

        _scroll.Content = _grid;
        _scroll.Background = Brushes.Transparent;
        _scroll.PointerPressed += OnBlankPressed;

        // Footer
        var hint = new TextBlock { Text = L.Get("Strings.shelfHint"), FontSize = 10, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("TextTertiaryBrush"));
        _share.Content = new SymbolIcon { Symbol = Symbol.Share, FontSize = 15 };
        _share.Width = 42;
        _share.Height = 28;
        _share.Padding = new Thickness(0);
        _share.HorizontalContentAlignment = HorizontalAlignment.Center;
        _share.VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(_share, L.Get("Strings.shelfActionShare"));
        AutomationProperties.SetName(_share, L.Get("Strings.shelfActionShare"));
        _share.Click += (_, _) => _service.Share(_service.FooterScope(), Rivet.App.Shell.WindowInterop.Handle(TopLevel.GetTopLevel(this)!));
        var trashIcon = new SymbolIcon { Symbol = Symbol.Delete, FontSize = 15 };
        trashIcon.Bind(SymbolIcon.ForegroundProperty, trashIcon.GetResourceObservable("DangerBrush"));
        _trash.Content = trashIcon;
        _trash.Width = 42;
        _trash.Height = 28;
        _trash.Padding = new Thickness(0);
        _trash.HorizontalContentAlignment = HorizontalAlignment.Center;
        _trash.VerticalContentAlignment = VerticalAlignment.Center;
        _trash.Click += (_, _) =>
        {
            if (_service.Selection.Count > 0)
            {
                _service.RemoveSelected();
            }
            else
            {
                _service.ClearAll();
            }
        };
        _footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6, MinHeight = 30 };
        _footer.Children.Add(hint);
        Grid.SetColumn(_share, 1);
        _footer.Children.Add(_share);
        Grid.SetColumn(_trash, 2);
        _footer.Children.Add(_trash);

        var body = new StackPanel { Spacing = ShelfConstants.CardSpacing, Children = { header, _empty, _scroll, _footer } };
        var content = new Panel { Children = { body } };
        if (surface == ShelfSurface.Docked)
        {
            // A faint brand watermark (128 wide, rotated −8°) at the bottom right.
            var mark = new SymbolIcon
            {
                Symbol = Symbol.Archive,
                FontSize = 112,
                Opacity = 0.06,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, 18),
                RenderTransform = new RotateTransform(-8),
            };
            content.Children.Insert(0, mark);
        }

        _card = new Border
        {
            Width = ShelfConstants.CardWidth,
            Padding = new Thickness(ShelfConstants.CardPadding),
            CornerRadius = new CornerRadius(ShelfConstants.CardRadius),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = content,
            BoxShadow = BoxShadows.Parse("0 6 20 0 #40000000"),
        };
        _card.Bind(Border.BackgroundProperty, _card.GetResourceObservable("PanelBackgroundBrush"));
        _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("PanelBorderBrush"));
        Content = _card;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnCardDragOver);
        AddHandler(DragDrop.DragOverEvent, OnCardDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHover(false));
        AddHandler(DragDrop.DropEvent, OnCardDrop);
        Focusable = true;
        KeyDown += OnKeyDown;
        Refresh();
    }

    /// <summary>The keep-open pin was clicked (the window owns the pin state).</summary>
    public event EventHandler? PinToggled;

    /// <summary>A tile drag or window move is running (holds the card open).</summary>
    public bool InteractionActive { get; private set; }

    public bool DropHover => _dropHover;

    public ShelfSurface Surface => _surface;

    public void SetPinned(bool pinned)
    {
        ToolTip.SetTip(_pin, L.Get(pinned ? "Strings.shelfUnpin" : "Strings.shelfPin"));
        AutomationProperties.SetName(_pin, L.Get(pinned ? "Strings.shelfUnpin" : "Strings.shelfPin"));
        if (_pin.Content is SymbolIcon icon)
        {
            icon.IconVariant = pinned ? IconVariant.Filled : IconVariant.Regular;
            if (pinned)
            {
                icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush"));
            }
            else
            {
                icon.ClearValue(SymbolIcon.ForegroundProperty);
            }
        }
    }

    /// <summary>Rebuilds the tiles and the header/footer state from the service.</summary>
    public void Refresh()
    {
        var leaves = _service.LeafCount;
        var selected = _service.Selection.Count;
        _title.Text = selected > 0 ? L.Format("Strings.shelfSelectedFormat", selected) : L.Get("Strings.shelfTitle");
        _badge.IsVisible = leaves > 0;
        _badgeText.Text = leaves.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var empty = _service.Store.IsEmpty;
        _empty.IsVisible = empty;
        _scroll.IsVisible = !empty;
        _footer.IsVisible = !empty;

        var trashText = L.Get(selected > 0 ? "Strings.shelfRemoveSelected" : "Strings.shelfClearAll");
        ToolTip.SetTip(_trash, trashText);
        AutomationProperties.SetName(_trash, trashText);
        var hasFile = ShelfTree.Leaves(_service.FooterScope()).Any(l => l.Kind == ShelfItemKind.File);
        _share.IsEnabled = hasFile;
        _share.Opacity = hasFile ? 1 : 0.4;

        _grid.Children.Clear();
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        foreach (var row in _service.VisibleRows())
        {
            _grid.Children.Add(new ShelfTileView(_service, this, row, scale));
        }
    }

    /// <summary>Scrolls the newest leaf (or its outermost collapsed pile) into view.</summary>
    public void RevealLast()
    {
        if (_grid.Children.LastOrDefault() is { } last)
        {
            last.BringIntoView();
        }
    }

    internal void BeginInteraction() => InteractionActive = true;

    internal void EndInteraction() => InteractionActive = false;

    internal IReadOnlyList<Guid> VisibleOrder() => _grid.Children.OfType<ShelfTileView>().Select(t => t.Row.Item.Id).ToList();

    private static void ConfigureRound(Button button, Symbol symbol, string tip)
    {
        button.Width = 30;
        button.Height = 30;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(15);
        button.Classes.Add("icon");
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Content = new SymbolIcon { Symbol = symbol, FontSize = 14 };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
    }

    private void OnMoveRegionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window window)
        {
            InteractionActive = true;
            try
            {
                window.BeginMoveDrag(e);
            }
            finally
            {
                InteractionActive = false;
            }
        }
    }

    private void OnBlankPressed(object? sender, PointerPressedEventArgs e)
    {
        // Blank space between and below tiles moves the window; tiles handle their own presses.
        if (e.Source is Visual v && v.FindAncestorOfType<ShelfTileView>(includeSelf: true) is null)
        {
            OnMoveRegionPressed(sender, e);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            _service.Selection.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers == KeyModifiers.Control)
        {
            _service.Selection.SelectAll(VisibleOrder());
            e.Handled = true;
        }
    }

    private void SetDropHover(bool hover)
    {
        _dropHover = hover;
        if (hover)
        {
            _card.BorderThickness = new Thickness(2);
            _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("AccentBrush"));
        }
        else
        {
            _card.BorderThickness = new Thickness(1);
            _card.Bind(Border.BorderBrushProperty, _card.GetResourceObservable("PanelBorderBrush"));
        }
    }

    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        // During an internal tile drag every non-tile target refuses.
        if (e.DataTransfer.Contains(InternalFormat) || !_service.IsAvailable)
        {
            e.DragEffects = DragDropEffects.None;
            SetDropHover(false);
            return;
        }

        var accept = ShelfDropReader.CanAccept(e.DataTransfer);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHover(accept);
    }

    private void OnCardDrop(object? sender, DragEventArgs e)
    {
        SetDropHover(false);
        if (e.DataTransfer.Contains(InternalFormat))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var entries = ShelfDropReader.Read(e.DataTransfer);
        e.DragEffects = _service.Drop(entries, null, _surface) ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.DragEffects != DragDropEffects.None)
        {
            RevealLast();
        }
    }

    internal static string FileName(ShelfItem item) => item.Path is null ? item.Title : Path.GetFileName(item.Path);

    internal static IBrush Faint(double alpha) => new ImmutableSolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 128, 128, 128));
}
