// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Features.Clipboard;
using Rivet.App.Modules;
using Rivet.Core.Clipboard;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Launcher;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;
using Rivet.Core.Snippets;

namespace Rivet.App.Features.Launcher;

/// <summary>
/// Runs the Command Bar (spec 06 §3.8): opening and closing, the background
/// loads of each opening (apps, windows, files, scripts, other modules'
/// providers), running rows and their actions, personalization (pins, names,
/// row shortcuts, hidden rows) and the run counts. Nothing typed is stored.
/// </summary>
public sealed class CommandBarController : IFeatureController, IDisposable
{
    private static readonly TimeSpan FileDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ScriptDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProviderDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SelectionBudget = TimeSpan.FromMilliseconds(150);

    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly CommandBarPreferences _preferences;
    private readonly ICommandBarPlatform _platform;
    private readonly CaretActions _caret;
    private readonly IShellService _shellService;
    private readonly IHotkeyService _hotkeys;
    private readonly ShortcutManager _shortcuts;
    private readonly FeatureRuntime _runtime;
    private readonly SearchProviderRegistry _providers;
    private readonly IHud? _hud;
    private readonly IKeyNameProvider? _keyNames;
    private readonly List<IDisposable> _rowShortcutHandles = [];
    private readonly IDisposable _settingsSubscription;
    private readonly Dictionary<string, IReadOnlyList<CommandRow>> _fileCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<CommandRow>> _providerCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid Script, string Argument), ScriptResult> _scriptCache = [];
    private CommandBarWindow? _window;
    private ForegroundApp? _target;
    private IDisposable? _snippetSuspension;
    private CancellationTokenSource? _fileSearch;
    private CancellationTokenSource? _scriptRun;
    private CancellationTokenSource? _providerSearch;
    private string? _fileQueryPending;
    private (Guid Script, string Argument)? _scriptPending;
    private string? _providerQueryPending;
    private IReadOnlyList<InstalledApp>? _apps;
    private IReadOnlyList<OpenWindow> _windows = [];
    private long _windowsReadAt;
    private List<CommandRow>? _osPages;
    private string? _osPagesLanguage;
    private bool _available;
    private bool _usageDirty;

    public CommandBarController(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsStore>();
        _preferences = services.GetRequiredService<CommandBarPreferences>();
        _platform = services.GetRequiredService<ICommandBarPlatform>();
        _caret = services.GetRequiredService<CaretActions>();
        _shellService = services.GetRequiredService<IShellService>();
        _hotkeys = services.GetRequiredService<IHotkeyService>();
        _shortcuts = services.GetRequiredService<ShortcutManager>();
        _runtime = services.GetRequiredService<FeatureRuntime>();
        _providers = services.GetRequiredService<SearchProviderRegistry>();
        _hud = services.GetService<IHud>();
        _keyNames = services.GetService<IKeyNameProvider>();
        Catalog = services.GetRequiredService<CommandBarCatalog>();
        Engine = new CommandBarEngine(_preferences, _preferences.LoadUsage())
        {
            CuratedFeatures = [FeatureIds.Screenshot, FeatureIds.KeepAwake, FeatureIds.ScreenOcr, FeatureIds.ClipboardHistory, FeatureIds.ColorPicker, FeatureIds.QuickToggles, FeatureIds.TextSnippets],
            ClipboardRows = Catalog.ClipboardRows,
            Answer = Catalog.AnswerRow,
            TypedUrl = Catalog.TypedUrlRow,
            EmojiRow = Catalog.EmojiRow,
        };
        Engine.FileRows = FileRows;
        Engine.ScriptAnswer = ScriptAnswer;
        Engine.NamedScriptId = query => NamedScript(query)?.Row;
        Session = new CommandBarSession(Engine, _preferences) { ExtraRows = ProviderRows };
        Catalog.OpenCategory = category =>
        {
            Session.SelectCategory(category);
            Refresh();
        };
        Catalog.FillField = text =>
        {
            Session.SetQuery(text);
            QueryChanged();
        };
        Catalog.RunScript = RunScriptFromRowAsync;
        _settingsSubscription = _settings.Observe(() => Dispatcher.UIThread.Post(OnSettingsChanged),
            CommandBarSettings.RowShortcuts, CommandBarSettings.EmojiSkinTone, CommandBarSettings.Hidden);
    }

    public IServiceProvider Services => _services;

    public CommandBarEngine Engine { get; }

    public CommandBarSession Session { get; }

    public CommandBarCatalog Catalog { get; }

    public ForegroundApp? Target => _target;

    /// <summary>Raised when the list or the mode changed (the window re-renders).</summary>
    public event EventHandler? Changed;

    public bool IsOpen => _window?.IsVisible == true;

    public CommandBarWindow Window => _window ??= new CommandBarWindow(this);

    /// <summary>The bar's own shortcut, for the footer.</summary>
    public string? OwnShortcutText =>
        _shortcuts.Find(CommandBarModule.RoleId) is { } role && _shortcuts.GetState(role) == ShortcutState.Active
            ? _shortcuts.GetChord(role).ToDisplayString(_keyNames)
            : null;

    public string ChordText(KeyChord chord) => chord.ToDisplayString(_keyNames);

    // ── Feature life ───────────────────────────────────────────────────

    public void Sync(bool available)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _available = available;
            RegisterRowShortcuts();
            if (!available)
            {
                Close(FloatingCloseReason.Action);
            }
        });
    }

    private void OnSettingsChanged()
    {
        Engine.InvalidateEmoji();
        RegisterRowShortcuts();
    }

    // ── Open / close ───────────────────────────────────────────────────

    public void Toggle()
    {
        if (IsOpen)
        {
            Close(FloatingCloseReason.Escape);
        }
        else
        {
            _ = OpenAsync();
        }
    }

    /// <summary>Opens the bar: remember the app in front, read its selection, then show and hydrate.</summary>
    public async Task OpenAsync(string? query = null, CommandRow? runAfterLoad = null)
    {
        if (IsOpen)
        {
            Window.Present();
            return;
        }

        _target = _caret.CaptureTarget();
        string? selection = null;
        if (_preferences.IsSourceEnabled(CommandSource.Selection) && _target is { IsSelf: false })
        {
            using var budget = new CancellationTokenSource(SelectionBudget);
            try
            {
                selection = await _platform.ReadSelectionAsync(CommandBarCatalog.SelectionMaxLength, budget.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Info("commandBar", $"Selection read failed ({ex.GetType().Name}).");
            }
        }

        _snippetSuspension ??= _services.GetService<SnippetExpansionService>()?.Suspend();
        ResetCaches();
        Engine.SelectionText = selection?.Trim();
        Engine.SelectionRows = Catalog.SelectionRows(selection);
        Engine.Catalog = [];
        Engine.ExternalRows = [];
        Session.Begin(_settings.Get(CommandBarSettings.CompactMode));
        Window.Present();
        Refresh();

        // Home hydration on the next turn, only for this opening.
        var presentation = Session.Presentation;
        await Task.Yield();
        if (presentation != Session.Presentation || !IsOpen)
        {
            return;
        }

        Hydrate();
        if (query is not null)
        {
            Session.SetQuery(query);
            QueryChanged();
        }

        _ = LoadAppsAsync(presentation, runAfterLoad);
    }

    private void Hydrate()
    {
        Engine.Catalog = Catalog.BuildCatalog();
        RefreshWindows(force: false);
        if (_preferences.IsSourceEnabled(CommandSource.MacSettings))
        {
            var language = Localizer.Current.Language.ToString();
            if (_osPages is null || _osPagesLanguage != language)
            {
                _osPages = Catalog.WindowsSettingsRows();
                _osPagesLanguage = language;
            }

            Engine.OsPages = _osPages;
        }
        else
        {
            Engine.OsPages = [];
        }

        if (_apps is not null)
        {
            Engine.Apps = _preferences.IsSourceEnabled(CommandSource.Apps) ? Catalog.AppRows(_apps, _windows) : [];
        }

        Session.Rebuild();
        Refresh();
    }

    private void RefreshWindows(bool force)
    {
        if (force || Environment.TickCount64 - _windowsReadAt > 4000)
        {
            try
            {
                _windows = _platform.GetWindows();
            }
            catch (Exception ex)
            {
                Log.Warn("commandBar", $"Listing windows failed: {ex.GetType().Name}");
                _windows = [];
            }

            _windowsReadAt = Environment.TickCount64;
        }

        Engine.Windows = _preferences.IsSourceEnabled(CommandSource.Windows) ? Catalog.WindowRows(_windows) : [];
        Engine.QuitRows = _preferences.IsSourceEnabled(CommandSource.QuitApps) ? Catalog.QuitRows(_windows) : [];
    }

    /// <summary>Installed apps, scanned at every opening off the UI thread (the previous list stays until it lands).</summary>
    private async Task LoadAppsAsync(int presentation, CommandRow? runAfterLoad)
    {
        if (!_preferences.IsSourceEnabled(CommandSource.Apps))
        {
            Engine.Apps = [];
            return;
        }

        try
        {
            var apps = await Task.Run(() => _platform.GetAppsAsync(CancellationToken.None)).ConfigureAwait(true);
            _apps = apps;
            if (presentation != Session.Presentation || !IsOpen)
            {
                return;
            }

            Engine.Apps = Catalog.AppRows(apps, _windows);
            Session.Rebuild();
            Refresh();
            if (runAfterLoad is not null && Engine.Apps.FirstOrDefault(r => r.StableKey == runAfterLoad.StableKey) is { } fresh)
            {
                Carry(Session.Plan(fresh));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("commandBar", $"Listing apps failed: {ex.GetType().Name}");
        }
    }

    public void Close(FloatingCloseReason reason)
    {
        _snippetSuspension?.Dispose();
        _snippetSuspension = null;
        ResetCaches();
        if (_window is { IsVisible: true } window)
        {
            window.Dismiss(reason);
        }

        Engine.SelectionRows = [];
        Engine.SelectionText = null;
        SaveUsage();
    }

    /// <summary>Called by the window after it hid (any reason).</summary>
    internal void OnWindowDismissed(FloatingCloseReason reason)
    {
        _snippetSuspension?.Dispose();
        _snippetSuspension = null;
        ResetCaches();
        Session.Begin(_settings.Get(CommandBarSettings.CompactMode));
        if (reason == FloatingCloseReason.Escape)
        {
            _caret.RestoreFocus(_target);
        }
    }

    private void ResetCaches()
    {
        _fileSearch?.Cancel();
        _scriptRun?.Cancel();
        _providerSearch?.Cancel();
        _fileSearch = _scriptRun = _providerSearch = null;
        _fileQueryPending = null;
        _scriptPending = null;
        _providerQueryPending = null;
        _fileCache.Clear();
        _scriptCache.Clear();
        _providerCache.Clear();
    }

    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    // ── Typing ─────────────────────────────────────────────────────────

    public void SetQuery(string text)
    {
        Session.SetQuery(text);
        QueryChanged();
    }

    private void QueryChanged()
    {
        if (Session.Mode == CommandBarMode.Search && Session.Query.Trim().Length > 0)
        {
            ScheduleScript(Session.Query);
        }
        else
        {
            _scriptRun?.Cancel();
        }

        Refresh();
    }

    // ── Files ──────────────────────────────────────────────────────────

    private IReadOnlyList<CommandRow> FileRows(string query)
    {
        var key = CommandBarSession.Fold(query);
        if (key.Length < 2)
        {
            return [];
        }

        if (_fileCache.TryGetValue(key, out var rows))
        {
            return rows;
        }

        if (_fileQueryPending != key)
        {
            _ = SearchFilesAsync(key);
        }

        return [];
    }

    private async Task SearchFilesAsync(string key)
    {
        _fileSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _fileSearch = cts;
        _fileQueryPending = key;
        var presentation = Session.Presentation;
        try
        {
            await Task.Delay(FileDebounce, cts.Token).ConfigureAwait(true);
            var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var scopes = _preferences.FileScopes.Select(s => CommandBarLinks.PlaceTarget(s, _platform.HomeFolder)).ToList();
            var ignores = _preferences.FileIgnores;
            var hits = scopes.Count > 0
                ? await _platform.SearchFilesAsync(scopes, words, ignores, 200, cts.Token).ConfigureAwait(true)
                : await _platform.RecentFilesAsync(words, 200, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || presentation != Session.Presentation)
            {
                return;
            }

            _fileCache[key] = hits.Select(Catalog.FileRow).ToList();
            if (_fileQueryPending == key)
            {
                _fileQueryPending = null;
            }

            if (CommandBarSession.Fold(Session.Query) == key)
            {
                Session.Rebuild();
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("commandBar", $"File search failed: {ex.GetType().Name}");
            _fileCache[key] = [];
        }
    }

    // ── Other modules' providers ───────────────────────────────────────

    private IReadOnlyList<CommandRow> ProviderRows(string query)
    {
        var key = query.Trim();
        if (key.Length < 2 || !ExternalProviders().Any())
        {
            return [];
        }

        if (_providerCache.TryGetValue(key, out var rows))
        {
            return rows;
        }

        if (_providerQueryPending != key)
        {
            _ = SearchProvidersAsync(key);
        }

        return [];
    }

    /// <summary>Other modules' providers (the bar's own sources are already in its pool).</summary>
    private IEnumerable<ISearchProvider> ExternalProviders() => _providers.Active.Where(p => !CommandBarProviders.IsOwn(p));

    private async Task SearchProvidersAsync(string key)
    {
        _providerSearch?.Cancel();
        var cts = new CancellationTokenSource();
        _providerSearch = cts;
        _providerQueryPending = key;
        var presentation = Session.Presentation;
        try
        {
            await Task.Delay(ProviderDebounce, cts.Token).ConfigureAwait(true);
            var results = new List<(double Score, CommandRow Row)>();
            foreach (var provider in ExternalProviders())
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    foreach (var result in await provider.SearchAsync(key, timeout.Token).ConfigureAwait(true))
                    {
                        results.Add((result.Score, CommandBarCatalog.ExternalRow(provider, result)));
                    }
                }
                catch (OperationCanceledException) when (!cts.IsCancellationRequested)
                {
                    Log.Info("commandBar", $"Provider {provider.Id} timed out.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("commandBar", $"Provider {provider.Id} failed: {ex.GetType().Name}");
                }
            }

            if (cts.IsCancellationRequested || presentation != Session.Presentation)
            {
                return;
            }

            _providerCache[key] = results.OrderByDescending(r => r.Score).Select(r => r.Row).Take(6).ToList();
            if (_providerQueryPending == key)
            {
                _providerQueryPending = null;
            }

            if (Session.Query.Trim() == key)
            {
                Session.Rebuild();
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Scripts ────────────────────────────────────────────────────────

    /// <summary>The script the query names (longest name wins) and its argument.</summary>
    private (string Row, CommandBarLink Link, string Argument)? NamedScript(string query)
    {
        if (!_preferences.IsSourceEnabled(CommandSource.Links))
        {
            return null;
        }

        (string, CommandBarLink, string)? best = null;
        var bestLength = -1;
        foreach (var link in _settings.Get(CommandBarSettings.Links).Where(l => l.Kind == CommandBarLinkKind.Script))
        {
            var argument = CommandBarLinks.TrailingArgument(query, link.Name);
            if (argument is null && !(link.RunsWithoutArgument && CommandBarLinks.NamesExactly(query, link.Name)))
            {
                continue;
            }

            var length = CommandBarSession.Fold(link.Name).Length;
            if (length > bestLength)
            {
                bestLength = length;
                best = ("link." + link.Id.ToString("D"), link, argument ?? string.Empty);
            }
        }

        return best;
    }

    private CommandRow? ScriptAnswer(string query)
    {
        if (NamedScript(query) is not { } named || !_scriptCache.TryGetValue((named.Link.Id, named.Argument), out var result))
        {
            return null;
        }

        if (!result.Failed && result.Output.Length == 0)
        {
            return null; // Empty output with exit 0: nothing to show.
        }

        var output = result.Failed ? L.Get("commandBar.scriptRunFailed") : result.Output;
        return new CommandRow
        {
            Id = "scriptAnswer." + named.Link.Id.ToString("D"),
            Title = output.Length > 300 ? output[..300] + "…" : output,
            Subtitle = result.Failed ? named.Link.Name : L.Get("commandBar.copyHint"),
            Icon = result.Failed ? "Warning" : "WindowConsole",
            CountsUsage = false,
            Pinnable = false,
            Nameable = false,
            Run = result.Failed ? null : _ => Catalog.CopyAsync(result.Output),
        };
    }

    private void ScheduleScript(string query)
    {
        if (NamedScript(query) is not { } named)
        {
            _scriptRun?.Cancel();
            _scriptPending = null;
            return;
        }

        var key = (named.Link.Id, named.Argument);
        if (_scriptCache.ContainsKey(key) || _scriptPending == key)
        {
            return;
        }

        _ = RunScriptAsync(named.Link, named.Argument, ScriptDebounce);
    }

    private async Task RunScriptAsync(CommandBarLink link, string argument, TimeSpan delay)
    {
        _scriptRun?.Cancel();
        var cts = new CancellationTokenSource();
        _scriptRun = cts;
        var key = (link.Id, argument);
        _scriptPending = key;
        var presentation = Session.Presentation;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(true);
            }

            var path = CommandBarLinks.PlaceTarget(link.Destination, _platform.HomeFolder);
            var result = await ScriptRunner.RunAsync(path, argument, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || presentation != Session.Presentation)
            {
                return;
            }

            _scriptCache[key] = result;
            _scriptPending = null;
            Session.Rebuild();
            Refresh();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Return on a script row: copy its answer when it exists, otherwise run it now (the bar stays open).</summary>
    private async Task RunScriptFromRowAsync(CommandBarLink link, string? argument)
    {
        var key = (link.Id, argument ?? string.Empty);
        if (_scriptCache.TryGetValue(key, out var result) && !result.Failed && result.Output.Length > 0)
        {
            Close(FloatingCloseReason.Action);
            await Catalog.CopyAsync(result.Output).ConfigureAwait(true);
            return;
        }

        await RunScriptAsync(link, argument ?? string.Empty, TimeSpan.Zero).ConfigureAwait(true);
    }

    // ── Running ────────────────────────────────────────────────────────

    /// <summary>Return / click / Ctrl+N: the plan for the current mode.</summary>
    public void Submit(bool reveal = false)
    {
        switch (Session.Mode)
        {
            case CommandBarMode.Search:
                if (Session.SelectedRow is { } row)
                {
                    Carry(Session.Plan(row, reveal));
                }
                else if (Session.IsEmptyResult)
                {
                    Session.ShowSuggestions();
                    Refresh();
                }
                else if (Session.Query.Length > 0)
                {
                    SetQuery(string.Empty);
                }

                break;
            case CommandBarMode.Argument:
                Carry(Session.SubmitArgument());
                break;
            case CommandBarMode.Confirm:
                Carry(Session.Confirm());
                break;
            case CommandBarMode.Actions:
                if (Session.SelectedAction is { } action)
                {
                    Carry(Session.RunAction(action));
                }

                break;
            case CommandBarMode.Naming:
                CommitName();
                break;
        }
    }

    /// <summary>Runs the row at index <paramref name="n"/> of the visible rows (Ctrl+1–9).</summary>
    public void RunNth(int n)
    {
        if (Session.Mode != CommandBarMode.Search)
        {
            return;
        }

        var rows = Session.Rows;
        if (n >= 0 && n < rows.Count)
        {
            Session.Select(rows[n]);
            Carry(Session.Plan(rows[n]));
        }
    }

    /// <summary>A click runs that row by identity, even if a reload moved it.</summary>
    public void RunClicked(CommandRow row)
    {
        if (Session.Mode == CommandBarMode.Search && Session.Select(row))
        {
            Carry(Session.Plan(row));
        }
    }

    public void Carry(RunPlan plan)
    {
        switch (plan)
        {
            case RunPlan.Refuse:
                _caret.Foreground.Beep();
                break;
            case RunPlan.Stay:
                QueryChanged();
                break;
            case RunPlan.Reveal reveal:
                Close(FloatingCloseReason.Action);
                _shellService.RevealInExplorer(reveal.Path);
                break;
            case RunPlan.Execute execute:
                _ = ExecuteAsync(execute.Row, execute.Context);
                break;
            case RunPlan.ExecuteAction action:
                _ = ExecuteActionAsync(action.Row, action.Action);
                break;
        }
    }

    private async Task ExecuteAsync(CommandRow row, CommandRunContext context, bool fromBar = true)
    {
        if (row.Run is null)
        {
            _caret.Foreground.Beep();
            return;
        }

        Engine.RecordRun(row, context.Query, fromBar);
        _usageDirty = true;
        var target = _target;
        if (!row.KeepsOpen)
        {
            Close(FloatingCloseReason.Action);
            if (!row.ActsAtCaret && target is not null && RestoresFocus(row))
            {
                _caret.RestoreFocus(target);
            }
        }

        try
        {
            await row.Run(context with { Target = target }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("commandBar", $"Row {row.Source} failed.", ex);
            _hud?.Show(L.Get("quickToggles.actionFailed"), HudStyle.Warning);
        }

        if (row.KeepsOpen && IsOpen)
        {
            Session.Rebuild();
            Refresh();
        }

        SaveUsage();
    }

    /// <summary>Rows that only copy or switch something give the focus back; rows that open a window take it themselves.</summary>
    private static bool RestoresFocus(CommandRow row) => row.Source is CommandSource.Answers or CommandSource.Calculator or CommandSource.Selection
        || row.Id.StartsWith("toggle.", StringComparison.Ordinal) || row.Id.StartsWith("scriptAnswer.", StringComparison.Ordinal);

    private async Task ExecuteActionAsync(CommandRow row, CommandRowAction action)
    {
        var inBar = action.Id.StartsWith("bar.", StringComparison.Ordinal);
        if (!inBar)
        {
            Close(FloatingCloseReason.Action);
        }

        try
        {
            await action.Run(new CommandRunContext { Target = _target, Query = Session.LearningQuery }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("commandBar", $"Row action {action.Id} failed.", ex);
        }

        if (inBar && IsOpen && Session.Mode == CommandBarMode.Actions)
        {
            Session.Escape();
        }

        Refresh();
    }

    private void SaveUsage()
    {
        if (_usageDirty)
        {
            _usageDirty = false;
            _preferences.SaveUsage(Engine.Usage);
        }
    }

    // ── Row actions (Ctrl+K) and personalization ───────────────────────

    public bool OpenActions()
    {
        if (Session.SelectedRow is not { } row)
        {
            return false;
        }

        var opened = Session.OpenActions(row, ActionsFor(row));
        Refresh();
        return opened;
    }

    /// <summary>The Ctrl+K list (spec 06 §3.8.8). Ids starting with "bar." keep the bar open.</summary>
    public IReadOnlyList<CommandRowAction> ActionsFor(CommandRow row)
    {
        var actions = new List<CommandRowAction>(row.Actions);
        if (row.RevealPath is { Length: > 0 } path)
        {
            actions.Add(new CommandRowAction("reveal", L.Get("commandBar.actionRevealInFinder"), "FolderOpen", _ =>
            {
                _shellService.RevealInExplorer(path);
                return Task.CompletedTask;
            }));
        }

        var key = row.StableKey;
        if (row.Pinnable && CommandSources.IsPinnable(row.Source))
        {
            var pinned = _preferences.Pins.Contains(key);
            actions.Add(new CommandRowAction("bar.pin", L.Get(pinned ? "commandBar.actionUnpin" : "commandBar.actionPin"), pinned ? "PinOff" : "Pin", _ =>
            {
                _preferences.TogglePin(key);
                return Task.CompletedTask;
            }));
        }

        if (row.Nameable && CommandSources.IsNameable(row.Source))
        {
            var named = _preferences.Aliases.ContainsKey(key);
            actions.Add(new CommandRowAction("bar.name", L.Get(named ? "commandBar.actionRename" : "commandBar.actionName"), "Rename", _ =>
            {
                Session.BeginNaming(_preferences.Aliases.GetValueOrDefault(key, string.Empty));
                Refresh();
                return Task.CompletedTask;
            }));
            var bound = _preferences.RowShortcuts.ContainsKey(key);
            actions.Add(new CommandRowAction("bar.shortcut", L.Get(bound ? "commandBar.actionShortcutChange" : "commandBar.actionShortcut"), "Keyboard", _ =>
            {
                Session.BeginCapture();
                Refresh();
                return Task.CompletedTask;
            }));
            if (bound)
            {
                actions.Add(new CommandRowAction("bar.shortcutRemove", L.Get("commandBar.actionShortcutRemove"), "Dismiss", _ =>
                {
                    _preferences.RemoveRowShortcut(key);
                    return Task.CompletedTask;
                }));
            }
        }

        actions.Add(new CommandRowAction("bar.hide", L.Get("commandBar.actionHide"), "EyeOff", _ =>
        {
            _preferences.SetHidden(key, true);
            return Task.CompletedTask;
        }));
        if (row.CountsUsage && Engine.Usage.Get(row.Id) is not null)
        {
            actions.Add(new CommandRowAction("bar.forget", L.Get("commandBar.actionForget"), "History", _ =>
            {
                Engine.Forget(row);
                _usageDirty = true;
                SaveUsage();
                return Task.CompletedTask;
            }));
        }

        return actions;
    }

    /// <summary>Ctrl+P: pin or unpin the selected row.</summary>
    public void TogglePinSelected()
    {
        if (Session.SelectedRow is { Pinnable: true } row && CommandSources.IsPinnable(row.Source))
        {
            var pinned = _preferences.TogglePin(row.StableKey);
            _hud?.Show(row.Title, HudStyle.Info, pinned ? "Pin" : "PinOff", TimeSpan.FromSeconds(1));
            Session.Rebuild();
            Refresh();
        }
        else
        {
            _caret.Foreground.Beep();
        }
    }

    private void CommitName()
    {
        if (Session.ModeRow is not { } row)
        {
            return;
        }

        if (_preferences.SetAlias(row.StableKey, Session.Query) is { } other)
        {
            var otherTitle = Engine.Catalog.Concat(Engine.Apps).Concat(Engine.OsPages).FirstOrDefault(r => r.StableKey == other)?.Title ?? other;
            Session.Message = L.Format("commandBar.aliasTakenFormat", otherTitle);
            Refresh();
            return;
        }

        Session.FinishPersonalization();
        Refresh();
    }

    /// <summary>Capture mode: Delete clears the binding; anything else is validated and saved.</summary>
    public void CaptureShortcut(KeyChord chord, bool clear)
    {
        if (Session.Mode != CommandBarMode.CapturingShortcut || Session.ModeRow is not { } row)
        {
            return;
        }

        if (clear)
        {
            _preferences.RemoveRowShortcut(row.StableKey);
            Session.FinishPersonalization();
            Refresh();
            return;
        }

        if (TrySetRowShortcut(row.StableKey, chord) is { } problem)
        {
            Session.Message = problem;
            Refresh();
            return;
        }

        Session.FinishPersonalization();
        Refresh();
    }

    /// <summary>
    /// Validates and saves a row shortcut (a modifier is required, reserved and
    /// feature combinations are refused, at most 64). Returns the problem, or null.
    /// </summary>
    public string? TrySetRowShortcut(string stableKey, KeyChord chord)
    {
        if (!chord.IsValidGlobalShortcut || ReservedShortcuts.IsReserved(chord))
        {
            return L.Get(chord.IsValidGlobalShortcut ? "win.shell.shortcutReserved" : "win.shell.shortcutInvalid");
        }

        if (_shortcuts.FindConflict(chord, null, includeInactive: false) is { } role)
        {
            return L.Format("win.shell.shortcutUsedByFormat", L.Get(role.TitleKey));
        }

        return _preferences.SetRowShortcut(stableKey, chord) switch
        {
            RowShortcutRefusal.NeedsModifier => L.Get("win.shell.shortcutInvalid"),
            RowShortcutRefusal.TooMany => L.Format("commandBar.rowShortcutsLimitFormat", CommandBarSettings.MaxRowShortcuts),
            _ => null,
        };
    }

    // ── Row shortcuts ──────────────────────────────────────────────────

    /// <summary>Row shortcut keys that Windows refused (Settings lists them).</summary>
    public HashSet<string> RefusedRowShortcuts { get; } = new(StringComparer.Ordinal);

    private void RegisterRowShortcuts()
    {
        foreach (var handle in _rowShortcutHandles)
        {
            handle.Dispose();
        }

        _rowShortcutHandles.Clear();
        RefusedRowShortcuts.Clear();
        if (!_available)
        {
            return;
        }

        foreach (var (key, chord) in _preferences.RowShortcuts)
        {
            var stableKey = key;
            var handle = _hotkeys.Register(chord, () => _ = RunRowShortcutAsync(stableKey));
            if (handle is null)
            {
                RefusedRowShortcuts.Add(key);
                Log.Warn("commandBar", "Windows refused a row shortcut.");
            }
            else
            {
                _rowShortcutHandles.Add(handle);
            }
        }
    }

    /// <summary>A row shortcut: rebuild that row fresh, then run it (or open the bar when it needs a prompt).</summary>
    private async Task RunRowShortcutAsync(string stableKey)
    {
        if (_shortcuts.IsSuspended)
        {
            return; // A shortcut recorder is listening.
        }

        var chord = _preferences.RowShortcuts.GetValueOrDefault(stableKey);
        var row = Catalog.BuildCatalog().FirstOrDefault(r => r.StableKey == stableKey)
                  ?? Catalog.WindowsSettingsRows().FirstOrDefault(r => r.StableKey == stableKey);
        if (row is null && stableKey.StartsWith("app.", StringComparison.Ordinal))
        {
            _apps ??= await Task.Run(() => _platform.GetAppsAsync(CancellationToken.None)).ConfigureAwait(true);
            if (_preferences.RowShortcuts.GetValueOrDefault(stableKey) != chord)
            {
                return; // The binding changed while the apps loaded.
            }

            _windows = _platform.GetWindows();
            row = Catalog.AppRows(_apps, _windows).FirstOrDefault(r => r.StableKey == stableKey);
        }

        if (row is null)
        {
            _caret.Foreground.Beep();
            return;
        }

        // A script that runs straight from its shortcut: silently, beep on failure (not while hidden or Links is off).
        if (row.IsScript && _settings.Get(CommandBarSettings.Links).FirstOrDefault(l => "link." + l.Id.ToString("D") == row.Id) is { RunsDirectly: true } script)
        {
            if (_preferences.Hidden.Contains(stableKey) || !_preferences.IsSourceEnabled(CommandSource.Links))
            {
                return;
            }

            var result = await ScriptRunner.RunAsync(CommandBarLinks.PlaceTarget(script.Destination, _platform.HomeFolder), string.Empty, CancellationToken.None).ConfigureAwait(true);
            if (result.Failed || result.ExitCode != 0)
            {
                _caret.Foreground.Beep();
            }

            return;
        }

        var needsPrompt = row.ConfirmPrompt is not null || row.Range is { Required: true } || (row.NameForArgument is not null && !row.RunsBare);
        if (needsPrompt)
        {
            await OpenAsync().ConfigureAwait(true);
            if (IsOpen)
            {
                Carry(Session.Plan(row));
                Refresh();
            }

            return;
        }

        if (IsOpen)
        {
            Close(FloatingCloseReason.Action); // keeps the target captured when the bar opened
        }
        else
        {
            _target = _caret.CaptureTarget();
        }

        await ExecuteAsync(row, new CommandRunContext(), fromBar: false).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _settingsSubscription.Dispose();
        foreach (var handle in _rowShortcutHandles)
        {
            handle.Dispose();
        }

        _rowShortcutHandles.Clear();
        SaveUsage();
    }
}
