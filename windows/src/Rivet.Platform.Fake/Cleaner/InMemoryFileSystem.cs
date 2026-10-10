// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Rivet.Core.Maintenance.Cleaner;

namespace Rivet.Platform.Fake.Cleaner;

/// <summary>
/// A Windows-style file tree held in memory, so the development build runs
/// the real Cleaner and Uninstaller scans on believable data without reading
/// or changing anything on the host disk.
/// </summary>
public sealed class InMemoryFileSystem : ICleanerFileSystem
{
    private const long Mb = 1024L * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextId = 1000;

    public InMemoryFileSystem()
    {
        SampleProfile.Populate(this);
    }

    public IReadOnlyList<FsEntry> List(string folder)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(Key(folder), out var node) && node.IsDirectory
                ? node.Children.Values.Select(c => c.ToEntry()).ToList()
                : [];
        }
    }

    public FsEntry? Stat(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(Key(path), out var node) ? node.ToEntry() : null;
        }
    }

    public FileIdentity? Identity(string path)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(Key(path), out var node) ? new FileIdentity(0xC0FFEE, 0, node.Id) : null;
        }
    }

    public TreeMeasure Measure(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(Key(path), out var node) || node.IsLink)
            {
                return new TreeMeasure(0, 0, DateTime.MinValue, node?.IsLink ?? false);
            }

            long bytes = 0;
            long files = 0;
            var newest = node.Written;
            var stack = new Stack<Node>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.Written > newest)
                {
                    newest = current.Written;
                }

                if (current.IsLink)
                {
                    continue;
                }

                if (current.IsDirectory)
                {
                    foreach (var child in current.Children.Values)
                    {
                        stack.Push(child);
                    }
                }
                else
                {
                    files++;
                    bytes += current.Size;
                }
            }

            return new TreeMeasure(bytes, files, newest, false);
        }
    }

    public bool IsInUse(string path)
    {
        lock (_gate)
        {
            return _inUse.Contains(Key(path));
        }
    }

    public string? ReadText(string path, int maxBytes) => ReadBytes(path, maxBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public byte[]? ReadBytes(string path, int maxBytes)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(Key(path), out var node) && node.Content is { } content && content.Length <= maxBytes ? content : null;
        }
    }

    /// <summary>Removes a subtree (the fake Recycle Bin and uninstallers use this). False when missing.</summary>
    public bool Remove(string path)
    {
        lock (_gate)
        {
            var key = Key(path);
            if (!_nodes.TryGetValue(key, out var node))
            {
                return false;
            }

            if (_nodes.TryGetValue(Key(SafePaths.Parent(key) ?? key), out var parent))
            {
                parent.Children.Remove(node.Name);
            }

            foreach (var stale in _nodes.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || SafePaths.IsInside(k, key)).ToList())
            {
                _nodes.Remove(stale);
            }

            return true;
        }
    }

    public void Folder(string path, double ageDays = 30)
    {
        lock (_gate)
        {
            Ensure(Key(path), isDirectory: true, ageDays);
        }
    }

    public void File(string path, long size, double ageDays = 30, bool inUse = false, byte[]? content = null)
    {
        lock (_gate)
        {
            var node = Ensure(Key(path), isDirectory: false, ageDays);
            node.Size = content?.Length ?? size;
            node.Content = content;
            if (inUse)
            {
                _inUse.Add(Key(path));
            }
        }
    }

    public void FileMb(string path, double megabytes, double ageDays = 30, bool inUse = false) =>
        File(path, (long)(megabytes * Mb), ageDays, inUse);

    public void Link(string path)
    {
        lock (_gate)
        {
            Ensure(Key(path), isDirectory: true, 30).IsLink = true;
        }
    }

    private static string Key(string path) => SafePaths.Normalize(path);

    private Node Ensure(string key, bool isDirectory, double ageDays)
    {
        if (_nodes.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var parentKey = SafePaths.Parent(key);
        Node? parent = null;
        if (parentKey is not null && !parentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            parent = Ensure(parentKey, isDirectory: true, ageDays);
        }

        var time = DateTime.UtcNow.AddDays(-ageDays);
        var node = new Node(key, SafePaths.FileName(key) is { Length: > 0 } name ? name : key, isDirectory, _nextId++, time);
        _nodes[key] = node;
        parent?.Children.TryAdd(node.Name, node);
        return node;
    }

    /// <summary>A minimal shortcut file pointing at <paramref name="target"/> (Unicode link info).</summary>
    public static byte[] Shortcut(string target)
    {
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0x2u | 0x80u);
        var volume = new byte[0x11];
        BinaryPrimitives.WriteUInt32LittleEndian(volume, 0x11);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(volume.AsSpan(12), 0x10);
        var ansi = Encoding.ASCII.GetBytes(target + "\0");
        var wide = Encoding.Unicode.GetBytes(target + "\0");
        const int headerSize = 0x24;
        var baseOffset = headerSize + volume.Length;
        var suffixOffset = baseOffset + ansi.Length;
        var wideOffset = suffixOffset + 1;
        var wideSuffixOffset = wideOffset + wide.Length;
        var size = wideSuffixOffset + 2;
        var info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(12), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), (uint)baseOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)suffixOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(28), (uint)wideOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32), (uint)wideSuffixOffset);
        volume.CopyTo(info, headerSize);
        ansi.CopyTo(info, baseOffset);
        wide.CopyTo(info, wideOffset);
        return [.. header, .. info, 0, 0, 0, 0];
    }

    private sealed class Node(string path, string name, bool isDirectory, ulong id, DateTime written)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public bool IsDirectory { get; } = isDirectory;

        public ulong Id { get; } = id;

        public DateTime Written { get; } = written;

        public long Size { get; set; }

        public byte[]? Content { get; set; }

        public bool IsLink { get; set; }

        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FsEntry ToEntry()
        {
            var flags = (IsDirectory ? EntryFlags.Directory : EntryFlags.None) | (IsLink ? EntryFlags.ReparsePoint : EntryFlags.None);
            return new FsEntry(Path, Name, flags, IsDirectory ? 0 : Size, Written, Written);
        }
    }
}
