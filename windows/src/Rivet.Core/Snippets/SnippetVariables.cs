// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Snippets;

/// <summary>A recognised date token inside a snippet text (for the builder's edit mode).</summary>
public sealed record DateToken(DateTokenKind Kind, string Pattern, string? TimeZoneId, int Start, int End)
{
    public string Text => Build(Kind, Pattern, TimeZoneId);

    /// <summary><c>{{&lt;kind&gt;[-tz(&lt;IANA id&gt;)]:&lt;pattern&gt;}}</c>.</summary>
    public static string Build(DateTokenKind kind, string pattern, string? timeZoneId)
    {
        var name = kind switch
        {
            DateTokenKind.Date => "date",
            DateTokenKind.Time => "time",
            _ => "datetime",
        };
        return timeZoneId is { Length: > 0 } zone ? $"{{{{{name}-tz({zone}):{pattern}}}}}" : $"{{{{{name}:{pattern}}}}}";
    }
}

/// <summary>
/// Snippet variables (spec 06 §6.12): <c>{{date}}</c>, <c>{{time}}</c>,
/// <c>{{datetime}}</c>, <c>{{clipboard}}</c> and pattern tokens with an
/// optional IANA time zone. A malformed tag stays literal and never swallows
/// a later valid one; the clipboard goes in last so pasted text is never re-expanded.
/// </summary>
public static class SnippetVariables
{
    public const string ClipboardToken = "{{clipboard}}";

    private static readonly (string Name, DateTokenKind Kind)[] Kinds =
    [
        ("date", DateTokenKind.Date),
        ("time", DateTokenKind.Time),
        ("datetime", DateTokenKind.DateTime),
    ];

    public static bool UsesClipboard(string replacement) => replacement.Contains(ClipboardToken, StringComparison.Ordinal);

    public static string Expand(string replacement, DateTimeOffset now, CultureInfo culture, TimeZoneInfo deviceZone, string? clipboard)
    {
        if (!replacement.Contains("{{", StringComparison.Ordinal))
        {
            return replacement;
        }

        var local = TimeZoneInfo.ConvertTime(now, deviceZone);
        var dateText = DateStyles.MediumDate(local, culture);
        var timeText = DateStyles.ShortTime(local, culture);
        var text = replacement
            .Replace("{{datetime}}", dateText + " " + timeText, StringComparison.Ordinal)
            .Replace("{{date}}", dateText, StringComparison.Ordinal)
            .Replace("{{time}}", timeText, StringComparison.Ordinal);

        if (text.Contains("{{date", StringComparison.Ordinal) || text.Contains("{{time", StringComparison.Ordinal))
        {
            text = ExpandPatterns(text, now, culture, deviceZone);
        }

        return text.Replace(ClipboardToken, clipboard ?? string.Empty, StringComparison.Ordinal);
    }

    private static string ExpandPatterns(string text, DateTimeOffset now, CultureInfo culture, TimeZoneInfo deviceZone)
    {
        var output = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                output.Append(text, i, text.Length - i);
                break;
            }

            output.Append(text, i, open - i);
            if (TryParseToken(text, open, out var token))
            {
                var zone = token.TimeZoneId is { } id ? TimeZones.Find(id)! : deviceZone;
                output.Append(IcuDateFormat.Format(now, token.Pattern, zone, culture));
                i = token.End;
            }
            else
            {
                output.Append("{{");
                i = open + 2;
            }
        }

        return output.ToString();
    }

    /// <summary>Parses the tag starting at <paramref name="open"/> (the "{{"); End is just after "}}".</summary>
    public static bool TryParseToken(string text, int open, out DateToken token)
    {
        token = null!;
        var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        var chunk = text[(open + 2)..close];
        if (chunk.Contains("{{", StringComparison.Ordinal))
        {
            return false; // an unterminated tag must not swallow the next one
        }

        foreach (var (name, kind) in Kinds)
        {
            if (!chunk.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = chunk[name.Length..];
            if (rest.StartsWith("-tz(", StringComparison.Ordinal))
            {
                var end = rest.IndexOf("):", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                var zoneId = rest[4..end];
                var pattern = rest[(end + 2)..];
                if (pattern.Length == 0 || TimeZones.Find(zoneId) is null)
                {
                    continue;
                }

                token = new DateToken(kind, pattern, zoneId, open, close + 2);
                return true;
            }

            if (rest.StartsWith(':') && rest.Length > 1)
            {
                token = new DateToken(kind, rest[1..], null, open, close + 2);
                return true;
            }
        }

        return false;
    }

    /// <summary>The token whose span strictly contains <paramref name="caret"/> (start &lt; caret &lt; end).</summary>
    public static DateToken? TokenAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                return null;
            }

            if (TryParseToken(text, open, out var token))
            {
                if (token.Start < caret && caret < token.End)
                {
                    return token;
                }

                i = token.End;
            }
            else
            {
                i = open + 2;
            }
        }

        return null;
    }
}
