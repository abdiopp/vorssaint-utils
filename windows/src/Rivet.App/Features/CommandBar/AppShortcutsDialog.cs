// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// "App shortcuts" (spec 06 §3.8.9): every installed app with its alias, its
/// own global shortcut and its pin, filterable by All / Pinned / With
/// shortcuts. One line per stable key; edits save immediately.
/// </summary>
public sealed class AppShortcutsDialog : Window
{
    private readonly CommandBarController _controller;
    private readonly CommandBarPreferences _preferences;
    private readonly ICommandBarPlatform _platform;
    private readonly TextBox _search = new() { Width = 260 };
    private readonly ComboBox _filter = new() { MinWidth = 160 };
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly TextBlock _message = new() { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _loading = new() { Classes = { "caption" }, Margin = new Thickness(4, 12), HorizontalAlignment = HorizontalAlignment.Center };
    private List<CommandRow> _apps = [];

    public AppShortcutsDialog(IServiceProvider services)
    {
        _controller = services.GetRequiredService<CommandBarController>();
        _preferences = services.GetRequiredService<CommandBarPreferences>();
        _platform = services.GetRequiredService<ICommandBarPlatform>();
        Title = L.Get("commandBar.appCenterTitle");
        Width = 780;
        Height = 560;
        MinWidth = 600;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush").ToBinding());

        _search.PlaceholderText = L.Get("commandBar.searchPlaceholder");
        AutomationProperties.SetName(_search, L.Get("commandBar.searchPlaceholder"));
        _search.TextChanged += (_, _) => Fill();
        _filter.ItemsSource = new[] { L.Get("commandBar.categoryAll"), L.Get("commandBar.pinnedTitle"), L.Get("commandBar.appShortcutsFilter") };
        _filter.SelectedIndex = 0;
        AutomationProperties.SetName(_filter, L.Get("commandBar.appCenterTitle"));
        _filter.SelectionChanged += (_, _) => Fill();
        _list.ItemTemplate = new FuncDataTemplate<CommandRow>((row, _) => row is null ? new Panel() : Line(row), supportsRecycling: false);
        _loading.Text = L.Get("commandBar.stillLooking");

        var done = new Button { Content = L.Get("win.commandBar.doneButton"), Classes = { "accent" }, IsDefault = true, MinWidth = 90 };
        done.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        header.Children.Add(new TextBlock { Text = L.Get("commandBar.appCenterCaption"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_search, 1);
        header.Children.Add(_search);
        Grid.SetColumn(_filter, 2);
        header.Children.Add(_filter);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,190,60"), ColumnSpacing = 10, Margin = new Thickness(12, 0, 24, 0) };
        foreach (var (key, column) in new[] { ("commandBar.kindApp", 0), ("commandBar.appAliasLabel", 1), ("commandBar.appShortcutLabel", 2), ("commandBar.pinnedTitle", 3) })
        {
            var label = new TextBlock { Text = L.Get(key).ToUpper(Localizer.Current.Culture), Classes = { "sectionTitle" }, FontSize = 10 };
            Grid.SetColumn(label, column);
            columns.Children.Add(label);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(_message);
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(16) };
        body.Children.Add(header);
        Grid.SetRow(columns, 1);
        body.Children.Add(columns);
        var listArea = new Panel { Children = { _list, _loading } };
        Grid.SetRow(listArea, 2);
        body.Children.Add(listArea);
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);
        Content = body;
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Shows the given rows without scanning (snapshot tests).</summary>
    public void ShowRows(IEnumerable<CommandRow> rows)
    {
        _apps = rows.ToList();
        _loading.IsVisible = false;
        Fill();
    }

    private async Task LoadAsync()
    {
        if (_apps.Count > 0)
        {
            return;
        }

        try
        {
            var apps = await Task.Run(() => _platform.GetAppsAsync(CancellationToken.None)).ConfigureAwait(true);
            ShowRows(_controller.Catalog.AppRows(apps, [])
                .GroupBy(r => r.StableKey, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        }
        catch (Exception ex)
        {
            Rivet.Core.Diagnostics.Log.Warn("commandBar", $"App shortcuts: listing apps failed ({ex.GetType().Name}).");
            _loading.Text = L.Get("quickToggles.actionFailed");
        }
    }

    private void Fill()
    {
        var query = CommandBarSession.Fold(_search.Text ?? string.Empty);
        var pins = _preferences.Pins.ToHashSet(StringComparer.Ordinal);
        var shortcuts = _preferences.RowShortcuts;
        var aliases = _preferences.Aliases;
        _list.ItemsSource = _apps.Where(row =>
        {
            var key = row.StableKey;
            if (_filter.SelectedIndex == 1 && !pins.Contains(key))
            {
                return false;
            }

            if (_filter.SelectedIndex == 2 && !shortcuts.ContainsKey(key))
            {
                return false;
            }

            return query.Length == 0
                   || CommandBarSession.Fold(row.Title).Contains(query, StringComparison.Ordinal)
                   || (aliases.TryGetValue(key, out var alias) && CommandBarSession.Fold(alias).Contains(query, StringComparison.Ordinal));
        }).ToList();
    }

    private Control Line(CommandRow row)
    {
        var key = row.StableKey;
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Classes = { "iconTile" }, Child = ClipboardUi.Icon("Apps", 13, "AccentBrush") },
                new TextBlock { Text = row.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };

        var alias = new TextBox
        {
            Text = _preferences.Aliases.GetValueOrDefault(key, string.Empty),
            PlaceholderText = L.Get("commandBar.aliasPlaceholder"),
            MaxLength = CommandBarSettings.MaxAliasLength,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(alias, $"{L.Get("commandBar.appAliasLabel")} · {row.Title}");
        void CommitAlias()
        {
            var text = alias.Text ?? string.Empty;
            if (text.Trim() == _preferences.Aliases.GetValueOrDefault(key, string.Empty))
            {
                return;
            }

            if (_preferences.SetAlias(key, text) is { } other)
            {
                var otherTitle = _apps.FirstOrDefault(a => a.StableKey == other)?.Title ?? other;
                ShowMessage(L.Format("commandBar.aliasTakenFormat", otherTitle));
            }
            else
            {
                ShowMessage(null);
            }
        }

        alias.LostFocus += (_, _) => CommitAlias();
        alias.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                CommitAlias();
                e.Handled = true;
            }
        };

        var recorder = new ShortcutRecorder { MinWidth = 140 };
        if (_preferences.RowShortcuts.TryGetValue(key, out var chord))
        {
            recorder.Chord = chord;
        }

        AutomationProperties.SetName(recorder, $"{L.Get("commandBar.appShortcutLabel")} · {row.Title}");
        recorder.ChordRecorded += (_, e) =>
        {
            if (e.Chord.IsEmpty)
            {
                return;
            }

            var problem = _controller.TrySetRowShortcut(key, e.Chord);
            ShowMessage(problem);
            recorder.Chord = _preferences.RowShortcuts.GetValueOrDefault(key);
        };
        var clear = ClipboardUi.IconButton("Dismiss", L.Get("commandBar.actionShortcutRemove"), () =>
        {
            _preferences.RemoveRowShortcut(key);
            recorder.Chord = default;
        }, 12);
        var shortcut = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { recorder, clear } };
        if (_controller.RefusedRowShortcuts.Contains(key))
        {
            var warning = ClipboardUi.Icon("Warning", 14, "WarningBrush");
            ToolTip.SetTip(warning, L.Get("win.commandBar.rowShortcutRefused"));
            shortcut.Children.Add(warning);
        }

        var pin = new ToggleButton
        {
            IsChecked = _preferences.Pins.Contains(key),
            Content = ClipboardUi.Icon("Pin", 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(pin, L.Get("commandBar.actionPin"));
        AutomationProperties.SetName(pin, $"{L.Get("commandBar.actionPin")} · {row.Title}");
        pin.IsCheckedChanged += (_, _) =>
        {
            var pinned = _preferences.Pins.Contains(key);
            if (pinned != (pin.IsChecked == true))
            {
                _preferences.TogglePin(key);
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200,190,60"), ColumnSpacing = 10, Margin = new Thickness(0, 2) };
        grid.Children.Add(name);
        Grid.SetColumn(alias, 1);
        grid.Children.Add(alias);
        Grid.SetColumn(shortcut, 2);
        grid.Children.Add(shortcut);
        Grid.SetColumn(pin, 3);
        grid.Children.Add(pin);
        return grid;
    }

    private void ShowMessage(string? text)
    {
        _message.Text = text;
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("WarningBrush").ToBinding());
    }
}
