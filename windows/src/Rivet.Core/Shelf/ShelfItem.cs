// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Shelf;

public enum ShelfItemKind
{
    File,
    Text,
    Link,

    /// <summary>A pile: several items dropped together or stacked.</summary>
    Batch,
}

/// <summary>
/// One shelf entry (spec 07 §3.1.12). Files are kept by reference (path plus a
/// file identity to find moved files again); pasted images, GIFs and received
/// virtual files are owned copies in the shelf's store. Identity is the id;
/// <see cref="Revision"/> changes when the same item's content changes
/// (thumbnail patched in, path healed) so its tile redraws.
/// </summary>
public sealed record ShelfItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required ShelfItemKind Kind { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>File path (kind File).</summary>
    public string? Path { get; init; }

    /// <summary>Text (kind Text), at most 200,000 characters.</summary>
    public string? Text { get; init; }

    /// <summary>Absolute non-file URL (kind Link).</summary>
    public string? Url { get; init; }

    /// <summary>Opaque file identity used to heal moved files (Windows: volume serial + file id).</summary>
    public string? Bookmark { get; init; }

    /// <summary>Children of a pile, in order.</summary>
    public IReadOnlyList<ShelfItem> Children { get; init; } = [];

    public bool Pinned { get; init; }

    public int Revision { get; init; }

    public bool IsPile => Kind == ShelfItemKind.Batch;

    public bool IsImage => Kind == ShelfItemKind.File && Path is not null && ShelfFileTypes.IsImage(Path);

    public static ShelfItem FileItem(string path, string? bookmark = null, string? title = null) => new()
    {
        Kind = ShelfItemKind.File,
        Path = path,
        Bookmark = bookmark,
        Title = title ?? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
    };

    /// <summary>A text item: truncated to 200,000 characters; null for whitespace-only text.</summary>
    public static ShelfItem? TextItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Length > ShelfConstants.MaxTextLength ? text[..ShelfConstants.MaxTextLength] : text;
        return new ShelfItem { Kind = ShelfItemKind.Text, Text = value, Title = TextTitle(value) };
    }

    /// <summary>A link item titled with the host (the whole URL without one); null for file: or unparsable URLs.</summary>
    public static ShelfItem? LinkItem(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.IsFile || uri.Scheme == Uri.UriSchemeFile)
        {
            return null;
        }

        return new ShelfItem { Kind = ShelfItemKind.Link, Url = uri.OriginalString, Title = HostOrUrl(uri) };
    }

    /// <summary>The host for URLs with an authority (https://host/…); the whole URL otherwise (mailto:, tel:…).</summary>
    public static string HostOrUrl(Uri uri)
    {
        var hasAuthority = uri.OriginalString.IndexOf("://", StringComparison.Ordinal) == uri.Scheme.Length;
        return hasAuthority && !string.IsNullOrEmpty(uri.Host) ? uri.Host : uri.OriginalString;
    }

    /// <summary>First line of the trimmed text, at most 48 characters.</summary>
    public static string TextTitle(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline >= 0 ? trimmed[..newline] : trimmed;
        if (line.Length <= ShelfConstants.TextTitleLength)
        {
            return line;
        }

        var cut = ShelfConstants.TextTitleLength;
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut];
    }

    /// <summary>Rebuilds an empty title from the payload (restores).</summary>
    public ShelfItem WithTitleFromPayload() => Title.Length > 0
        ? this
        : Kind switch
        {
            ShelfItemKind.File when Path is not null => this with { Title = System.IO.Path.GetFileName(Path) },
            ShelfItemKind.Text when Text is not null => this with { Title = TextTitle(Text) },
            ShelfItemKind.Link when Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) => this with { Title = HostOrUrl(uri) },
            _ => this,
        };
}

public static class ShelfFileTypes
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".ico", ".avif",
    };

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".raf", ".rw2", ".pef", ".srw",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv", ".webm", ".3gp", ".mpg", ".mpeg",
    };

    /// <summary>Images that get an inline thumbnail (RAW files go through the system thumbnailer).</summary>
    public static bool IsImage(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    public static bool IsRaw(string path) => RawExtensions.Contains(System.IO.Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(System.IO.Path.GetExtension(path));
}
