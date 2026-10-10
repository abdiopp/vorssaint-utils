// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Localization;
using Rivet.Core.Snippets;

namespace Rivet.App.Features.Snippets;

/// <summary>The result of the snippet editor.</summary>
public enum SnippetEditorResult
{
    Cancelled,
    Saved,
    Deleted,
}

/// <summary>
/// The snippet editor (spec 06 §3.6.1): name, trigger, expansion mode,
/// capitalization, folder (with suggestions), library visibility and the text,
/// with a date/time builder that inserts or edits tokens at the caret.
/// </summary>
public sealed class SnippetEditorDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _trigger = new() { FontFamily = ClipboardUi.MonoFont };
    private readonly ComboBox _mode = new() { MinWidth = 220 };
    private readonly CheckBox _ignoreCase = new();
    private readonly TextBox _folder = new();
    private readonly CheckBox _showInLibrary = new();
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96, MaxHeight = 220 };
    private readonly Button _dateButton = new();
    private readonly Button _save = new() { Classes = { "accent" }, MinWidth = 90 };
    private readonly TextBlock _problem = new() { Classes = { "caption" }, IsVisible = false };
    private readonly TextSnippet _original;
    private readonly IReadOnlyList<TextSnippet> _others;
    private SnippetEditorResult _result;

    public SnippetEditorDialog(TextSnippet snippet, IReadOnlyList<TextSnippet> all, bool isNew)
    {
        _original = snippet;
        _others = all;
        Title = L.Get(isNew ? "snippets.newTitle" : "snippets.editTitle");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = snippet.Name;
        _name.PlaceholderText = L.Get("snippets.namePlaceholder");
        _trigger.Text = snippet.Trigger;
        _trigger.PlaceholderText = L.Get("snippets.triggerPlaceholder");
        _mode.ItemsSource = new[] { L.Get("snippets.expansionImmediate"), L.Get("snippets.expansionDelimiter") };
        _mode.SelectedIndex = snippet.Expansion == SnippetExpansion.Immediate ? 0 : 1;
        _ignoreCase.Content = L.Get("snippets.ignoreCaseLabel");
        _ignoreCase.IsChecked = snippet.IgnoresCase;
        _folder.Text = snippet.Folder;
        _folder.PlaceholderText = L.Get("snippets.folderPlaceholder");
        var folders = all.Select(s => s.Folder).Where(f => f.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (folders.Count > 0)
        {
            var menu = new MenuFlyout();
            foreach (var folder in folders)
            {
                var item = new MenuItem { Header = folder };
                item.Click += (_, _) => _folder.Text = folder;
                menu.Items.Add(item);
            }

            var suggestions = ClipboardUi.IconButton("ChevronDown", L.Get("snippets.folderLabel"), () => { }, 12);
            suggestions.Flyout = menu;
            _folder.InnerRightContent = suggestions;
        }

        _showInLibrary.Content = L.Get("snippets.showInLibraryLabel");
        _showInLibrary.IsChecked = snippet.ShowsInLibrary;
        _text.Text = snippet.Replacement;
        _text.PlaceholderText = L.Get("snippets.replacementPlaceholder");
        foreach (var (box, key) in new[] { (_name, "snippets.nameLabel"), (_trigger, "snippets.triggerLabel"), (_folder, "snippets.folderLabel"), (_text, "snippets.replacementLabel") })
        {
            AutomationProperties.SetName(box, L.Get(key));
        }

        _dateButton.Click += (_, _) => OpenBuilder();
        _text.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty)
            {
                UpdateDateButton();
                Validate();
            }
        };
        _trigger.TextChanged += (_, _) => Validate();
        _ignoreCase.IsCheckedChanged += (_, _) => Validate();

        _save.Content = L.Get("snippets.saveButton");
        _save.Click += (_, _) =>
        {
            _result = SnippetEditorResult.Saved;
            Close();
        };
        var cancel = new Button { Content = L.Get("clipboard.cancel"), IsCancel = true, MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _save } };
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (!isNew)
        {
            var delete = new Button { Content = L.Get("snippets.deleteButton") };
            delete.Bind(Button.ForegroundProperty, delete.GetResourceObservable("DangerBrush").ToBinding());
            delete.Click += (_, _) =>
            {
                _result = SnippetEditorResult.Deleted;
                Close();
            };
            bottom.Children.Add(delete);
        }

        Grid.SetColumn(buttons, 2);
        bottom.Children.Add(buttons);

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), RowSpacing = 10, ColumnSpacing = 12 };
        void AddRow(int row, string labelKey, Control control)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = L.Get(labelKey), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            form.Children.Add(label);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            form.Children.Add(control);
        }

        AddRow(0, "snippets.nameLabel", _name);
        AddRow(1, "snippets.triggerLabel", _trigger);
        AddRow(2, "snippets.expansionLabel", _mode);
        AddRow(3, "snippets.folderLabel", _folder);
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _ignoreCase, _showInLibrary } };
        Grid.SetRow(checks, 4);
        Grid.SetColumn(checks, 1);
        form.Children.Add(checks);

        var textHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        textHeader.Children.Add(new TextBlock { Text = L.Get("snippets.replacementLabel"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_dateButton, 1);
        textHeader.Children.Add(_dateButton);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                form,
                textHeader,
                _text,
                new TextBlock { Text = L.Get("snippets.variablesHint"), Classes = { "caption" } },
                new TextBlock { Text = L.Get("snippets.editorFormatCaption"), Classes = { "caption" } },
                _problem,
                bottom,
            },
        };
        _problem.Bind(TextBlock.ForegroundProperty, _problem.GetResourceObservable("WarningBrush").ToBinding());
        UpdateDateButton();
        Validate();
    }

    /// <summary>The edited snippet (after <see cref="SnippetEditorResult.Saved"/>).</summary>
    public TextSnippet Edited => _original with
    {
        Name = (_name.Text ?? string.Empty).Trim(' '),
        Trigger = SnippetSettings.SanitizeTrigger(_trigger.Text ?? string.Empty),
        Expansion = _mode.SelectedIndex == 0 ? SnippetExpansion.Immediate : SnippetExpansion.AfterDelimiter,
        IgnoresCase = _ignoreCase.IsChecked == true,
        Folder = (_folder.Text ?? string.Empty).Trim(),
        ShowsInLibrary = _showInLibrary.IsChecked == true,
        Replacement = _text.Text ?? string.Empty,
    };

    public async Task<SnippetEditorResult> ShowAsync(Window owner)
    {
        await ShowDialog(owner).ConfigureAwait(true);
        return _result;
    }

    private void Validate()
    {
        var validation = SnippetValidator.Validate(Edited, _others);
        _save.IsEnabled = validation == SnippetValidation.Valid;
        _problem.Text = validation switch
        {
            SnippetValidation.TriggerTooShort when (_trigger.Text ?? string.Empty).Length > 0 => L.Get("snippets.triggerTooShort"),
            SnippetValidation.DuplicateTrigger => L.Get("snippets.duplicateTrigger"),
            _ => string.Empty,
        };
        _problem.IsVisible = _problem.Text.Length > 0;
    }

    private DateToken? TokenAtCaret() => SnippetVariables.TokenAt(_text.Text ?? string.Empty, _text.CaretIndex);

    private void UpdateDateButton() =>
        _dateButton.Content = L.Get(TokenAtCaret() is null ? "snippets.dateTimeInsertButton" : "snippets.dateTimeEditButton");

    private void OpenBuilder()
    {
        var existing = TokenAtCaret();
        var selectionStart = Math.Min(_text.SelectionStart, _text.SelectionEnd);
        var selectionEnd = Math.Max(_text.SelectionStart, _text.SelectionEnd);
        var builder = new DateTimeBuilder(existing);
        var flyout = new Flyout { Content = builder, Placement = PlacementMode.BottomEdgeAlignedRight };
        builder.Done += (_, token) =>
        {
            flyout.Hide();
            if (token is null)
            {
                return;
            }

            var text = _text.Text ?? string.Empty;
            int start;
            int end;
            if (existing is not null)
            {
                start = existing.Start;
                end = existing.End;
            }
            else
            {
                start = Math.Clamp(selectionStart, 0, text.Length);
                end = Math.Clamp(selectionEnd, start, text.Length);
            }

            _text.Text = text[..start] + token + text[end..];
            _text.CaretIndex = start + token.Length;
            _text.Focus();
        };
        flyout.ShowAt(_dateButton);
    }
}

/// <summary>
/// The date/time variable builder (spec 06 §3.6.4): type, style, time zone
/// search, custom pattern and a live preview. Named styles freeze the region's
/// pattern of this moment into the token.
/// </summary>
public sealed class DateTimeBuilder : UserControl
{
    private static readonly DateStyle[] StyleChoices = [DateStyle.Short, DateStyle.Medium, DateStyle.Long, DateStyle.Full, DateStyle.Iso8601, DateStyle.Custom];

    private readonly RadioButton[] _kinds;
    private readonly ComboBox _style = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _pattern = new() { FontFamily = ClipboardUi.MonoFont };
    private readonly TextBlock _zoneValue = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _zoneSearch = new();
    private readonly TextBlock _zoneStatus = new() { Classes = { "caption" }, FontSize = 11 };
    private readonly ListBox _zones = new() { MaxHeight = 140 };
    private readonly TextBlock _preview = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _note = new() { Classes = { "caption" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button _confirm = new() { Classes = { "accent" } };
    private string? _zone;

    public DateTimeBuilder(DateToken? existing)
    {
        Width = 340;
        var culture = CultureInfo.CurrentCulture;
        _kinds =
        [
            new RadioButton { Content = L.Get("snippets.dateTimeKindDate"), GroupName = "dtkind" },
            new RadioButton { Content = L.Get("snippets.dateTimeKindTime"), GroupName = "dtkind" },
            new RadioButton { Content = L.Get("snippets.dateTimeKindDateTime"), GroupName = "dtkind" },
        ];
        var kind = existing?.Kind ?? DateTokenKind.DateTime;
        _kinds[(int)kind].IsChecked = true;
        _style.ItemsSource = StyleChoices.Select(StyleName).ToList();
        var style = existing is null ? DateStyle.Iso8601 : DateStyles.StyleOf(existing.Kind, existing.Pattern, culture);
        _style.SelectedIndex = Array.IndexOf(StyleChoices, style);
        _pattern.Text = style == DateStyle.Custom ? existing?.Pattern : string.Empty;
        _pattern.PlaceholderText = "yyyy-MM-dd HH:mm";
        AutomationProperties.SetName(_pattern, L.Get("snippets.dateTimePatternLabel"));
        _zone = existing?.TimeZoneId;
        _zoneSearch.PlaceholderText = L.Get("snippets.dateTimeTimezoneSearchPlaceholder");
        AutomationProperties.SetName(_zoneSearch, L.Get("snippets.dateTimeTimezoneSearchPlaceholder"));
        _confirm.Content = L.Get(existing is null ? "snippets.dateTimeConfirmInsert" : "snippets.dateTimeConfirmUpdate");

        foreach (var radio in _kinds)
        {
            radio.IsCheckedChanged += (_, _) => Refresh();
        }

        _style.SelectionChanged += (_, _) => Refresh();
        _pattern.TextChanged += (_, _) => Refresh();
        _zoneSearch.TextChanged += (_, _) => SearchZones();
        _zoneSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && TimeZones.Resolve(_zoneSearch.Text ?? string.Empty) is { } resolved)
            {
                _zone = resolved;
                e.Handled = true;
                Refresh();
            }
        };
        _zones.SelectionChanged += (_, _) =>
        {
            if (_zones.SelectedItem is string id)
            {
                _zone = id;
                Refresh();
            }
        };
        var clearZone = ClipboardUi.IconButton("Dismiss", L.Get("snippets.dateTimeTimezoneClear"), () =>
        {
            _zone = null;
            Refresh();
        }, 11);
        var cancel = new Button { Content = L.Get("clipboard.cancel") };
        cancel.Click += (_, _) => Done?.Invoke(this, null);
        _confirm.Click += (_, _) => Done?.Invoke(this, Token());

        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var radio in _kinds)
        {
            kinds.Children.Add(radio);
        }

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("snippets.dateTimeTypeLabel"), kinds,
                Label("snippets.dateTimeStyleLabel"), _style, _note,
                Label("snippets.dateTimePatternLabel"), _pattern,
                Label("snippets.dateTimeTimezoneLabel"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _zoneValue, clearZone } },
                _zoneSearch, _zoneStatus, _zones,
                Label("snippets.dateTimePreviewLabel"), _preview,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _confirm } },
            },
        };
        SearchZones();
        Refresh();
    }

    /// <summary>Raised with the token text, or null when cancelled.</summary>
    public event EventHandler<string?>? Done;

    private static TextBlock Label(string key) => new() { Text = L.Get(key), Classes = { "caption" }, FontWeight = FontWeight.SemiBold };

    private static string StyleName(DateStyle style) => style switch
    {
        DateStyle.Short => L.Get("snippets.dateTimeStyleShort"),
        DateStyle.Medium => L.Get("snippets.dateTimeStyleMedium"),
        DateStyle.Long => L.Get("snippets.dateTimeStyleLong"),
        DateStyle.Full => L.Get("snippets.dateTimeStyleFull"),
        DateStyle.Iso8601 => L.Get("snippets.dateTimeStyleISO8601"),
        _ => L.Get("snippets.dateTimeStyleCustom"),
    };

    private DateTokenKind Kind => (DateTokenKind)Math.Max(0, Array.FindIndex(_kinds, r => r.IsChecked == true));

    private DateStyle Style => _style.SelectedIndex is >= 0 and < 6 ? StyleChoices[_style.SelectedIndex] : DateStyle.Iso8601;

    private string Pattern => Style == DateStyle.Custom ? (_pattern.Text ?? string.Empty) : DateStyles.Pattern(Kind, Style, CultureInfo.CurrentCulture);

    private string? Token() => Pattern.Length == 0 ? null : DateToken.Build(Kind, Pattern, _zone);

    private void SearchZones()
    {
        var query = _zoneSearch.Text ?? string.Empty;
        var matches = query.Trim().Length == 0 ? [] : TimeZones.Search(query);
        _zones.ItemsSource = matches;
        _zones.IsVisible = matches.Count > 0;
        var resolved = query.Trim().Length > 0 ? TimeZones.Resolve(query) : null;
        _zoneStatus.IsVisible = query.Trim().Length > 0 && (resolved is not null || matches.Count == 0);
        _zoneStatus.Text = resolved is not null ? "✓ " + L.Get("snippets.dateTimeTimezoneValid") : "✕ " + L.Get("snippets.dateTimeTimezoneInvalid");
    }

    private void Refresh()
    {
        var custom = Style == DateStyle.Custom;
        _pattern.IsVisible = custom;
        _note.IsVisible = Style is DateStyle.Short or DateStyle.Medium or DateStyle.Long or DateStyle.Full;
        _note.Text = L.Get("snippets.dateTimeStyleLocaleNote");
        _zoneValue.Text = _zone ?? L.Get("snippets.dateTimeTimezoneDeviceDefault");
        _confirm.IsEnabled = Pattern.Length > 0;
        var zone = _zone is { } id ? TimeZones.Find(id) ?? TimeZoneInfo.Local : TimeZoneInfo.Local;
        _preview.Text = Pattern.Length == 0 ? string.Empty : IcuDateFormat.Format(DateTimeOffset.Now, Pattern, zone, CultureInfo.CurrentCulture);
    }
}
