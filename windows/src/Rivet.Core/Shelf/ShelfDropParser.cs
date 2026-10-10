// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Shelf;

/// <summary>One dragged item with whatever representations the source offered.</summary>
public sealed record ShelfDropEntry
{
    public IReadOnlyList<string> Files { get; init; } = [];

    public byte[]? GifData { get; init; }

    /// <summary>Encoded image (PNG, JPEG, BMP…); stored as PNG.</summary>
    public byte[]? ImageData { get; init; }

    public string? Url { get; init; }

    public string? Text { get; init; }
}

public enum ShelfDropPartKind
{
    File,
    Gif,
    Image,
    Link,
    Text,
}

/// <summary>A parsed piece of a drop. GIF and image data become owned files in the store.</summary>
public sealed record ShelfDropPart(ShelfDropPartKind Kind, ShelfItem? Item = null, byte[]? Data = null);

/// <summary>
/// Turns a drop into shelf items (spec 07 §3.1.10), first match per dragged
/// item: file paths (by reference, de-duplicated by standardized path) → GIF
/// data → other image data → the first non-file URL → plain text (≤ 200,000
/// characters; whitespace-only rejected). Rich text or HTML without a plain
/// text flavour is ignored. A browser's virtual ".url" shortcut together with
/// a URL yields a link, as on macOS.
/// </summary>
public static class ShelfDropParser
{
    public static bool CanAccept(IReadOnlyList<ShelfDropEntry> entries) => entries.Any(e =>
        e.Files.Count > 0 || e.GifData is { Length: > 0 } || e.ImageData is { Length: > 0 } || IsWebUrl(e.Url) || !string.IsNullOrWhiteSpace(e.Text));

    public static IReadOnlyList<ShelfDropPart> Parse(IReadOnlyList<ShelfDropEntry> entries)
    {
        var parts = new List<ShelfDropPart>();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var files = entry.Files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

            // A virtual .url shortcut file next to a real URL: prefer the link.
            if (IsWebUrl(entry.Url) && files.Count > 0 && files.All(f => f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
            {
                files.Clear();
            }

            if (files.Count > 0)
            {
                foreach (var file in files)
                {
                    var standardized = Standardize(file);
                    if (seenPaths.Add(standardized))
                    {
                        parts.Add(new ShelfDropPart(ShelfDropPartKind.File, ShelfItem.FileItem(standardized)));
                    }
                }

                continue;
            }

            if (entry.GifData is { Length: > 0 } gif)
            {
                parts.Add(new ShelfDropPart(ShelfDropPartKind.Gif, Data: gif));
                continue;
            }

            if (entry.ImageData is { Length: > 0 } image)
            {
                parts.Add(new ShelfDropPart(ShelfDropPartKind.Image, Data: image));
                continue;
            }

            if (IsWebUrl(entry.Url) && ShelfItem.LinkItem(entry.Url!) is { } link)
            {
                parts.Add(new ShelfDropPart(ShelfDropPartKind.Link, link));
                continue;
            }

            if (entry.Text is { } text && ShelfItem.TextItem(text) is { } note)
            {
                // A dragged link often arrives as text only.
                if (LooksLikeSingleUrl(text) && ShelfItem.LinkItem(text.Trim()) is { } textLink)
                {
                    parts.Add(new ShelfDropPart(ShelfDropPartKind.Link, textLink));
                }
                else
                {
                    parts.Add(new ShelfDropPart(ShelfDropPartKind.Text, note));
                }
            }
        }

        return parts;
    }

    /// <summary>Full path, no trailing separator (except for roots).</summary>
    public static string Standardize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static bool IsWebUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme != Uri.UriSchemeFile;

    private static bool LooksLikeSingleUrl(string text)
    {
        var trimmed = text.Trim();
        return !trimmed.Any(char.IsWhiteSpace)
               && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https";
    }
}
