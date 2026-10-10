// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentIcons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Actions;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Modules.RadialMenu;
using Rivet.Core.Shortcuts;
using SkiaSharp;

namespace Rivet.App.Features.RadialMenu;

/// <summary>What the item editor sheet returned.</summary>
public enum RadialEditorResult
{
    Cancelled,
    Saved,
    Removed,
}

/// <summary>
/// The item editor sheet (spec 07 §3.2.13): action kind, the per-kind target
/// (app or file chooser, link with the website-icon fetch, shortcut recorder,
/// tool / quick toggle / layout / media pickers, or a submenu at the root),
/// a custom name, a custom icon from a curated grid, and Remove / Cancel /
/// Save. Save is enabled only for a valid target (the same rule as cleanup).
/// </summary>
public sealed class RadialItemEditor : Window
{
    /// <summary>The curated icon grid (Fluent names; "Automatic" first).</summary>
    public static readonly string[] Icons =
    [
        "Star", "Heart", "Home", "Folder", "Document", "Image", "Camera", "Video", "MusicNote2", "Mic", "Speaker2", "Headphones",
        "Mail", "Chat", "Calendar", "Clock", "Timer", "Alarm", "Globe", "Link", "Search", "Bookmark", "Tag", "Flag",
        "Keyboard", "Code", "Terminal", "Bug", "Wrench", "Settings", "Apps", "Grid", "Window", "Desktop", "Phone", "Tablet",
        "Play", "Pause", "Next", "Previous", "Record", "Stop", "Copy", "Clipboard", "Cut", "Delete", "Edit", "Pen",
        "Note", "Notebook", "Book", "Lightbulb", "Sparkle", "Wand", "Rocket", "Trophy", "Gift", "Cart", "Wallet", "Money",
        "Person", "People", "Lock", "LockOpen", "Shield", "Key", "Cloud", "WeatherSunny", "WeatherMoon", "Leaf", "Food", "Drink",
        "Airplane", "Vehicle", "Map", "Location", "Compass", "Archive", "Box", "Print", "Share", "Send", "ArrowSync", "ArrowDownload",
        "Eye", "Color", "Paint", "Ruler", "Calculator", "Translate", "Games", "Emoji",
    ];

    private readonly IServiceProvider _services;
    private readonly RadialPresenter _presenter;
    private readonly bool _allowSubmenu;
    private readonly ComboBox _kind = new() { MinWidth = 260 };
    private readonly ContentControl _target = new();
    private readonly TextBox _name = new() { MaxLength = RadialProfile.MaxNameLength };
    private readonly TextBlock _error = new() { Classes = { "caption" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Classes = { "accent" }, MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly WrapPanel _iconGrid = new() { ItemSpacing = 4, LineSpacing = 4 };
    private readonly List<RadialItemKind> _kinds;
    private RadialItem _item;
    private string _payload;
    private string _symbol;
    private string? _customIcon;
    private RadialEditorResult _result = RadialEditorResult.Cancelled;

    public RadialItemEditor(IServiceProvider services, RadialPresenter presenter, RadialItem item, bool allowSubmenu, bool isNew)
    {
        _services = services;
        _presenter = presenter;
        _item = item;
        _payload = item.Payload;
        _symbol = item.SymbolName;
        _customIcon = item.CustomIconData;
        _allowSubmenu = allowSubmenu || item.IsSubmenu;
        Title = L.Get("radialMenu.actionLabel");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));

        _kinds = [RadialItemKind.App, RadialItemKind.File, RadialItemKind.Url, RadialItemKind.Shortcut, RadialItemKind.Tool, RadialItemKind.QuickToggle, RadialItemKind.WindowLayout, RadialItemKind.Media];
        if (_allowSubmenu)
        {
            _kinds.Add(RadialItemKind.Submenu);
        }

        _kind.ItemsSource = _kinds.Select(KindTitle).ToList();
        _kind.SelectedIndex = Math.Max(0, _kinds.IndexOf(item.Kind));
        AutomationProperties.SetName(_kind, L.Get("radialMenu.actionLabel"));
        _kind.SelectionChanged += (_, _) =>
        {
            var kind = _kinds[Math.Max(0, _kind.SelectedIndex)];
            if (kind != _item.Kind)
            {
                _item = _item with { Kind = kind };
                _payload = DefaultPayload(kind);
                BuildTarget();
                Validate();
            }
        };

        _name.Text = item.Name;
        _name.PlaceholderText = L.Get("radialMenu.automaticLabel");
        AutomationProperties.SetName(_name, L.Get("radialMenu.nameLabel"));

        var remove = new Button { Content = L.Get("radialMenu.deleteButton"), IsVisible = !isNew };
        remove.Bind(ForegroundProperty, remove.GetResourceObservable("DangerBrush"));
        remove.Click += (_, _) =>
        {
            _result = RadialEditorResult.Removed;
            Close();
        };
        var cancel = new Button { Content = L.Get("Strings.mediaCancel"), IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        _save.Content = L.Get("radialMenu.saveButton");
        _save.Click += (_, _) =>
        {
            if (Validate())
            {
                _result = RadialEditorResult.Saved;
                Close();
            }
        };

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        buttons.Children.Add(remove);
        Grid.SetColumn(cancel, 2);
        buttons.Children.Add(cancel);
        Grid.SetColumn(_save, 3);
        buttons.Children.Add(_save);

        BuildIconGrid();
        BuildTarget();
        Content = new ScrollViewer
        {
            MaxHeight = 760,
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 12,
                Children =
                {
                    Labeled(L.Get("radialMenu.actionLabel"), _kind),
                    _target,
                    _error,
                    Labeled(L.Get("radialMenu.nameLabel"), _name),
                    new TextBlock { Text = L.Get("radialMenu.iconLabel"), FontWeight = FontWeight.SemiBold },
                    _iconGrid,
                    buttons,
                },
            },
        };
        Validate();
    }

    /// <summary>The edited item (valid when the result is <see cref="RadialEditorResult.Saved"/>).</summary>
    public RadialItem Item
    {
        get
        {
            var payload = _payload;
            RadialProfilesCodec.IsValidPayload(_item.Kind, ref payload);
            return _item with
            {
                Payload = payload,
                Name = (_name.Text ?? string.Empty).Trim(),
                SymbolName = _symbol,
                CustomIconData = _item.Kind == RadialItemKind.Url ? _customIcon : null,
            };
        }
    }

    public async Task<RadialEditorResult> ShowAsync(Window owner)
    {
        await ShowDialog(owner).ConfigureAwait(true);
        return _result;
    }

    internal static string KindTitle(RadialItemKind kind) => L.Get(kind switch
    {
        RadialItemKind.App => "radialMenu.kindApp",
        RadialItemKind.File => "radialMenu.kindFile",
        RadialItemKind.Url => "radialMenu.kindURL",
        RadialItemKind.Shortcut => "radialMenu.kindShortcut",
        RadialItemKind.Tool => "win.radialMenu.kindTool",
        RadialItemKind.QuickToggle => "quickToggles.pageTitle",
        RadialItemKind.WindowLayout => "radialMenu.presetWindowLayout",
        RadialItemKind.Media => "radialMenu.kindMedia",
        _ => "radialMenu.kindSubmenu",
    });

    private static string DefaultPayload(RadialItemKind kind) => kind switch
    {
        RadialItemKind.QuickToggle => RadialQuickToggleIds.DarkMode,
        RadialItemKind.WindowLayout => "maximize",
        RadialItemKind.Media => RadialMediaIds.PlayPause,
        _ => string.Empty,
    };

    private static Control Labeled(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private bool Validate()
    {
        var payload = _payload;
        var valid = RadialProfilesCodec.IsValidPayload(_item.Kind, ref payload);
        _save.IsEnabled = valid;
        _error.IsVisible = !valid && _item.Kind == RadialItemKind.Url && _payload.Trim().Length > 0;
        _error.Text = L.Get("radialMenu.urlInvalid");
        return valid;
    }

    // ── Targets ─────────────────────────────────────────────────────────

    private void BuildTarget()
    {
        _target.Content = _item.Kind switch
        {
            RadialItemKind.App => PathTarget(L.Get("radialMenu.kindApp"), folder: false, apps: true),
            RadialItemKind.File => PathTarget(L.Get("radialMenu.kindFile"), folder: true, apps: false),
            RadialItemKind.Url => UrlTarget(),
            RadialItemKind.Shortcut => ShortcutTarget(),
            RadialItemKind.Tool => PickerTarget(L.Get("radialMenu.toolLabel"), ToolChoices()),
            RadialItemKind.QuickToggle => PickerTarget(L.Get("quickToggles.pageTitle"), RadialQuickToggleIds.All.Select(id => (id, L.Get(RadialLabels.QuickToggleTitleKey(id)))).ToList()),
            RadialItemKind.WindowLayout => PickerTarget(L.Get("radialMenu.presetWindowLayout"), WindowLayoutActions.All.Select(id => (id, L.Get(WindowLayoutActions.TitleKey(id)))).ToList()),
            RadialItemKind.Media => PickerTarget(L.Get("radialMenu.mediaLabel"), RadialMediaIds.All.Select(id => (id, _presenter.Label(new RadialItem { Kind = RadialItemKind.Media, Payload = id }, NowPlayingState.Loading))).ToList()),
            _ => new TextBlock { Text = L.Get("radialMenu.submenuCaption"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap },
        };
    }

    /// <summary>Every app action (Command Bar, panel and tray all run the same actions) except the wheel's own.</summary>
    private List<(string, string)> ToolChoices()
    {
        var actions = _services.GetRequiredService<ActionRegistry>().Available
            .Where(a => !a.Id.StartsWith("radialMenu.", StringComparison.Ordinal))
            .Select(a => (a.Id, L.Get(a.TitleKey)))
            .GroupBy(a => a.Item2)
            .Select(g => g.First())
            .OrderBy(a => a.Item2, StringComparer.CurrentCulture)
            .ToList();
        if (_payload.Length > 0 && actions.All(a => a.Id != _payload))
        {
            // A tool from a macOS backup (a feature id) stays selectable.
            actions.Insert(0, (_payload, _presenter.Label(_item with { Payload = _payload })));
        }

        if (_payload.Length == 0 && actions.Count > 0)
        {
            _payload = actions[0].Id;
        }

        return actions;
    }

    private Control PickerTarget(string label, List<(string Id, string Title)> choices)
    {
        var combo = new ComboBox { ItemsSource = choices.Select(c => c.Title).ToList(), MinWidth = 260, MaxDropDownHeight = 360 };
        AutomationProperties.SetName(combo, label);
        if (choices.FindIndex(c => c.Id == _payload) is var index and >= 0)
        {
            combo.SelectedIndex = index;
        }
        else if (choices.Count > 0)
        {
            combo.SelectedIndex = 0;
            _payload = choices[0].Id;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                _payload = choices[combo.SelectedIndex].Id;
                Validate();
            }
        };
        return Labeled(label, combo);
    }

    private Control PathTarget(string label, bool folder, bool apps)
    {
        var box = new TextBox { Text = _payload, PlaceholderText = apps ? "C:\\Program Files\\…\\app.exe" : "~\\Downloads" };
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) =>
        {
            _payload = (box.Text ?? string.Empty).Trim();
            Validate();
        };
        var choose = new Button { Content = L.Get("radialMenu.chooseButton") };
        choose.Click += async (_, _) =>
        {
            var provider = StorageProvider;
            string? path = null;
            if (folder)
            {
                var menu = new MenuFlyout();
                var fileItem = new MenuItem { Header = L.Get("win.radialMenu.chooseFile") };
                var folderItem = new MenuItem { Header = L.Get("win.radialMenu.chooseFolder") };
                fileItem.Click += async (_, _) => SetPath(box, (await provider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false }).ConfigureAwait(true)).FirstOrDefault()?.TryGetLocalPath());
                folderItem.Click += async (_, _) => SetPath(box, (await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false }).ConfigureAwait(true)).FirstOrDefault()?.TryGetLocalPath());
                menu.Items.Add(fileItem);
                menu.Items.Add(folderItem);
                menu.ShowAt(choose);
                return;
            }

            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(L.Get("win.shelf.applications")) { Patterns = ["*.exe", "*.lnk", "*.appref-ms"] }],
            }).ConfigureAwait(true);
            path = files.FirstOrDefault()?.TryGetLocalPath();
            SetPath(box, path);
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(box);
        Grid.SetColumn(choose, 1);
        grid.Children.Add(choose);
        return Labeled(label, grid);
    }

    private static void SetPath(TextBox box, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            box.Text = path;
        }
    }

    private Control UrlTarget()
    {
        var box = new TextBox { Text = _payload, PlaceholderText = L.Get("radialMenu.urlPlaceholder") };
        AutomationProperties.SetName(box, L.Get("radialMenu.kindURL"));
        box.TextChanged += (_, _) =>
        {
            _payload = box.Text ?? string.Empty;
            Validate();
        };
        var status = new TextBlock { Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
        var fetch = new Button { Content = L.Get("radialMenu.fetchFaviconButton") };
        fetch.Click += async (_, _) =>
        {
            if (RadialLinks.Normalize(_payload) is not { } url || RadialLinks.FaviconUri(url) is not { } icon)
            {
                status.Text = L.Get("radialMenu.fetchFaviconError");
                return;
            }

            fetch.IsEnabled = false;
            status.Text = L.Get("radialMenu.fetchFaviconLoading");
            var data = await FetchIconAsync(icon).ConfigureAwait(true);
            fetch.IsEnabled = true;
            if (data is null)
            {
                status.Text = L.Get("radialMenu.fetchFaviconError");
                return;
            }

            _customIcon = data;
            _symbol = string.Empty;
            status.Text = L.Get("radialMenu.fetchFaviconSuccess");
            BuildIconGrid();
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Labeled(L.Get("radialMenu.kindURL"), box),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(130, 0, 0, 0), Children = { fetch, status } },
                new TextBlock { Text = L.Get("radialMenu.fetchFaviconDisclaimer"), Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(130, 0, 0, 0) },
            },
        };
    }

    /// <summary>Downloads the site's /favicon.ico once and stores it as a 64×64 PNG (≤ 64 KiB).</summary>
    private static async Task<string?> FetchIconAsync(Uri icon)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var bytes = await http.GetByteArrayAsync(icon).ConfigureAwait(false);
            if (bytes.Length is 0 or > 1_048_576)
            {
                return null;
            }

            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null)
            {
                return null;
            }

            using var scaled = decoded.Resize(new SKImageInfo(64, 64), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var image = SKImage.FromBitmap(scaled ?? decoded);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            var data = Convert.ToBase64String(png.ToArray());
            return RadialProfilesCodec.IsUsableIcon(data) ? data : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException)
        {
            Log.Info("radialMenu", $"Website icon fetch failed: {ex.Message}");
            return null;
        }
    }

    private Control ShortcutTarget()
    {
        var recorder = new ShortcutRecorder { AllowClear = true, MinWidth = 220 };
        if (KeyChord.TryParse(_payload, out var chord))
        {
            recorder.Chord = chord;
        }

        AutomationProperties.SetName(recorder, L.Get("radialMenu.kindShortcut"));
        recorder.ChordRecorded += (_, e) =>
        {
            recorder.Chord = e.Chord;
            _payload = e.Chord.IsEmpty ? string.Empty : e.Chord.ToStorageString();
            Validate();
        };
        return Labeled(L.Get("radialMenu.kindShortcut"), recorder);
    }

    // ── Icons ───────────────────────────────────────────────────────────

    private void BuildIconGrid()
    {
        _iconGrid.Children.Clear();
        _iconGrid.Children.Add(IconButton(null, _symbol.Length == 0));
        foreach (var name in Icons.Where(IconConverter.IsKnown))
        {
            _iconGrid.Children.Add(IconButton(name, _symbol == name));
        }
    }

    private Control IconButton(string? name, bool selected)
    {
        Control face = name is null
            ? new TextBlock { Text = L.Get("radialMenu.automaticLabel"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
            : new SymbolIcon { Symbol = IconConverter.Parse(name), FontSize = 16 };
        var button = new ToggleButton
        {
            IsChecked = selected,
            Content = face,
            MinWidth = 34,
            Height = 34,
            Padding = new Thickness(name is null ? 8 : 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, name ?? L.Get("radialMenu.automaticLabel"));
        AutomationProperties.SetName(button, name ?? L.Get("radialMenu.automaticLabel"));
        button.Click += (_, _) =>
        {
            _symbol = name ?? string.Empty;
            BuildIconGrid();
        };
        return button;
    }
}
