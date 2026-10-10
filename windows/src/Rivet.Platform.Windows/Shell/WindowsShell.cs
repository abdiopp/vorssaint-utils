// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Rivet.Platform.Windows.Shell;

public sealed unsafe class WindowsShell : IShellService
{
    public void OpenUrl(string url) => Start(url);

    public void OpenFile(string path) => Start(path);

    public void OpenSystemSettings(string uri) => Start(uri);

    public void RevealInExplorer(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is not null && Directory.Exists(parent))
            {
                Start(parent);
            }

            return;
        }

        ITEMIDLIST* pidl = null;
        try
        {
            if (PInvoke.SHParseDisplayName(full, null, out pidl, 0, out _).Succeeded && pidl != null)
            {
                PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).ThrowOnFailure();
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or Win32Exception)
        {
            Log.Warn("shell", "SHOpenFolderAndSelectItems failed; falling back to explorer /select.", ex);
        }
        finally
        {
            if (pidl != null)
            {
                PInvoke.ILFree(pidl);
            }
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
    }

    public bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
        if (list.Count == 0)
        {
            return true;
        }

        // Double-null-terminated list of paths.
        var from = string.Join('\0', list) + "\0\0";
        fixed (char* pFrom = from)
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = PInvoke.FO_DELETE,
                pFrom = pFrom,
                fFlags = (ushort)(FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
                                  | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOERRORUI),
            };
            var result = PInvoke.SHFileOperation(ref op);
            if (result != 0 || op.fAnyOperationsAborted)
            {
                Log.Warn("shell", $"Recycle failed (code {result}, aborted {op.fAnyOperationsAborted.Value != 0}).");
                return false;
            }
        }

        return true;
    }

    public void OpenWith(string path)
    {
        var info = new OPENASINFO
        {
            oaifInFlags = OPEN_AS_INFO_FLAGS.OAIF_ALLOW_REGISTRATION | OPEN_AS_INFO_FLAGS.OAIF_EXEC,
        };
        fixed (char* file = Path.GetFullPath(path))
        {
            info.pcszFile = file;
            PInvoke.SHOpenWithDialog(HWND.Null, in info);
        }
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn("shell", $"Could not open '{target}'.", ex);
        }
    }
}
