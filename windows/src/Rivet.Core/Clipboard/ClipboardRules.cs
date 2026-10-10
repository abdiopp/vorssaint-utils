// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rivet.Core.Clipboard;

/// <summary>"Skip text that looks sensitive" (spec 06 §6.13).</summary>
public static partial class ClipboardSensitiveText
{
    private static readonly string[] Words = ["password", "passwd", "secret", "token", "apikey", "api_key", "authorization"];

    public static bool LooksSensitive(string text)
    {
        var lower = text.ToLowerInvariant();
        if (Words.Any(w => lower.Contains(w, StringComparison.Ordinal)))
        {
            return true;
        }

        if (ClipboardPreferredText.IsSingleWebUrl(text))
        {
            return false;
        }

        if (Guid().IsMatch(text))
        {
            return false;
        }

        if (text.Length is < 20 or > 160 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var hasLetter = false;
        var hasDigit = false;
        var hasSymbol = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                hasLetter = true;
            }
            else if (Rune.IsDigit(rune))
            {
                hasDigit = true;
            }
            else if (!Rune.IsWhiteSpace(rune))
            {
                hasSymbol = true;
            }
        }

        return hasLetter && hasDigit && hasSymbol;
    }

    [GeneratedRegex(@"^\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$")]
    private static partial Regex Guid();
}

/// <summary>Which text a copy stands for when it carries both plain text and a link (spec 06 §3.2.3).</summary>
public static class ClipboardPreferredText
{
    /// <summary>A lone http(s) link with a host and no whitespace anywhere.</summary>
    public static bool IsSingleWebUrl(string text)
    {
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
               && uri.Host.Length > 0;
    }

    /// <param name="plain">The plain-text flavour (trimmed by this method).</param>
    /// <param name="webUrl">The first http(s) link with a host found in the URL flavours.</param>
    public static string? Preferred(string? plain, string? webUrl)
    {
        var text = plain?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = null;
        }

        if (text is not null && IsSingleWebUrl(text))
        {
            return text;
        }

        var url = webUrl is not null && IsSingleWebUrl(webUrl.Trim()) ? webUrl.Trim() : null;
        if (url is null)
        {
            return text;
        }

        if (text is null)
        {
            return url;
        }

        if (!text.Any(char.IsWhiteSpace))
        {
            var withoutScheme = url[(url.IndexOf(':') + 1)..];
            if (text.StartsWith("//", StringComparison.Ordinal)
                || text == withoutScheme
                || text == withoutScheme.TrimStart('/'))
            {
                return url;
            }
        }

        return text;
    }
}

/// <summary>Clipboard change counter acceptance (spec 06 §3.1.2).</summary>
public static class ClipboardChangeCount
{
    /// <param name="read">The counter seen by the read.</param>
    /// <param name="since">The last known counter when the read was scheduled.</param>
    /// <param name="last">The last known counter now (own writes raise it).</param>
    /// <returns>The accepted counter, or null when there is nothing new.</returns>
    public static uint? Accepted(uint read, uint since, uint last)
    {
        if (read < since)
        {
            return read; // the clipboard server restarted and counts from zero again
        }

        return read > last ? read : null;
    }
}

public enum AutoClearDecision
{
    Wait,
    NoteChange,
    Clear,
}

/// <summary>The delay trigger's per-tick decision (spec 06 §3.3).</summary>
public static class ClipboardAutoClearRules
{
    public static AutoClearDecision Decide(uint counter, uint lastChangeCount, uint? lastClearedChangeCount, DateTimeOffset now, DateTimeOffset lastChangeDate, TimeSpan delay)
    {
        if (counter != lastChangeCount)
        {
            return AutoClearDecision.NoteChange;
        }

        if (lastClearedChangeCount == counter)
        {
            return AutoClearDecision.Wait;
        }

        return now - lastChangeDate >= delay ? AutoClearDecision.Clear : AutoClearDecision.Wait;
    }
}

/// <summary>JSON preview layout that never re-encodes values (spec 06 §6.14).</summary>
public static class ClipboardJsonFormat
{
    public const int MaxInputBytes = 256 * 1024;
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>The laid-out JSON, or null when the text is not a JSON object/array (or too large).</summary>
    public static string? Pretty(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length || text[start] is not ('{' or '[') || Encoding.UTF8.GetByteCount(text) > MaxInputBytes)
        {
            return null;
        }

        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return null;
        }

        var output = new StringBuilder(text.Length + (text.Length / 2));
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                output.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case ' ' or '\t' or '\r' or '\n':
                    break;
                case '"':
                    inString = true;
                    output.Append(c);
                    break;
                case '{' or '[':
                    var closer = c == '{' ? '}' : ']';
                    var next = i + 1;
                    while (next < text.Length && text[next] is ' ' or '\t' or '\r' or '\n')
                    {
                        next++;
                    }

                    if (next < text.Length && text[next] == closer)
                    {
                        output.Append(c).Append(closer);
                        i = next;
                    }
                    else
                    {
                        depth++;
                        output.Append(c);
                        Break(output, depth);
                    }

                    break;
                case '}' or ']':
                    TrimTrailingSpaces(output);
                    if (output.Length > 0 && output[^1] == '\n')
                    {
                        output.Length--;
                    }

                    depth = Math.Max(0, depth - 1);
                    Break(output, depth);
                    output.Append(c);
                    break;
                case ',':
                    output.Append(c);
                    Break(output, depth);
                    break;
                case ':':
                    output.Append(": ");
                    break;
                default:
                    output.Append(c);
                    break;
            }

            if (output.Length > MaxOutputChars)
            {
                return null;
            }
        }

        return output.ToString();
    }

    private static void Break(StringBuilder output, int depth)
    {
        output.Append('\n');
        output.Append(' ', depth * 2);
    }

    private static void TrimTrailingSpaces(StringBuilder output)
    {
        while (output.Length > 0 && output[^1] == ' ')
        {
            output.Length--;
        }
    }
}
