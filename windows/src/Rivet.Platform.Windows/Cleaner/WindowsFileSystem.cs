// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Maintenance.Cleaner;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Rivet.Platform.Windows.Cleaner;

/// <summary>
/// The file system for the Cleaner and the Uninstaller. Directories are read
/// with <c>GetFileInformationByHandleEx(FileIdBothDirectoryInfo)</c>, which
/// returns allocation sizes, attributes and file ids in one pass; handles are
/// opened with FILE_FLAG_OPEN_REPARSE_POINT so links are never followed.
/// </summary>
public sealed unsafe class WindowsFileSystem : ICleanerFileSystem
{
    private const uint AttributeReadOnly = 0x1;
    private const uint AttributeHidden = 0x2;
    private const uint AttributeSystem = 0x4;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const uint AttributeOffline = 0x1000;
    private const uint AttributeRecallOnOpen = 0x40000;
    private const uint AttributeRecallOnDataAccess = 0x400000;
    private const uint ErrorNoMoreFiles = 18;
    private const uint ErrorSharingViolation = 32;
    private const uint ErrorLockViolation = 33;
    private const uint Synchronize = 0x00100000;

    private const FILE_SHARE_MODE ShareAll = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;
    private const FILE_FLAGS_AND_ATTRIBUTES NoFollow = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;

    public IReadOnlyList<FsEntry> List(string folder)
    {
        var entries = new List<FsEntry>();
        Enumerate(folder, raw => entries.Add(raw.ToEntry(folder)));
        return entries;
    }

    public FsEntry? Stat(string path)
    {
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                info = new DirectoryInfo(path);
                if (!info.Exists)
                {
                    return null;
                }
            }

            // FileInfo/DirectoryInfo read the entry itself, not a link's target.
            var attributes = (uint)info.Attributes;
            var flags = Flags(attributes);
            var length = info is FileInfo file && (attributes & AttributeDirectory) == 0 ? file.Length : 0;
            return new FsEntry(SafePaths.Normalize(path), info.Name, flags, length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public FileIdentity? Identity(string path)
    {
        using var handle = OpenNoFollow(path, (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES);
        if (handle is null)
        {
            return null;
        }

        FILE_ID_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, new Span<byte>(&info, sizeof(FILE_ID_INFO))))
        {
            return null;
        }

        var id = new ReadOnlySpan<byte>(&info.FileId, 16);
        return new FileIdentity(info.VolumeSerialNumber, BitConverter.ToUInt64(id[8..]), BitConverter.ToUInt64(id[..8]));
    }

    public TreeMeasure Measure(string path, CancellationToken cancellationToken)
    {
        var root = Stat(path);
        if (root is null || root.IsReparsePoint)
        {
            return new TreeMeasure(0, 0, root?.LastWriteUtc ?? DateTime.MinValue, root?.IsReparsePoint ?? false);
        }

        if (!root.IsDirectory)
        {
            // Allocated size of a single file (a cloud placeholder occupies nothing locally).
            var allocated = root.IsCloudPlaceholder ? 0 : AllocatedSize(path, root.Length);
            return new TreeMeasure(allocated, 1, root.LastWriteUtc, false);
        }

        long bytes = 0;
        long files = 0;
        var newest = root.LastWriteUtc;
        var links = false;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = stack.Pop();
            Enumerate(folder, raw =>
            {
                var written = raw.LastWriteUtc;
                if (written > newest)
                {
                    newest = written;
                }

                if ((raw.Attributes & AttributeReparsePoint) != 0)
                {
                    links = true;
                    return;
                }

                if ((raw.Attributes & AttributeDirectory) != 0)
                {
                    stack.Push(Path.Combine(folder, raw.Name));
                }
                else
                {
                    files++;
                    bytes += raw.AllocationSize;
                }
            });
        }

        return new TreeMeasure(bytes, files, newest, links);
    }

    public bool IsInUse(string path)
    {
        // Asking for delete access while allowing everything else fails only when another
        // process holds the file without sharing deletion.
        var handle = PInvoke.CreateFile(path, (uint)FILE_ACCESS_RIGHTS.DELETE, ShareAll, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, NoFollow, null);
        if (!handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        var error = (uint)Marshal.GetLastPInvokeError();
        handle.Dispose();
        return error is ErrorSharingViolation or ErrorLockViolation;
    }

    public string? ReadText(string path, int maxBytes) =>
        ReadBytes(path, maxBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public byte[]? ReadBytes(string path, int maxBytes)
    {
        var entry = Stat(path);
        if (entry is null || entry.IsDirectory || entry.IsReparsePoint || entry.IsCloudPlaceholder || entry.Length > maxBytes)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, maxBytes)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static EntryFlags Flags(uint attributes)
    {
        var flags = EntryFlags.None;
        if ((attributes & AttributeDirectory) != 0)
        {
            flags |= EntryFlags.Directory;
        }

        if ((attributes & AttributeHidden) != 0)
        {
            flags |= EntryFlags.Hidden;
        }

        if ((attributes & AttributeSystem) != 0)
        {
            flags |= EntryFlags.System;
        }

        if ((attributes & AttributeReparsePoint) != 0)
        {
            flags |= EntryFlags.ReparsePoint;
        }

        if ((attributes & (AttributeOffline | AttributeRecallOnOpen | AttributeRecallOnDataAccess)) != 0)
        {
            flags |= EntryFlags.CloudPlaceholder;
        }

        if ((attributes & AttributeReadOnly) != 0)
        {
            flags |= EntryFlags.ReadOnly;
        }

        return flags;
    }

    private static SafeFileHandle? OpenNoFollow(string path, uint access)
    {
        var handle = PInvoke.CreateFile(path, access, ShareAll, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, NoFollow, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static long AllocatedSize(string path, long fallback)
    {
        using var handle = OpenNoFollow(path, (uint)FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES);
        if (handle is null)
        {
            return fallback;
        }

        // FILE_STANDARD_INFO: AllocationSize, EndOfFile, NumberOfLinks, DeletePending, Directory.
        var buffer = stackalloc byte[24];
        return PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileStandardInfo, new Span<byte>(buffer, 24))
            ? *(long*)buffer
            : fallback;
    }

    /// <summary>Calls <paramref name="visit"/> for every entry of a folder (never "." or "..").</summary>
    private static void Enumerate(string folder, Action<RawEntry> visit)
    {
        using var handle = OpenNoFollow(folder, (uint)FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY | Synchronize);
        if (handle is null)
        {
            return;
        }

        const int size = 64 * 1024;
        var buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            while (PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdBothDirectoryInfo, new Span<byte>(buffer, size)))
            {
                var offset = 0;
                while (true)
                {
                    var info = (FILE_ID_BOTH_DIR_INFO*)(buffer + offset);
                    var name = new string((char*)&info->FileName, 0, (int)(info->FileNameLength / 2));
                    if (name is not ("." or ".."))
                    {
                        visit(new RawEntry(name, info->FileAttributes, info->EndOfFile, info->AllocationSize, info->LastWriteTime, info->CreationTime));
                    }

                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    offset += (int)info->NextEntryOffset;
                    if (offset >= size)
                    {
                        break;
                    }
                }
            }

            var error = (uint)Marshal.GetLastPInvokeError();
            if (error != ErrorNoMoreFiles && error != 0)
            {
                Rivet.Core.Diagnostics.Log.Debug("cleaner", $"Listing {folder} stopped with error {error}.");
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private readonly record struct RawEntry(string Name, uint Attributes, long EndOfFile, long AllocationSize, long LastWriteTicks, long CreationTicks)
    {
        public DateTime LastWriteUtc => ToUtc(LastWriteTicks);

        public FsEntry ToEntry(string folder) =>
            new(Path.Combine(folder, Name), Name, Flags(Attributes), (Attributes & AttributeDirectory) != 0 ? 0 : EndOfFile, LastWriteUtc, ToUtc(CreationTicks));

        private static DateTime ToUtc(long fileTime)
        {
            try
            {
                return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.MinValue;
            }
        }
    }
}
