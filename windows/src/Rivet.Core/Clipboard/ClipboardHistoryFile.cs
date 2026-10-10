// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Clipboard;

/// <summary>
/// <c>ClipboardHistory.json</c> (spec 06 §5.1): a JSON array, pinned entries
/// first, dates as seconds since 2001-01-01 (the Swift encoding), at most
/// 96 MiB. Written atomically; the file inherits the per-user ACL of
/// %LOCALAPPDATA%. Contents never reach the log.
/// </summary>
public static class ClipboardHistoryFile
{
    public const string FileName = "ClipboardHistory.json";
    public const string ImageFolderName = "ClipboardImages";

    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static double ToSwiftSeconds(DateTimeOffset date) => (date - ReferenceDate).TotalSeconds;

    public static DateTimeOffset FromSwiftSeconds(double seconds) =>
        double.IsFinite(seconds) ? ReferenceDate.AddSeconds(Math.Clamp(seconds, -1e10, 1e10)) : DateTimeOffset.UtcNow;

    /// <summary>The UTF-8 size of one encoded entry (plus its separating comma).</summary>
    public static long EncodedSize(ClipboardEntry entry) => Encode(entry).Length + 1;

    public static byte[] Encode(ClipboardEntry entry)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + (entry.Text.Length * 2));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id.ToString("D").ToUpperInvariant());
            writer.WriteString("text", entry.Text);
            writer.WriteNumber("copiedAt", ToSwiftSeconds(entry.CopiedAt));
            if (entry.PinnedAt is { } pinned)
            {
                writer.WriteNumber("pinnedAt", ToSwiftSeconds(pinned));
            }

            writer.WriteString("kind", entry.Kind switch
            {
                ClipboardEntryKind.Image => "image",
                ClipboardEntryKind.Files => "files",
                _ => "text",
            });
            writer.WriteStartArray("filePaths");
            foreach (var path in entry.FilePaths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            if (entry.ImageFile is { } file)
            {
                writer.WriteString("imageFile", file);
            }

            if (entry.ImageHash is { } hash)
            {
                writer.WriteString("imageHash", hash);
            }

            if (entry.ImageWidth is { } width)
            {
                writer.WriteNumber("imageWidth", width);
            }

            if (entry.ImageHeight is { } height)
            {
                writer.WriteNumber("imageHeight", height);
            }

            if (entry.SourceApp is { } source)
            {
                writer.WriteString("sourceBundleID", source);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Encodes pinned entries first, then recent, skipping any entry whose
    /// encoding would push the file past <paramref name="budget"/>.
    /// Returns the bytes and the entries that were actually written.
    /// </summary>
    public static (byte[] Bytes, IReadOnlyList<ClipboardEntry> Written) EncodeAll(IReadOnlyList<ClipboardEntry> entries, long budget = ClipboardHistory.FileBudgetBytes)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'[');
        var written = new List<ClipboardEntry>(entries.Count);
        foreach (var entry in entries.Where(e => e.IsPinned).Concat(entries.Where(e => !e.IsPinned)))
        {
            var bytes = Encode(entry);
            var extra = bytes.Length + (written.Count > 0 ? 1 : 0);
            if (stream.Length + extra + 1 > budget)
            {
                continue;
            }

            if (written.Count > 0)
            {
                stream.WriteByte((byte)',');
            }

            stream.Write(bytes);
            written.Add(entry);
        }

        stream.WriteByte((byte)']');
        return (stream.ToArray(), written);
    }

    public static IReadOnlyList<ClipboardEntry> Decode(ReadOnlySpan<byte> json)
    {
        var entries = new List<ClipboardEntry>();
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true, MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && DecodeEntry(element) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return entries;
    }

    private static ClipboardEntry? DecodeEntry(JsonElement element)
    {
        var id = element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String && Guid.TryParse(idElement.GetString(), out var parsed)
            ? parsed
            : Guid.NewGuid();
        var kind = element.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
            ? kindElement.GetString() switch
            {
                "image" => ClipboardEntryKind.Image,
                "files" => ClipboardEntryKind.Files,
                _ => ClipboardEntryKind.Text,
            }
            : ClipboardEntryKind.Text;
        var entry = new ClipboardEntry
        {
            Id = id,
            Kind = kind,
            Text = String(element, "text") ?? string.Empty,
            CopiedAt = Number(element, "copiedAt") is { } copied ? FromSwiftSeconds(copied) : DateTimeOffset.UtcNow,
            PinnedAt = Number(element, "pinnedAt") is { } pinned ? FromSwiftSeconds(pinned) : null,
            FilePaths = element.TryGetProperty("filePaths", out var paths) && paths.ValueKind == JsonValueKind.Array
                ? paths.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList()
                : [],
            ImageFile = SafeImageName(String(element, "imageFile")),
            ImageHash = String(element, "imageHash"),
            ImageWidth = Number(element, "imageWidth") is { } w ? (int)w : null,
            ImageHeight = Number(element, "imageHeight") is { } h ? (int)h : null,
            SourceApp = String(element, "sourceBundleID"),
        };

        return kind switch
        {
            ClipboardEntryKind.Image when entry.ImageFile is null => null,
            ClipboardEntryKind.Files when entry.FilePaths.Count == 0 => null,
            ClipboardEntryKind.Text when entry.Text.Length == 0 => null,
            _ => entry,
        };
    }

    /// <summary>Only plain <c>name.png</c> file names, never a path that could escape the image folder.</summary>
    private static string? SafeImageName(string? name) =>
        name is not null && name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != ".." ? name : null;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    /// <summary>Reads the file; a symlink, a non-regular file, an oversized or unreadable file gives an empty history.</summary>
    public static IReadOnlyList<ClipboardEntry> Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || info.Length > ClipboardHistory.FileBudgetBytes)
            {
                return [];
            }

            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("clipboard", "The clipboard history file could not be read.", ex);
            return [];
        }
    }

    /// <summary>Atomic write: temporary file, then replace.</summary>
    public static bool Write(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("clipboard", "The clipboard history file could not be written.", ex);
            return false;
        }
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>The <c>ClipboardImages</c> folder: one <c>&lt;UUID&gt;.png</c> per image entry.</summary>
public sealed class ClipboardImageStore(string folder)
{
    public string Folder { get; } = folder;

    public string PathOf(string fileName) => Path.Combine(Folder, fileName);

    public static string NewName() => Guid.NewGuid().ToString("D").ToUpperInvariant() + ".png";

    /// <summary>Writes the PNG under <paramref name="name"/> (atomically). False on failure.</summary>
    public bool Save(string name, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(name);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("clipboard", "A clipboard image could not be saved.", ex);
            return false;
        }
    }

    public byte[]? Read(string fileName)
    {
        try
        {
            var path = PathOf(fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Exists(string fileName) => File.Exists(PathOf(fileName));

    /// <summary>Deletes every image no entry references.</summary>
    public void Sweep(IEnumerable<string> referenced)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var keep = referenced.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder))
            {
                var name = Path.GetFileName(file);
                var isTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (isTemp && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-1))
                {
                    continue; // a save in progress
                }

                if (!keep.Contains(name))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("clipboard", "Sweeping clipboard images failed.", ex);
        }
    }
}
