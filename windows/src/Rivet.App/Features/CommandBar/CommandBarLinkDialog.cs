// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// "Your shortcuts" editor (spec 06 §3.8.10): Site or link / Folder or file /
/// Script, name, destination with a file picker, placeholder chips for links
/// and places, and the two script switches. Save needs a name and a destination.
/// </summary>
public sealed class CommandBarLinkDialog : Window
{
    private readonly CommandBarLink _original;
    private readonly ComboBox _kind = new() { MinWidth = 200 };
    private readonly TextBox _name = new();
    private readonly TextBox _destination = new();
    private readonly Button _choose = new();
    private readonly StackPanel _placeholders = new() { Spacing = 6 };
    private readonly StackPanel _scriptOptions = new() { Spacing = 6 };
    private readonly CheckBox _runsBare = new();
    private readonly CheckBox _runsDirectly = new();
    private readonly Button _save = new() { Classes = { "accent" }, MinWidth = 90 };
    private bool _saved;

    public CommandBarLinkDialog(CommandBarLink? link)
    {
        _original = link ?? new CommandBarLink();
        Title = L.Get("commandBar.linksTitle");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush").ToBinding());

        _kind.ItemsSource = new[] { L.Get("commandBar.linkKindLink"), L.Get("commandBar.linkKindPlace"), L.Get("commandBar.linkKindScript") };
        _kind.SelectedIndex = (int)_original.Kind;
        _kind.SelectionChanged += (_, _) => UpdateKind();
        _name.Text = _original.Name;
        _name.PlaceholderText = L.Get("win.commandBar.linkNamePlaceholder");
        _destination.Text = _original.Destination;
        _destination.PlaceholderText = "https://example.com/search?q={query}";
        AutomationProperties.SetName(_kind, L.Get("commandBar.linksTitle"));
        AutomationProperties.SetName(_name, L.Get("win.commandBar.linkNameLabel"));
        AutomationProperties.SetName(_destination, L.Get("commandBar.linkDestinationLabel"));
        _choose.Content = L.Get("win.commandBar.chooseButton");
        _choose.Click += async (_, _) => await ChooseAsync().ConfigureAwait(true);
        _name.TextChanged += (_, _) => Validate();
        _destination.TextChanged += (_, _) => Validate();

        var chips = new WrapPanel();
        foreach (var (token, meaning) in new[]
                 {
                     ("{query}", "commandBar.placeholderQuery"), ("{clipboard}", "commandBar.placeholderClipboard"),
                     ("{selection}", "commandBar.placeholderSelection"), ("{date}", "commandBar.placeholderDate"),
                 })
        {
            var text = token;
            var chip = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = token, FontFamily = ClipboardUi.MonoFont, FontSize = 12 },
                        new TextBlock { Text = L.Get(meaning), Classes = { "caption" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Padding = new Thickness(8, 2),
                Margin = new Thickness(0, 0, 6, 6),
                MinHeight = 0,
            };
            chip.Click += (_, _) =>
            {
                var current = _destination.Text ?? string.Empty;
                var caret = Math.Clamp(_destination.CaretIndex, 0, current.Length);
                _destination.Text = current[..caret] + text + current[caret..];
                _destination.CaretIndex = caret + text.Length;
                _destination.Focus();
            };
            chips.Children.Add(chip);
        }

        _placeholders.Children.Add(new TextBlock { Text = L.Get("commandBar.linkPlaceholdersHint"), Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _placeholders.Children.Add(chips);
        _runsBare.Content = L.Get("commandBar.scriptRunsWithoutArgument");
        _runsBare.IsChecked = _original.RunsWithoutArgument;
        _runsDirectly.Content = L.Get("commandBar.scriptRunsDirectly");
        _runsDirectly.IsChecked = _original.RunsDirectly;
        _scriptOptions.Children.Add(new TextBlock { Text = L.Get("win.commandBar.scriptHint"), Classes = { "caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        _scriptOptions.Children.Add(_runsBare);
        _scriptOptions.Children.Add(_runsDirectly);

        _save.Content = L.Get("snippets.saveButton");
        _save.IsDefault = true;
        _save.Click += (_, _) =>
        {
            _saved = true;
            Close();
        };
        var cancel = new Button { Content = L.Get("clipboard.cancel"), IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => Close();

        var destinationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        destinationRow.Children.Add(_destination);
        Grid.SetColumn(_choose, 1);
        destinationRow.Children.Add(_choose);

        Content = new Border
        {
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    _kind,
                    Label("win.commandBar.linkNameLabel"),
                    _name,
                    Label("commandBar.linkDestinationLabel"),
                    destinationRow,
                    _placeholders,
                    _scriptOptions,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { cancel, _save } },
                },
            },
        };
        UpdateKind();
        Validate();
    }

    private CommandBarLinkKind Kind => (CommandBarLinkKind)Math.Max(0, _kind.SelectedIndex);

    /// <summary>The edited entry (names and destinations trimmed).</summary>
    public CommandBarLink Edited => _original with
    {
        Kind = Kind,
        Name = (_name.Text ?? string.Empty).Trim(),
        Destination = (_destination.Text ?? string.Empty).Trim(),
        RunsWithoutArgument = Kind == CommandBarLinkKind.Script && _runsBare.IsChecked == true,
        RunsDirectly = Kind == CommandBarLinkKind.Script && _runsDirectly.IsChecked == true,
    };

    /// <summary>The saved entry, or null when cancelled.</summary>
    public async Task<CommandBarLink?> ShowAsync(Window owner)
    {
        await ShowDialog(owner).ConfigureAwait(true);
        return _saved ? Edited : null;
    }

    private static TextBlock Label(string key) => new() { Text = L.Get(key), Classes = { "caption" }, Margin = new Thickness(0, 4, 0, -4) };

    private void UpdateKind()
    {
        _choose.IsVisible = Kind != CommandBarLinkKind.Link;
        _placeholders.IsVisible = Kind != CommandBarLinkKind.Script;
        _scriptOptions.IsVisible = Kind == CommandBarLinkKind.Script;
        _destination.PlaceholderText = Kind switch
        {
            CommandBarLinkKind.Link => "https://example.com/search?q={query}",
            CommandBarLinkKind.Place => @"~\Documents",
            _ => @"~\Scripts\hello.ps1",
        };
    }

    private void Validate() =>
        _save.IsEnabled = (_name.Text ?? string.Empty).Trim().Length > 0 && (_destination.Text ?? string.Empty).Trim().Length > 0;

    private async Task ChooseAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = L.Get("win.commandBar.chooseButton"),
            FileTypeFilter = Kind == CommandBarLinkKind.Script
                ? [new FilePickerFileType(L.Get("commandBar.linkKindScript")) { Patterns = ["*.ps1", "*.cmd", "*.bat", "*.exe", "*.com"] }]
                : null,
        }).ConfigureAwait(true);
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        _destination.Text = path;
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _name.Text = Path.GetFileNameWithoutExtension(path);
        }
    }
}
