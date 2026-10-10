// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.QuickPanel;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.QuickPanel;

/// <summary>
/// The quick panel (spec 06 §3.9): a floating 3-column grid of tools with
/// number badges, live dots, an edit mode (hide, drag to reorder, add back,
/// the clipboard options card) and hosted mini tools that replace the grid.
/// Arrows move clamped (no wrap), Enter or 1–9 activate, Esc steps back.
/// </summary>
public sealed class QuickPanelWindow : FloatingPanel
{
    public const double PanelWidth = 420;
    private const int Columns = 3;

    private readonly IServiceProvider _services;
    private readonly QuickPanelCatalog _catalog;
    private readonly ActionRegistry _actions;
    private readonly ISettingsStore _settings;
    private readonly ShortcutManager _shortcuts;
    private readonly IKeyNameProvider? _keyNames;
    private readonly Button _leading;
    private readonly Button _edit = new();
    private readonly UniformGrid _grid = new() { Columns = Columns };
    private readonly ContentControl _options = new() { IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _addBack = new() { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _empty = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(12, 24), IsVisible = false };
    private readonly StackPanel _gridArea = new();
    private readonly ScrollViewer _hostedArea = new() { MaxHeight = 470, IsVisible = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _footerShortcut = new() { Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(QuickPanelTile Tile, Button Button)> _tileButtons = [];
    private List<QuickPanelTile> _tiles = [];
    private QuickPanelTile? _hosted;
    private string? _optionsFor;
    private bool _editing;
    private int _selected;
    private string? _dragging;

    public QuickPanelWindow(IServiceProvider services)
    {
        _services = services;
        _catalog = services.GetRequiredService<QuickPanelCatalog>();
        _actions = services.GetRequiredService<ActionRegistry>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _keyNames = services.GetService<IKeyNameProvider>();
        Title = L.Get("Strings.launcherName");
        Width = PanelWidth + 20;
        SizeToContent = SizeToContent.Height;
        Surface.CornerRadius = new CornerRadius(22);

        _leading = ClipboardUi.IconButton("Dismiss", L.Get("Strings.menuClose"), OnLeading, 14);
        _edit.Content = ClipboardUi.Icon("Options", 16);
        _edit.Classes.Add("icon");
        _edit.Background = Brushes.Transparent;
        _edit.BorderThickness = new Thickness(0);
        ToolTip.SetTip(_edit, L.Get("Strings.launcherEditHint"));
        AutomationProperties.SetName(_edit, L.Get("Strings.launcherEditHint"));
        _edit.Click += (_, _) => SetEditing(!_editing);

        var mark = new AppGlyph { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.Bind(AppGlyph.ForegroundProperty, mark.GetResourceObservable("AccentBrush").ToBinding());
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(_leading);
        Grid.SetColumn(mark, 1);
        header.Children.Add(mark);
        Grid.SetColumn(_edit, 2);
        header.Children.Add(_edit);

        _empty.Text = L.Get("Strings.launcherEmptyState");
        _gridArea.Children.Add(_grid);
        _gridArea.Children.Add(_empty);
        _gridArea.Children.Add(_options);
        _gridArea.Children.Add(_addBack);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ClipboardUi.Icon("Keyboard", 12, "TextTertiaryBrush"), _footerShortcut } });
        var esc = new TextBlock { Text = "Esc", Classes = { "caption", "tertiary" }, FontSize = 11 };
        Grid.SetColumn(esc, 2);
        footer.Children.Add(esc);

        Surface.Child = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { header, _gridArea, _hostedArea, footer },
        };

        AddHandler(PointerMovedEvent, OnPointerMovedTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _dragging = null, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>While hosting a tool or editing, the panel behaves like a small working window.</summary>
    protected override bool DismissOnDeactivate => _hosted is null && !_editing;

    public bool IsEditing => _editing;

    public QuickPanelTile? HostedTile => _hosted;

    public IReadOnlyList<QuickPanelTile> Tiles => _tiles;

    public int SelectedIndex => _selected;

    /// <summary>Opens (or hides when already open): fresh availability, grid, first tile selected.</summary>
    public void Open()
    {
        if (IsVisible)
        {
            Dismiss(FloatingCloseReason.Escape);
            return;
        }

        _hosted = null;
        _optionsFor = null;
        _editing = false;
        _selected = 0;
        Rebuild();
        Present();
    }

    protected override void Place() => PlaceUpperCenter();

    protected override void OnPresented() => Focus();

    protected override void OnDismissed(FloatingCloseReason reason)
    {
        _editing = false;
        _optionsFor = null;
        _hosted = null;
        _hostedArea.Content = null;
    }

    // ── Rendering ──────────────────────────────────────────────────────

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    public void Rebuild()
    {
        _tiles = _catalog.Ordered(includeHidden: false);
        _selected = _tiles.Count == 0 ? -1 : Math.Clamp(_selected, 0, _tiles.Count - 1);
        var hidden = _catalog.Hidden;

        _leading.Content = ClipboardUi.Icon(_hosted is null ? "Dismiss" : "ChevronLeft", 14);
        var leadingText = L.Get(_hosted is null ? "Strings.menuClose" : "Strings.actionBack");
        ToolTip.SetTip(_leading, leadingText);
        AutomationProperties.SetName(_leading, leadingText);
        _edit.IsVisible = _hosted is null;
        _edit.Content = ClipboardUi.Icon(_editing ? "Checkmark" : "Options", 16, _editing ? "AccentBrush" : "TextSecondaryBrush");
        _footerShortcut.Text = ShortcutText() ?? L.Get("Strings.launcherKeysHint");

        _gridArea.IsVisible = _hosted is null;
        _hostedArea.IsVisible = _hosted is not null;
        if (_hosted is not null)
        {
            Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
            return;
        }

        _grid.Children.Clear();
        _tileButtons.Clear();
        var visible = _tiles.Where(t => _editing || !hidden.Contains(t.Id)).ToList();
        for (var i = 0; i < _tiles.Count; i++)
        {
            var button = TileButton(_tiles[i], i, hidden.Contains(_tiles[i].Id));
            _tileButtons.Add((_tiles[i], button));
            _grid.Children.Add(button);
        }

        _empty.IsVisible = visible.Count == 0;
        RebuildOptions();
        RebuildAddBack(hidden);
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    private string? ShortcutText() =>
        _shortcuts.Find(QuickPanelModule.RoleId) is { } role && _shortcuts.GetState(role) == ShortcutState.Active
            ? _shortcuts.GetChord(role).ToDisplayString(_keyNames)
            : null;

    private Button TileButton(QuickPanelTile tile, int index, bool isHidden)
    {
        var selected = index == _selected && !_editing;
        var live = _catalog.IsLive(tile);
        var plate = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Classes = { "iconTile" },
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = ClipboardUi.Icon(tile.Icon, 21, live ? "AccentBrush" : "TextPrimaryBrush"),
        };
        if (live)
        {
            plate.Background = Brush("AccentFaintBrush", Brushes.LightBlue);
        }

        var badges = new Grid { Width = 58, Height = 50, HorizontalAlignment = HorizontalAlignment.Center, Children = { plate } };
        if (index < 9 && !_editing)
        {
            badges.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 0),
                MinWidth = 14,
                Background = Brush("PanelCardBrush", Brushes.WhiteSmoke),
                Child = new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "tertiary" } },
            });
        }

        if (live)
        {
            badges.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brush("SuccessBrush", Brushes.LimeGreen),
                Stroke = Brush("PanelBackgroundBrush", Brushes.White),
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }

        if (_editing)
        {
            var toggleHidden = new Button
            {
                Padding = new Thickness(0),
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brush(isHidden ? "AccentBrush" : "DangerBrush", Brushes.IndianRed),
                Content = ClipboardUi.Icon(isHidden ? "Add" : "Subtract", 11, "TextOnAccentBrush"),
            };
            var text = L.Get(isHidden ? "Strings.panelShowItem" : "Strings.panelHideItem");
            ToolTip.SetTip(toggleHidden, text);
            AutomationProperties.SetName(toggleHidden, $"{text} · {_catalog.Title(tile)}");
            toggleHidden.Click += (_, _) =>
            {
                _catalog.SetHidden(tile.Id, !isHidden);
                if (!isHidden && _optionsFor == tile.Id)
                {
                    _optionsFor = null;
                }

                Rebuild();
            };
            badges.Children.Add(toggleHidden);
            if (tile.HasInlineOptions || tile.SettingsPageId is not null)
            {
                badges.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = ClipboardUi.Icon("Settings", 12, "TextTertiaryBrush"),
                });
            }
        }

        var title = new TextBlock
        {
            Text = _catalog.Title(tile),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            Height = 32,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 4, 2, 0),
        };
        var button = new Button
        {
            Content = new StackPanel { Children = { badges, title } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(4, 8, 4, 4),
            Margin = new Thickness(5),
            CornerRadius = new CornerRadius(12),
            Background = selected ? Brush("AccentFaintBrush", Brushes.LightBlue) : Brushes.Transparent,
            BorderBrush = selected ? Brush("AccentBrush", Brushes.DodgerBlue) : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Opacity = isHidden ? 0.45 : 1,
            Focusable = false,
            Tag = tile.Id,
        };
        AutomationProperties.SetName(button, _catalog.Title(tile));
        button.Click += (_, _) =>
        {
            if (_editing)
            {
                ToggleOptions(tile);
            }
            else
            {
                _selected = index;
                _ = ActivateAsync(tile);
            }
        };
        button.PointerEntered += (_, _) =>
        {
            if (!_editing && _selected != index)
            {
                _selected = index;
                UpdateSelection();
            }
        };
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_editing && e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
            {
                _dragging = tile.Id;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return button;
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _tileButtons.Count; i++)
        {
            var selected = i == _selected && !_editing;
            _tileButtons[i].Button.Background = selected ? Brush("AccentFaintBrush", Brushes.LightBlue) : Brushes.Transparent;
            _tileButtons[i].Button.BorderBrush = selected ? Brush("AccentBrush", Brushes.DodgerBlue) : Brushes.Transparent;
        }
    }

    /// <summary>Drag reorder in edit mode: the dragged tool takes the place of the tile under the pointer.</summary>
    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (!_editing || _dragging is not { } id || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        foreach (var (tile, button) in _tileButtons)
        {
            if (tile.Id == id || !button.IsVisible)
            {
                continue;
            }

            var point = e.GetPosition(button);
            if (point.X >= 0 && point.Y >= 0 && point.X <= button.Bounds.Width && point.Y <= button.Bounds.Height)
            {
                _catalog.MoveTo(id, _catalog.IndexOf(tile.Id));
                Rebuild();
                return;
            }
        }
    }

    private void RebuildAddBack(HashSet<string> hidden)
    {
        _addBack.Children.Clear();
        var hiddenTiles = _catalog.Ordered(includeHidden: true).Where(t => hidden.Contains(t.Id)).ToList();
        _addBack.IsVisible = _editing && hiddenTiles.Count > 0;
        if (!_addBack.IsVisible)
        {
            return;
        }

        _addBack.Children.Add(new TextBlock { Text = L.Get("Strings.launcherAddSection").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, FontSize = 10 });
        var chips = new WrapPanel();
        foreach (var tile in hiddenTiles)
        {
            var captured = tile;
            var chip = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { ClipboardUi.Icon("Add", 11, "AccentBrush"), new TextBlock { Text = _catalog.Title(tile), FontSize = 12 } } },
                Padding = new Thickness(8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(11),
                MinHeight = 0,
            };
            chip.Click += (_, _) =>
            {
                _catalog.SetHidden(captured.Id, false);
                Rebuild();
            };
            chips.Children.Add(chip);
        }

        _addBack.Children.Add(chips);
    }

    // ── Options card (edit mode) ───────────────────────────────────────

    private void ToggleOptions(QuickPanelTile tile)
    {
        if (!tile.HasInlineOptions)
        {
            if (tile.SettingsPageId is { } page)
            {
                Dismiss(FloatingCloseReason.Action);
                _services.GetService<IAppShell>()?.OpenSettings(page);
            }

            return;
        }

        _optionsFor = _optionsFor == tile.Id ? null : tile.Id;
        Rebuild();
    }

    private void RebuildOptions()
    {
        _options.IsVisible = _editing && _optionsFor == "clipboard";
        if (!_options.IsVisible)
        {
            _options.Content = null;
            return;
        }

        var enabled = new ToggleSwitch { Classes = { "compact" }, IsChecked = _settings.Get(ClipboardSettings.Enabled) };
        AutomationProperties.SetName(enabled, L.Get("clipboard.enable"));
        var limit = new ComboBox
        {
            ItemsSource = ClipboardSettings.LimitChoices.Select(v => v == 0 ? L.Get("clipboard.limitUnlimited") : v.ToString("N0", CultureInfo.CurrentCulture)).ToList(),
            SelectedIndex = Array.IndexOf(ClipboardSettings.LimitChoices, _settings.Get(ClipboardSettings.Limit)),
            MinWidth = 120,
        };
        AutomationProperties.SetName(limit, L.Get("clipboard.limit"));
        limit.SelectionChanged += (_, _) =>
        {
            if (limit.SelectedIndex >= 0)
            {
                _settings.Set(ClipboardSettings.Limit, ClipboardSettings.LimitChoices[limit.SelectedIndex]);
            }
        };
        var limitRow = Line(L.Get("clipboard.limit"), limit);
        limitRow.IsVisible = enabled.IsChecked == true;
        enabled.IsCheckedChanged += (_, _) =>
        {
            _settings.Set(ClipboardSettings.Enabled, enabled.IsChecked == true);
            limitRow.IsVisible = enabled.IsChecked == true;
        };
        _options.Content = new Border
        {
            Classes = { "card" },
            Padding = new Thickness(12, 8),
            Child = new StackPanel { Spacing = 6, Children = { Line(L.Get("clipboard.enable"), enabled), limitRow } },
        };
    }

    private static Grid Line(string text, Control trailing)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(trailing, 1);
        grid.Children.Add(trailing);
        return grid;
    }

    // ── Activation ─────────────────────────────────────────────────────

    public async Task ActivateAsync(QuickPanelTile tile)
    {
        switch (tile.Kind)
        {
            case QuickTileKind.Hosted when tile.CreateView is { } create:
                _hosted = tile;
                _hostedArea.Content = new Border { Padding = new Thickness(0, 0, 4, 0), Child = create(_services) };
                Rebuild();
                break;
            case QuickTileKind.Switch when tile.Switch is { } setting:
                _settings.Set(setting, !_settings.Get(setting));
                Rebuild();
                break;
            case QuickTileKind.Action when tile.ActionId is { } actionId:
                if (tile.KeepsOpen)
                {
                    await _actions.InvokeAsync(actionId, ActionSource.QuickPanel).ConfigureAwait(true);
                    Rebuild();
                }
                else
                {
                    Dismiss(FloatingCloseReason.Action);
                    DispatcherTimer.RunOnce(() => _ = _actions.InvokeAsync(actionId, ActionSource.QuickPanel), tile.Delay);
                }

                break;
        }
    }

    public void SetEditing(bool editing)
    {
        if (_editing == editing)
        {
            return;
        }

        _editing = editing;
        if (!editing)
        {
            _optionsFor = null;
        }

        Rebuild();
    }

    private void OnLeading()
    {
        if (_hosted is not null)
        {
            CloseHosted();
        }
        else
        {
            Dismiss(FloatingCloseReason.Escape);
        }
    }

    private void CloseHosted()
    {
        _hosted = null;
        _hostedArea.Content = null;
        Rebuild();
    }

    // ── Keys ───────────────────────────────────────────────────────────

    protected override void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Close the hosted tool › the options card › edit mode › the panel.
            if (_hosted is not null)
            {
                CloseHosted();
            }
            else if (_optionsFor is not null)
            {
                _optionsFor = null;
                Rebuild();
            }
            else if (_editing)
            {
                SetEditing(false);
            }
            else
            {
                Dismiss(FloatingCloseReason.Escape);
            }

            e.Handled = true;
            return;
        }

        if (_editing || _hosted is not null || (e.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt | Avalonia.Input.KeyModifiers.Meta)) != 0)
        {
            return;
        }

        var count = _tiles.Count;
        switch (e.Key)
        {
            case Key.Enter when _selected >= 0 && _selected < count:
                _ = ActivateAsync(_tiles[_selected]);
                break;
            case Key.Left:
                _selected = QuickPanelSettings.Move(_selected, count, -1, 0, Columns);
                UpdateSelection();
                break;
            case Key.Right:
                _selected = QuickPanelSettings.Move(_selected, count, 1, 0, Columns);
                UpdateSelection();
                break;
            case Key.Up:
                _selected = QuickPanelSettings.Move(_selected, count, 0, -1, Columns);
                UpdateSelection();
                break;
            case Key.Down:
                _selected = QuickPanelSettings.Move(_selected, count, 0, 1, Columns);
                UpdateSelection();
                break;
            case >= Key.D1 and <= Key.D9 when e.Key - Key.D1 < count:
                _selected = e.Key - Key.D1;
                _ = ActivateAsync(_tiles[_selected]);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when e.Key - Key.NumPad1 < count:
                _selected = e.Key - Key.NumPad1;
                _ = ActivateAsync(_tiles[_selected]);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
