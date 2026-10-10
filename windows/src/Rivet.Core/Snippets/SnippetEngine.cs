// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Snippets;

public enum SnippetKeyKind
{
    /// <summary>A key that produced text (layout-aware, at most 4 UTF-16 units).</summary>
    Character,

    Backspace,

    /// <summary>Arrows, Esc, Home, End, Page Up/Down, Delete: the caret moved, the buffer resets.</summary>
    Navigation,

    /// <summary>Ctrl or Win held without producing text (a shortcut, not typing).</summary>
    Shortcut,

    /// <summary>Modifiers alone, dead keys and anything else that changes nothing.</summary>
    Ignored,
}

public readonly record struct SnippetKey(SnippetKeyKind Kind, string Text = "", int VirtualKey = 0);

/// <summary>An expansion the engine asks for.</summary>
public sealed record SnippetMatch(TextSnippet Snippet, int DeleteCount, int? TrailingKey, string TrailingText, string FailureText);

/// <summary>
/// The typed-trigger buffer and matcher (spec 06 §3.6.2): a rolling 64-character
/// buffer, delimiters (space, Tab, Return) for "after delimiter" snippets,
/// immediate matches on every other character, the longest trigger wins.
/// Not thread-safe: the keyboard hook thread owns it. Never persisted.
/// </summary>
public sealed class SnippetEngine
{
    public const int BufferLimit = 64;

    private TextSnippet[] _immediate = [];
    private TextSnippet[] _delimited = [];
    private string _buffer = string.Empty;

    public string Buffer => _buffer;

    public bool HasSnippets => _immediate.Length + _delimited.Length > 0;

    /// <summary>Splits the enabled snippets with a trigger into the two modes (called at every sync).</summary>
    public void Configure(IEnumerable<TextSnippet> snippets)
    {
        var enabled = snippets.Where(s => s.Enabled && s.Trigger.Length > 0).ToArray();
        Volatile.Write(ref _immediate, enabled.Where(s => s.Expansion == SnippetExpansion.Immediate).ToArray());
        Volatile.Write(ref _delimited, enabled.Where(s => s.Expansion == SnippetExpansion.AfterDelimiter).ToArray());
    }

    public void Reset() => _buffer = string.Empty;

    public SnippetMatch? Process(SnippetKey key)
    {
        switch (key.Kind)
        {
            case SnippetKeyKind.Backspace:
                if (_buffer.Length > 0)
                {
                    var info = StringInfo.GetTextElementEnumerator(_buffer);
                    var lastStart = 0;
                    while (info.MoveNext())
                    {
                        lastStart = info.ElementIndex;
                    }

                    _buffer = _buffer[..lastStart];
                }

                return null;
            case SnippetKeyKind.Navigation or SnippetKeyKind.Shortcut:
                _buffer = string.Empty;
                return null;
            case SnippetKeyKind.Ignored:
                return null;
        }

        var typed = key.Text;
        if (typed.Length == 0)
        {
            return null;
        }

        if (typed[0] is ' ' or '\t' or '\r' or '\n')
        {
            var match = BestMatch(Volatile.Read(ref _delimited), _buffer);
            _buffer = string.Empty;
            return match is null
                ? null
                : new SnippetMatch(match, match.TriggerLength, key.VirtualKey == 0 ? null : key.VirtualKey, typed, typed);
        }

        var combined = _buffer + typed;
        _buffer = combined.Length > BufferLimit ? combined[^BufferLimit..] : combined;
        var immediate = BestMatch(Volatile.Read(ref _immediate), _buffer);
        if (immediate is null)
        {
            return null;
        }

        _buffer = string.Empty;
        var typedLength = new StringInfo(typed).LengthInTextElements;
        return new SnippetMatch(immediate, Math.Max(0, immediate.TriggerLength - typedLength), null, string.Empty, typed);
    }

    /// <summary>The longest trigger completing the buffer (exact suffix, or case-insensitive for "ignore capitalization").</summary>
    public static TextSnippet? BestMatch(IEnumerable<TextSnippet> snippets, string buffer)
    {
        TextSnippet? best = null;
        foreach (var snippet in snippets)
        {
            var trigger = snippet.Trigger;
            if (trigger.Length == 0 || trigger.Length > buffer.Length)
            {
                continue;
            }

            var tail = buffer.AsSpan(buffer.Length - trigger.Length);
            var matches = snippet.IgnoresCase
                ? tail.Equals(trigger, StringComparison.OrdinalIgnoreCase)
                : tail.SequenceEqual(trigger);
            if (matches && (best is null || trigger.Length > best.Trigger.Length))
            {
                best = snippet;
            }
        }

        return best;
    }
}
