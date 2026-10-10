// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.App;

namespace Rivet.Core.Clipboard;

/// <summary>
/// Windows clipboard format names and the policies built on them (spec 06
/// §7.2). Predefined formats are reported by the platform with their CF_ name
/// ("CF_UNICODETEXT"); registered ones by their registered name.
/// </summary>
public static class ClipboardFormats
{
    public const string UnicodeText = "CF_UNICODETEXT";
    public const string Text = "CF_TEXT";
    public const string OemText = "CF_OEMTEXT";
    public const string Locale = "CF_LOCALE";
    public const string Hdrop = "CF_HDROP";
    public const string Html = "HTML Format";
    public const string Rtf = "Rich Text Format";
    public const string Png = "PNG";
    public const string UrlW = "UniformResourceLocatorW";
    public const string Url = "UniformResourceLocator";
    public const string MozUrl = "text/x-moz-url";

    /// <summary>Password managers mark secrets with these; such copies are never read.</summary>
    public const string ExcludeFromMonitors = "ExcludeClipboardContentFromMonitorProcessing";

    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";
    public const string ViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>Private marker on every write this app makes (the Windows stand-in for org.nspasteboard.source).</summary>
    public static string OwnSource => $"{AppIdentity.Id}.Source";

    /// <summary>Formats that only describe the plain text (synthesized or locale info).</summary>
    private static readonly HashSet<string> PlainFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        UnicodeText, Text, OemText, Locale, UrlW, Url, MozUrl,
    };

    /// <summary>
    /// Browser notes that only record where a copy came from; losing them on
    /// a rewrite loses nothing the user pasted.
    /// </summary>
    private static readonly HashSet<string> SourceNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chromium internal source URL", "Chromium internal source RFH token", "msSourceUrl",
        "text/x-moz-url-priv", "text/_moz_htmlcontext", "text/_moz_htmlinfo", "text/x-moz-url-desc",
    };

    private static readonly HashSet<string> MediaFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        Hdrop, "CF_DIB", "CF_DIBV5", "CF_BITMAP", "CF_ENHMETAFILE", "CF_METAFILEPICT", "CF_TIFF",
        "CF_WAVE", "CF_RIFF", "CF_PALETTE", Png, "JFIF", "GIF", "image/png", "image/jpeg", "image/gif",
        "image/bmp", "image/svg+xml", "image/webp", "FileGroupDescriptorW", "FileGroupDescriptor",
        "FileContents", "Shell IDList Array", "FileName", "FileNameW", "Portable Document Format",
    };

    /// <summary>The writer marked the copy as secret or not for clipboard managers (spec 06 §7.2).</summary>
    public static bool IsConcealed(IEnumerable<string> formats, Func<string, uint?> readDword)
    {
        foreach (var format in formats)
        {
            if (format.Equals(ExcludeFromMonitors, StringComparison.OrdinalIgnoreCase)
                || format.Equals(ViewerIgnore, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (format.Equals(CanIncludeInHistory, StringComparison.OrdinalIgnoreCase) && readDword(format) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the clipboard may be replaced by a cleaned link without losing
    /// anything: only text, link, rich-text and source-note formats. Unknown
    /// registered formats are refused (Windows apps keep real content in
    /// private formats), which is stricter than the macOS app.
    /// </summary>
    public static bool CanRewrite(IReadOnlyCollection<string> formats)
    {
        if (formats.Count == 0)
        {
            return false;
        }

        foreach (var format in formats)
        {
            if (PlainFormats.Contains(format) || SourceNotes.Contains(format)
                || format.Equals(Html, StringComparison.OrdinalIgnoreCase)
                || format.Equals(Rtf, StringComparison.OrdinalIgnoreCase)
                || format.Equals(OwnSource, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>A file, image, audio or document copy: plain-text paste forwards it untouched.</summary>
    public static bool HasMedia(IEnumerable<string> formats) =>
        formats.Any(f => MediaFormats.Contains(f) || f.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                         || f.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || f.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
}
