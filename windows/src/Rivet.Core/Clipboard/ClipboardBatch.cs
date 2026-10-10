// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;

namespace Rivet.Core.Clipboard;

/// <summary>
/// What a single entry or a batch of entries writes to the clipboard (spec 06
/// §3.2.11). Stale content (a missing image file, no existing file) aborts
/// the write so the user's clipboard stays intact.
/// </summary>
public static class ClipboardBatch
{
    /// <summary>
    /// Builds the write data; null when the content is stale.
    /// <paramref name="entries"/> must be in history order.
    /// </summary>
    public static ClipboardWriteData? Build(IReadOnlyList<ClipboardEntry> entries, Func<string, byte[]?> readImage, Func<string, bool> fileExists)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        if (entries.Count == 1)
        {
            var entry = entries[0];
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Image:
                    var png = entry.ImageFile is { } file ? readImage(file) : null;
                    return png is null ? null : new ClipboardWriteData { Png = png };
                case ClipboardEntryKind.Files:
                    var existing = entry.FilePaths.Where(fileExists).ToList();
                    return existing.Count == 0 ? null : new ClipboardWriteData { Files = existing };
                default:
                    return ClipboardWriteData.FromText(entry.Text);
            }
        }

        if (entries.All(e => e.Kind == ClipboardEntryKind.Files))
        {
            var paths = entries.SelectMany(e => e.FilePaths).Where(fileExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return paths.Count == 0 ? null : new ClipboardWriteData { Files = paths };
        }

        if (entries.Any(e => e.Kind == ClipboardEntryKind.Image))
        {
            var html = new StringBuilder();
            var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0 Segoe UI;}}\f0 ");
            var plain = new List<string>();
            foreach (var entry in entries)
            {
                switch (entry.Kind)
                {
                    case ClipboardEntryKind.Image:
                        var png = entry.ImageFile is { } file ? readImage(file) : null;
                        if (png is null)
                        {
                            return null;
                        }

                        html.Append("<img src=\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\"><br>");
                        rtf.Append(@"{\pict\pngblip\picw").Append(entry.ImageWidth ?? 0).Append(@"\pich").Append(entry.ImageHeight ?? 0).Append(' ')
                            .Append(Convert.ToHexString(png)).Append(@"}\par ");
                        break;
                    case ClipboardEntryKind.Files:
                        var paths = string.Join("\n", entry.FilePaths);
                        html.Append(Escape(paths).Replace("\n", "<br>")).Append("<br>");
                        rtf.Append(RtfEscape(paths)).Append(@"\par ");
                        plain.Add(paths);
                        break;
                    default:
                        html.Append(Escape(entry.Text).Replace("\n", "<br>")).Append("<br>");
                        rtf.Append(RtfEscape(entry.Text)).Append(@"\par ");
                        plain.Add(entry.Text);
                        break;
                }
            }

            rtf.Append('}');
            return new ClipboardWriteData { HtmlFragment = html.ToString(), Rtf = rtf.ToString(), Text = string.Join("\n", plain) };
        }

        var texts = entries.Select(e => e.Kind == ClipboardEntryKind.Files ? string.Join("\n", e.FilePaths) : e.Text);
        return ClipboardWriteData.FromText(string.Join("\n", texts));
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    /// <summary>RTF text with \, {, } escaped, line breaks as \line and non-ASCII as \uN?.</summary>
    public static string RtfEscape(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            switch (c)
            {
                case '\\' or '{' or '}':
                    builder.Append('\\').Append(c);
                    break;
                case '\n':
                    builder.Append(@"\line ");
                    break;
                case '\t':
                    builder.Append(@"\tab ");
                    break;
                default:
                    if (c > 0x7F)
                    {
                        builder.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
