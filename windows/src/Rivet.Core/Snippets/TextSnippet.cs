// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Clipboard;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Shortcuts;

namespace Rivet.Core.Snippets;

public enum SnippetExpansion
{
    /// <summary>"Right away": expands as soon as the trigger is complete.</summary>
    Immediate,

    /// <summary>"After space, Tab or Return".</summary>
    AfterDelimiter,
}

/// <summary>One text snippet (spec 06 §3.6.1, §5.5). Stored as a JSON array in <c>textSnippets</c>.</summary>
public sealed record TextSnippet
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string Trigger { get; init; } = string.Empty;

    /// <summary>The text; may hold ICU date patterns and IANA ids, kept byte for byte.</summary>
    public string Replacement { get; init; } = string.Empty;

    public SnippetExpansion Expansion { get; init; } = SnippetExpansion.AfterDelimiter;

    public bool Enabled { get; init; } = true;

    public bool IgnoresCase { get; init; }

    public string Folder { get; init; } = string.Empty;

    public bool ShowsInLibrary { get; init; } = true;

    /// <summary>The name, or the trigger when the snippet has no name.</summary>
    public string DisplayName => Name.Length > 0 ? Name : Trigger;

    /// <summary>One line of the text: newlines as spaces.</summary>
    public string PreviewLine => Replacement.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');

    /// <summary>Length of the trigger in user-perceived characters (backspaces to delete it).</summary>
    public int TriggerLength => new StringInfo(Trigger).LengthInTextElements;
}

public static class SnippetSettings
{
    public const int MaxTriggerLength = 40;
    public const int MinTriggerLength = 2;

    public static Setting<bool> ExpansionEnabled => FeatureKeys.TextSnippetsEnabled;

    public static Setting<bool> LibraryEnabled => FeatureKeys.SnippetLibraryEnabled;

    public static readonly Setting<List<TextSnippet>> Snippets = new("textSnippets", [], SanitizeList);

    public static readonly Setting<string> LibraryShortcut = new("snippetLibraryShortcut", string.Empty);

    /// <summary>Ctrl+Alt+Win+I (spec 05 §6.1; Win+L locks the PC).</summary>
    public static readonly KeyChord DefaultLibraryShortcut = new(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win, 'I');

    public static readonly Setting<bool> SoundEnabled = new("snippetSoundEnabled", false);

    /// <summary>A system sound name (a .wav in %windir%\Media without extension).</summary>
    public static readonly Setting<string> SoundName = new("snippetSoundName", DefaultSound);

    public const string DefaultSound = "Windows Ding";

    /// <summary>Removes all whitespace and cuts to 40 characters.</summary>
    public static string SanitizeTrigger(string trigger)
    {
        var compact = new string(trigger.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return compact.Length > MaxTriggerLength ? compact[..MaxTriggerLength] : compact;
    }

    private static List<TextSnippet> SanitizeList(List<TextSnippet> list) =>
        list.Where(s => s is not null)
            .Select(s => s with
            {
                Name = (s.Name ?? string.Empty).Trim(' '),
                Trigger = SanitizeTrigger(s.Trigger ?? string.Empty),
                Replacement = s.Replacement ?? string.Empty,
                Folder = (s.Folder ?? string.Empty).Trim(),
            })
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .ToList();
}

public enum SnippetValidation
{
    Valid,
    TriggerTooShort,
    DuplicateTrigger,
    EmptyText,
}

public static class SnippetValidator
{
    /// <summary>Editor checks: trigger ≥ 2 characters, unique (case-insensitively if either ignores case), text not empty.</summary>
    public static SnippetValidation Validate(TextSnippet draft, IEnumerable<TextSnippet> others)
    {
        var trigger = SnippetSettings.SanitizeTrigger(draft.Trigger);
        if (trigger.Length < SnippetSettings.MinTriggerLength)
        {
            return SnippetValidation.TriggerTooShort;
        }

        foreach (var other in others.Where(o => o.Id != draft.Id))
        {
            var comparison = draft.IgnoresCase || other.IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(other.Trigger, trigger, comparison))
            {
                return SnippetValidation.DuplicateTrigger;
            }
        }

        return draft.Replacement.Length == 0 ? SnippetValidation.EmptyText : SnippetValidation.Valid;
    }
}

/// <summary>The snippet library's content (spec 06 §3.7): eligible snippets grouped by folder.</summary>
public static class SnippetLibrary
{
    public sealed record Section(string Folder, IReadOnlyList<TextSnippet> Snippets);

    /// <summary>
    /// Enabled snippets shown in the library, filtered by the query (name,
    /// trigger, text or folder; case and diacritic insensitive), folders sorted
    /// naturally, loose snippets last, stored order inside each group.
    /// </summary>
    public static IReadOnlyList<Section> Sections(IEnumerable<TextSnippet> snippets, string query, CultureInfo culture)
    {
        var q = TextFold.ForCommand(query.Trim());
        var eligible = snippets.Where(s => s.Enabled && s.ShowsInLibrary)
            .Where(s => q.Length == 0
                        || TextFold.ForCommand(s.Name).Contains(q, StringComparison.Ordinal)
                        || TextFold.ForCommand(s.Trigger).Contains(q, StringComparison.Ordinal)
                        || TextFold.ForCommand(s.Replacement).Contains(q, StringComparison.Ordinal)
                        || TextFold.ForCommand(s.Folder).Contains(q, StringComparison.Ordinal))
            .ToList();
        var compare = StringComparer.Create(culture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        var sections = eligible.Where(s => s.Folder.Length > 0)
            .GroupBy(s => s.Folder)
            .OrderBy(g => g.Key, compare)
            .Select(g => new Section(g.Key, g.ToList()))
            .ToList();
        var loose = eligible.Where(s => s.Folder.Length == 0).ToList();
        if (loose.Count > 0)
        {
            sections.Add(new Section(string.Empty, loose));
        }

        return sections;
    }

    public static bool HasEligible(IEnumerable<TextSnippet> snippets) => snippets.Any(s => s.Enabled && s.ShowsInLibrary);
}
