// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Clipboard;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Clipboard;

/// <summary>
/// The clipboard history window (spec 06 §3.2.8): a strip of cards along the
/// bottom of the screen with search, a preview sidebar, a selection pile and
/// keyboard paste (Ctrl+1–9). Picking a card puts it on the clipboard, gives
/// focus back to the app that was in front and pastes there.
/// </summary>
public sealed class ClipboardHistoryWindow : FloatingPanel
{
    private const double CardWidth = 184;
    private const double CardHeight = 210;

    private readonly ClipboardHistoryService _history;
    private readonly CaretActions _caret;
    private readonly ISettingsStore _settings;
    private readonly IHud? _hud;
    private readonly TextBox _search = new() { PlaceholderText = string.Empty, MinWidth = 220 };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(14, 4, 14, 8) };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _empty = new() { Classes = { "caption" }, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _previewToggle = new() { Classes = { "icon" } };
    private readonly Border _previewPane = new() { Width = 280, BorderThickness = new Thickness(1, 0, 0, 0), IsVisible = false };
    private readonly Grid _layout = new();
    private readonly HashSet<Guid> _batch = [];
    private IReadOnlyList<ClipboardEntry> _visible = [];
    private ForegroundApp? _target;
    private int _selected = -1;
    private bool _selectionShown;
    private Guid? _anchor;
    private Guid? _editing;
    private TextBox? _editor;

    public ClipboardHistoryWindow(IServiceProvider services)
    {
        _history = services.GetRequiredService<ClipboardHistoryService>();
        _caret = services.GetRequiredService<CaretActions>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _hud = services.GetService<IHud>();
        Title = L.Get("clipboard.title");
        SizeToContent = SizeToContent.Manual;
        BuildLayout();
        _search.TextChanged += (_, _) =>
        {
            _selected = _visible.Count > 0 ? 0 : -1;
            _selectionShown = false;
            Rebuild();
        };
        _history.History.Changed += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.UIThread.Post(Rebuild);
            }
        };
    }

    /// <summary>Opens the window; the app in front becomes the paste target.</summary>
    public void Open()
    {
        if (IsVisible)
        {
            Dismiss(FloatingCloseReason.Escape);
            return;
        }

        var front = _caret.Foreground.Current();
        _target = front is { IsSelf: false } ? front : null;
        _history.CheckNow();
        _batch.Clear();
        _search.Text = string.Empty;
        _selected = -1;
        _selectionShown = false;
        _editing = null;
        _previewPane.IsVisible = _settings.Get(ClipboardSettings.QuickPreview);
        _previewToggle.IsChecked = _previewPane.IsVisible;
        _history.PanelHasKeyboard = true;
        Rebuild();
        Present();
        _scroller.Offset = default;
    }

    protected override void OnPresented() => _search.Focus();

    protected override void OnDismissed(FloatingCloseReason reason)
    {
        _history.PanelHasKeyboard = false;
        _history.CheckNow();
        _batch.Clear();
        if (reason == FloatingCloseReason.Escape)
        {
            _caret.RestoreFocus(_target);
        }
    }

    protected override bool DismissOnDeactivate => _editing is null;

    protected override void Place()
    {
        var (area, scale) = PointerScreen();
        var width = (area.Width / scale) - 32 + 20;
        var height = Math.Min(318, (area.Height / scale) - 16) + 20;
        Width = Math.Max(400, width);
        Height = Math.Max(240, height);
        var x = area.X + (int)((16 - 10) * scale);
        var y = area.Bottom - (int)(8 * scale) - (int)(Height * scale) + (int)(10 * scale);
        Position = new Avalonia.PixelPoint(x, y);
    }

    // ── Layout ─────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        _search.PlaceholderText = L.Get("clipboard.search");
        _search.InnerLeftContent = new Border { Padding = new Thickness(8, 0, 0, 0), Child = ClipboardUi.Icon("Search", 14) };
        AutomationProperties.SetName(_search, L.Get("clipboard.search"));
        _previewToggle.Content = ClipboardUi.Icon("PanelRight", 16, "TextPrimaryBrush");
        ToolTip.SetTip(_previewToggle, L.Get("clipboard.previewLabel"));
        AutomationProperties.SetName(_previewToggle, L.Get("clipboard.previewLabel"));
        _previewToggle.IsCheckedChanged += (_, _) => SetPreview(_previewToggle.IsChecked == true);
        var close = ClipboardUi.IconButton("Dismiss", L.Get("clipboard.cancel"), () => Dismiss(FloatingCloseReason.Escape), 14);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(14, 10, 10, 4) };
        var title = new TextBlock { Text = L.Get("clipboard.title"), FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        toolbar.Children.Add(title);
        Grid.SetColumn(_search, 1);
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _search.Width = 320;
        toolbar.Children.Add(_search);
        Grid.SetColumn(_previewToggle, 2);
        toolbar.Children.Add(_previewToggle);
        Grid.SetColumn(close, 3);
        toolbar.Children.Add(close);

        _scroller.Content = _strip;
        var body = new Panel { Children = { _scroller, _empty } };
        var footerBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 0, 14, 10) };
        footerBar.Children.Add(_footer);
        Grid.SetColumn(_count, 1);
        footerBar.Children.Add(_count);

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        main.Children.Add(toolbar);
        Grid.SetRow(body, 1);
        main.Children.Add(body);
        Grid.SetRow(footerBar, 2);
        main.Children.Add(footerBar);

        _previewPane.Bind(Border.BorderBrushProperty, _previewPane.GetResourceObservable("SeparatorBrush").ToBinding());
        _layout.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _layout.Children.Add(main);
        Grid.SetColumn(_previewPane, 1);
        _layout.Children.Add(_previewPane);
        Surface.Child = _layout;
    }

    private void SetPreview(bool open)
    {
        _previewPane.IsVisible = open;
        _settings.Set(ClipboardSettings.QuickPreview, open);
        RenderPreview();
    }

    private string Query => _search.Text ?? string.Empty;

    private void Rebuild()
    {
        var entries = _history.Search(Query);
        _visible = entries;
        if (_selected >= _visible.Count)
        {
            _selected = _visible.Count - 1;
        }

        _strip.Children.Clear();
        var searching = Query.Trim().Length > 0;
        var dividerAdded = false;
        for (var i = 0; i < _visible.Count; i++)
        {
            var entry = _visible[i];
            if (!searching && !dividerAdded && !entry.IsPinned && i > 0)
            {
                dividerAdded = true;
                var divider = new Border { Width = 1, Margin = new Thickness(2, 16) };
                divider.Bind(Border.BackgroundProperty, divider.GetResourceObservable("SeparatorBrush").ToBinding());
                _strip.Children.Add(divider);
            }

            _strip.Children.Add(BuildCard(entry, i));
        }

        _empty.Text = _history.History.Entries.Count == 0 ? L.Get("clipboard.empty") : L.Get("clipboard.noResults");
        _empty.IsVisible = _visible.Count == 0;
        _scroller.IsVisible = _visible.Count > 0;
        RebuildFooter();
        RenderPreview();
    }

    private Control BuildCard(ClipboardEntry entry, int index)
    {
        var selected = _selectionShown && index == _selected;
        var batched = _batch.Contains(entry.Id);
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(selected || batched ? 2.5 : 1),
            Padding = new Thickness(10, 6, 10, 6),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable(selected || batched ? "AccentFaintBrush" : "PanelCardBrush").ToBinding());
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable(selected || batched ? "AccentBrush" : "PanelCardBorderBrush").ToBinding());

        // Header: marker, source app, pin or Copy on hover.
        var marker = batched ? ClipboardUi.Icon("CheckmarkCircle", 14, "AccentBrush") : ClipboardUi.Icon(entry.IsPinned ? "Pin" : ClipboardUi.KindIcon(entry), 14);
        var source = new TextBlock
        {
            Text = entry.SourceApp is null ? string.Empty : ClipboardUi.SourceName(entry.SourceApp),
            Classes = { "caption" },
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = ClipboardUi.IconButton("Copy", L.Get("clipboard.copy"), () => _ = CopyAsync([entry]), 13);
        var more = ClipboardUi.IconButton("MoreHorizontal", L.Get("clipboard.edit"), () => { }, 13);
        more.Flyout = BuildMenu(entry);
        var hoverButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, IsVisible = false, Children = { copy, more } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6, Height = 28 };
        header.Children.Add(marker);
        Grid.SetColumn(source, 1);
        header.Children.Add(source);
        Grid.SetColumn(hoverButtons, 2);
        header.Children.Add(hoverButtons);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 22 };
        footer.Children.Add(new TextBlock { Text = ClipboardUi.ShortTime(entry.CopiedAt), Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        if (index < 9)
        {
            var badge = new Border { Classes = { "pill" }, Child = new TextBlock { Text = $"Ctrl+{index + 1}", Classes = { "tertiary" }, FontSize = 10 } };
            Grid.SetColumn(badge, 1);
            footer.Children.Add(badge);
        }

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(header);
        content.Children.Add(footer);
        content.Children.Add(CardBody(entry));
        card.Child = content;

        ToolTip.SetTip(card, entry.Kind == ClipboardEntryKind.Files ? string.Join("\n", entry.FilePaths) : null);
        AutomationProperties.SetName(card, entry.Preview.Length > 80 ? entry.Preview[..80] : entry.Preview);
        card.PointerEntered += (_, _) => hoverButtons.IsVisible = true;
        card.PointerExited += (_, _) => hoverButtons.IsVisible = false;
        card.PointerReleased += (_, e) => OnCardClick(entry, index, e);
        card.ContextFlyout = BuildMenu(entry);
        return card;
    }

    private Control CardBody(ClipboardEntry entry)
    {
        switch (entry.Kind)
        {
            case ClipboardEntryKind.Image:
            case ClipboardEntryKind.Files when entry.IsSingleImageFile:
                var image = new Image { Stretch = Stretch.Uniform, MaxHeight = 100, HorizontalAlignment = HorizontalAlignment.Left };
                if (ClipboardUi.PicturePath(entry, _history.Images) is { } path)
                {
                    ClipboardUi.LoadThumbnail(image, path, 360);
                }

                var lines = new StackPanel { Spacing = 4, Children = { image } };
                if (entry.Kind == ClipboardEntryKind.Files)
                {
                    lines.Children.Add(new TextBlock { Text = ClipboardEntry.FileName(entry.FilePaths[0]), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                }
                else
                {
                    lines.Children.Add(new TextBlock { Text = ClipboardUi.ImageCaption(entry), Classes = { "caption" }, FontSize = 11 });
                }

                return lines;
            case ClipboardEntryKind.Files:
                return new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = ClipboardUi.FilesTitle(entry), FontWeight = FontWeight.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = string.Join("\n", entry.FilePaths.Take(5).Select(ClipboardEntry.FileName)), Classes = { "caption" }, FontSize = 11, MaxLines = 5, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                };
            default:
                var text = ClipboardUi.Highlighted(entry.CardPreview, Query, 12.5, 7);
                if (entry.Color is { } color)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    row.Children.Add(ClipboardUi.Swatch(color.ToArgb()));
                    row.Children.Add(text);
                    return row;
                }

                return text;
        }
    }

    private MenuFlyout BuildMenu(ClipboardEntry entry)
    {
        MenuItem Item(string key, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = L.Get(key), IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        var searching = Query.Trim().Length > 0;
        return new MenuFlyout
        {
            Items =
            {
                Item("Strings.menuPaste", () => _ = PasteAsync([entry])),
                Item("clipboard.copy", () => _ = CopyAsync([entry])),
                new Separator(),
                Item(entry.IsPinned ? "clipboard.unpin" : "clipboard.pin", () => _history.TogglePin(entry.Id)),
                Item("clipboard.moveUp", () => _history.Move(entry.Id, -1), !searching && _history.History.CanMove(entry.Id, -1)),
                Item("clipboard.moveDown", () => _history.Move(entry.Id, 1), !searching && _history.History.CanMove(entry.Id, 1)),
                new Separator(),
                Item("clipboard.delete", () => _history.Delete([entry.Id])),
            },
        };
    }

    private void RebuildFooter()
    {
        _footer.Children.Clear();
        if (_batch.Count > 0)
        {
            var paste = new Button { Content = L.Format("clipboard.pasteSelectedFormat", _batch.Count), Classes = { "accent" } };
            paste.Click += (_, _) => _ = PasteAsync(BatchEntries());
            var copy = new Button { Content = L.Format("clipboard.copySelectedFormat", _batch.Count) };
            copy.Click += (_, _) => _ = CopyAsync(BatchEntries());
            var delete = new Button { Content = L.Format("clipboard.deleteSelectedFormat", _batch.Count) };
            delete.Bind(Button.ForegroundProperty, delete.GetResourceObservable("DangerBrush").ToBinding());
            delete.Click += (_, _) => DeleteBatch();
            var clear = new Button { Content = L.Get("clipboard.clearSelection") };
            clear.Click += (_, _) =>
            {
                _batch.Clear();
                Rebuild();
            };
            _footer.Children.Add(paste);
            _footer.Children.Add(copy);
            _footer.Children.Add(delete);
            _footer.Children.Add(clear);
            _count.Text = string.Empty;
            return;
        }

        var clearRecent = new Button { Content = L.Get("clipboard.clearRecent"), IsEnabled = _history.History.RecentCount > 0 };
        clearRecent.Click += (_, _) => _ = ConfirmClearAsync();
        _footer.Children.Add(clearRecent);
        _count.Text = _history.History.Entries.Count.ToString(CultureInfo.CurrentCulture);
    }

    private IReadOnlyList<ClipboardEntry> BatchEntries() =>
        _history.History.Entries.Where(e => _batch.Contains(e.Id)).ToList();

    // ── Preview sidebar ────────────────────────────────────────────────

    private ClipboardEntry? Highlighted => _selected >= 0 && _selected < _visible.Count ? _visible[_selected] : _visible.FirstOrDefault();

    private void RenderPreview()
    {
        if (!_previewPane.IsVisible)
        {
            return;
        }

        var entry = Highlighted;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 8, 6) };
        header.Children.Add(new TextBlock { Text = L.Get("clipboard.previewLabel"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var close = ClipboardUi.IconButton("Dismiss", L.Get("clipboard.cancel"), () => _previewToggle.IsChecked = false, 12);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        Control body;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (entry is null)
        {
            body = new TextBlock { Text = L.Get("clipboard.empty"), Classes = { "caption" }, Margin = new Thickness(12) };
        }
        else if (_editing == entry.Id && entry.Kind == ClipboardEntryKind.Text)
        {
            _editor = new TextBox { Text = entry.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0) };
            body = _editor;
            var cancel = new Button { Content = L.Get("clipboard.cancel") };
            cancel.Click += (_, _) => StopEditing();
            var save = new Button { Content = L.Get("clipboard.save"), Classes = { "accent" } };
            save.Click += (_, _) =>
            {
                var result = _history.Edit(entry.Id, _editor.Text ?? string.Empty);
                if (result is ClipboardEditResult.Saved or ClipboardEditResult.Unchanged)
                {
                    StopEditing();
                }
                else
                {
                    _caret.Foreground.Beep();
                }
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
        }
        else
        {
            body = PreviewBody(entry);
            if (entry.Kind == ClipboardEntryKind.Text)
            {
                var edit = new Button { Content = L.Get("clipboard.edit") };
                edit.Click += (_, _) =>
                {
                    _editing = entry.Id;
                    RenderPreview();
                    _editor?.Focus();
                };
                buttons.Children.Add(edit);
            }

            var copy = new Button { Content = L.Get("clipboard.copy"), Classes = { "accent" } };
            copy.Click += (_, _) => _ = CopyAsync([entry]);
            buttons.Children.Add(copy);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 6, 12, 10) };
        if (entry is not null)
        {
            var stamp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (entry.IsPinned)
            {
                stamp.Children.Add(ClipboardUi.Icon("Pin", 11));
            }

            stamp.Children.Add(new TextBlock { Text = ClipboardUi.ShortTime(entry.CopiedAt), Classes = { "caption" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            footer.Children.Add(stamp);
        }

        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        layout.Children.Add(body);
        _previewPane.Child = layout;
    }

    private Control PreviewBody(ClipboardEntry entry)
    {
        switch (entry.Kind)
        {
            case ClipboardEntryKind.Image:
                var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(12, 0) };
                if (entry.ImageFile is { } file)
                {
                    ClipboardUi.LoadThumbnail(image, _history.Images.PathOf(file), 480);
                }

                return new StackPanel { Spacing = 6, Children = { image, new TextBlock { Text = ClipboardUi.ImageCaption(entry), Classes = { "caption" }, Margin = new Thickness(12, 0) } } };
            case ClipboardEntryKind.Files:
                var files = new SelectableTextBlock
                {
                    Text = string.Join("\n", entry.FilePaths),
                    Classes = { "mono" },
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 0),
                };
                return new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Spacing = 6,
                        Children = { new TextBlock { Text = ClipboardUi.FilesTitle(entry), FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 0) }, files },
                    },
                };
            default:
                var text = new SelectableTextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(12, 0) };
                var trimmed = entry.Text.TrimStart();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    var source = entry.Text;
                    _ = Task.Run(() => ClipboardJsonFormat.Pretty(source)).ContinueWith(task =>
                    {
                        if (task.Result is { } pretty)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                text.Text = pretty;
                                text.Classes.Add("mono");
                                text.FontSize = 11.5;
                            });
                        }
                    }, TaskScheduler.Default);
                }

                return new ScrollViewer { Content = text };
        }
    }

    private void StopEditing()
    {
        _editing = null;
        _editor = null;
        RenderPreview();
        _search.Focus();
    }

    // ── Actions ────────────────────────────────────────────────────────

    private void OnCardClick(ClipboardEntry entry, int index, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ToggleBatch(entry);
            _anchor = entry.Id;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var from = _anchor is { } a ? _visible.ToList().FindIndex(x => x.Id == a) : Math.Max(0, _selected);
            if (from < 0)
            {
                from = 0;
            }

            for (var i = Math.Min(from, index); i <= Math.Max(from, index); i++)
            {
                _batch.Add(_visible[i].Id);
            }

            Rebuild();
            return;
        }

        _ = PasteAsync(_batch.Contains(entry.Id) ? BatchEntries() : [entry]);
    }

    private void ToggleBatch(ClipboardEntry entry)
    {
        if (!_batch.Remove(entry.Id))
        {
            _batch.Add(entry.Id);
        }

        Rebuild();
    }

    private async Task PasteAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var target = _target;
        Dismiss(FloatingCloseReason.Action);
        if (!await _history.CopyAsync(entries).ConfigureAwait(true))
        {
            _caret.Foreground.Beep();
            return;
        }

        var outcome = await _caret.PasteAsync(target, strictModifiers: false, TimeSpan.FromMilliseconds(120)).ConfigureAwait(true);
        ReportOutcome(outcome);
    }

    private async Task CopyAsync(IReadOnlyList<ClipboardEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var target = _target;
        Dismiss(FloatingCloseReason.Action);
        if (!await _history.CopyAsync(entries).ConfigureAwait(true))
        {
            _caret.Foreground.Beep();
            return;
        }

        _caret.RestoreFocus(target);
        _hud?.Show(L.Get("clipboard.copied"), HudStyle.Success, "Copy");
    }

    private void ReportOutcome(CaretOutcome outcome)
    {
        if (outcome == CaretOutcome.Elevated)
        {
            _hud?.Show(L.Get("win.clipboard.elevatedTarget"), HudStyle.Warning);
        }
    }

    private void DeleteBatch()
    {
        _history.Delete(_batch.ToList());
        _batch.Clear();
        Rebuild();
    }

    private async Task ConfirmClearAsync()
    {
        var counted = _history.History.RecentIds();
        if (counted.Count == 0)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(this, L.Format("clipboard.clearRecentConfirmFormat", counted.Count), L.Get("clipboard.clearRecentConfirmMessage"),
            L.Get("clipboard.clearRecent"), L.Get("clipboard.cancel"), destructive: true).ConfigureAwait(true);
        if (confirmed)
        {
            _history.ClearUnpinned(counted);
        }

        Activate();
        _search.Focus();
    }

    // ── Keyboard map (spec 06 §3.2.8, Windows keys per §7.4) ───────────

    protected override void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editing is not null)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                StopEditing();
            }

            return;
        }

        var modifiers = e.KeyModifiers;
        var ctrl = modifiers == KeyModifiers.Control;
        var alt = modifiers == KeyModifiers.Alt;
        var none = modifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Escape when none:
                if (_batch.Count > 0)
                {
                    _batch.Clear();
                    Rebuild();
                }
                else
                {
                    Dismiss(FloatingCloseReason.Escape);
                }

                break;
            case Key.Space when none && _selectionShown:
                _previewToggle.IsChecked = !_previewPane.IsVisible;
                break;
            case Key.Enter when none:
                if (_batch.Count > 0)
                {
                    _ = PasteAsync(BatchEntries());
                }
                else if (Highlighted is { } entry)
                {
                    _ = PasteAsync([entry]);
                }

                break;
            case Key.Enter when ctrl:
                if (Highlighted is { } toggled)
                {
                    ToggleBatch(toggled);
                }

                break;
            case Key.Enter when modifiers == KeyModifiers.Shift:
                _ = CopyAsync(_batch.Count > 0 ? BatchEntries() : Highlighted is { } h ? [h] : []);
                break;
            case Key.C when ctrl && _batch.Count > 0:
                _ = CopyAsync(BatchEntries());
                break;
            case Key.A when ctrl && (_batch.Count > 0 || Query.Length == 0):
                foreach (var entry in _visible)
                {
                    _batch.Add(entry.Id);
                }

                Rebuild();
                break;
            case Key.P when alt:
                if (Highlighted is { } pinned)
                {
                    _history.TogglePin(pinned.Id);
                }

                break;
            case Key.Back or Key.Delete when alt:
                if (_batch.Count > 0)
                {
                    DeleteBatch();
                }
                else if (Highlighted is { } deleted)
                {
                    _history.Delete([deleted.Id]);
                }

                break;
            case Key.Back or Key.Delete when ctrl && _batch.Count > 0:
                DeleteBatch();
                break;
            case Key.Down or Key.Right when none:
            case Key.N when ctrl:
                MoveSelection(1);
                break;
            case Key.Up or Key.Left when none:
            case Key.P when ctrl:
                MoveSelection(-1);
                break;
            case >= Key.D1 and <= Key.D9 when ctrl:
            case >= Key.NumPad1 and <= Key.NumPad9 when ctrl:
                var n = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                if (n < _visible.Count)
                {
                    _ = PasteAsync([_visible[n]]);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        if (!_selectionShown)
        {
            _selectionShown = true;
            _selected = Math.Max(0, _selected);
        }
        else
        {
            _selected = Math.Clamp(_selected + delta, 0, _visible.Count - 1);
        }

        Rebuild();
        if (_selected >= 0 && _strip.Children.OfType<Border>().Where(b => b.Width == CardWidth).ElementAtOrDefault(_selected) is { } card)
        {
            card.BringIntoView();
        }
    }
}
