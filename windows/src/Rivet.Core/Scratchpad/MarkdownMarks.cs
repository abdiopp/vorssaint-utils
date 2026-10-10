// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Rivet.Core.Modules.Scratchpad;

public enum MarkdownMark
{
    Bold,
    Italic,
    Strikethrough,
    Heading,
    Bullet,
    Numbered,
    Quote,
    Code,
    Link,
}

/// <summary>Text plus selection, in UTF-16 offsets (the same units as .NET strings and the editor).</summary>
public readonly record struct TextEdit(string Text, int SelectionStart, int SelectionLength)
{
    public int SelectionEnd => SelectionStart + SelectionLength;
}

/// <summary>
/// The nine Markdown helper marks of the Scratchpad (spec 07 §3.3.8). Each is
/// one replacement and a toggle: a second click removes the mark.
/// <list type="bullet">
/// <item>Inline marks (bold <c>**</c>, strikethrough <c>~~</c>, code <c>`</c>) trim the
/// selection, remove a marker found at its edges (inside or just outside;
/// <c>**one** and **two**</c> joins into <c>**one and two**</c>), or wrap it.</item>
/// <item>Italic counts runs of <c>*</c>: an odd run on both sides means italic is present.</item>
/// <item>Link replaces an enclosing <c>[label](address)</c> by its label, or wraps the
/// selection as <c>[sel](url)</c> with "url" selected. Brackets match by depth and
/// honour backslash escapes; inside an image nothing happens.</item>
/// <item>Line marks (heading cycle, bullet, numbered, quote) apply to every
/// touched non-blank line, replacing an existing line mark instead of stacking.</item>
/// </list>
/// Text uses "\n" line breaks.
/// </summary>
public static class MarkdownMarks
{
    public const string LinkPlaceholder = "url";

    public static TextEdit Apply(MarkdownMark mark, string text, int selectionStart, int selectionLength)
    {
        text ??= string.Empty;
        var (start, end) = Clamp(text, selectionStart, selectionLength);
        return mark switch
        {
            MarkdownMark.Bold => Inline(text, start, end, "**"),
            MarkdownMark.Strikethrough => Inline(text, start, end, "~~"),
            MarkdownMark.Code => Inline(text, start, end, "`"),
            MarkdownMark.Italic => Italic(text, start, end),
            MarkdownMark.Link => Link(text, start, end),
            _ => LineMark(mark, text, start, end),
        };
    }

    /// <summary>Clamps to the text and widens so a selection never splits a surrogate pair.</summary>
    public static (int Start, int End) Clamp(string text, int start, int length)
    {
        var s = Math.Clamp(start, 0, text.Length);
        var e = Math.Clamp(start + Math.Max(0, length), s, text.Length);
        if (s > 0 && s < text.Length && char.IsLowSurrogate(text[s]) && char.IsHighSurrogate(text[s - 1]))
        {
            s--;
        }

        if (e > 0 && e < text.Length && char.IsLowSurrogate(text[e]) && char.IsHighSurrogate(text[e - 1]))
        {
            e++;
        }

        return (s, e);
    }

    private static (int Start, int End) TrimSelection(string text, int start, int end)
    {
        var s = start;
        var e = end;
        while (s < e && char.IsWhiteSpace(text[s]))
        {
            s++;
        }

        while (e > s && char.IsWhiteSpace(text[e - 1]))
        {
            e--;
        }

        // A selection of only whitespace is kept as it is.
        return s == e && end > start ? (start, end) : (s, e);
    }

    private static TextEdit Inline(string text, int start, int end, string marker)
    {
        var original = new TextEdit(text, start, end - start);
        var (s, e) = TrimSelection(text, start, end);
        var selected = text[s..e];
        var m = marker.Length;

        // Marker inside the selection's edges.
        if (selected.Length >= 2 * m && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
        {
            var inner = selected[m..^m];
            return RemoveOrJoin(text, s, e, s, e, inner, marker) ?? original;
        }

        // Marker just outside the selection.
        if (s >= m && e + m <= text.Length && string.CompareOrdinal(text, s - m, marker, 0, m) == 0 && string.CompareOrdinal(text, e, marker, 0, m) == 0)
        {
            return RemoveOrJoin(text, s - m, e + m, s, e, selected, marker) ?? original;
        }

        var wrapped = string.Concat(text[..(s)], marker, selected, marker, text[(e)..]);
        return new TextEdit(wrapped, s + m, selected.Length);
    }

    /// <summary>
    /// The marked span is [outerStart, outerEnd) with <paramref name="inner"/>
    /// between the markers. No inner markers: remove the pair. An even number
    /// of unescaped inner markers and no other markup: join under one pair.
    /// Otherwise leave the text alone (null).
    /// </summary>
    private static TextEdit? RemoveOrJoin(string text, int outerStart, int outerEnd, int innerStart, int innerEnd, string inner, string marker)
    {
        var occurrences = FindOccurrences(inner, marker);
        if (occurrences.Count == 0)
        {
            var removed = string.Concat(text[..(outerStart)], inner, text[(outerEnd)..]);
            return new TextEdit(removed, outerStart, inner.Length);
        }

        if (occurrences.Count % 2 != 0 || occurrences.Any(i => IsEscaped(inner, i)))
        {
            return null;
        }

        var joinedInner = inner.Replace(marker, string.Empty, StringComparison.Ordinal);
        if (joinedInner.Contains(marker[0], StringComparison.Ordinal))
        {
            return null;
        }

        var joined = string.Concat(text[..(outerStart)], marker, joinedInner, marker, text[(outerEnd)..]);
        return new TextEdit(joined, outerStart + marker.Length, joinedInner.Length);
    }

    private static List<int> FindOccurrences(string value, string marker)
    {
        var result = new List<int>();
        var index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            result.Add(index);
            index += marker.Length;
        }

        return result;
    }

    private static TextEdit Italic(string text, int start, int end)
    {
        var original = new TextEdit(text, start, end - start);
        var (s, e) = TrimSelection(text, start, end);
        var selected = text[s..e];

        var leadingInside = CountRun(text, s, e, forward: true);
        var trailingInside = leadingInside == selected.Length ? 0 : CountRun(text, s, e, forward: false);
        if (leadingInside > 0 && trailingInside > 0)
        {
            if (leadingInside >= 3 || trailingInside >= 3)
            {
                // "***" inside the selection: the italic boundary is ambiguous.
                return original;
            }

            if (leadingInside % 2 == 1 && trailingInside % 2 == 1)
            {
                var inner = selected[1..^1];
                return new TextEdit(string.Concat(text[..(s)], inner, text[(e)..]), s, inner.Length);
            }
        }

        var before = CountRun(text, 0, s, forward: false);
        var after = CountRun(text, e, text.Length, forward: true);
        if (before % 2 == 1 && after % 2 == 1)
        {
            var removed = string.Concat(text[..(s - 1)], selected, text[(e + 1)..]);
            return new TextEdit(removed, s - 1, selected.Length);
        }

        return new TextEdit(string.Concat(text[..(s)], "*", selected, "*", text[(e)..]), s + 1, selected.Length);
    }

    /// <summary>Length of the run of '*' at the start (forward) or end of [from, to).</summary>
    private static int CountRun(string text, int from, int to, bool forward)
    {
        var count = 0;
        if (forward)
        {
            for (var i = from; i < to && text[i] == '*'; i++)
            {
                count++;
            }
        }
        else
        {
            for (var i = to - 1; i >= from && text[i] == '*'; i--)
            {
                count++;
            }
        }

        return count;
    }

    private static TextEdit Link(string text, int start, int end)
    {
        var enclosing = FindLinks(text)
            .Where(l => l.Open < start && end <= l.CloseParen)
            .OrderBy(l => l.CloseParen - l.Open)
            .FirstOrDefault();
        if (enclosing.CloseParen > 0)
        {
            if (enclosing.IsImage)
            {
                return new TextEdit(text, start, end - start);
            }

            var label = text[(enclosing.Open + 1)..enclosing.CloseBracket];
            var replaced = string.Concat(text[..(enclosing.Open)], label, text[(enclosing.CloseParen + 1)..]);
            return new TextEdit(replaced, enclosing.Open, label.Length);
        }

        var (s, e) = TrimSelection(text, start, end);
        var selected = text[s..e];
        var wrapped = string.Concat(text[..(s)], "[", selected, "](", LinkPlaceholder, ")", text[(e)..]);
        return new TextEdit(wrapped, s + selected.Length + 3, LinkPlaceholder.Length);
    }

    public readonly record struct LinkSpan(int Open, int CloseBracket, int CloseParen, bool IsImage);

    /// <summary>All <c>[label](address)</c> spans, brackets and parentheses matched by depth, escapes honoured.</summary>
    public static IReadOnlyList<LinkSpan> FindLinks(string text)
    {
        var result = new List<LinkSpan>();
        for (var open = 0; open < text.Length; open++)
        {
            if (text[open] != '[' || IsEscaped(text, open))
            {
                continue;
            }

            var closeBracket = MatchClosing(text, open, '[', ']');
            if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
            {
                continue;
            }

            var closeParen = MatchClosing(text, closeBracket + 1, '(', ')');
            if (closeParen < 0)
            {
                continue;
            }

            var isImage = open > 0 && text[open - 1] == '!' && !IsEscaped(text, open - 1);
            result.Add(new LinkSpan(open, closeBracket, closeParen, isImage));
        }

        return result;
    }

    private static int MatchClosing(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n' && open == '(')
            {
                return -1;
            }

            if ((c != open && c != close) || IsEscaped(text, i))
            {
                continue;
            }

            depth += c == open ? 1 : -1;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A character preceded by an odd number of backslashes is escaped.</summary>
    public static bool IsEscaped(string text, int index)
    {
        var count = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
        {
            count++;
        }

        return count % 2 == 1;
    }

    // ── Line marks ──────────────────────────────────────────────────────

    private enum LineKind
    {
        None,
        Heading,
        Bullet,
        Numbered,
        Quote,
    }

    private readonly record struct ParsedLine(int Start, int IndentLength, LineKind Kind, int Level, int MarkLength, string Text)
    {
        public bool IsBlank => Text.Trim().Length == 0;

        public int PrefixPosition => Start + IndentLength;

        public string Indent => Text[..IndentLength];

        public string Content => Text[(IndentLength + MarkLength)..];
    }

    private static TextEdit LineMark(MarkdownMark mark, string text, int start, int end)
    {
        if (text.Length == 0)
        {
            var prefix = mark switch
            {
                MarkdownMark.Heading => "# ",
                MarkdownMark.Bullet => "- ",
                MarkdownMark.Numbered => "1. ",
                _ => "> ",
            };
            return new TextEdit(prefix, prefix.Length, 0);
        }

        var lines = ParseLines(text);
        var first = LineIndexAt(lines, start);
        var last = LineIndexAt(lines, end);
        if (end > start && last > first && end == lines[last].Start)
        {
            last--;
        }

        var touched = Enumerable.Range(first, last - first + 1).Where(i => !lines[i].IsBlank).ToList();
        if (touched.Count == 0)
        {
            return new TextEdit(text, start, end - start);
        }

        var targetKind = mark switch
        {
            MarkdownMark.Heading => LineKind.Heading,
            MarkdownMark.Bullet => LineKind.Bullet,
            MarkdownMark.Numbered => LineKind.Numbered,
            _ => LineKind.Quote,
        };

        // The current step is the mark every touched line already has.
        var allSameKind = touched.All(i => lines[i].Kind == targetKind);
        var commonLevel = allSameKind && targetKind == LineKind.Heading && touched.Select(i => lines[i].Level).Distinct().Count() == 1
            ? lines[touched[0]].Level
            : 0;

        Func<int, string> newPrefix = targetKind switch
        {
            LineKind.Heading when allSameKind && commonLevel is 1 => _ => "## ",
            LineKind.Heading when allSameKind && commonLevel is 2 => _ => "### ",
            LineKind.Heading when allSameKind && commonLevel >= 3 => _ => string.Empty,
            LineKind.Heading => _ => "# ",
            LineKind.Numbered when allSameKind => _ => string.Empty,
            LineKind.Numbered => n => $"{n}. ",
            LineKind.Bullet => _ => allSameKind ? string.Empty : "- ",
            _ => _ => allSameKind ? string.Empty : "> ",
        };

        var builder = new StringBuilder(text.Length + (touched.Count * 4));
        var changes = new List<(int Position, int OldLength, int NewLength)>();
        var number = 0;
        var touchedSet = touched.ToHashSet();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var line = lines[i];
            if (!touchedSet.Contains(i))
            {
                builder.Append(line.Text);
                continue;
            }

            number++;
            var prefix = newPrefix(number);
            builder.Append(line.Indent).Append(prefix).Append(line.Content);
            changes.Add((line.PrefixPosition, line.MarkLength, prefix.Length));
        }

        var newText = builder.ToString();
        var newStart = MapOffset(start, changes);
        var newEnd = Math.Max(newStart, MapOffset(end, changes));
        return new TextEdit(newText, newStart, newEnd - newStart);
    }

    private static int MapOffset(int offset, List<(int Position, int OldLength, int NewLength)> changes)
    {
        var mapped = offset;
        foreach (var (position, oldLength, newLength) in changes)
        {
            if (offset < position)
            {
                break;
            }

            if (offset > position && offset < position + oldLength)
            {
                // Inside the replaced mark: land right after the new mark.
                mapped += position + newLength - offset;
                break;
            }

            if (offset >= position + oldLength)
            {
                mapped += newLength - oldLength;
            }
        }

        return mapped;
    }

    private static List<ParsedLine> ParseLines(string text)
    {
        var result = new List<ParsedLine>();
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            var lineText = newline < 0 ? text[start..] : text[start..newline];
            result.Add(Parse(start, lineText));
            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return result;
    }

    private static ParsedLine Parse(int start, string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] is ' ' or '\t')
        {
            indent++;
        }

        var rest = line.AsSpan(indent);
        var hashes = 0;
        while (hashes < rest.Length && rest[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is >= 1 and <= 6 && hashes < rest.Length && rest[hashes] == ' ')
        {
            return new ParsedLine(start, indent, LineKind.Heading, hashes, hashes + 1, line);
        }

        if (rest.Length >= 2 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
        {
            return new ParsedLine(start, indent, LineKind.Bullet, 0, 2, line);
        }

        if (rest.Length >= 2 && rest[0] == '>' && rest[1] == ' ')
        {
            return new ParsedLine(start, indent, LineKind.Quote, 0, 2, line);
        }

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < rest.Length && rest[digits] == '.' && rest[digits + 1] == ' ')
        {
            return new ParsedLine(start, indent, LineKind.Numbered, 0, digits + 2, line);
        }

        return new ParsedLine(start, indent, LineKind.None, 0, 0, line);
    }

    private static int LineIndexAt(List<ParsedLine> lines, int offset)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (offset >= lines[i].Start)
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>Alt+Up / Alt+Down: swap the block of touched lines with the line above or below (spec 07 §3.3.7).</summary>
public static class ScratchpadLineMover
{
    /// <summary>
    /// Returns the moved text and selection, or null at the first/last line
    /// (the key then falls through). Swapping two identical lines returns the
    /// same text with only the selection moved, so no undo step is needed.
    /// </summary>
    public static TextEdit? Move(string text, int selectionStart, int selectionLength, bool up)
    {
        var (start, end) = MarkdownMarks.Clamp(text, selectionStart, selectionLength);
        var lines = text.Split('\n').ToList();
        var starts = new List<int>(lines.Count);
        var offset = 0;
        foreach (var line in lines)
        {
            starts.Add(offset);
            offset += line.Length + 1;
        }

        int LineOf(int position)
        {
            for (var i = starts.Count - 1; i >= 0; i--)
            {
                if (position >= starts[i])
                {
                    return i;
                }
            }

            return 0;
        }

        var first = LineOf(start);
        var last = LineOf(end);
        if (end > start && last > first && end == starts[last])
        {
            last--;
        }

        if ((up && first == 0) || (!up && last >= lines.Count - 1))
        {
            return null;
        }

        var block = lines.GetRange(first, last - first + 1);
        int shift;
        if (up)
        {
            var above = lines[first - 1];
            lines.RemoveRange(first - 1, block.Count + 1);
            lines.InsertRange(first - 1, [.. block, above]);
            shift = -(above.Length + 1);
        }
        else
        {
            var below = lines[last + 1];
            lines.RemoveRange(first, block.Count + 1);
            lines.InsertRange(first, [below, .. block]);
            shift = below.Length + 1;
        }

        return new TextEdit(string.Join('\n', lines), start + shift, end - start);
    }
}
