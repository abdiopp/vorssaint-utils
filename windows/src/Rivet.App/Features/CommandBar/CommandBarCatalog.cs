// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Features.Toggles;
using Rivet.App.Modules;
using Rivet.Core.Actions;
using Rivet.Core.App;
using Rivet.Core.Clipboard;
using Rivet.Core.Contracts;
using Rivet.Core.Features;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;
using Rivet.Core.Toggles;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// Builds the Command Bar's rows from every source (spec 06 §3.8.7): the
/// app's actions and feature switches, quick toggles, power, Settings pages,
/// Windows Settings pages, snippets, saved shortcuts, folders, answers, apps,
/// windows, quit rows, clipboard history, emoji, computed answers, typed
/// addresses, files and the current selection. Rows are rebuilt at every
/// opening so they never carry stale state.
/// </summary>
public sealed class CommandBarCatalog
{
    public const string OwnActionId = "commandBarWindow.toggle";
    public const int SelectionMaxLength = 20_000;
    public const int CaseChangeMaxLength = 5_000;

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly CommandBarPreferences _preferences;
    private readonly ActionRegistry _actions;
    private readonly FeatureRuntime _runtime;
    private readonly SettingsPageRegistry _pages;
    private readonly ShortcutManager _shortcuts;
    private readonly IKeyNameProvider? _keyNames;
    private readonly IAppShell _shell;
    private readonly IShellService _shellService;
    private readonly ICommandBarPlatform _platform;
    private readonly ClipboardLane _lane;
    private readonly CaretActions _caret;
    private readonly IHud? _hud;

    public CommandBarCatalog(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _preferences = services.GetRequiredService<CommandBarPreferences>();
        _actions = services.GetRequiredService<ActionRegistry>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _pages = services.GetRequiredService<SettingsPageRegistry>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _keyNames = services.GetService<IKeyNameProvider>();
        _shell = services.GetRequiredService<IAppShell>();
        _shellService = services.GetRequiredService<IShellService>();
        _platform = services.GetRequiredService<ICommandBarPlatform>();
        _lane = services.GetRequiredService<ClipboardLane>();
        _caret = services.GetRequiredService<CaretActions>();
        _hud = services.GetService<IHud>();
    }

    /// <summary>Opens a category inside the bar (set by the controller): the "Emoji" browse row.</summary>
    public Action<CommandCategory>? OpenCategory { get; set; }

    /// <summary>Puts text in the bar's field (set by the controller): "Use it in the search".</summary>
    public Action<string>? FillField { get; set; }

    /// <summary>Runs a saved script from its row (set by the controller, which owns the script cache).</summary>
    public Func<CommandBarLink, string?, Task>? RunScript { get; set; }

    private static string AppName => AppIdentity.DisplayName;

    private CultureInfo Culture => Localizer.Current.Culture;

    // ── Catalog ────────────────────────────────────────────────────────

    /// <summary>Actions, switches, quick toggles, power, Settings pages, snippets, saved shortcuts, folders and answers.</summary>
    public List<CommandRow> BuildCatalog()
    {
        var rows = new List<CommandRow>();
        rows.AddRange(ActionRows());
        rows.AddRange(ToggleRows());
        rows.AddRange(QuickToggleRows());
        rows.AddRange(PowerRows());
        if (_preferences.IsSourceEnabled(CommandSource.Emoji))
        {
            rows.Add(new CommandRow
            {
                Id = "emoji.browse",
                Title = L.Get("commandBar.sourceEmoji"),
                Subtitle = L.Get("commandBar.everythingTitle"),
                Keywords = "emoji smiley",
                Icon = "Emoji",
                KeepsOpen = true,
                CountsUsage = false,
                Run = _ =>
                {
                    OpenCategory?.Invoke(CommandCategory.Emoji);
                    return Task.CompletedTask;
                },
            });
        }

        rows.AddRange(SettingsPageRows());
        rows.AddRange(SnippetRows());
        rows.AddRange(LinkRows());
        rows.AddRange(FolderRows());
        rows.AddRange(AnswerRows());
        return rows;
    }

    private string? ShortcutTextFor(string actionId)
    {
        foreach (var role in _shortcuts.Roles)
        {
            if (role.ActionId == actionId && _shortcuts.GetState(role) == ShortcutState.Active)
            {
                return _shortcuts.GetChord(role).ToDisplayString(_keyNames);
            }
        }

        return null;
    }

    private static string GroupTitle(string featureId) =>
        FeatureCatalog.Find(featureId) is { } feature ? L.Get(FeatureCatalog.GroupTitleKey(feature.Group)) : AppName;

    private IEnumerable<CommandRow> ActionRows()
    {
        foreach (var action in _actions.Available)
        {
            if (action.Id == OwnActionId || action.Id.StartsWith(QuickTogglesModule.ActionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var featureTitle = FeatureCatalog.Find(action.FeatureId) is { } feature ? L.Get(feature.TitleKey) : string.Empty;
            var id = action.Id;
            yield return new CommandRow
            {
                Id = "action." + id,
                Title = L.Get(action.TitleKey),
                Subtitle = action.SubtitleKey is { } subtitle ? L.Get(subtitle) : GroupTitle(action.FeatureId),
                Section = GroupTitle(action.FeatureId),
                Keywords = string.Join(' ', action.Keywords.Append(featureTitle)),
                Icon = action.Icon,
                FeatureId = action.FeatureId.Length > 0 ? action.FeatureId : null,
                ShortcutText = ShortcutTextFor(id),
                Run = _ => _actions.InvokeAsync(id, ActionSource.CommandBar),
            };
        }
    }

    /// <summary>"Turn on %@" / "Turn off %@" for every installed feature with exactly one switch.</summary>
    private IEnumerable<CommandRow> ToggleRows()
    {
        foreach (var feature in FeatureCatalog.All)
        {
            if (feature.EnableKeys.Count != 1 || !_runtime.IsAvailable(feature.Id))
            {
                continue;
            }

            var key = feature.EnableKeys[0];
            var on = _settings.Get(key);
            var name = L.Get(feature.TitleKey);
            yield return new CommandRow
            {
                Id = "toggle." + feature.Id,
                Title = L.Format(on ? "commandBar.turnOffFormat" : "commandBar.turnOnFormat", name),
                Subtitle = L.Get(FeatureCatalog.GroupTitleKey(feature.Group)),
                Keywords = name,
                Icon = feature.Icon,
                IsLive = on,
                FeatureId = feature.Id,
                Role = 1,
                Run = _ =>
                {
                    var now = !_settings.Get(key);
                    _settings.Set(key, now);
                    _hud?.Show(L.Format(now ? "win.commandBar.featureOnFormat" : "win.commandBar.featureOffFormat", name), now ? HudStyle.Success : HudStyle.Info, feature.Icon);
                    return Task.CompletedTask;
                },
            };
        }
    }

    private IEnumerable<CommandRow> QuickToggleRows()
    {
        if (!_runtime.IsAvailable(FeatureIds.QuickToggles) || _services.GetService<QuickToggleCatalog>() is not { } toggles)
        {
            yield break;
        }

        foreach (var id in toggles.Order)
        {
            // Mic mute is its own module's action row; sleep is a power row.
            if (id is QuickToggleId.MicMute or QuickToggleId.Sleep || !toggles.IsAvailable(id))
            {
                continue;
            }

            var toggle = id;
            yield return new CommandRow
            {
                Id = "action." + QuickToggleSettings.StorageId(id),
                Title = toggles.Title(id),
                Subtitle = L.Get("quickToggles.pageTitle"),
                Keywords = toggles.Caption(id),
                Icon = toggles.Icon(id),
                FeatureId = FeatureIds.QuickToggles,
                ConfirmPrompt = id == QuickToggleId.EmptyTrash ? L.Get("quickToggles.emptyTrashConfirmTitle") : null,
                Run = async _ =>
                {
                    if (!await toggles.RunAsync(toggle, null, null, ActionSource.CommandBar, confirmed: true).ConfigureAwait(true)
                        && toggles.Service.StateOf(toggle) == QuickToggleState.Failed)
                    {
                        _hud?.Show(L.Get("quickToggles.actionFailed"), HudStyle.Warning);
                    }
                },
            };
        }
    }

    private IEnumerable<CommandRow> PowerRows()
    {
        CommandRow Power(string key, string titleKey, string icon, PowerAction action, string? confirmKey) => new()
        {
            Id = "action.power." + key,
            Title = L.Get(titleKey),
            Subtitle = L.Get("win.commandBar.powerSubtitle"),
            Keywords = "power " + key,
            Icon = icon,
            FeatureId = FeatureIds.CommandBar,
            ConfirmPrompt = confirmKey is null ? null : L.Get(confirmKey),
            Run = async _ =>
            {
                await Task.Delay(150).ConfigureAwait(true);
                if (!_platform.Power(action))
                {
                    _hud?.Show(L.Get("quickToggles.actionFailed"), HudStyle.Warning);
                }
            },
        };

        yield return Power("sleep", "commandBar.powerSleep", "WeatherMoon", PowerAction.Sleep, null);
        yield return Power("restart", "commandBar.powerRestart", "ArrowClockwise", PowerAction.Restart, "commandBar.powerRestartConfirm");
        yield return Power("shutDown", "commandBar.powerShutDown", "Power", PowerAction.ShutDown, "commandBar.powerShutDownConfirm");
        yield return Power("logOut", "commandBar.powerLogOut", "SignOut", PowerAction.LogOut, "commandBar.powerLogOutConfirm");
    }

    private IEnumerable<CommandRow> SettingsPageRows()
    {
        var subtitle = L.Format("win.commandBar.settingsSubtitleFormat", AppName);
        foreach (var page in _pages.Pages)
        {
            if (page.FeatureIds.Count > 0 && !page.FeatureIds.Any(_runtime.IsAvailable))
            {
                continue;
            }

            var id = page.Id;
            var keywords = page.KeywordKeys.Select(L.Get).Concat(page.Keywords);
            yield return new CommandRow
            {
                Id = "settings." + id,
                Title = L.Get(page.TitleKey),
                Subtitle = subtitle,
                Keywords = string.Join(' ', keywords),
                Icon = page.Icon,
                FeatureId = page.FeatureIds.Count > 0 ? page.FeatureIds[0] : null,
                Role = 2,
                Run = _ =>
                {
                    _shell.OpenSettings(id);
                    return Task.CompletedTask;
                },
            };
        }
    }

    private IEnumerable<CommandRow> SnippetRows()
    {
        if (!_runtime.IsAvailable(FeatureIds.TextSnippets))
        {
            yield break;
        }

        foreach (var snippet in _settings.Get(SnippetSettings.Snippets).Where(s => s.Enabled && s.Name.Length > 0))
        {
            var captured = snippet;
            yield return new CommandRow
            {
                Id = "snippet." + snippet.Id.ToString("D"),
                Title = snippet.Name,
                Subtitle = L.Get("commandBar.kindSnippet"),
                Keywords = $"{snippet.Trigger} {snippet.Folder}",
                Icon = "TextExpand",
                ActsAtCaret = true,
                Run = ctx => InsertSnippetAsync(captured, ctx),
            };
        }
    }

    private async Task InsertSnippetAsync(TextSnippet snippet, CommandRunContext context)
    {
        string? clipboard = null;
        if (SnippetVariables.UsesClipboard(snippet.Replacement))
        {
            clipboard = await ClipboardText.ReadAsync(_lane, TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }

        var text = SnippetVariables.Expand(snippet.Replacement, DateTimeOffset.Now, CultureInfo.CurrentCulture, TimeZoneInfo.Local, clipboard);
        await TypeAsync(context, text).ConfigureAwait(true);
    }

    /// <summary>Types at the caret of the app that was in front when the bar opened.</summary>
    public async Task TypeAsync(CommandRunContext context, string text)
    {
        var outcome = await _caret.TypeAsync(context.Target as ForegroundApp, text).ConfigureAwait(true);
        Report(outcome);
    }

    private void Report(CaretOutcome outcome)
    {
        if (outcome == CaretOutcome.Elevated)
        {
            _hud?.Show(L.Get("win.clipboard.elevatedTarget"), HudStyle.Warning);
        }
        else if (outcome == CaretOutcome.CopiedOnly)
        {
            _caret.Foreground.Beep();
        }
    }

    private IEnumerable<CommandRow> LinkRows()
    {
        foreach (var link in _settings.Get(CommandBarSettings.Links))
        {
            var captured = link;
            var script = link.Kind == CommandBarLinkKind.Script;
            var search = link.IsSearch;
            yield return new CommandRow
            {
                Id = "link." + link.Id.ToString("D"),
                Title = link.Name,
                Subtitle = script
                    ? L.Get(link.RunsWithoutArgument ? "commandBar.scriptBareSearchHint" : "commandBar.scriptSearchHint")
                    : search ? L.Get("commandBar.linkSearchHint") : L.Get("commandBar.kindLink"),
                Keywords = L.Get("commandBar.kindLink"),
                Icon = link.Kind switch
                {
                    CommandBarLinkKind.Place => "Folder",
                    CommandBarLinkKind.Script => "WindowConsole",
                    _ => "Globe",
                },
                NameForArgument = script || search ? link.Name : null,
                IsScript = script,
                RunsBare = script && link.RunsWithoutArgument,
                KeepsOpen = script,
                RevealPath = link.Kind != CommandBarLinkKind.Link ? ExpandHome(link.Destination) : null,
                Run = ctx => script ? RunScript?.Invoke(captured, ctx.Argument) ?? Task.CompletedTask : OpenLinkAsync(captured, ctx.Argument),
            };
        }
    }

    private string ExpandHome(string path) => CommandBarLinks.PlaceTarget(path, _platform.HomeFolder);

    /// <summary>Opens a saved link or place, filling its placeholders.</summary>
    public async Task OpenLinkAsync(CommandBarLink link, string? argument, string? selection = null)
    {
        string? clipboard = null;
        if (link.Destination.Contains("{clipboard}", StringComparison.Ordinal))
        {
            clipboard = await ClipboardText.ReadAsync(_lane, TimeSpan.FromSeconds(1)).ConfigureAwait(true);
        }

        var values = new LinkPlaceholderValues(argument, clipboard, selection, DateTimeOffset.Now, CultureInfo.CurrentCulture);
        var expanded = CommandBarLinks.Expand(link.Destination, link.Kind, values);
        if (link.Kind == CommandBarLinkKind.Link)
        {
            _shellService.OpenUrl(CommandBarLinks.LinkTarget(expanded));
            return;
        }

        var path = CommandBarLinks.PlaceTarget(expanded, _platform.HomeFolder);
        if (File.Exists(path) || Directory.Exists(path))
        {
            _shellService.OpenFile(path);
        }
        else
        {
            _hud?.Show(link.Name, HudStyle.Warning, "Warning");
        }
    }

    private IEnumerable<CommandRow> FolderRows()
    {
        if (!_preferences.IsSourceEnabled(CommandSource.Folders))
        {
            yield break;
        }

        foreach (var folder in _platform.KnownFolders())
        {
            var path = folder.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            yield return new CommandRow
            {
                Id = "folder." + folder.Key,
                Title = folder.Key == "home" ? Path.GetFileName(path.TrimEnd('\\', '/')) : L.Get("win.commandBar.folder." + folder.Key),
                Subtitle = L.Get("commandBar.kindFolder"),
                Keywords = folder.Key,
                Icon = "Folder",
                RevealPath = path,
                Run = _ =>
                {
                    _shellService.OpenFile(path);
                    return Task.CompletedTask;
                },
            };
        }
    }

    /// <summary>"Answers about this PC": battery, memory, storage, today and the time.</summary>
    private IEnumerable<CommandRow> AnswerRows()
    {
        if (!_preferences.IsSourceEnabled(CommandSource.Answers))
        {
            yield break;
        }

        SystemAnswers answers;
        try
        {
            answers = _platform.ReadAnswers();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("commandBar", $"Reading the answers failed: {ex.GetType().Name}");
            answers = new SystemAnswers();
        }

        var culture = Culture;
        if (answers.BatteryPercent is { } battery)
        {
            var value = string.Format(culture, "{0}%", battery);
            yield return Answer("battery", L.Get("commandBar.answerBatteryLabel"),
                answers.Charging ? L.Get("commandBar.answerBatteryCharging") : answers.PluggedIn ? L.Get("commandBar.answerBatteryPlugged") : L.Get("commandBar.answerBatteryLabel"),
                value, value, "Battery10");
        }

        if (answers.MemoryTotal > 0)
        {
            var percent = (int)Math.Round(answers.MemoryUsed * 100.0 / answers.MemoryTotal);
            var used = QuickToggleCatalog.FormatBytes(answers.MemoryUsed);
            var total = QuickToggleCatalog.FormatBytes(answers.MemoryTotal);
            yield return Answer("memory", L.Get("commandBar.answerMemoryLabel"), L.Format("commandBar.answerMemoryFormat", used, total),
                string.Format(culture, "{0}%", percent), $"{used} / {total}", "Ram");
        }

        if (answers.StorageTotal > 0)
        {
            var percent = (int)Math.Round(answers.StorageFree * 100.0 / answers.StorageTotal);
            var free = QuickToggleCatalog.FormatBytes(answers.StorageFree);
            yield return Answer("storage", L.Get("commandBar.answerStorageLabel"), L.Format("commandBar.answerStorageFormat", free, QuickToggleCatalog.FormatBytes(answers.StorageTotal)),
                string.Format(culture, "{0}%", percent), free, "Storage");
        }

        var now = DateTimeOffset.Now;
        var shortDate = now.ToString("d", culture);
        yield return Answer("date", L.Get("commandBar.answerDateLabel"), now.ToString("D", culture), shortDate, shortDate, "CalendarToday");
        var time = now.ToString("T", culture);
        yield return Answer("time", L.Get("commandBar.answerTimeLabel"), now.ToString("D", culture), time, time, "Clock");
    }

    private CommandRow Answer(string key, string title, string subtitle, string value, string copyText, string icon) => new()
    {
        Id = "answer." + key,
        Title = title,
        Subtitle = subtitle,
        Value = value,
        Icon = icon,
        Section = L.Get("commandBar.kindAnswer"),
        Run = _ => CopyAsync(copyText),
    };

    /// <summary>Copies text and shows it in the HUD (answers, script output, "Copy it").</summary>
    public async Task CopyAsync(string text)
    {
        var ok = await ClipboardText.CopyAsync(_lane, text).ConfigureAwait(true);
        var preview = text.Length > 60 ? text[..60] + "…" : text;
        _hud?.Show(ok ? preview : L.Get("commandBar.copyFailed"), ok ? HudStyle.Success : HudStyle.Warning, ok ? "Copy" : "Warning");
    }

    // ── Apps, windows, quit rows, Windows Settings ─────────────────────

    public List<CommandRow> AppRows(IReadOnlyList<InstalledApp> apps, IReadOnlyList<OpenWindow> windows)
    {
        var rows = new List<CommandRow>(apps.Count);
        foreach (var app in apps)
        {
            var captured = app;
            var appWindows = windows.Where(w => IsWindowOf(app, w)).ToList();
            var running = appWindows.Count > 0;
            var actions = new List<CommandRowAction>();
            if (running)
            {
                var pid = appWindows[0].ProcessId;
                actions.Add(new CommandRowAction("quit", L.Format("commandBar.quitFormat", app.Name), "Dismiss", _ =>
                {
                    _platform.CloseApp(pid);
                    return Task.CompletedTask;
                }, L.Format("commandBar.quitConfirmFormat", app.Name)));
                actions.Add(new CommandRowAction("restart", L.Format("commandBar.restartAppFormat", app.Name), "ArrowClockwise", _ => RestartAppAsync(captured, pid)));
            }

            var keywords = new List<string>(app.AlternateNames);
            if (app.RevealPath is { } revealed && Path.GetFileNameWithoutExtension(revealed) is { Length: > 0 } fileName
                && !string.Equals(fileName, app.Name, StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(fileName);
            }

            rows.Add(new CommandRow
            {
                Id = "app." + app.Identity,
                Title = app.Name,
                Subtitle = L.Get("commandBar.kindApp"),
                Keywords = string.Join(' ', keywords),
                IconPath = app.IconPath ?? app.RevealPath,
                Icon = "Apps",
                IsLive = running,
                RevealPath = app.RevealPath,
                Actions = actions,
                Run = _ =>
                {
                    if (appWindows.Count > 0 && _platform.Activate(appWindows[0]))
                    {
                        return Task.CompletedTask;
                    }

                    if (!_platform.Launch(captured))
                    {
                        _hud?.Show(captured.Name, HudStyle.Warning, "Warning");
                    }

                    return Task.CompletedTask;
                },
            });
        }

        return rows;
    }

    /// <summary>A window belongs to an app by executable (shortcut target), by package id, or by identity.</summary>
    public static bool IsWindowOf(InstalledApp app, OpenWindow window) =>
        (app.ExecutablePath is { } exe && string.Equals(window.AppIdentity, exe, StringComparison.OrdinalIgnoreCase))
        || (window.AppUserModelId is { } aumid && string.Equals(aumid, app.Identity, StringComparison.OrdinalIgnoreCase))
        || string.Equals(window.AppIdentity, app.Identity, StringComparison.OrdinalIgnoreCase);

    private async Task RestartAppAsync(InstalledApp app, int processId)
    {
        _platform.CloseApp(processId);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (OperationCanceledException)
        {
            return; // It never quit (a save dialog is probably waiting).
        }
        catch (InvalidOperationException)
        {
        }

        _platform.Launch(app);
    }

    public List<CommandRow> WindowRows(IReadOnlyList<OpenWindow> windows)
    {
        var rows = new List<CommandRow>();
        foreach (var window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.Title) || string.Equals(window.Title, window.AppName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var captured = window;
            rows.Add(new CommandRow
            {
                Id = "window." + window.Handle.ToString(CultureInfo.InvariantCulture),
                StableKey = "window." + (window.AppIdentity ?? window.AppName) + "|" + window.Title,
                Title = window.Title,
                Subtitle = window.AppName,
                Keywords = L.Get("commandBar.kindWindow") + " " + window.AppName,
                IconPath = window.ExecutablePath,
                Icon = "Window",
                CountsUsage = false,
                Run = async _ =>
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    _platform.Activate(captured);
                },
            });
        }

        return rows;
    }

    public List<CommandRow> QuitRows(IReadOnlyList<OpenWindow> windows)
    {
        var rows = new List<CommandRow>();
        foreach (var group in windows.GroupBy(w => w.AppIdentity ?? w.AppName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var name = first.AppName;
            rows.Add(new CommandRow
            {
                Id = "quit." + group.Key,
                Title = L.Format("commandBar.quitFormat", name),
                Subtitle = L.Get("commandBar.sourceQuitApps"),
                IconPath = first.ExecutablePath,
                Icon = "Dismiss",
                ConfirmPrompt = L.Format("commandBar.quitConfirmFormat", name),
                CountsUsage = first.AppIdentity is not null,
                Run = async _ =>
                {
                    _platform.Activate(first);
                    await Task.Delay(120).ConfigureAwait(true);
                    _platform.CloseApp(first.ProcessId);
                },
            });
        }

        return rows;
    }

    public List<CommandRow> WindowsSettingsRows()
    {
        var subtitle = L.Get("commandBar.sourceMacSettings");
        return WindowsSettingsPages.All.Select(page => new CommandRow
        {
            Id = page.RowId,
            Title = L.Get(page.TitleKey),
            Subtitle = subtitle,
            Keywords = page.Keywords,
            Icon = "Settings",
            Run = _ =>
            {
                _shellService.OpenSystemSettings(page.Uri);
                return Task.CompletedTask;
            },
        }).ToList();
    }

    // ── Clipboard, emoji, answers, typed addresses, files ──────────────

    public IReadOnlyList<CommandRow> ClipboardRows(string query, int limit)
    {
        if (_services.GetService<ClipboardHistoryService>() is not { } history
            || !_runtime.IsAvailable(FeatureIds.ClipboardHistory)
            || (!_settings.Get(ClipboardSettings.Enabled) && history.History.Entries.Count == 0))
        {
            return [];
        }

        var entries = query.Trim().Length == 0 ? history.History.Entries.Take(limit) : history.Search(query).Take(limit);
        return entries.Select(entry => ClipboardRow(history, entry)).ToList();
    }

    private CommandRow ClipboardRow(ClipboardHistoryService history, ClipboardEntry entry)
    {
        var title = entry.Kind switch
        {
            ClipboardEntryKind.Image => ClipboardUi.ImageCaption(entry),
            ClipboardEntryKind.Files => ClipboardUi.FilesTitle(entry),
            _ => entry.Preview,
        };
        return new CommandRow
        {
            Id = "clipboard." + entry.Id.ToString("D"),
            Title = title,
            Subtitle = L.Get("commandBar.kindClipboard"),
            Keywords = entry.SearchableText(history.ImageLabel),
            Icon = entry.Kind == ClipboardEntryKind.Files ? "Document" : "ClipboardPaste",
            ImagePath = ClipboardUi.PicturePath(entry, history.Images),
            Swatch = entry.Color?.ToArgb(),
            CountsUsage = false,
            Pinnable = false,
            Nameable = false,
            ActsAtCaret = true,
            Run = async ctx =>
            {
                if (!await history.CopyAsync([entry]).ConfigureAwait(true))
                {
                    _caret.Foreground.Beep();
                    return;
                }

                Report(await _caret.PasteAsync(ctx.Target as ForegroundApp, strictModifiers: true, TimeSpan.FromMilliseconds(150)).ConfigureAwait(true));
            },
        };
    }

    public CommandRow EmojiRow(EmojiEntry entry)
    {
        var tone = _preferences.SkinTone;
        var glyph = CommandBarEmoji.ApplySkinTone(entry.Glyph, tone);
        var actions = CommandBarEmoji.OtherTones(entry.Glyph, tone)
            .Select(t => new CommandRowAction("tone." + t.Tone, t.Glyph, "Emoji", ctx => TypeAsync(ctx, t.Glyph)))
            .ToList();
        return new CommandRow
        {
            Id = "emoji." + entry.Glyph,
            Title = entry.Name,
            Glyph = glyph,
            CompletionText = entry.Name,
            Subtitle = L.Get("commandBar.kindEmoji"),
            Keywords = entry.Keywords,
            ActsAtCaret = true,
            Actions = actions,
            Run = ctx => TypeAsync(ctx, glyph),
        };
    }

    public CommandRow? AnswerRow(string query)
    {
        if (CommandBarAnswers.Compute(query, Culture, DateTimeOffset.Now) is not { } answer)
        {
            return null;
        }

        return new CommandRow
        {
            Id = answer.RowId,
            Title = answer.Title,
            Subtitle = answer.Subtitle,
            Icon = answer.Kind switch
            {
                AnswerKind.Math => "Calculator",
                AnswerKind.Units => "ArrowSwap",
                AnswerKind.Date => "CalendarToday",
                _ => "Color",
            },
            Swatch = answer.Swatch,
            CompletionText = answer.Math is { } math ? CommandBarMath.ReusableText(math.Value, Culture) : null,
            CountsUsage = false,
            Pinnable = false,
            Nameable = false,
            Run = _ => CopyAsync(answer.CopyText),
        };
    }

    public CommandRow? TypedUrlRow(string query)
    {
        if (CommandBarLinks.TypedUrl(query) is not { } url)
        {
            return null;
        }

        return new CommandRow
        {
            Id = "action.openURL",
            Title = query.Trim(),
            Subtitle = L.Get("commandBar.openInBrowser"),
            Icon = "Globe",
            CountsUsage = false,
            Pinnable = false,
            Nameable = false,
            Run = _ =>
            {
                _shellService.OpenUrl(url);
                return Task.CompletedTask;
            },
        };
    }

    public CommandRow FileRow(FileHit hit)
    {
        var home = _platform.HomeFolder;
        var folder = hit.Folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + hit.Folder[home.Length..] : hit.Folder;
        var path = hit.Path;
        return new CommandRow
        {
            Id = "file." + path,
            Title = hit.Name,
            Subtitle = folder,
            IconPath = path,
            Icon = "Document",
            RevealPath = path,
            CountsUsage = false,
            Pinnable = false,
            Nameable = false,
            Run = _ =>
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    _shellService.OpenFile(path);
                }
                else
                {
                    _hud?.Show(hit.Name, HudStyle.Warning, "Warning");
                }

                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Rows acting on the text selected when the bar opened (spec 06 §3.8.7 P15).</summary>
    public List<CommandRow> SelectionRows(string? selection)
    {
        var text = selection?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var heading = L.Get("commandBar.kindSelection");
        var rows = new List<CommandRow>
        {
            new()
            {
                Id = "selection.copy",
                Title = L.Get("commandBar.selectionCopy"),
                Subtitle = heading,
                Icon = "Copy",
                Run = _ => CopyAsync(text),
            },
            new()
            {
                Id = "selection.search",
                Title = L.Get("commandBar.selectionSearch"),
                Subtitle = heading,
                Icon = "Search",
                KeepsOpen = true,
                CountsUsage = false,
                Run = _ =>
                {
                    FillField?.Invoke(text);
                    return Task.CompletedTask;
                },
            },
        };

        if (_services.GetService<UrlCleanerService>() is { } cleaner && _runtime.IsAvailable(FeatureIds.UrlCleaner)
            && cleaner.Evaluate(text) is { Kind: UrlCleanOutcomeKind.Removed or UrlCleanOutcomeKind.Rewritten, Url: { } cleaned })
        {
            rows.Add(new CommandRow
            {
                Id = "selection.cleanURL",
                Title = L.Get("commandBar.actionCleanURL"),
                Subtitle = heading,
                Icon = "LinkDismiss",
                Run = async _ =>
                {
                    await cleaner.CopyAsync(cleaned).ConfigureAwait(true);
                    _hud?.Show(cleaned, HudStyle.Success, "Link");
                },
            });
        }

        if (text.Length <= CaseChangeMaxLength)
        {
            var culture = Culture;
            void AddCase(string id, string titleKey, string converted)
            {
                if (converted != text)
                {
                    rows.Add(new CommandRow
                    {
                        Id = "selection." + id,
                        Title = L.Get(titleKey),
                        Subtitle = heading,
                        Icon = "TextCaseTitle",
                        ActsAtCaret = true,
                        Run = ctx => TypeAsync(ctx, converted),
                    });
                }
            }

            AddCase("upper", "commandBar.selectionUpper", text.ToUpper(culture));
            AddCase("lower", "commandBar.selectionLower", text.ToLower(culture));
            AddCase("title", "commandBar.selectionTitleCase", culture.TextInfo.ToTitleCase(text.ToLower(culture)));
        }

        if (_services.GetService<IShelfIntake>() is { IsAvailable: true } shelf)
        {
            rows.Add(new CommandRow
            {
                Id = "selection.shelf",
                Title = L.Get("commandBar.selectionShelf"),
                Subtitle = heading,
                Icon = "TrayItemAdd",
                Run = _ =>
                {
                    shelf.AddText(text);
                    return Task.CompletedTask;
                },
            });
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var characters = new StringInfo(text).LengthInTextElements;
        var count = L.Format("commandBar.selectionCountFormat", words, characters);
        rows.Add(new CommandRow
        {
            Id = "selection.count",
            Title = L.Get("commandBar.selectionCount"),
            Subtitle = count,
            Value = characters.ToString("N0", Culture),
            Icon = "TextWordCount",
            CountsUsage = false,
            Run = _ => CopyAsync(count),
        });
        return rows;
    }

    /// <summary>A result from another module's search provider.</summary>
    public static CommandRow ExternalRow(ISearchProvider provider, SearchResult result) => new()
    {
        Id = $"ext.{provider.Id}.{result.Id}",
        Title = result.Title,
        Subtitle = result.Subtitle ?? result.Category,
        Icon = result.Icon ?? "Apps",
        IconPath = result.IconPath,
        CountsUsage = false,
        Pinnable = false,
        Nameable = false,
        Run = _ => result.Activate(),
    };
}
