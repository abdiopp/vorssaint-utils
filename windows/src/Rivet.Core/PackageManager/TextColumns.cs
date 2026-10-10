// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>
/// Terminal display widths. winget pads its tables by display width (ICU
/// East Asian Width: wide and fullwidth characters take two cells,
/// combining marks none), so columns must be sliced by cells, not chars.
/// </summary>
public static class TextColumns
{
    public static int Width(Rune rune)
    {
        var value = rune.Value;
        if (value < 0x20)
        {
            return 0;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
    }

    public static int Width(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += Width(rune);
        }

        return width;
    }

    /// <summary>The text between display columns [<paramref name="start"/>, <paramref name="end"/>); end -1 means to the end.</summary>
    public static string Slice(string text, int start, int end)
    {
        var builder = new StringBuilder();
        var column = 0;
        var lastIncluded = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var width = Width(rune);

            // A character belongs to the column where it starts; a zero-width
            // mark stays with the character before it.
            var include = width == 0 ? lastIncluded : column >= start && (end < 0 || column < end);
            if (include)
            {
                builder.Append(rune.ToString());
            }

            lastIncluded = include;
            column += width;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether the cell at display column <paramref name="column"/> is blank:
    /// a space, or past the end of the line. A wide character that covers the
    /// column is not blank.
    /// </summary>
    public static bool IsBlankAt(string text, int column)
    {
        var position = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var width = Width(rune);
            if (width > 0 && column >= position && column < position + width)
            {
                return Rune.IsWhiteSpace(rune);
            }

            position += width;
            if (position > column)
            {
                break;
            }
        }

        return position <= column;
    }

    /// <summary>Display columns where each space-separated token of <paramref name="header"/> starts.</summary>
    public static IReadOnlyList<int> TokenStarts(string header)
    {
        var starts = new List<int>();
        var column = 0;
        var previousWasSpace = true;
        foreach (var rune in header.EnumerateRunes())
        {
            var isSpace = Rune.IsWhiteSpace(rune);
            if (!isSpace && previousWasSpace)
            {
                starts.Add(column);
            }

            previousWasSpace = isSpace;
            column += Width(rune);
        }

        return starts;
    }

    private static bool IsWide(int c) =>
        c is (>= 0x1100 and <= 0x115F)
            or (>= 0x231A and <= 0x231B)
            or (>= 0x2329 and <= 0x232A)
            or (>= 0x23E9 and <= 0x23EC)
            or (>= 0x2E80 and <= 0x303E)
            or (>= 0x3041 and <= 0x33FF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xA000 and <= 0xA4CF)
            or (>= 0xA960 and <= 0xA97F)
            or (>= 0xAC00 and <= 0xD7A3)
            or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE10 and <= 0xFE19)
            or (>= 0xFE30 and <= 0xFE6F)
            or (>= 0xFF00 and <= 0xFF60)
            or (>= 0xFFE0 and <= 0xFFE6)
            or (>= 0x1F300 and <= 0x1F64F)
            or (>= 0x1F900 and <= 0x1F9FF)
            or (>= 0x20000 and <= 0x2FFFD)
            or (>= 0x30000 and <= 0x3FFFD);
}
