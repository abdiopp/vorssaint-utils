// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Clipboard;
using Rivet.Core.Localization;

namespace Rivet.Core.Launcher;

/// <summary>The Command Bar's modes (spec 06 §3.8.3).</summary>
public enum CommandBarMode
{
    Search,

    /// <summary>Waiting for the number a row needs ("0 to 100").</summary>
    Argument,

    /// <summary>A destructive row asks first; any other key cancels.</summary>
    Confirm,

    /// <summary>The Ctrl+K list of the selected row.</summary>
    Actions,

    /// <summary>Typing the name the row answers to.</summary>
    Naming,

    /// <summary>Recording a row shortcut.</summary>
    CapturingShortcut,
}

/// <summary>What Esc did (spec 06 §3.8.4).</summary>
public enum EscapeOutcome
{
    ClearedQuery,
    LeftCategory,
    Collapsed,
    SteppedBack,
    Close,
}

/// <summary>What the window should do after Return, a click or Ctrl+1–9.</summary>
public abstract record RunPlan
{
    /// <summary>Nothing to run: the bar changed state (a mode, the field) and stays open.</summary>
    public sealed record Stay : RunPlan;

    /// <summary>The input was refused (an invalid number, no row).</summary>
    public sealed record Refuse : RunPlan;

    /// <summary>Run the row; close the bar first unless it keeps it open.</summary>
    public sealed record Execute(CommandRow Row, CommandRunContext Context) : RunPlan;

    /// <summary>Run one of the row's extra actions.</summary>
    public sealed record ExecuteAction(CommandRow Row, CommandRowAction Action) : RunPlan;

    /// <summary>"Show in File Explorer" (Ctrl+Enter on a real file, folder or app).</summary>
    public sealed record Reveal(CommandRow Row, string Path) : RunPlan;
}

/// <summary>
/// One Command Bar opening, without any UI (spec 06 §3.8.1–§3.8.6): the
/// field, the category, the list and its selection, the modes and the Esc
/// ladder. The window renders it and carries out the plans it returns. A new
/// <see cref="Presentation"/> starts at every opening so results of an older
/// one are dropped.
/// </summary>
public sealed class CommandBarSession
{
    public const int MaxListedRows = 16;

    private readonly CommandBarEngine _engine;
    private readonly CommandBarPreferences _preferences;
    private string? _learningQuery;
    private CommandRowAction? _pendingAction;

    public CommandBarSession(CommandBarEngine engine, CommandBarPreferences preferences)
    {
        _engine = engine;
        _preferences = preferences;
    }

    public CommandBarEngine Engine => _engine;

    public int Presentation { get; private set; }

    public string Query { get; private set; } = string.Empty;

    public CommandCategory? Category { get; private set; }

    public CommandBarMode Mode { get; private set; }

    /// <summary>The row an argument, confirmation, action list, name or shortcut is for.</summary>
    public CommandRow? ModeRow { get; private set; }

    /// <summary>The query to restore when a mode steps back.</summary>
    public string SavedQuery { get; private set; } = string.Empty;

    public bool Compact { get; private set; }

    /// <summary>Compact mode: the list was opened with ↓ for this opening.</summary>
    public bool Peeked { get; private set; }

    public IReadOnlyList<CommandListItem> Items { get; private set; } = [];

    /// <summary>Index into <see cref="Items"/> of the selected row (or into <see cref="Actions"/> in actions mode); −1 when none.</summary>
    public int Selected { get; private set; } = -1;

    public IReadOnlyList<CommandRowAction> Actions { get; private set; } = [];

    /// <summary>A short message under the field (a refused name, a refused shortcut).</summary>
    public string? Message { get; set; }

    /// <summary>Rows appended to typed results by other modules' providers (already ranked by them).</summary>
    public Func<string, IReadOnlyList<CommandRow>>? ExtraRows { get; set; }

    /// <summary>The confirmation prompt shown in confirm mode.</summary>
    public string? ConfirmPrompt => Mode == CommandBarMode.Confirm ? _pendingAction?.ConfirmPrompt ?? ModeRow?.ConfirmPrompt : null;

    /// <summary>Whether the list area shows (compact home hides it until ↓).</summary>
    public bool ShowsList => Mode != CommandBarMode.Search || !Compact || Peeked || Query.Trim().Length > 0 || Category is not null;

    public bool IsHome => Mode == CommandBarMode.Search && Query.Trim().Length == 0 && Category is null;

    /// <summary>The query learning remembers (the text before a Tab completion).</summary>
    public string LearningQuery => _learningQuery ?? Query;

    public CommandRow? SelectedRow => Mode == CommandBarMode.Search && Selected >= 0 && Selected < Items.Count ? Items[Selected].Row : null;

    public CommandRowAction? SelectedAction => Mode == CommandBarMode.Actions && Selected >= 0 && Selected < Actions.Count ? Actions[Selected] : null;

    /// <summary>The rows of the list in order (headings skipped).</summary>
    public IReadOnlyList<CommandRow> Rows => Items.Where(i => i.Row is not null).Select(i => i.Row!).ToList();

    /// <summary>Chips: "All" (null) then the categories with content.</summary>
    public IReadOnlyList<CommandCategory?> Chips =>
        [null, .. _engine.AvailableCategories().Cast<CommandCategory?>()];

    /// <summary>Chips show on the browse list and inside a category.</summary>
    public bool ShowsChips => Mode == CommandBarMode.Search && Query.Trim().Length == 0 && ShowsList;

    /// <summary>Starts a new opening.</summary>
    public void Begin(bool compact)
    {
        Presentation++;
        Compact = compact;
        Peeked = false;
        Query = string.Empty;
        SavedQuery = string.Empty;
        _learningQuery = null;
        Category = null;
        Mode = CommandBarMode.Search;
        ModeRow = null;
        _pendingAction = null;
        Actions = [];
        Message = null;
        Rebuild(selectFirst: true);
    }

    /// <summary>The field changed. Typing cancels a pending confirmation.</summary>
    public void SetQuery(string text, bool fromCompletion = false)
    {
        if (text == Query && Mode == CommandBarMode.Search)
        {
            return;
        }

        if (Mode == CommandBarMode.Confirm)
        {
            Mode = CommandBarMode.Search;
            ModeRow = null;
            _pendingAction = null;
        }

        if (Mode is CommandBarMode.Argument or CommandBarMode.Naming)
        {
            Query = text;
            Message = null;
            return;
        }

        if (!fromCompletion)
        {
            _learningQuery = null;
        }

        Query = text;
        Message = null;
        Rebuild(selectFirst: true);
    }

    /// <summary>Rebuilds the list. A changed query selects row 0; a background reload keeps the row by id.</summary>
    public void Rebuild(bool selectFirst = false)
    {
        if (Mode != CommandBarMode.Search)
        {
            return;
        }

        var previous = SelectedRow?.Id;
        Items = BuildItems();
        var rowIndexes = Enumerable.Range(0, Items.Count).Where(i => Items[i].Row is not null).ToList();
        if (rowIndexes.Count == 0)
        {
            Selected = -1;
            return;
        }

        if (!selectFirst && previous is not null)
        {
            var kept = rowIndexes.FirstOrDefault(i => Items[i].Row!.Id == previous, -1);
            if (kept >= 0)
            {
                Selected = kept;
                return;
            }
        }

        Selected = rowIndexes[0];
    }

    private List<CommandListItem> BuildItems()
    {
        if (!ShowsList)
        {
            return [];
        }

        var trimmed = Query.Trim();
        if (Category is { } category)
        {
            var rows = _engine.Category(category, Query);
            var items = new List<CommandListItem> { CommandListItem.Heading($"{CommandBarEngine.CategoryTitle(category)} · {rows.Count}") };
            items.AddRange(rows.Select(CommandListItem.For));
            return items;
        }

        if (trimmed.Length == 0)
        {
            return _engine.Home();
        }

        var results = _engine.Search(Query);
        if (ExtraRows?.Invoke(Query) is { Count: > 0 } extra && !trimmed.StartsWith(':'))
        {
            var ids = results.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var hidden = _preferences.Hidden;
            results.AddRange(extra.Where(r => ids.Add(r.Id) && !hidden.Contains(r.StableKey)));
            if (results.Count > MaxListedRows)
            {
                results.RemoveRange(MaxListedRows, results.Count - MaxListedRows);
            }
        }

        return results.Select(CommandListItem.For).ToList();
    }

    /// <summary>↑/↓ (wraps): rows in search mode, actions in actions mode.</summary>
    public void Move(int delta)
    {
        if (Mode == CommandBarMode.Actions)
        {
            if (Actions.Count > 0)
            {
                Selected = ((Selected + delta) % Actions.Count + Actions.Count) % Actions.Count;
            }

            return;
        }

        if (Mode != CommandBarMode.Search)
        {
            return;
        }

        if (delta > 0 && Peek())
        {
            return;
        }

        var rowIndexes = Enumerable.Range(0, Items.Count).Where(i => Items[i].Row is not null).ToList();
        if (rowIndexes.Count == 0)
        {
            return;
        }

        var position = rowIndexes.IndexOf(Selected);
        position = position < 0 ? 0 : ((position + delta) % rowIndexes.Count + rowIndexes.Count) % rowIndexes.Count;
        Selected = rowIndexes[position];
    }

    /// <summary>Selects a row by identity (hover, click).</summary>
    public bool Select(CommandRow row)
    {
        var index = Items.ToList().FindIndex(i => i.Row is not null && ReferenceEquals(i.Row, row));
        if (index < 0)
        {
            index = Items.ToList().FindIndex(i => i.Row?.Id == row.Id);
        }

        if (index < 0)
        {
            return false;
        }

        Selected = index;
        return true;
    }

    public void SelectAction(int index)
    {
        if (Mode == CommandBarMode.Actions && index >= 0 && index < Actions.Count)
        {
            Selected = index;
        }
    }

    /// <summary>Compact home: ↓ shows the list for this opening.</summary>
    public bool Peek()
    {
        if (Compact && !Peeked && IsHome)
        {
            Peeked = true;
            Rebuild(selectFirst: true);
            return true;
        }

        return false;
    }

    public void SelectCategory(CommandCategory? category)
    {
        Category = category;
        if (category is not null)
        {
            Peeked = true;
        }

        Rebuild(selectFirst: true);
    }

    /// <summary>←/→ with an empty field: walk the chips ("All" first, wraps).</summary>
    public bool WalkCategory(int delta)
    {
        if (Mode != CommandBarMode.Search || Query.Length > 0 || !ShowsChips)
        {
            return false;
        }

        var chips = Chips;
        var index = chips.ToList().IndexOf(Category);
        index = ((index + delta) % chips.Count + chips.Count) % chips.Count;
        SelectCategory(chips[index]);
        return true;
    }

    /// <summary>The Esc ladder.</summary>
    public EscapeOutcome Escape()
    {
        switch (Mode)
        {
            case CommandBarMode.Argument:
            case CommandBarMode.Actions:
                BackToSearch(restoreQuery: true);
                return EscapeOutcome.SteppedBack;
            case CommandBarMode.Confirm:
                BackToSearch(restoreQuery: false);
                return EscapeOutcome.SteppedBack;
            case CommandBarMode.Naming:
            case CommandBarMode.CapturingShortcut:
                Mode = CommandBarMode.Actions;
                Message = null;
                Query = SavedQuery;
                Selected = Actions.Count > 0 ? 0 : -1;
                return EscapeOutcome.SteppedBack;
        }

        if (Query.Length > 0)
        {
            SetQuery(string.Empty);
            return EscapeOutcome.ClearedQuery;
        }

        if (Category is not null)
        {
            SelectCategory(null);
            return EscapeOutcome.LeftCategory;
        }

        if (Compact && Peeked)
        {
            Peeked = false;
            Rebuild(selectFirst: true);
            return EscapeOutcome.Collapsed;
        }

        return EscapeOutcome.Close;
    }

    private void BackToSearch(bool restoreQuery)
    {
        Mode = CommandBarMode.Search;
        ModeRow = null;
        _pendingAction = null;
        Actions = [];
        Message = null;
        if (restoreQuery)
        {
            Query = SavedQuery;
        }

        Rebuild();
    }

    /// <summary>
    /// Tab: the calculator answer becomes a reusable number; other rows put
    /// their title in the field (emoji: their name, keeping ":"). Learning
    /// still remembers the text typed before the completion.
    /// </summary>
    public bool Complete()
    {
        if (Mode != CommandBarMode.Search || SelectedRow is not { } row || row.Source == CommandSource.Answers)
        {
            return false;
        }

        if (row.Source == CommandSource.Calculator && row.Id != "math.result")
        {
            return false;
        }

        var text = row.CompletionText ?? row.Title;
        if (row.Source == CommandSource.Emoji && Query.TrimStart().StartsWith(':'))
        {
            text = ":" + text;
        }

        _learningQuery ??= Query;
        SetQuery(text, fromCompletion: true);
        return true;
    }

    /// <summary>Return / click / Ctrl+N on a row in search mode.</summary>
    public RunPlan Plan(CommandRow row, bool reveal = false)
    {
        if (reveal && row.RevealPath is { Length: > 0 } path)
        {
            return new RunPlan.Reveal(row, path);
        }

        // Saved searches and scripts wait for what is typed after their name.
        if (row.NameForArgument is { } name)
        {
            var argument = CommandBarLinks.TrailingArgument(Query, name);
            if (argument is null && !row.RunsBare)
            {
                SetQuery(name + " ");
                return new RunPlan.Stay();
            }

            return Execute(row, new CommandRunContext { Argument = argument, Query = LearningQuery });
        }

        int? number = null;
        if (row.Range is { } range)
        {
            var (_, trailing) = CommandBarSearch.SplitTrailingNumber(Query);
            if (trailing is { } value)
            {
                number = Math.Clamp(value, range.Min, range.Max);
            }
            else if (range.Required)
            {
                SavedQuery = Query;
                Mode = CommandBarMode.Argument;
                ModeRow = row;
                Query = string.Empty;
                return new RunPlan.Stay();
            }
        }

        if (row.ConfirmPrompt is not null)
        {
            Mode = CommandBarMode.Confirm;
            ModeRow = row;
            _pendingAction = null;
            return new RunPlan.Stay();
        }

        return Execute(row, new CommandRunContext { Number = number, Query = LearningQuery });
    }

    private RunPlan Execute(CommandRow row, CommandRunContext context) => new RunPlan.Execute(row, context);

    /// <summary>Return in argument mode: digits (optional %), at most 4, clamped.</summary>
    public RunPlan SubmitArgument()
    {
        if (Mode != CommandBarMode.Argument || ModeRow is not { Range: { } range } row)
        {
            return new RunPlan.Refuse();
        }

        if (CommandBarSearch.ArgumentValue(Query, range) is not { } value)
        {
            return new RunPlan.Refuse();
        }

        var context = new CommandRunContext { Number = value, Query = SavedQuery };
        BackToSearch(restoreQuery: true);
        return Execute(row, context);
    }

    /// <summary>Return (or the Confirm button) in confirm mode.</summary>
    public RunPlan Confirm()
    {
        if (Mode != CommandBarMode.Confirm || ModeRow is not { } row)
        {
            return new RunPlan.Refuse();
        }

        var action = _pendingAction;
        BackToSearch(restoreQuery: false);
        return action is null
            ? Execute(row, new CommandRunContext { Query = LearningQuery })
            : new RunPlan.ExecuteAction(row, action);
    }

    /// <summary>Ctrl+K: the row's actions (built by the caller, personalization included).</summary>
    public bool OpenActions(CommandRow row, IReadOnlyList<CommandRowAction> actions)
    {
        if (Mode != CommandBarMode.Search || actions.Count == 0)
        {
            return false;
        }

        SavedQuery = Query;
        Mode = CommandBarMode.Actions;
        ModeRow = row;
        Actions = actions;
        Selected = 0;
        return true;
    }

    /// <summary>Return on an action: confirm first when it asks.</summary>
    public RunPlan RunAction(CommandRowAction action)
    {
        if (Mode != CommandBarMode.Actions || ModeRow is not { } row)
        {
            return new RunPlan.Refuse();
        }

        if (action.ConfirmPrompt is not null)
        {
            Mode = CommandBarMode.Confirm;
            _pendingAction = action;
            Actions = [];
            Query = SavedQuery;
            return new RunPlan.Stay();
        }

        return new RunPlan.ExecuteAction(row, action);
    }

    /// <summary>"Give it your own name": the field holds the current alias.</summary>
    public void BeginNaming(string currentAlias)
    {
        if (ModeRow is null)
        {
            return;
        }

        Mode = CommandBarMode.Naming;
        Query = currentAlias;
        Message = null;
    }

    /// <summary>"Give it a shortcut": the next key combination is captured.</summary>
    public void BeginCapture()
    {
        if (ModeRow is null)
        {
            return;
        }

        Mode = CommandBarMode.CapturingShortcut;
        Message = null;
    }

    /// <summary>Leaves naming or capture after a successful save, back to search with the saved query.</summary>
    public void FinishPersonalization() => BackToSearch(restoreQuery: true);

    /// <summary>The empty state's "See suggestions": clears the query and the category.</summary>
    public void ShowSuggestions()
    {
        Category = null;
        SetQuery(string.Empty);
        Rebuild(selectFirst: true);
    }

    /// <summary>The placeholder of the field for the current mode.</summary>
    public string Placeholder => Mode switch
    {
        CommandBarMode.Argument when ModeRow?.Range is { } range => L.Format("commandBar.argumentRangeFormat", range.Min, range.Max),
        CommandBarMode.Naming => L.Get("commandBar.aliasPlaceholder"),
        _ => L.Get("commandBar.searchPlaceholder"),
    };

    /// <summary>The heading over an empty typed search ("Nothing here by that name.").</summary>
    public bool IsEmptyResult => Mode == CommandBarMode.Search && ShowsList && !IsHome && Items.All(i => i.Row is null);

    /// <summary>Folded text used to compare queries (exposed for the script and file caches).</summary>
    public static string Fold(string query) => TextFold.ForCommand(query);
}
