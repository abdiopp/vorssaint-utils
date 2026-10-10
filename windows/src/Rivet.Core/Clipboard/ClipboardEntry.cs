// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Launcher;

namespace Rivet.Core.Clipboard;

public enum ClipboardEntryKind
{
    Text,
    Image,
    Files,
}

/// <summary>
/// One saved clipboard item (spec 06 §3.2.1). Immutable: the history replaces
/// entries with <c>with</c> copies so list snapshots handed to views never change.
/// </summary>
public sealed record ClipboardEntry
{
    public const int PreviewLimit = 2000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".heic", ".heif", ".tiff", ".tif", ".gif", ".webp", ".bmp", ".ico", ".icns", ".svg", ".avif",
    };

    public Guid Id { get; init; } = Guid.NewGuid();

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PinnedAt { get; init; }

    public ClipboardEntryKind Kind { get; init; } = ClipboardEntryKind.Text;

    /// <summary>Absolute paths in copy order (files entries).</summary>
    public IReadOnlyList<string> FilePaths { get; init; } = [];

    /// <summary><c>&lt;UUID&gt;.png</c> inside the image store (image entries).</summary>
    public string? ImageFile { get; init; }

    /// <summary>Lower-case hex SHA-256 of the PNG bytes (dedup).</summary>
    public string? ImageHash { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    /// <summary>App identity of the writer when known (lower-case executable path or AppUserModelID).</summary>
    public string? SourceApp { get; init; }

    public bool IsPinned => PinnedAt is not null;

    public string DimensionsText => $"{ImageWidth ?? 0}×{ImageHeight ?? 0}";

    /// <summary>One line for lists: newlines and tabs as spaces, at most 2,000 characters plus "…".</summary>
    public string Preview
    {
        get
        {
            switch (Kind)
            {
                case ClipboardEntryKind.Image:
                    return DimensionsText;
                case ClipboardEntryKind.Files:
                    return string.Join(", ", FilePaths.Select(FileName));
                default:
                    var cut = Text.Length > PreviewLimit;
                    var prefix = cut ? Text[..PreviewLimit] : Text;
                    var flat = prefix.Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ').Trim();
                    if (flat.Length == 0)
                    {
                        flat = prefix;
                    }

                    return cut ? flat + "…" : flat;
            }
        }
    }

    /// <summary>Like <see cref="Preview"/> but keeps line breaks (tabs become spaces).</summary>
    public string CardPreview
    {
        get
        {
            if (Kind != ClipboardEntryKind.Text)
            {
                return Preview;
            }

            var cut = Text.Length > PreviewLimit;
            var prefix = (cut ? Text[..PreviewLimit] : Text).Replace("\r\n", "\n").Replace('\t', ' ');
            var trimmed = prefix.Trim();
            if (trimmed.Length == 0)
            {
                trimmed = prefix;
            }

            return cut ? trimmed + "…" : trimmed;
        }
    }

    /// <summary>A colour swatch when the whole text is one colour value.</summary>
    public ColorValue? Color => Kind == ClipboardEntryKind.Text && Text.Length <= 96 && ColorValue.TryParse(Text, out var color) ? color : null;

    /// <summary>Single-line text cut to <paramref name="max"/> characters (tray tooltip).</summary>
    public string MenuBarText(int max, string imageLabel)
    {
        if (Kind == ClipboardEntryKind.Image)
        {
            return $"{imageLabel} · {DimensionsText}";
        }

        var builder = new StringBuilder(Preview.Length);
        foreach (var c in Preview)
        {
            builder.Append(c is '\n' or '\r' or (char)0x2028 or (char)0x2029 or '\u0085' or '\u000B' or '\u000C' ? ' ' : c);
        }

        var text = builder.ToString();
        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>What search matches against: the text, "Image png W×H", or the file names.</summary>
    public string SearchableText(string imageLabel) => Kind switch
    {
        ClipboardEntryKind.Image => $"{imageLabel} png {DimensionsText}",
        ClipboardEntryKind.Files => (FilePaths.Any(IsImagePath) ? imageLabel + " " : string.Empty) + string.Join(' ', FilePaths.Select(FileName)),
        _ => Text,
    };

    /// <summary>A single file that is an image (shown with a thumbnail).</summary>
    public bool IsSingleImageFile => Kind == ClipboardEntryKind.Files && FilePaths.Count == 1 && IsImagePath(FilePaths[0]);

    /// <summary>Same content as <paramref name="other"/> (dedup rule): equal text, image hash or ordered paths.</summary>
    public bool HasSameContent(ClipboardEntry other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            ClipboardEntryKind.Image => ImageHash is not null && string.Equals(ImageHash, other.ImageHash, StringComparison.Ordinal),
            ClipboardEntryKind.Files => FilePaths.SequenceEqual(other.FilePaths, StringComparer.Ordinal),
            _ => string.Equals(Text, other.Text, StringComparison.Ordinal),
        };
    }

    /// <summary>UTF-8 size of the text, for the 64 MiB text budget.</summary>
    public long TextBytes => Kind == ClipboardEntryKind.Text ? Encoding.UTF8.GetByteCount(Text) : 0;

    public static bool IsImagePath(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 && ImageExtensions.Contains(name[dot..]);
    }

    /// <summary>The last path component; splits on both separators so Windows paths work everywhere.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? trimmed : name;
    }
}
