// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Features.Clipboard;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// Settings → Command Bar (spec 06 §3.8.14): open/recenter, compact mode,
/// skin tone, the global shortcut, app shortcuts, the sources, file search
/// folders and ignores, saved shortcuts, and the personalization lists (row
/// shortcuts, names, pins, hidden rows) with "Forget what I use most".
/// </summary>
public sealed class CommandBarSettingsPage : SettingsPage
{
    /// <summary>Sources with a switch, in the Settings order (Menus, Uninstall and Kill have no Windows source here).</summary>
    public static readonly CommandSource[] SwitchableSources =
    [
        CommandSource.Actions, CommandSource.Apps, CommandSource.Windows, CommandSource.QuitApps, CommandSource.SettingsPages,
        CommandSource.MacSettings, CommandSource.Snippets, CommandSource.Clipboard, CommandSource.Emoji, CommandSource.Folders,
        CommandSource.Links, CommandSource.Files, CommandSource.Answers, CommandSource.Calculator, CommandSource.Selection,
    ];

    private readonly IServiceProvider _services;
    private readonly CommandBarController _controller;
    private readonly CommandBarPreferences _preferences;
    private readonly ICommandBarPlatform _platform;
    private readonly Button _recenter;
    private readonly StackPanel _scopes = new() { Spacing = 4 };
    private readonly StackPanel _ignores = new() { Spacing = 4 };
    private readonly StackPanel _links = new() { Spacing = 4 };
    private readonly StackPanel _rowShortcuts = new() { Spacing = 4 };
    private readonly StackPanel _names = new() { Spacing = 4 };
    private readonly StackPanel _pins = new() { Spacing = 4 };
    private readonly StackPanel _hidden = new() { Spacing = 4 };
    private readonly Dictionary<CommandSource, ToggleSwitch> _sourceSwitches = [];
    private Dictionary<string, string>? _titles;
    private bool _appsRequested;

    public CommandBarSettingsPage(IServiceProvider services)
        : base(services.GetRequiredService<ISettingsStore>())
    {
        _services = services;
        _controller = services.GetRequiredService<CommandBarController>();
        _preferences = services.GetRequiredService<CommandBarPreferences>();
        _platform = services.GetRequiredService<ICommandBarPlatform>();
        var shortcuts = services.GetRequiredService<ShortcutManager>();

        var open = ActionButton(L.Get("commandBar.openButton"), () => _ = _controller.OpenAsync(), "Search", accent: true);
        _recenter = ActionButton(L.Get("commandBar.resetPositionButton"), () => _preferences.PositionOffset = (0, 0), "ArrowReset");
        var tones = new List<(string Value, string Label)>
        {
            (string.Empty, "✋"), ("light", "✋🏻"), ("mediumLight", "✋🏼"), ("medium", "✋🏽"), ("mediumDark", "✋🏾"), ("dark", "✋🏿"),
        };
        var skinTone = Choice(CommandBarSettings.EmojiSkinTone, "Emoji", "commandBar.emojiSkinToneLabel", "commandBar.emojiSkinToneCaption", tones);

        var ignoreBox = new TextBox { PlaceholderText = L.Get("commandBar.filesIgnorePlaceholder"), Width = 240 };
        AutomationProperties.SetName(ignoreBox, L.Get("commandBar.filesIgnorePlaceholder"));
        var addIgnore = new Button { Content = L.Get("commandBar.filesIgnoreAdd") };
        addIgnore.Click += (_, _) =>
        {
            var text = (ignoreBox.Text ?? string.Empty).Trim();
            if (text.Length > 0)
            {
                _preferences.SetFileIgnores(_preferences.FileIgnores.Append(text));
                ignoreBox.Text = string.Empty;
            }
        };
        var moreFiles = new Expander
        {
            Header = L.Get("win.commandBar.moreOptions"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Caption(L.Get("commandBar.filesIgnoreCaption")),
                    _ignores,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ignoreBox, addIgnore } },
                },
            },
        };

        Content = Stack(
            Header("commandBar.pageTitle", "commandBar.hubDescription"),
            Card(null,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, _recenter } },
                Note(L.Get("commandBar.settingsCaption")),
                Note(L.Get("commandBar.positionCaption")),
                Note(L.Get("commandBar.privacyNote")),
                TryLine(),
                Toggle(CommandBarSettings.CompactMode, "PanelTopContract", "commandBar.compactModeToggle", "commandBar.compactModeCaption"),
                skinTone),
            Card(null,
                Toggle(CommandBarSettings.ShortcutEnabled, "Keyboard", "commandBar.shortcutToggle", "win.commandBar.shortcutCaption"),
                shortcuts.Find(CommandBarModule.RoleId) is { } role ? new ShortcutRoleRow(role) : null),
            Card("commandBar.appCenterTitle",
                Row("Apps", L.Get("commandBar.appCenterTitle"), L.Get("commandBar.appCenterCaption"),
                    ActionButton(L.Get("commandBar.appCenterTitle"), () => _ = OpenAppCenterAsync(), "Apps"))),
            Card("commandBar.sourcesTitle", SourcesList(), Note(L.Get("commandBar.sourcesCaption"))),
            Card("commandBar.filesTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Caption(L.Get("commandBar.filesCaption")),
                        _scopes,
                        ActionButton(L.Get("commandBar.filesAddFolder"), () => _ = AddFoldersAsync(), "FolderAdd"),
                        moreFiles,
                    },
                }),
            Card("commandBar.linksTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children = { _links, ActionButton(L.Get("commandBar.linkAddButton"), () => _ = EditLinkAsync(null), "Add") },
                }),
            Card("commandBar.rowShortcutsTitle", _rowShortcuts),
            Card("commandBar.namedTitle", _names),
            Card("commandBar.pinnedTitle", _pins),
            Card("commandBar.hiddenTitle",
                new StackPanel
                {
                    Spacing = 8,
                    Children = { _hidden, ActionButton(L.Get("commandBar.forgetAllButton"), ForgetAll, "History") },
                }));

        Track(Settings.Observe(() => Dispatcher.UIThread.Post(RebuildAll),
            CommandBarSettings.Pins, CommandBarSettings.Aliases, CommandBarSettings.Hidden, CommandBarSettings.RowShortcuts,
            CommandBarSettings.Links, CommandBarSettings.FileScopes, CommandBarSettings.FileIgnores, CommandBarSettings.PositionOffset,
            CommandBarSettings.DisabledSources));
        RebuildAll();
    }

    // ── Header block ───────────────────────────────────────────────────

    private Control TryLine()
    {
        var wrap = new WrapPanel();
        wrap.Children.Add(new TextBlock { Text = L.Get("commandBar.tryTheseLabel"), Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4) });
        foreach (var example in new[] { "100 km to mi", "2+2*3", "brightness 40", "fire", L.Get("commandBar.answerBatteryLabel").ToLower(Localizer.Current.Culture) })
        {
            var text = example;
            var chip = new Button { Content = new TextBlock { Text = text, FontFamily = ClipboardUi.MonoFont, FontSize = 11 }, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 6, 4), CornerRadius = new CornerRadius(9), MinHeight = 0 };
            chip.Click += (_, _) => _ = _controller.OpenAsync(text);
            wrap.Children.Add(chip);
        }

        return wrap;
    }

    // ── Sources ────────────────────────────────────────────────────────

    private Control SourcesList()
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var source in SwitchableSources)
        {
            var captured = source;
            var toggle = new ToggleSwitch { Classes = { "compact" }, IsChecked = _preferences.IsSourceEnabled(source), IsEnabled = source != CommandSource.Actions };
            AutomationProperties.SetName(toggle, SourceTitle(source));
            toggle.IsCheckedChanged += (_, _) => _preferences.SetSourceEnabled(captured, toggle.IsChecked == true);
            _sourceSwitches[source] = toggle;
            stack.Children.Add(Row(SourceIcon(source), SourceTitle(source), null, toggle));
        }

        return stack;
    }

    public static string SourceTitle(CommandSource source) => L.Get(source switch
    {
        CommandSource.Actions => "commandBar.sourceActions",
        CommandSource.Apps => "commandBar.sourceApps",
        CommandSource.Windows => "commandBar.sourceWindows",
        CommandSource.QuitApps => "commandBar.sourceQuitApps",
        CommandSource.SettingsPages => "commandBar.sourceSettingsPages",
        CommandSource.MacSettings => "commandBar.sourceMacSettings",
        CommandSource.Snippets => "commandBar.sourceSnippets",
        CommandSource.Clipboard => "commandBar.sourceClipboard",
        CommandSource.Emoji => "commandBar.sourceEmoji",
        CommandSource.Folders => "commandBar.sourceFolders",
        CommandSource.Links => "commandBar.linksTitle",
        CommandSource.Files => "commandBar.sourceFiles",
        CommandSource.Answers => "commandBar.sourceAnswers",
        CommandSource.Calculator => "commandBar.sourceCalculator",
        CommandSource.Selection => "commandBar.sourceSelection",
        _ => "commandBar.sourceMenus",
    });

    private static string SourceIcon(CommandSource source) => source switch
    {
        CommandSource.Actions => "Flash",
        CommandSource.Apps => "Apps",
        CommandSource.Windows => "Window",
        CommandSource.QuitApps => "Dismiss",
        CommandSource.SettingsPages => "Settings",
        CommandSource.MacSettings => "WindowSettings",
        CommandSource.Snippets => "TextExpand",
        CommandSource.Clipboard => "ClipboardPaste",
        CommandSource.Emoji => "Emoji",
        CommandSource.Folders => "Folder",
        CommandSource.Links => "Link",
        CommandSource.Files => "Document",
        CommandSource.Answers => "Info",
        CommandSource.Calculator => "Calculator",
        CommandSource.Selection => "TextT",
        _ => "Apps",
    };

    // ── Lists ──────────────────────────────────────────────────────────

    private void RebuildAll()
    {
        _titles = null;
        _recenter.IsEnabled = _preferences.PositionOffset != (0, 0);
        foreach (var (source, toggle) in _sourceSwitches)
        {
            toggle.IsChecked = _preferences.IsSourceEnabled(source);
        }

        RebuildScopes();
        RebuildIgnores();
        RebuildLinks();
        RebuildPersonalization();
    }

    private void RebuildScopes()
    {
        _scopes.Children.Clear();
        var scopes = _preferences.FileScopes;
        if (scopes.Count == 0)
        {
            _scopes.Children.Add(Caption(L.Get("win.commandBar.filesEmpty")));
            return;
        }

        foreach (var scope in scopes)
        {
            var captured = scope;
            _scopes.Children.Add(Line(scope, null, "Folder", () => _preferences.SetFileScopes(_preferences.FileScopes.Where(s => !string.Equals(s, captured, StringComparison.OrdinalIgnoreCase)))));
        }
    }

    private void RebuildIgnores()
    {
        _ignores.Children.Clear();
        foreach (var name in CommandBarPreferences.BuiltInIgnores)
        {
            _ignores.Children.Add(new TextBlock { Text = name, Classes = { "caption", "tertiary" }, FontFamily = ClipboardUi.MonoFont });
        }

        foreach (var ignore in _preferences.FileIgnores)
        {
            var captured = ignore;
            _ignores.Children.Add(Line(ignore, null, "EyeOff", () => _preferences.SetFileIgnores(_preferences.FileIgnores.Where(s => !string.Equals(s, captured, StringComparison.OrdinalIgnoreCase)))));
        }
    }

    private void RebuildLinks()
    {
        _links.Children.Clear();
        var links = Settings.Get(CommandBarSettings.Links);
        if (links.Count == 0)
        {
            _links.Children.Add(Caption(L.Get("commandBar.linksEmpty")));
            return;
        }

        foreach (var link in links)
        {
            var captured = link;
            var edit = new Button { Content = L.Get("win.commandBar.editButton") };
            edit.Click += (_, _) => _ = EditLinkAsync(captured);
            var remove = new Button { Content = L.Get("commandBar.removeButton") };
            remove.Click += (_, _) => Settings.Set(CommandBarSettings.Links, Settings.Get(CommandBarSettings.Links).Where(l => l.Id != captured.Id).ToList());
            var icon = link.Kind switch
            {
                CommandBarLinkKind.Script => "WindowConsole",
                CommandBarLinkKind.Place => "Folder",
                _ => link.IsSearch ? "Search" : "Link",
            };
            _links.Children.Add(LineWith(link.Name, link.Destination, icon, edit, remove));
        }
    }

    private void RebuildPersonalization()
    {
        _rowShortcuts.Children.Clear();
        var bindings = _preferences.RowShortcuts;
        if (bindings.Count == 0)
        {
            _rowShortcuts.Children.Add(Caption(L.Get("commandBar.rowShortcutsEmpty")));
        }

        foreach (var (key, chord) in bindings.OrderBy(kv => TitleOf(kv.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var captured = key;
            var refused = _controller.RefusedRowShortcuts.Contains(key);
            var line = Line(TitleOf(key), refused ? L.Get("win.commandBar.rowShortcutRefused") : null, "Keyboard",
                () => _preferences.RemoveRowShortcut(captured), _controller.ChordText(chord));
            _rowShortcuts.Children.Add(line);
        }

        _names.Children.Clear();
        var aliases = _preferences.Aliases;
        if (aliases.Count == 0)
        {
            _names.Children.Add(Caption(L.Get("commandBar.namedEmpty")));
        }

        foreach (var (key, alias) in aliases.OrderBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            var captured = key;
            _names.Children.Add(Line(alias, TitleOf(key), "Rename", () => _preferences.SetAlias(captured, string.Empty)));
        }

        _pins.Children.Clear();
        var pins = _preferences.Pins;
        if (pins.Count == 0)
        {
            _pins.Children.Add(Caption(L.Get("commandBar.pinnedEmpty")));
        }

        foreach (var key in pins)
        {
            var captured = key;
            _pins.Children.Add(Line(TitleOf(key), null, "Pin", () => _preferences.Unpin(captured)));
        }

        _hidden.Children.Clear();
        var hidden = _preferences.Hidden;
        if (hidden.Count == 0)
        {
            _hidden.Children.Add(Caption(L.Get("commandBar.hiddenEmpty")));
        }

        foreach (var key in hidden.OrderBy(TitleOf, StringComparer.CurrentCultureIgnoreCase))
        {
            var captured = key;
            _hidden.Children.Add(Line(TitleOf(key), null, "EyeOff", () => _preferences.SetHidden(captured, false)));
        }
    }

    /// <summary>A readable title for a stored stable key (catalog rows now, apps once scanned).</summary>
    private string TitleOf(string stableKey)
    {
        _titles ??= BuildTitles();
        if (_titles.TryGetValue(stableKey, out var title))
        {
            return title;
        }

        if (stableKey.StartsWith("app.", StringComparison.Ordinal) && !_appsRequested)
        {
            _appsRequested = true;
            _ = LoadAppTitlesAsync();
        }

        if (stableKey.StartsWith("window.", StringComparison.Ordinal) && stableKey.IndexOf('|', StringComparison.Ordinal) is var bar and > 0)
        {
            return stableKey[(bar + 1)..];
        }

        var dot = stableKey.IndexOf('.', StringComparison.Ordinal);
        var rest = dot >= 0 ? stableKey[(dot + 1)..] : stableKey;
        return Path.GetFileNameWithoutExtension(rest.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : rest;
    }

    private Dictionary<string, string> BuildTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var row in _controller.Catalog.BuildCatalog().Concat(_controller.Catalog.WindowsSettingsRows()).Concat(_controller.Engine.Apps))
            {
                titles.TryAdd(row.StableKey, row.Title);
            }
        }
        catch (Exception ex)
        {
            Rivet.Core.Diagnostics.Log.Info("commandBar", $"Settings titles failed ({ex.GetType().Name}).");
        }

        return titles;
    }

    private async Task LoadAppTitlesAsync()
    {
        try
        {
            var apps = await Task.Run(() => _platform.GetAppsAsync(CancellationToken.None)).ConfigureAwait(true);
            var titles = _titles ??= BuildTitles();
            foreach (var row in _controller.Catalog.AppRows(apps, []))
            {
                titles.TryAdd(row.StableKey, row.Title);
            }

            RebuildPersonalization();
        }
        catch (Exception ex)
        {
            Rivet.Core.Diagnostics.Log.Info("commandBar", $"App titles failed ({ex.GetType().Name}).");
        }
    }

    private Control Line(string title, string? detail, string icon, Action remove, string? badge = null)
    {
        var button = new Button { Content = L.Get("commandBar.removeButton") };
        button.Click += (_, _) => remove();
        Control? badgeControl = badge is null ? null : new Border { Classes = { "pill" }, VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = badge, FontSize = 12 } };
        return LineWith(title, detail, icon, badgeControl, button);
    }

    private Control LineWith(string title, string? detail, string icon, params Control?[] trailing)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(new Border { VerticalAlignment = VerticalAlignment.Center, Child = ClipboardUi.Icon(icon, 15) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis } } };
        if (!string.IsNullOrEmpty(detail))
        {
            text.Children.Add(new TextBlock { Text = detail, Classes = { "caption", "tertiary" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var control in trailing.OfType<Control>())
        {
            buttons.Children.Add(control);
        }

        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    // ── Actions ────────────────────────────────────────────────────────

    private void ForgetAll()
    {
        _controller.Engine.ForgetAll();
        _preferences.SaveUsage(_controller.Engine.Usage);
        _services.GetService<Rivet.Core.Contracts.IHud>()?.Show(L.Get("commandBar.forgetAllButton"), Rivet.Core.Contracts.HudStyle.Info, "History");
    }

    private async Task AddFoldersAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = true, Title = L.Get("commandBar.filesAddFolder") }).ConfigureAwait(true);
        var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().Select(p => CommandBarLinks.AbbreviateHome(p, _platform.HomeFolder));
        _preferences.SetFileScopes(_preferences.FileScopes.Concat(paths));
    }

    private async Task EditLinkAsync(CommandBarLink? link)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new CommandBarLinkDialog(link);
        if (await dialog.ShowAsync(owner).ConfigureAwait(true) is not { } saved)
        {
            return;
        }

        var links = Settings.Get(CommandBarSettings.Links).ToList();
        var index = links.FindIndex(l => l.Id == saved.Id);
        if (index >= 0)
        {
            links[index] = saved;
        }
        else
        {
            links.Add(saved);
        }

        Settings.Set(CommandBarSettings.Links, links);
    }

    private async Task OpenAppCenterAsync()
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await new AppShortcutsDialog(_services).ShowDialog(owner).ConfigureAwait(true);
            RebuildAll();
        }
    }
}
