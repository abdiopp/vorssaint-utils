// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Clipboard;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Snippets;

namespace Rivet.App.Features.Snippets;

/// <summary>
/// The quick snippet menu (spec 06 §3.7): a searchable list grouped by folder.
/// Return (or a click, or Ctrl+1–9) types the snippet where the caret was;
/// typed-trigger expansion is suspended while the menu is open.
/// </summary>
public sealed class SnippetLibraryWindow : FloatingPanel
{
    private readonly ISettingsStore _settings;
    private readonly CaretActions _caret;
    private readonly SnippetExpansionService _expansion;
    private readonly IAppShell _shell;
    private readonly ClipboardLane _lane;
    private readonly IHud? _hud;
    private readonly TextBox _search = new();
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly ScrollViewer _scroller = new() { MaxHeight = 330, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly List<(TextSnippet Snippet, Button Row)> _rows = [];
    private ForegroundApp? _target;
    private IDisposable? _suspension;
    private int _selected;

    public SnippetLibraryWindow(IServiceProvider services)
    {
        _settings = services.GetRequiredService<ISettingsStore>();
        _caret = services.GetRequiredService<CaretActions>();
        _expansion = services.GetRequiredService<SnippetExpansionService>();
        _shell = services.GetRequiredService<IAppShell>();
        _lane = services.GetRequiredService<ClipboardLane>();
        _hud = services.GetService<IHud>();
        Title = L.Get("snippets.libraryTitle");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        Surface.CornerRadius = new CornerRadius(16);

        _search.PlaceholderText = L.Get("snippets.librarySearchPlaceholder");
        _search.InnerLeftContent = new Border { Padding = new Thickness(10, 0, 0, 0), Child = ClipboardUi.Icon("Search", 15) };
        var clear = ClipboardUi.IconButton("Dismiss", L.Get("Strings.urlCleanerClearButton"), () => _search.Text = string.Empty, 12);
        clear.IsVisible = false;
        _search.InnerRightContent = clear;
        _search.FontSize = 15;
        _search.BorderThickness = new Thickness(0);
        _search.Background = Brushes.Transparent;
        AutomationProperties.SetName(_search, L.Get("snippets.librarySearchPlaceholder"));
        _search.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(_search.Text);
            _selected = 0;
            Rebuild();
        };
        _scroller.Content = _list;

        var manage = new Button { Classes = { "link" }, Content = L.Get("snippets.manageButton") };
        manage.Click += (_, _) =>
        {
            Dismiss(FloatingCloseReason.Action);
            _shell.OpenSettings(SnippetsModule.PageId);
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 6, 14, 10) };
        footer.Children.Add(new TextBlock { Text = L.Get("snippets.libraryFooterHint"), Classes = { "caption", "tertiary" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(manage, 1);
        footer.Children.Add(manage);

        var separator = new Border { Classes = { "separator" }, Margin = new Thickness(0, 2, 0, 4) };
        Surface.Child = new StackPanel
        {
            Children =
            {
                new Border { Padding = new Thickness(6, 8, 6, 4), Child = _search },
                separator,
                new Border { Padding = new Thickness(6, 0), Child = _scroller },
                footer,
            },
        };
    }

    public void Open()
    {
        if (IsVisible)
        {
            Dismiss(FloatingCloseReason.Escape);
            return;
        }

        _target = _caret.CaptureTarget();
        _suspension ??= _expansion.Suspend();
        _search.Text = string.Empty;
        _selected = 0;
        Rebuild();
        Present();
    }

    protected override void OnPresented() => _search.Focus();

    protected override void Place() => PlaceUpperCenter();

    protected override void OnDismissed(FloatingCloseReason reason)
    {
        _suspension?.Dispose();
        _suspension = null;
        if (reason == FloatingCloseReason.Escape)
        {
            _caret.RestoreFocus(_target);
        }
    }

    private void Rebuild()
    {
        var snippets = _settings.Get(SnippetSettings.Snippets);
        var sections = SnippetLibrary.Sections(snippets, _search.Text ?? string.Empty, CultureInfo.CurrentCulture);
        _list.Children.Clear();
        _rows.Clear();
        if (!SnippetLibrary.HasEligible(snippets))
        {
            _list.Children.Add(Message(L.Get("snippets.libraryEmpty")));
        }
        else if (sections.Count == 0)
        {
            _list.Children.Add(Message(L.Get("snippets.libraryNoResults")));
        }

        foreach (var section in sections)
        {
            if (section.Folder.Length > 0)
            {
                _list.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(8, 8, 8, 2),
                    Children =
                    {
                        ClipboardUi.Icon("Folder", 12),
                        new TextBlock { Text = section.Folder, Classes = { "sectionTitle" }, VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            }

            foreach (var snippet in section.Snippets)
            {
                var row = BuildRow(snippet, _rows.Count);
                _rows.Add((snippet, row));
                _list.Children.Add(row);
            }
        }

        _selected = Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        UpdateSelection();
        Dispatcher.UIThread.Post(Place, DispatcherPriority.Background);
    }

    private static TextBlock Message(string text) => new() { Text = text, Classes = { "caption" }, Margin = new Thickness(12, 14), TextWrapping = TextWrapping.Wrap };

    private Button BuildRow(TextSnippet snippet, int index)
    {
        var chip = new Border
        {
            Classes = { "pill" },
            Child = new TextBlock { Text = snippet.Trigger, Classes = { "mono" }, FontSize = 11 },
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = snippet.DisplayName, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, chip } };
        var preview = new TextBlock { Text = snippet.PreviewLine, Classes = { "caption" }, FontSize = 11.5, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var enter = new TextBlock { Text = "↩", Classes = { "tertiary" }, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        var badge = index < 9 ? new TextBlock { Text = $"Ctrl+{index + 1}", Classes = { "caption", "tertiary" }, FontSize = 10, VerticalAlignment = VerticalAlignment.Center } : null;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(new StackPanel { Spacing = 2, Children = { top, preview } });
        if (badge is not null)
        {
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        Grid.SetColumn(enter, 2);
        grid.Children.Add(enter);
        var row = new Button { Classes = { "row" }, Content = grid, Tag = enter };
        AutomationProperties.SetName(row, snippet.DisplayName);
        row.Click += (_, _) => _ = InsertAsync(snippet);
        row.PointerMoved += (_, _) =>
        {
            if (_selected != index)
            {
                _selected = index;
                UpdateSelection();
            }
        };
        return row;
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i].Row;
            var selected = i == _selected;
            row.Classes.Set("selectedRow", selected);
            row.Background = selected ? this.FindResource("AccentFaintBrush") as IBrush : Brushes.Transparent;
            if (row.Tag is TextBlock enter)
            {
                enter.Opacity = selected ? 1 : 0;
            }
        }

        if (_selected >= 0 && _selected < _rows.Count)
        {
            _rows[_selected].Row.BringIntoView();
        }
    }

    /// <summary>Types the snippet where the caret was: variables now, then the caret flow (no sound).</summary>
    private async Task InsertAsync(TextSnippet snippet)
    {
        var target = _target;
        Dismiss(FloatingCloseReason.Action);
        string? clipboard = null;
        if (SnippetVariables.UsesClipboard(snippet.Replacement))
        {
            var (content, completed) = await _lane.RunAsync(TimeSpan.FromSeconds(1), c => c.Read(ClipboardReadParts.Text)).ConfigureAwait(true);
            clipboard = completed && content is { IsConcealed: false } ? content.Text : null;
        }

        var text = SnippetVariables.Expand(snippet.Replacement, DateTimeOffset.Now, CultureInfo.CurrentCulture, TimeZoneInfo.Local, clipboard);
        var outcome = await _caret.TypeAsync(target, text).ConfigureAwait(true);
        if (outcome == CaretOutcome.Elevated)
        {
            _hud?.Show(L.Get("win.clipboard.elevatedTarget"), HudStyle.Warning);
        }
        else if (outcome == CaretOutcome.CopiedOnly)
        {
            // No app to type into (the library was opened over the desktop or the taskbar).
            _caret.Foreground.Beep();
        }
    }

    protected override void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss(FloatingCloseReason.Escape);
                break;
            case Key.Enter when _rows.Count > 0:
                _ = InsertAsync(_rows[_selected].Snippet);
                break;
            case Key.Down:
                _selected = Math.Min(_rows.Count - 1, _selected + 1);
                UpdateSelection();
                break;
            case Key.Up:
                _selected = Math.Max(0, _selected - 1);
                UpdateSelection();
                break;
            case >= Key.D1 and <= Key.D9 when e.KeyModifiers == KeyModifiers.Control:
                var n = e.Key - Key.D1;
                if (n < _rows.Count)
                {
                    _ = InsertAsync(_rows[n].Snippet);
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
