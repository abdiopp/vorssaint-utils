// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;

namespace Rivet.Core.Launcher;

/// <summary>The category chips, in their fixed order (spec 06 §3.8.5).</summary>
public enum CommandCategory
{
    Actions,
    Apps,
    Clipboard,
    Windows,
    SettingsPages,
    MacSettings,
    Snippets,
    Emoji,
    Folders,
    Links,
}

/// <summary>One line of the list: a section heading or a row.</summary>
public sealed record CommandListItem(string? Header, CommandRow? Row)
{
    public static CommandListItem Heading(string text) => new(text, null);

    public static CommandListItem For(CommandRow row) => new(null, row);
}

/// <summary>
/// Builds what the Command Bar lists (spec 06 §3.8.5–§3.8.6): the home list
/// (pinned, suggestions, catalog groups, recent clipboard), category browsing
/// and the typed search pipeline. The view model feeds it rows as background
/// loads land; nothing typed is persisted (only run counts).
/// </summary>
public sealed class CommandBarEngine
{
    public const int HomeSuggestionSlots = 7;
    public const int GroupLimit = 12;
    public const int CategoryLimit = 40;
    public const int EmojiLimit = 40;

    private readonly CommandBarPreferences _preferences;

    public CommandBarEngine(CommandBarPreferences preferences, CommandBarUsage usage)
    {
        _preferences = preferences;
        Usage = usage;
    }

    public CommandBarUsage Usage { get; }

    public CommandBarQueryMemory Memory { get; } = new();

    public CommandBarQueryHabits Habits { get; } = new();

    /// <summary>Actions, feature toggles, settings pages, snippets, saved links, folders, answers.</summary>
    public IReadOnlyList<CommandRow> Catalog { get; set; } = [];

    public IReadOnlyList<CommandRow> Apps { get; set; } = [];

    public IReadOnlyList<CommandRow> OsPages { get; set; } = [];

    public IReadOnlyList<CommandRow> Windows { get; set; } = [];

    public IReadOnlyList<CommandRow> QuitRows { get; set; } = [];

    /// <summary>Rows from providers other modules registered (shown after ranking like actions).</summary>
    public IReadOnlyList<CommandRow> ExternalRows { get; set; } = [];

    /// <summary>Rows acting on the text selected when the bar opened (§3.8.7 P15).</summary>
    public IReadOnlyList<CommandRow> SelectionRows { get; set; } = [];

    /// <summary>The selected text the selection rows act on (heading preview).</summary>
    public string? SelectionText { get; set; }

    /// <summary>(query, limit) → clipboard rows; an empty query gives the most recent ones.</summary>
    public Func<string, int, IReadOnlyList<CommandRow>>? ClipboardRows { get; set; }

    /// <summary>Cached file results for this exact query (the view model schedules the search).</summary>
    public Func<string, IReadOnlyList<CommandRow>>? FileRows { get; set; }

    /// <summary>A saved script's cached answer for this query, if any.</summary>
    public Func<string, CommandRow?>? ScriptAnswer { get; set; }

    /// <summary>The script row the query names (longest name), so its siblings leave the pool.</summary>
    public Func<string, string?>? NamedScriptId { get; set; }

    public Func<string, CommandRow?>? Answer { get; set; }

    public Func<string, CommandRow?>? TypedUrl { get; set; }

    public Func<EmojiEntry, CommandRow>? EmojiRow { get; set; }

    /// <summary>Feature ids whose main action fills the suggestions when nothing was used yet.</summary>
    public IReadOnlyList<string> CuratedFeatures { get; set; } = [];

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    private RankingContext Context(bool habitsOnly = false) => new()
    {
        Pins = _preferences.Pins.ToHashSet(StringComparer.Ordinal),
        Aliases = _preferences.Aliases,
        Usage = Usage,
        Memory = Memory,
        Habits = Habits,
        Now = Now(),
        HabitsOnly = habitsOnly,
    };

    private bool Offerable(CommandRow row, HashSet<string> hidden) =>
        !hidden.Contains(row.StableKey) && _preferences.IsSourceEnabled(row.Source);

    // ── Home ───────────────────────────────────────────────────────────

    public List<CommandListItem> Home()
    {
        var hidden = _preferences.Hidden;
        var items = new List<CommandListItem>();
        if (SelectionText is { Length: > 0 } selected && _preferences.IsSourceEnabled(CommandSource.Selection))
        {
            var rows = SelectionRows.Where(r => Offerable(r, hidden)).ToList();
            if (rows.Count > 0)
            {
                var preview = selected.Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (preview.Length > 44)
                {
                    preview = preview[..44] + "…";
                }

                items.Add(CommandListItem.Heading(L.Get("commandBar.selectedTitle") + " · " + preview));
                items.AddRange(rows.Select(CommandListItem.For));
            }
        }

        var offerable = Catalog.Concat(ExternalRows).Concat(Apps).Concat(OsPages).Concat(Windows)
            .Where(r => Offerable(r, hidden))
            .GroupBy(r => r.Id).Select(g => g.First())
            .ToList();
        var byKey = offerable.GroupBy(r => r.StableKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);

        var pins = _preferences.Pins.Select(p => byKey.GetValueOrDefault(p)).OfType<CommandRow>().ToList();
        if (pins.Count > 0)
        {
            items.Add(CommandListItem.Heading(L.Get("commandBar.pinnedTitle")));
            foreach (var row in pins)
            {
                items.Add(CommandListItem.For(row));
                shown.Add(row.Id);
            }
        }

        var slots = Math.Max(0, HomeSuggestionSlots - pins.Count);
        var suggestions = new List<CommandRow>();
        var byId = offerable.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var id in Usage.MostUsed())
        {
            if (suggestions.Count >= slots)
            {
                break;
            }

            if (byId.TryGetValue(id, out var row) && row.CountsUsage && shown.Add(row.Id))
            {
                suggestions.Add(row);
            }
        }

        foreach (var feature in CuratedFeatures)
        {
            if (suggestions.Count >= slots)
            {
                break;
            }

            var row = Catalog.FirstOrDefault(r => r.FeatureId == feature && r.Role == 0 && r.Source == CommandSource.Actions && Offerable(r, hidden) && !shown.Contains(r.Id));
            if (row is not null && shown.Add(row.Id))
            {
                suggestions.Add(row);
            }
        }

        if (suggestions.Count > 0)
        {
            items.Add(CommandListItem.Heading(L.Get("commandBar.suggestionsLabel")));
            items.AddRange(suggestions.Select(CommandListItem.For));
        }

        // Every remaining catalog row, grouped under its area heading.
        var groups = new List<(string Heading, List<CommandRow> Rows)>();
        foreach (var row in Catalog.Concat(ExternalRows).Where(r => Offerable(r, hidden) && !shown.Contains(r.Id)))
        {
            var heading = HeadingOf(row);
            var group = groups.FirstOrDefault(g => g.Heading == heading);
            if (group.Rows is null)
            {
                group = (heading, []);
                groups.Add(group);
            }

            if (group.Rows.Count < GroupLimit)
            {
                group.Rows.Add(row);
            }
        }

        foreach (var (heading, rows) in groups)
        {
            items.Add(CommandListItem.Heading(heading));
            items.AddRange(rows.Select(CommandListItem.For));
        }

        if (_preferences.IsSourceEnabled(CommandSource.Clipboard) && ClipboardRows?.Invoke(string.Empty, 6) is { Count: > 0 } clips)
        {
            items.Add(CommandListItem.Heading(L.Get("commandBar.kindClipboard")));
            items.AddRange(clips.Select(CommandListItem.For));
        }

        return items;
    }

    /// <summary>The home heading of a catalog row.</summary>
    public static string HeadingOf(CommandRow row) => row.Source switch
    {
        CommandSource.Answers => L.Get("commandBar.kindAnswer"),
        CommandSource.Links => L.Get("commandBar.kindLink"),
        CommandSource.Snippets => L.Get("commandBar.kindSnippet"),
        CommandSource.Folders => L.Get("commandBar.kindFolder"),
        _ => string.IsNullOrEmpty(row.Section ?? row.Subtitle) ? L.Get("commandBar.everythingTitle") : (row.Section ?? row.Subtitle)!,
    };

    // ── Categories ─────────────────────────────────────────────────────

    public IReadOnlyList<CommandRow> RowsOf(CommandCategory category)
    {
        var hidden = _preferences.Hidden;
        IEnumerable<CommandRow> rows = category switch
        {
            CommandCategory.Actions => Catalog.Concat(ExternalRows).Where(r => r.Source == CommandSource.Actions),
            CommandCategory.Apps => Apps,
            CommandCategory.Clipboard => ClipboardRows?.Invoke(string.Empty, 60) ?? [],
            CommandCategory.Windows => Windows,
            CommandCategory.SettingsPages => Catalog.Where(r => r.Source == CommandSource.SettingsPages),
            CommandCategory.MacSettings => OsPages,
            CommandCategory.Snippets => Catalog.Where(r => r.Source == CommandSource.Snippets),
            CommandCategory.Emoji => EmojiRows(),
            CommandCategory.Folders => Catalog.Where(r => r.Source == CommandSource.Folders),
            _ => Catalog.Where(r => r.Source == CommandSource.Links),
        };
        return rows.Where(r => Offerable(r, hidden)).ToList();
    }

    public static CommandSource SourceOf(CommandCategory category) => category switch
    {
        CommandCategory.Actions => CommandSource.Actions,
        CommandCategory.Apps => CommandSource.Apps,
        CommandCategory.Clipboard => CommandSource.Clipboard,
        CommandCategory.Windows => CommandSource.Windows,
        CommandCategory.SettingsPages => CommandSource.SettingsPages,
        CommandCategory.MacSettings => CommandSource.MacSettings,
        CommandCategory.Snippets => CommandSource.Snippets,
        CommandCategory.Emoji => CommandSource.Emoji,
        CommandCategory.Folders => CommandSource.Folders,
        _ => CommandSource.Links,
    };

    /// <summary>Chips that have content and whose source is on (Apps and Windows Settings: always).</summary>
    public IReadOnlyList<CommandCategory> AvailableCategories() =>
        Enum.GetValues<CommandCategory>()
            .Where(c => _preferences.IsSourceEnabled(SourceOf(c)))
            .Where(c => c is CommandCategory.Apps or CommandCategory.MacSettings or CommandCategory.Emoji || RowsOf(c).Count > 0)
            .ToList();

    /// <summary>A category: used rows first (count, recency), unused keep catalog order; typing filters only it.</summary>
    public List<CommandRow> Category(CommandCategory category, string query)
    {
        if (query.Trim().Length > 0)
        {
            if (category == CommandCategory.Clipboard)
            {
                return ClipboardRows?.Invoke(query, CategoryLimit).ToList() ?? [];
            }

            return CommandBarSearch.Rank(RowsOf(category), query, Context(habitsOnly: true))
                .Select(r => r.Row)
                .Where(r => r.Source != CommandSource.Answers || category == CommandCategory.Actions)
                .Take(CategoryLimit)
                .ToList();
        }

        var rows = RowsOf(category);
        if (category is CommandCategory.Clipboard or CommandCategory.Emoji)
        {
            return rows.ToList();
        }

        var used = rows.Where(r => Usage.Get(r.Id) is not null)
            .OrderByDescending(r => Usage.Get(r.Id)!.Count)
            .ThenByDescending(r => Usage.Get(r.Id)!.LastUsedUnix);
        return used.Concat(rows.Where(r => Usage.Get(r.Id) is null)).ToList();
    }

    public static string CategoryTitle(CommandCategory category) => category switch
    {
        CommandCategory.Actions => L.Get("hub.groupTools"),
        CommandCategory.Apps => L.Get("commandBar.sourceApps"),
        CommandCategory.Clipboard => L.Get("commandBar.sourceClipboard"),
        CommandCategory.Windows => L.Get("commandBar.sourceWindows"),
        CommandCategory.SettingsPages => L.Get("commandBar.sourceSettingsPages"),
        CommandCategory.MacSettings => L.Get("commandBar.sourceMacSettings"),
        CommandCategory.Snippets => L.Get("commandBar.sourceSnippets"),
        CommandCategory.Emoji => L.Get("commandBar.sourceEmoji"),
        CommandCategory.Folders => L.Get("commandBar.sourceFolders"),
        _ => L.Get("commandBar.linksTitle"),
    };

    // ── Typed search ───────────────────────────────────────────────────

    public List<CommandRow> Search(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        if (trimmed.StartsWith(':'))
        {
            return SearchEmoji(trimmed[1..].Trim());
        }

        var hidden = _preferences.Hidden;
        var sums = _preferences.IsSourceEnabled(CommandSource.Calculator);
        var answer = sums ? Answer?.Invoke(trimmed) : null;
        var colorPreview = answer?.Id == "color.preview" ? answer : null;
        if (colorPreview is not null)
        {
            answer = null;
        }

        var typedUrl = TypedUrl?.Invoke(trimmed);
        var linksOn = _preferences.IsSourceEnabled(CommandSource.Links);
        var scriptAnswer = linksOn ? ScriptAnswer?.Invoke(trimmed) : null;
        var namedScript = linksOn ? NamedScriptId?.Invoke(trimmed) : null;

        // "brightness 40": rank with the words when a numeric row matches them.
        var (text, number) = CommandBarSearch.SplitTrailingNumber(trimmed);
        var effective = trimmed;
        if (number is not null && CommandBarSearch.Rank(Catalog.Where(r => r.Range is not null).ToList(), text, RankingContext.Empty).Count > 0)
        {
            effective = text;
        }

        var pool = new List<CommandRow>(Catalog.Count + Apps.Count + OsPages.Count + Windows.Count + 16);
        pool.AddRange(SelectionRows);
        pool.AddRange(Catalog);
        pool.AddRange(ExternalRows);
        pool.AddRange(Apps);
        pool.AddRange(OsPages);
        pool.AddRange(Windows);
        if (MentionsQuit(trimmed))
        {
            pool.AddRange(QuitRows);
        }

        if (_preferences.IsSourceEnabled(CommandSource.Clipboard) && ClipboardRows is not null)
        {
            pool.AddRange(ClipboardRows(trimmed, 4));
        }

        if (_preferences.IsSourceEnabled(CommandSource.Files) && FileRows is not null)
        {
            pool.AddRange(FileRows(trimmed));
        }

        pool.RemoveAll(r => !Offerable(r, hidden));
        if (linksOn)
        {
            // Only the longest-named script the query names stays eligible, and its answer stands in for it.
            pool.RemoveAll(r => r.IsScript && r.NameForArgument is { } name
                                && (CommandBarLinks.TrailingArgument(trimmed, name) is not null || CommandBarLinks.NamesExactly(trimmed, name))
                                && (r.Id != namedScript || scriptAnswer is not null));
        }

        var ranked = CommandBarSearch.Rank(pool, effective, Context()).Select(r => r.Row);
        return CommandBarSearch.Assemble(answer, typedUrl, scriptAnswer, ranked, effective, colorPreview);
    }

    private List<CommandRow> SearchEmoji(string rest)
    {
        if (!_preferences.IsSourceEnabled(CommandSource.Emoji))
        {
            return [];
        }

        var rows = EmojiRows();
        if (rest.Length == 0)
        {
            return rows.Take(EmojiLimit).ToList();
        }

        return CommandBarSearch.Rank(rows, rest, Context(habitsOnly: true)).Select(r => r.Row).Take(EmojiLimit).ToList();
    }

    private IReadOnlyList<CommandRow>? _emojiRows;

    private IReadOnlyList<CommandRow> EmojiRows()
    {
        if (EmojiRow is null)
        {
            return [];
        }

        return _emojiRows ??= CommandBarEmoji.All.Select(EmojiRow).ToList();
    }

    /// <summary>Drops the cached emoji rows (the skin tone changed).</summary>
    public void InvalidateEmoji() => _emojiRows = null;

    /// <summary>
    /// Quit rows join a typed search only when a token of 2+ characters
    /// prefixes (or is prefixed by) a word of the localized "Quit %@" verb.
    /// </summary>
    public static bool MentionsQuit(string query)
    {
        var verb = TextFold.ForCommand(L.Get("commandBar.quitFormat").Replace("%@", string.Empty, StringComparison.Ordinal));
        var verbWords = verb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (verbWords.Length == 0)
        {
            return false;
        }

        foreach (var token in TextFold.ForCommand(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 2))
        {
            if (verbWords.Any(w => w.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(w, StringComparison.Ordinal)))
            {
                return true;
            }

            // CJK verbs have no spaces: containment.
            if (verbWords.Any(w => w.Any(c => c > 0x2E80) && (token.Contains(w, StringComparison.Ordinal) || w.Contains(token, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    // ── Learning ───────────────────────────────────────────────────────

    /// <summary>Records a run: usage (persisted) and, when typed in the bar, the session-only query memory and habits.</summary>
    public void RecordRun(CommandRow row, string learningQuery, bool fromBar)
    {
        if (!row.CountsUsage)
        {
            return;
        }

        var now = Now();
        Usage.Record(row.Id, now);
        if (fromBar && learningQuery.Trim().Length > 0)
        {
            Memory.Record(learningQuery, row.Id);
            Habits.Record(learningQuery, row.Id, now);
        }
    }

    /// <summary>"Forget how often I use this".</summary>
    public void Forget(CommandRow row)
    {
        Usage.Forget(row.Id);
        Memory.Forget(row.Id);
        Habits.Forget(row.Id);
    }

    /// <summary>"Forget what I use most".</summary>
    public void ForgetAll()
    {
        Usage.Clear();
        Memory.Clear();
        Habits.Clear();
    }
}
