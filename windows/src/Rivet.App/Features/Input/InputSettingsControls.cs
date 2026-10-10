// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Diagnostics;
using Rivet.Core.Input;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Input;

/// <summary>Builders shared by the input pages on top of <see cref="SettingsPage"/>.</summary>
public abstract class InputSettingsPage : SettingsPage
{
    protected InputSettingsPage(ISettingsStore settings)
        : base(settings)
    {
    }

    /// <summary>A stepper row ("25 ms") bound to an integer setting.</summary>
    protected SettingsRow Stepper(Setting<int> setting, string? icon, string titleKey, string? captionKey, int min, int max, int step, string unit = "ms")
    {
        var property = Track(Settings.Bind(setting));
        var box = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            Value = property.Value,
            FormatString = "0",
            Width = 120,
            ClipValueToMinMax = true,
        };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } value)
            {
                property.Value = (int)Math.Round(value);
            }
        };
        property.PropertyChanged += (_, _) => box.Value = property.Value;
        AutomationProperties.SetName(box, L.Get(titleKey));
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center } } };
        return Row(icon, L.Get(titleKey), captionKey is null ? null : L.Get(captionKey), trailing);
    }

    /// <summary>A slider row bound to an integer setting, with a value label.</summary>
    protected SettingsRow IntSlider(Setting<int> setting, string? icon, string titleKey, int min, int max, int step, Func<int, string> format, string? captionKey = null)
    {
        var property = Track(Settings.Bind(setting));
        var slider = new Avalonia.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Width = 180,
            Value = property.Value,
        };
        var label = new TextBlock { MinWidth = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = format(property.Value) };
        slider.ValueChanged += (_, e) =>
        {
            var value = (int)Math.Round(e.NewValue);
            property.Value = value;
            label.Text = format(value);
        };
        property.PropertyChanged += (_, _) =>
        {
            slider.Value = property.Value;
            label.Text = format(property.Value);
        };
        AutomationProperties.SetName(slider, L.Get(titleKey));
        return Row(icon, L.Get(titleKey), captionKey is null ? null : L.Get(captionKey), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, label } });
    }

    /// <summary>A slider row for a millisecond setting stored as a double.</summary>
    protected SettingsRow MsSlider(Setting<double> setting, string? icon, string titleKey, double min, double max, double step)
    {
        var property = Track(Settings.Bind(setting));
        var slider = new Avalonia.Controls.Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step, TickFrequency = step, IsSnapToTickEnabled = true, Width = 180, Value = property.Value };
        var label = new TextBlock { MinWidth = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = FormatMs(property.Value) };
        slider.ValueChanged += (_, e) =>
        {
            property.Value = e.NewValue;
            label.Text = FormatMs(e.NewValue);
        };
        AutomationProperties.SetName(slider, L.Get(titleKey));
        return Row(icon, L.Get(titleKey), null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { slider, label } });
    }

    protected static string FormatMs(double ms) => $"{Math.Round(ms):0} ms";

    /// <summary>Shows <paramref name="control"/> only while <paramref name="setting"/> is on.</summary>
    protected Control ShowWhen(Control control, Setting<bool> setting)
    {
        control.IsVisible = Settings.Get(setting);
        Track(Settings.Observe(setting.Key, () => Dispatcher.UIThread.Post(() => control.IsVisible = Settings.Get(setting))));
        return control;
    }

    /// <summary>Shows <paramref name="control"/> only while <paramref name="visible"/> holds, re-checked when any of <paramref name="settings"/> changes.</summary>
    protected Control ShowWhen(Control control, Func<bool> visible, params SettingDefinition[] settings)
    {
        control.IsVisible = visible();
        Track(Settings.Observe(() => Dispatcher.UIThread.Post(() => control.IsVisible = visible()), settings));
        return control;
    }

    /// <summary>A coloured status line (green "Working now", orange warnings).</summary>
    protected static TextBlock StatusText(string text, string brushKey = "MetricGreenBrush")
    {
        var block = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40, 0, 0, 0) };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(brushKey).ToBinding());
        return block;
    }

    /// <summary>
    /// A card whose separators follow the visibility of the row below them,
    /// so rows shown only in some modes never leave doubled lines behind.
    /// </summary>
    protected static SettingsCard LiveCard(string? titleKey, params Control?[] rows)
    {
        var stack = new StackPanel { Spacing = 2 };
        var first = true;
        foreach (var row in rows.OfType<Control>())
        {
            if (!first)
            {
                var separator = new Border { Classes = { "separator" }, Margin = new Thickness(40, 4, 0, 4) };
                separator.Bind(IsVisibleProperty, row.GetObservable(IsVisibleProperty));
                stack.Children.Add(separator);
            }

            stack.Children.Add(row);
            first = false;
        }

        return new SettingsCard { Header = titleKey is null ? null : L.Get(titleKey), Content = stack };
    }

    /// <summary>An indented note under a row.</summary>
    protected static TextBlock IndentedNote(string text, string brushKey = "TextSecondaryBrush")
    {
        var note = Note(text, brushKey);
        note.Margin = new Thickness(40, 0, 0, 0);
        return note;
    }
}

/// <summary>
/// "Apps to leave alone" for one feature (spec 07 §3.7.8): a disclosure with a
/// count, expanded when the list has entries; each row shows the app's name
/// and path with a Remove button; "Add an app…" opens a picker over running
/// apps with a file chooser for any .exe.
/// </summary>
public sealed class AppExclusionEditor : UserControl
{
    private readonly ISettingsStore _settings;
    private readonly Setting<string[]> _setting;
    private readonly IAppCatalog? _catalog;
    private readonly string _titleKey;
    private readonly string? _emptyKey;
    private readonly bool _collapsible;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Disclosure? _disclosure;
    private readonly TextBlock _header = new() { FontSize = 14 };
    private readonly IDisposable _observer;

    public AppExclusionEditor(ISettingsStore settings, Setting<string[]> setting, IAppCatalog? catalog, string? captionKey,
        string titleKey = "mouseExceptions.listTitle", string addKey = "mouseExceptions.addButton", string? emptyKey = null, bool collapsible = true)
    {
        _settings = settings;
        _setting = setting;
        _catalog = catalog;
        _titleKey = titleKey;
        _emptyKey = emptyKey;
        _collapsible = collapsible;

        var add = new Button { Content = L.Get(addKey), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        add.Click += async (_, _) => await AddAsync();
        var body = new StackPanel { Spacing = 6 };
        if (captionKey is not null)
        {
            body.Children.Add(new TextBlock { Text = L.Get(captionKey), Classes = { "caption" } });
        }

        body.Children.Add(_rows);
        body.Children.Add(add);

        if (collapsible)
        {
            _disclosure = new Disclosure(string.Empty, body);
            Content = _disclosure;
        }
        else
        {
            body.Children.Insert(0, _header);
            Content = body;
        }

        Margin = new Thickness(collapsible ? 36 : 40, 2, 0, 2);
        _observer = settings.Observe(setting.Key, () => Dispatcher.UIThread.Post(Rebuild));
        Rebuild();
        if (_disclosure is not null)
        {
            _disclosure.IsExpanded = Entries.Length > 0;
        }
    }

    private string[] Entries => _settings.Get(_setting);

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _observer.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild()
    {
        var entries = Entries;
        _header.Text = L.Get(_titleKey);
        _disclosure?.SetTitle($"{L.Get(_titleKey)} ({entries.Length})");
        _rows.Children.Clear();
        if (entries.Length == 0 && _emptyKey is not null)
        {
            _rows.Children.Add(new TextBlock { Text = L.Get(_emptyKey), Classes = { "caption", "tertiary" } });
        }

        foreach (var entry in entries)
        {
            _rows.Children.Add(BuildRow(entry));
        }
    }

    private Control BuildRow(string entry)
    {
        var identity = new AppIdentityInfo(entry);
        var name = new TextBlock { Text = AppExclusionList.IsPath(entry) ? identity.DisplayName : entry, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { name } };
        if (AppExclusionList.IsPath(entry))
        {
            text.Children.Add(new TextBlock { Text = entry, Classes = { "caption", "tertiary" }, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var remove = new Button { Classes = { "icon" }, Content = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Subtract, FontSize = 14 } };
        ToolTip.SetTip(remove, L.Get("mouseExceptions.removeButton"));
        AutomationProperties.SetName(remove, L.Get("mouseExceptions.removeButton"));
        remove.Click += (_, _) => _settings.Set(_setting, Entries.Where(e => !string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)).ToArray());

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(new SymbolIcon { Symbol = FluentIcons.Common.Symbol.AppGeneric, FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return grid;
    }

    private async Task AddAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var picked = await AppPickerWindow.PickAsync(owner, _catalog);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            _settings.Set(_setting, [.. Entries, picked]);
            if (_disclosure is not null)
            {
                _disclosure.IsExpanded = true;
            }
        }
    }
}

/// <summary>
/// A light disclosure (chevron + title) that shows or hides its content. The
/// Fluent Expander draws a heavy filled header that does not sit well inside
/// settings cards, especially in the dark theme.
/// </summary>
public sealed class Disclosure : UserControl
{
    private readonly Control _content;
    private readonly SymbolIcon _chevron = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private bool _expanded;

    public Disclosure(string title, Control content, bool expanded = false)
    {
        _content = content;
        _content.Margin = new Thickness(22, 2, 0, 4);
        _title.Text = title;
        var header = new Button
        {
            Classes = { "row" },
            Padding = new Thickness(4, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _chevron, _title } },
        };
        header.Click += (_, _) => IsExpanded = !IsExpanded;
        AutomationProperties.SetName(header, title);
        Content = new StackPanel { Spacing = 0, Children = { header, _content } };
        IsExpanded = expanded;
    }

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _content.IsVisible = value;
            _chevron.Symbol = value ? FluentIcons.Common.Symbol.ChevronDown : FluentIcons.Common.Symbol.ChevronRight;
        }
    }

    public void SetTitle(string title) => _title.Text = title;
}

/// <summary>Searchable list of running apps plus "Browse…" for any executable.</summary>
public sealed class AppPickerWindow : Window
{
    private readonly ListBox _list = new() { Height = 300 };
    private readonly TextBox _search = new();
    private List<AppCatalogEntry> _all = [];
    private string? _result;

    private AppPickerWindow(IAppCatalog? catalog)
    {
        Title = L.Get("mouseExceptions.addButton").TrimEnd('…', '.');
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _search.PlaceholderText = L.Get("win.shell.searchSettings");
        _search.TextChanged += (_, _) => Filter();
        _list.DoubleTapped += (_, _) => Accept();
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AppCatalogEntry>((entry, _) =>
            new StackPanel
            {
                Margin = new Thickness(2),
                Children =
                {
                    new TextBlock { Text = entry?.Name, FontSize = 13 },
                    new TextBlock { Text = entry?.Path, Classes = { "caption", "tertiary" }, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });

        var browse = new Button { Content = L.Get("win.input.browseApp") };
        browse.Click += async (_, _) => await BrowseAsync();
        var add = new Button { Content = L.Get("win.input.addSelectedApp"), Classes = { "accent" } };
        add.Click += (_, _) => Accept();
        var cancel = new Button { Content = L.Get("mouseButtons.captureCancel") };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { browse, cancel, add } };
        Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { _search, _list, buttons } };
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush").ToBinding());

        Task.Run(() =>
        {
            try
            {
                return catalog?.ListApps().OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
            }
            catch (Exception ex)
            {
                Log.Warn("input", "Could not list apps.", ex);
                return [];
            }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _all = t.Result;
            Filter();
        }), TaskScheduler.Default);
    }

    public static async Task<string?> PickAsync(Window owner, IAppCatalog? catalog)
    {
        var picker = new AppPickerWindow(catalog);
        await picker.ShowDialog(owner);
        return picker._result;
    }

    private void Filter()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        _list.ItemsSource = query.Length == 0
            ? _all
            : _all.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || a.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Accept()
    {
        if (_list.SelectedItem is AppCatalogEntry entry)
        {
            _result = entry.Path;
            Close();
        }
    }

    private async Task BrowseAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(L.Get("win.input.programsFilter")) { Patterns = ["*.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _result = path;
            Close();
        }
    }
}
