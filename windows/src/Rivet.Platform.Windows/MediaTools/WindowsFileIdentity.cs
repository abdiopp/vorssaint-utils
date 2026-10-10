// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Modules.MediaTools;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace Rivet.Platform.Windows.MediaTools;

/// <summary>
/// Same-file checks by volume serial + 128-bit file id (hard links and
/// symlinks resolve to the same id), hidden-flag clearing, and moves that retry
/// briefly when antivirus or the indexer holds a new file.
/// </summary>
public sealed unsafe class WindowsFileIdentity : IFileIdentity
{
    public bool AreSameFile(string first, string second)
    {
        var a = Identity(first);
        var b = Identity(second);
        if (a is not null && b is not null)
        {
            return a == b;
        }

        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public void ClearHidden(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Hidden))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Move(string source, string destination, bool replace)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, replace);
                return;
            }
            catch (IOException) when (attempt < 5 && File.Exists(source) && (replace || !File.Exists(destination)))
            {
                Thread.Sleep(150);
            }
        }
    }

    private static string? Identity(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        using SafeFileHandle handle = PInvoke.CreateFile(path, 0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid)
        {
            return null;
        }

        FILE_ID_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, new Span<byte>(&info, sizeof(FILE_ID_INFO))))
        {
            return null;
        }

        return $"{info.VolumeSerialNumber:X16}:{Convert.ToHexString(new ReadOnlySpan<byte>(&info.FileId, sizeof(FILE_ID_128)))}";
    }
}
