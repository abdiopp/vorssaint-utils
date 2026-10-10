// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Clipboard;

/// <summary>
/// The Clipboard tool hosted in the tray panel and the quick panel (spec 06
/// §3.2.13): the capture switch, search, Clear unpinned, a button for the
/// history window, and the list with move, pin, copy and delete buttons.
/// </summary>
public sealed class ClipboardPanelView : UserControl
{
    private readonly ClipboardHistoryService _history;
    private readonly ISettingsStore _settings;
    private readonly ShortcutManager _shortcuts;
    private readonly IKeyNameProvider? _keyNames;
    private readonly ActionRegistry _actions;
    private readonly TextBox _search = new();
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _caption = new() { Classes = { "caption" } };
    private readonly TextBlock _shortcut = new() { Classes = { "caption", "tertiary" } };
    private readonly Button _clear;
    private readonly List<IDisposable> _subscriptions = [];
    private Guid? _copiedId;
    private long _copiedAt;

    public ClipboardPanelView(IServiceProvider services)
    {
        _history = services.GetRequiredService<ClipboardHistoryService>();
        _settings = services.GetRequiredService<ISettingsStore>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _keyNames = services.GetService<IKeyNameProvider>();
        _actions = services.GetRequiredService<ActionRegistry>();

        var enabled = _settings.Bind(ClipboardSettings.Enabled);
        _subscriptions.Add(enabled);
        var toggle = new CheckBox { Content = L.Get("clipboard.enable"), IsChecked = enabled.Value };
        toggle.IsCheckedChanged += (_, _) => enabled.Value = toggle.IsChecked == true;
        enabled.PropertyChanged += (_, _) =>
        {
            toggle.IsChecked = enabled.Value;
            Refresh();
        };

        _search.PlaceholderText = L.Get("clipboard.search");
        _search.InnerLeftContent = new Border { Padding = new Thickness(8, 0, 0, 0), Child = ClipboardUi.Icon("Search", 13) };
        AutomationProperties.SetName(_search, L.Get("clipboard.search"));
        _search.TextChanged += (_, _) => Refresh();
        _clear = ClipboardUi.IconButton("Delete", L.Get("clipboard.clearRecent"), () => _ = ConfirmClearAsync());
        var open = ClipboardUi.IconButton("Open", L.Get("clipboard.title"), () => _ = _actions.InvokeAsync("clipboardHistory.show", ActionSource.Panel));
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        searchRow.Children.Add(_search);
        Grid.SetColumn(_clear, 1);
        searchRow.Children.Add(_clear);
        Grid.SetColumn(open, 2);
        searchRow.Children.Add(open);

        var card = new Border
        {
            Classes = { "card" },
            Child = new StackPanel { Spacing = 6, Children = { toggle, _caption, _shortcut, searchRow } },
        };
        var scroller = new ScrollViewer { MaxHeight = 260, Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new StackPanel { Spacing = 8, Children = { card, scroller } };
        _history.History.Changed += OnHistoryChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _history.History.Changed -= OnHistoryChanged;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var on = _settings.Get(ClipboardSettings.Enabled);
        _caption.Text = on ? L.Get("clipboard.caption") : L.Get("clipboard.disabled");
        var role = _shortcuts.Find("clipboard");
        _shortcut.IsVisible = on && role is not null && _shortcuts.IsWanted(role);
        if (role is not null)
        {
            _shortcut.Text = $"{L.Get("clipboard.shortcut")}: {_shortcuts.GetChord(role).ToDisplayString(_keyNames)}";
        }

        var entries = _history.History.Entries;
        _search.IsEnabled = entries.Count > 0;
        _clear.IsEnabled = _history.History.RecentCount > 0;
        var query = _search.Text ?? string.Empty;
        var shown = _history.Search(query);
        var searching = query.Trim().Length > 0;
        _list.Children.Clear();
        var pinnedLabelAdded = false;
        foreach (var entry in shown.Take(200))
        {
            if (entry.IsPinned && !pinnedLabelAdded && !searching)
            {
                pinnedLabelAdded = true;
                _list.Children.Add(new TextBlock { Text = L.Get("clipboard.pinned").ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, Margin = new Thickness(4, 2) });
            }

            _list.Children.Add(Row(entry, searching));
        }

        if (shown.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = entries.Count == 0 ? L.Get("clipboard.empty") : L.Get("clipboard.noResults"), Classes = { "caption" }, Margin = new Thickness(4) });
        }
    }

    private Control Row(ClipboardEntry entry, bool searching)
    {
        Control preview = entry.Kind switch
        {
            ClipboardEntryKind.Image => ImagePreview(entry),
            ClipboardEntryKind.Files when entry.IsSingleImageFile => ImagePreview(entry),
            ClipboardEntryKind.Files => new TextBlock { Text = ClipboardUi.FilesTitle(entry), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            _ => TextPreview(entry),
        };

        var up = ClipboardUi.IconButton("ArrowUp", L.Get("clipboard.moveUp"), () => _history.Move(entry.Id, -1), 12);
        up.IsEnabled = !searching && _history.History.CanMove(entry.Id, -1);
        var down = ClipboardUi.IconButton("ArrowDown", L.Get("clipboard.moveDown"), () => _history.Move(entry.Id, 1), 12);
        down.IsEnabled = !searching && _history.History.CanMove(entry.Id, 1);
        var pin = ClipboardUi.IconButton(entry.IsPinned ? "PinOff" : "Pin", L.Get(entry.IsPinned ? "clipboard.unpin" : "clipboard.pin"), () => _history.TogglePin(entry.Id), 12);
        var copied = _copiedId == entry.Id;
        var copy = new Button
        {
            Classes = { "icon" },
            Content = new TextBlock { Text = copied ? "✓ " + L.Get("clipboard.copied") : L.Get("clipboard.copy"), FontSize = 11 },
        };
        AutomationProperties.SetName(copy, L.Get("clipboard.copy"));
        copy.Click += async (_, _) =>
        {
            if (_copiedId == entry.Id && Environment.TickCount64 - _copiedAt < 500)
            {
                return; // the second click of a double click: the row was rebuilt under the pointer
            }

            if (await _history.CopyAsync([entry]).ConfigureAwait(true))
            {
                _copiedId = entry.Id;
                _copiedAt = Environment.TickCount64;
                Refresh();
            }
        };
        var delete = ClipboardUi.IconButton("Delete", L.Get("clipboard.delete"), () => _history.Delete([entry.Id]), 12);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Children = { up, down, pin, copy, delete } };
        var time = new TextBlock { Text = ClipboardUi.ShortTime(entry.CopiedAt), Classes = { "caption", "tertiary" }, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(time);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);
        return new Border
        {
            Classes = { "card" },
            Padding = new Thickness(8, 6, 4, 2),
            Child = new StackPanel { Spacing = 2, Children = { preview, bottom } },
        };
    }

    private Control TextPreview(ClipboardEntry entry)
    {
        var text = ClipboardUi.Highlighted(entry.CardPreview, _search.Text ?? string.Empty, 12, 3);
        if (entry.Color is { } color)
        {
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { ClipboardUi.Swatch(color.ToArgb()), text } };
        }

        return text;
    }

    private Control ImagePreview(ClipboardEntry entry)
    {
        var image = new Image { Width = 110, Height = 40, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        if (ClipboardUi.PicturePath(entry, _history.Images) is { } path)
        {
            ClipboardUi.LoadThumbnail(image, path, 220);
        }

        var caption = entry.Kind == ClipboardEntryKind.Files ? ClipboardEntry.FileName(entry.FilePaths[0]) : ClipboardUi.ImageCaption(entry);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { image, new TextBlock { Text = caption, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center } } };
    }

    private async Task ConfirmClearAsync()
    {
        var counted = _history.History.RecentIds();
        if (counted.Count == 0)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ConfirmDialog.ShowAsync(owner, L.Format("clipboard.clearRecentConfirmFormat", counted.Count), L.Get("clipboard.clearRecentConfirmMessage"),
            L.Get("clipboard.clearRecent"), L.Get("clipboard.cancel"), destructive: true).ConfigureAwait(true);
        if (confirmed)
        {
            _history.ClearUnpinned(counted);
        }
    }
}
