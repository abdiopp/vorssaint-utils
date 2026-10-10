// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Clipboard;

/// <summary>
/// The one search normalization shared by the clipboard history, the Command
/// Bar, the snippet library and the unit/date tokenizers (spec 06 §6.1).
/// Locale-independent on purpose: a Turkish locale must not turn I into ı.
/// Folding is NFKD, combining marks dropped, invariant lower case, NFC; NFKD
/// also folds full-width forms and ligatures, which Apple's width folding
/// keeps (an accepted difference).
/// </summary>
public static class TextFold
{
    /// <summary>Case, diacritic and width folding of a whole string.</summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (IsPlainLowerAscii(text))
        {
            return text;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToLowerInvariant().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Clipboard fold (<c>normC</c>): fold, then <c>\n</c> and <c>\t</c> become
    /// spaces and the ends are trimmed. Runs of spaces are kept.
    /// </summary>
    public static string ForClipboard(string text)
    {
        var folded = Fold(text);
        if (folded.Contains('\n') || folded.Contains('\t'))
        {
            folded = folded.Replace('\n', ' ').Replace('\t', ' ');
        }

        return folded.Trim();
    }

    /// <summary>
    /// Command Bar fold (<c>normB</c>): drop invisible format characters
    /// (category Cf at or above U+00AD), fold, then collapse every run of
    /// Unicode whitespace (including U+3000) into one space.
    /// </summary>
    public static string ForCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var stripped = text;
        if (HasFormatCharacters(text))
        {
            var builder = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (!(c >= '­' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
                {
                    builder.Append(c);
                }
            }

            stripped = builder.ToString();
        }

        var folded = Fold(stripped);
        return string.Join(' ', SplitWords(folded));
    }

    /// <summary>Splits on any Unicode whitespace, dropping empty parts.</summary>
    public static string[] SplitWords(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var words = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0)
                {
                    words.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            words.Add(text[start..]);
        }

        return words.ToArray();
    }

    /// <summary>
    /// Folds <paramref name="text"/> one character at a time and returns, for
    /// every character of the folded result, the index of the original
    /// character it came from. Used to highlight matches in the original text.
    /// </summary>
    public static (string Folded, int[] SourceIndex) FoldWithMap(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            var piece = Fold(text.Substring(i, length));
            foreach (var c in piece)
            {
                builder.Append(c);
                map.Add(i);
            }

            i += length;
        }

        return (builder.ToString(), map.ToArray());
    }

    private static bool IsPlainLowerAscii(string text)
    {
        foreach (var c in text)
        {
            if (c > 0x7F || c is >= 'A' and <= 'Z')
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasFormatCharacters(string text)
    {
        foreach (var c in text)
        {
            if (c >= '­' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }
}
