// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Rivet.Core.Clipboard;

/// <summary>Plain text out of the clipboard's rich formats (RTF and CF_HTML), for paste as plain text.</summary>
public static partial class RichText
{
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "headerl", "headerr", "headerf",
        "footer", "footerl", "footerr", "footerf", "footnote", "themedata", "colorschememapping", "datastore",
        "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "mmathPr",
        "fldinst", "filetbl", "revtbl", "pgdsctbl", "userprops", "nonshppict", "bkmkstart", "bkmkend",
    };

    static RichText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>The visible text of an RTF document (paragraphs become newlines, tables tabs).</summary>
    public static string RtfToText(string rtf)
    {
        var output = new StringBuilder(rtf.Length / 2);
        var stack = new Stack<(bool Skip, int Uc)>();
        var skip = false;
        var uc = 1;
        var pendingSkipChars = 0;
        var codePage = Encoding.GetEncoding(1252);
        var i = 0;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    stack.Push((skip, uc));
                    i++;
                    break;
                case '}':
                    (skip, uc) = stack.Count > 0 ? stack.Pop() : (false, 1);
                    i++;
                    break;
                case '\\':
                    i = ReadControl(rtf, i + 1, output, ref skip, ref uc, ref pendingSkipChars, ref codePage);
                    break;
                case '\r' or '\n':
                    i++;
                    break;
                default:
                    if (pendingSkipChars > 0)
                    {
                        pendingSkipChars--;
                    }
                    else if (!skip)
                    {
                        output.Append(c);
                    }

                    i++;
                    break;
            }
        }

        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static int ReadControl(string rtf, int i, StringBuilder output, ref bool skip, ref int uc, ref int pendingSkipChars, ref Encoding codePage)
    {
        if (i >= rtf.Length)
        {
            return i;
        }

        var c = rtf[i];
        if (c is '\\' or '{' or '}')
        {
            Emit(output, c, skip, ref pendingSkipChars);
            return i + 1;
        }

        if (c == '\'')
        {
            if (i + 2 < rtf.Length && int.TryParse(rtf.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                var text = codePage.GetString([(byte)value]);
                foreach (var ch in text)
                {
                    Emit(output, ch, skip, ref pendingSkipChars);
                }
            }

            return Math.Min(rtf.Length, i + 3);
        }

        if (c == '*')
        {
            skip = true;
            return i + 1;
        }

        if (c == '~')
        {
            Emit(output, ' ', skip, ref pendingSkipChars);
            return i + 1;
        }

        if (c == '_')
        {
            Emit(output, '‑', skip, ref pendingSkipChars);
            return i + 1;
        }

        if (!char.IsAsciiLetter(c))
        {
            // \- optional hyphen, \: index entry, or an escaped newline (= \par).
            if (c is '\r' or '\n')
            {
                Emit(output, '\n', skip, ref pendingSkipChars);
            }

            return i + 1;
        }

        var start = i;
        while (i < rtf.Length && char.IsAsciiLetter(rtf[i]))
        {
            i++;
        }

        var word = rtf[start..i];
        int? parameter = null;
        var paramStart = i;
        if (i < rtf.Length && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
        {
            i++;
            while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
            {
                i++;
            }

            if (int.TryParse(rtf.AsSpan(paramStart, i - paramStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p))
            {
                parameter = p;
            }
        }

        if (i < rtf.Length && rtf[i] == ' ')
        {
            i++;
        }

        if (SkippedDestinations.Contains(word))
        {
            skip = true;
            return i;
        }

        switch (word)
        {
            case "par" or "line" or "sect" or "page" or "row":
                Emit(output, '\n', skip, ref pendingSkipChars);
                break;
            case "tab" or "cell":
                Emit(output, '\t', skip, ref pendingSkipChars);
                break;
            case "emdash":
                Emit(output, '—', skip, ref pendingSkipChars);
                break;
            case "endash":
                Emit(output, '–', skip, ref pendingSkipChars);
                break;
            case "bullet":
                Emit(output, '•', skip, ref pendingSkipChars);
                break;
            case "lquote":
                Emit(output, '‘', skip, ref pendingSkipChars);
                break;
            case "rquote":
                Emit(output, '’', skip, ref pendingSkipChars);
                break;
            case "ldblquote":
                Emit(output, '“', skip, ref pendingSkipChars);
                break;
            case "rdblquote":
                Emit(output, '”', skip, ref pendingSkipChars);
                break;
            case "uc" when parameter is { } ucValue:
                uc = Math.Max(0, ucValue);
                break;
            case "u" when parameter is { } code:
                Emit(output, (char)(code < 0 ? code + 65536 : code), skip, ref pendingSkipChars);
                pendingSkipChars = uc;
                break;
            case "ansicpg" when parameter is { } cp:
                try
                {
                    codePage = Encoding.GetEncoding(cp);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                }

                break;
        }

        return i;
    }

    private static void Emit(StringBuilder output, char c, bool skip, ref int pendingSkipChars)
    {
        if (pendingSkipChars > 0)
        {
            pendingSkipChars--;
            return;
        }

        if (!skip)
        {
            output.Append(c);
        }
    }

    /// <summary>The fragment of a CF_HTML clipboard payload (between StartFragment and EndFragment), or the whole HTML.</summary>
    public static string HtmlFragment(string cfHtml)
    {
        var start = HeaderValue(cfHtml, "StartFragment");
        var end = HeaderValue(cfHtml, "EndFragment");
        if (start is { } s && end is { } e && e > s)
        {
            // Offsets are byte offsets into the UTF-8 payload.
            var bytes = Encoding.UTF8.GetBytes(cfHtml);
            if (e <= bytes.Length)
            {
                return Encoding.UTF8.GetString(bytes, s, e - s);
            }
        }

        var html = HeaderValue(cfHtml, "StartHTML") is { } h && h >= 0 && h < Encoding.UTF8.GetByteCount(cfHtml)
            ? Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cfHtml)[h..])
            : cfHtml;
        return html;
    }

    private static int? HeaderValue(string cfHtml, string name)
    {
        var match = Regex.Match(cfHtml, name + @":(-?\d+)", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    /// <summary>The visible text of an HTML fragment: block tags become newlines, entities decoded.</summary>
    public static string HtmlToText(string html)
    {
        var text = HiddenBlocks().Replace(html, string.Empty);
        text = Comments().Replace(text, string.Empty);
        text = Whitespace().Replace(text, " ");
        text = LineBreakTags().Replace(text, "\n");
        text = CellTags().Replace(text, "\t");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Split('\n').Select(l => l.Trim(' ')).ToList();
        return string.Join('\n', lines).Trim('\n', ' ');
    }

    /// <summary>
    /// Whether a copy's HTML flavour is nothing but the link itself (spec 06
    /// §3.5.2 step 5), so replacing the clipboard with the cleaned link loses nothing.
    /// </summary>
    public static bool HtmlIsOnlyLink(string html, string link)
    {
        if (Encoding.UTF8.GetByteCount(html) > 64 * 1024)
        {
            return false;
        }

        var lower = html.ToLowerInvariant();
        string[] media = ["<img", "<video", "<audio", "<picture", "<svg", "<iframe", "<object", "<embed"];
        if (media.Any(m => lower.Contains(m, StringComparison.Ordinal)))
        {
            return false;
        }

        var body = HeadLikeBlocks().Replace(html, string.Empty);
        var target = NormalizeAddress(link);
        var hrefs = Hrefs().Matches(body).Select(m => m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value)
            .Select(h => NormalizeAddress(h.Replace("&amp;", "&", StringComparison.Ordinal)))
            .ToList();
        if (hrefs.Any(h => h != target))
        {
            return false;
        }

        var visible = Tags().Replace(body, string.Empty).Replace("&amp;", "&", StringComparison.Ordinal).Trim();
        return hrefs.Count > 0 || visible.Length == 0 || visible == link;
    }

    /// <summary>Scheme and host lower-cased, an empty path becomes "/".</summary>
    public static string NormalizeAddress(string address)
    {
        var text = address.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return text;
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return text;
        }

        var afterScheme = text[(schemeEnd + 3)..];
        var pathStart = afterScheme.IndexOfAny(['/', '?', '#']);
        var authority = pathStart >= 0 ? afterScheme[..pathStart] : afterScheme;
        var rest = pathStart >= 0 ? afterScheme[pathStart..] : string.Empty;
        if (rest.Length == 0 || rest[0] != '/')
        {
            rest = "/" + rest;
        }

        return text[..schemeEnd].ToLowerInvariant() + "://" + authority.ToLowerInvariant() + rest;
    }

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadLikeBlocks();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|tr|h[1-6]|blockquote|pre|table|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellTags();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex("""href\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex Hrefs();
}
