// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Launcher;

/// <summary>A numeric argument a row accepts ("brightness 40").</summary>
public sealed record NumericRange(int Min, int Max, bool Required);

/// <summary>What a row's run gets: the typed argument and the app that was in front when the bar opened.</summary>
public sealed record CommandRunContext
{
    /// <summary>A number typed after the row's words, already clamped to its range.</summary>
    public int? Number { get; init; }

    /// <summary>Text typed after a saved search's or script's name.</summary>
    public string? Argument { get; init; }

    /// <summary>Opaque target for rows that type or paste at the caret (the foreground app when the bar opened).</summary>
    public object? Target { get; init; }

    /// <summary>The query that led to the run (for learning; never persisted).</summary>
    public string Query { get; init; } = string.Empty;
}

/// <summary>An extra action of a row (Ctrl+K): quit an app, skin tones, show in File Explorer…</summary>
public sealed record CommandRowAction(string Id, string Title, string Icon, Func<CommandRunContext, Task> Run, string? ConfirmPrompt = null, bool Destructive = false);

/// <summary>
/// One Command Bar result (spec 06 §3.8.7). The id prefix names the source
/// (<c>app.</c>, <c>window.</c>, <c>settings.</c> … anything else is an
/// action); <see cref="StableKey"/> keys pins, names and hidden rows.
/// </summary>
public sealed class CommandRow
{
    private string? _stableKey;

    public required string Id { get; init; }

    public string StableKey
    {
        get => _stableKey ?? Id;
        init => _stableKey = value;
    }

    public required string Title { get; init; }

    /// <summary>The text ranked against instead of the title (emoji rank on their name).</summary>
    public string? MatchTitle { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>Extra search words (folded when ranking).</summary>
    public string Keywords { get; init; } = string.Empty;

    /// <summary>Fluent icon name.</summary>
    public string? Icon { get; init; }

    /// <summary>A file or app whose shell icon to show.</summary>
    public string? IconPath { get; init; }

    /// <summary>A character shown as the row's icon (emoji rows).</summary>
    public string? Glyph { get; init; }

    /// <summary>A thumbnail (clipboard images).</summary>
    public string? ImagePath { get; init; }

    /// <summary>Colour swatch (0xAARRGGBB) for colour rows and clipboard colours.</summary>
    public uint? Swatch { get; init; }

    /// <summary>Green "live" dot: a running app, a feature that is on.</summary>
    public bool IsLive { get; init; }

    /// <summary>The value chip of answer rows ("87%").</summary>
    public string? Value { get; init; }

    /// <summary>What Tab puts in the field (the calculator's reusable number, an emoji's name); defaults to the title.</summary>
    public string? CompletionText { get; init; }

    public string? ShortcutText { get; init; }

    public bool CountsUsage { get; init; } = true;

    public bool Pinnable { get; init; } = true;

    public bool Nameable { get; init; } = true;

    /// <summary>Running the row leaves the bar open (categories, searches waiting for an argument).</summary>
    public bool KeepsOpen { get; init; }

    public NumericRange? Range { get; init; }

    /// <summary>Asked inline before running ("Empty the Recycle Bin?").</summary>
    public string? ConfirmPrompt { get; init; }

    /// <summary>A file location for "Show in File Explorer" (Ctrl+Enter).</summary>
    public string? RevealPath { get; init; }

    /// <summary>Shown in orange instead of the subtitle: the row needs setup first.</summary>
    public string? SetupHint { get; init; }

    /// <summary>The owning feature (feature ordering puts its main command before its settings).</summary>
    public string? FeatureId { get; init; }

    /// <summary>Ordering turn inside a feature: 0 main command, 1 preset, 2 settings page.</summary>
    public int Role { get; init; }

    /// <summary>The row types or pastes at the caret: the bar hands focus back first.</summary>
    public bool ActsAtCaret { get; init; }

    /// <summary>Heading on the home list (defaults to the subtitle).</summary>
    public string? Section { get; init; }

    /// <summary>Saved searches and scripts rank against the whole query once it names them.</summary>
    public string? NameForArgument { get; init; }

    /// <summary>A saved script row (only the longest-named script the query names stays eligible).</summary>
    public bool IsScript { get; init; }

    /// <summary>
    /// A row with <see cref="NameForArgument"/> that may also run with nothing
    /// typed after its name (scripts marked "Also run when its name is typed on its own").
    /// </summary>
    public bool RunsBare { get; init; }

    public IReadOnlyList<CommandRowAction> Actions { get; init; } = [];

    public Func<CommandRunContext, Task>? Run { get; init; }

    public CommandSource Source => CommandSources.Of(Id);

    public override string ToString() => Id;
}

/// <summary>Source ids, persisted in <c>commandBarDisabledSources</c> (spec 06 §6.3.6). Never rename.</summary>
public enum CommandSource
{
    Actions,
    Apps,
    Menus,
    Windows,
    QuitApps,
    UninstallApps,
    SettingsPages,
    MacSettings,
    Snippets,
    Clipboard,
    Emoji,
    Folders,
    Answers,
    Calculator,
    Selection,
    Links,
    Files,
    KillProcess,
}

public static class CommandSources
{
    private static readonly (string Prefix, CommandSource Source)[] Prefixes =
    [
        ("app.", CommandSource.Apps), ("menu.", CommandSource.Menus), ("window.", CommandSource.Windows),
        ("quit.", CommandSource.QuitApps), ("uninstall.", CommandSource.UninstallApps), ("settings.", CommandSource.SettingsPages),
        ("macsettings.", CommandSource.MacSettings), ("snippet.", CommandSource.Snippets), ("clipboard.", CommandSource.Clipboard),
        ("emoji.", CommandSource.Emoji), ("folder.", CommandSource.Folders), ("answer.", CommandSource.Answers),
        ("selection.", CommandSource.Selection), ("link.", CommandSource.Links), ("file.", CommandSource.Files), ("kill.", CommandSource.KillProcess),
        ("math.", CommandSource.Calculator), ("units.", CommandSource.Calculator), ("color.", CommandSource.Calculator), ("date.", CommandSource.Calculator),
    ];

    /// <summary>The source of a row id; anything without a known prefix is an action.</summary>
    public static CommandSource Of(string id)
    {
        if (id is "emoji.browse" or "kill.browse" or "uninstall.browse" or "uninstall.finder")
        {
            return CommandSource.Actions;
        }

        foreach (var (prefix, source) in Prefixes)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return source;
            }
        }

        return CommandSource.Actions;
    }

    /// <summary>The persisted spelling ("macSettings", "quitApps"…).</summary>
    public static string StorageId(CommandSource source)
    {
        var name = source.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public static CommandSource? Parse(string id) =>
        Enum.GetValues<CommandSource>().Cast<CommandSource?>().FirstOrDefault(s => StorageId(s!.Value) == id);

    /// <summary>Per-kind caps in a typed list (actions uncapped).</summary>
    public static int? Cap(string id)
    {
        foreach (var (prefix, cap) in Caps)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return cap;
            }
        }

        return null;
    }

    private static readonly (string Prefix, int Cap)[] Caps =
    [
        ("app.", 5), ("window.", 4), ("quit.", 3), ("menu.", 5), ("emoji.", 6), ("settings.", 4),
        ("macsettings.", 4), ("clipboard.", 4), ("snippet.", 4), ("file.", 4), ("toggle.", 5),
    ];

    /// <summary>Ranking bias: menus −80, files and settings pages −40, apps +80.</summary>
    public static int RankBias(CommandSource source) => source switch
    {
        CommandSource.Menus => -80,
        CommandSource.Files => -40,
        CommandSource.SettingsPages => -40,
        CommandSource.Apps => 80,
        _ => 0,
    };

    /// <summary>Pinnable sources (spec 06 §3.8.8).</summary>
    public static bool IsPinnable(CommandSource source) => source is CommandSource.Actions or CommandSource.Apps or CommandSource.Windows
        or CommandSource.SettingsPages or CommandSource.MacSettings or CommandSource.Snippets or CommandSource.Folders
        or CommandSource.Links or CommandSource.Answers or CommandSource.Calculator;

    /// <summary>Nameable sources (alias and row shortcut).</summary>
    public static bool IsNameable(CommandSource source) => source is CommandSource.Actions or CommandSource.Apps or CommandSource.QuitApps
        or CommandSource.SettingsPages or CommandSource.MacSettings or CommandSource.Snippets or CommandSource.Emoji
        or CommandSource.Folders or CommandSource.Answers or CommandSource.Calculator or CommandSource.Links;
}
