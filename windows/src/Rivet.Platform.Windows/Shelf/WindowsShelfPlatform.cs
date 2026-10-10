// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.Shelf;
using Rivet.Core.Platform;
using Rivet.Core.Util;
using Rivet.Platform.Windows.CleaningMode;
using Rivet.Platform.Windows.RadialMenu;
using Rivet.Platform.Windows.Scratchpad;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Shelf;

/// <summary>
/// The shelf's Windows plumbing (spec 07 §7.1): the app under the pointer
/// (WindowFromPoint → root → process image), SM_SWAPBUTTON-aware button
/// state, SM_CXDRAG/SM_CYDRAG, the move/size WinEvents, the shell drag image
/// window as a content-drag hint, NTFS file ids as bookmarks (volume + file
/// id, resolved with OpenFileById without UI), SHGetFileInfo type names,
/// shell thumbnails, the File Explorer selection (Shell.Application on a
/// short-lived STA thread), the share sheet (DataTransferManager for desktop
/// apps) and multi-select reveal (SHOpenFolderAndSelectItems per folder).
/// </summary>
public sealed class WindowsShelfPlatform : IShelfPlatform
{
    private const uint EventMoveSizeStart = 0x000A;
    private const uint EventMoveSizeEnd = 0x000B;
    private static readonly uint OwnProcessId = PInvoke.GetCurrentProcessId();

    public bool MouseButtonsSwapped => PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0;

    public (int X, int Y) DragThreshold =>
        (Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG)), Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG)));

    public unsafe string? ProcessPathAt(PixelPoint point)
    {
        var root = RootAt(point);
        return root.IsNull ? null : Win32Windows.ProcessPathOf((nint)root.Value);
    }

    public unsafe bool IsOwnWindowAt(PixelPoint point)
    {
        var root = RootAt(point);
        if (root.IsNull)
        {
            return false;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(root, &pid);
        return pid == OwnProcessId;
    }

    public bool IsPrimaryButtonDown()
    {
        // GetAsyncKeyState reports physical buttons; the primary is the right one when swapped.
        var vk = MouseButtonsSwapped ? 0x02 : 0x01;
        return (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public IDisposable WatchMoveSize(Action<bool> changed)
    {
        WINEVENTPROC callback = (_, eventType, _, _, _, _, _) =>
        {
            if (eventType == EventMoveSizeStart)
            {
                changed(true);
            }
            else if (eventType == EventMoveSizeEnd)
            {
                changed(false);
            }
        };
        var hook = PInvoke.SetWinEventHook(EventMoveSizeStart, EventMoveSizeEnd, HMODULE.Null, callback, 0, 0, 0);
        return new Token(() =>
        {
            if (!hook.IsNull)
            {
                PInvoke.UnhookWinEvent(hook);
            }

            GC.KeepAlive(callback);
        });
    }

    public bool IsDragImageVisible()
    {
        var window = PInvoke.FindWindow("SysDragImage", null);
        return !window.IsNull && PInvoke.IsWindowVisible(window);
    }

    public unsafe string? CreateBookmark(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            using var handle = OpenForQuery(path);
            if (handle is null)
            {
                return null;
            }

            FILE_ID_INFO info;
            if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileIdInfo, new Span<byte>(&info, sizeof(FILE_ID_INFO))))
            {
                return null;
            }

            var id = new ReadOnlySpan<byte>(&info.FileId, sizeof(FILE_ID_128));
            return string.Create(CultureInfo.InvariantCulture, $"fid1|{root}|{info.VolumeSerialNumber:X16}|{Convert.ToHexString(id)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public unsafe string? ResolveBookmark(string bookmark)
    {
        var parts = bookmark.Split('|');
        if (parts.Length != 4 || parts[0] != "fid1" || parts[3].Length != 32)
        {
            return null;
        }

        try
        {
            var root = parts[1];
            if (!Directory.Exists(root))
            {
                return null;
            }

            using var volume = OpenForQuery(root);
            if (volume is null)
            {
                return null;
            }

            var idBytes = Convert.FromHexString(parts[3]);
            var descriptor = new FILE_ID_DESCRIPTOR
            {
                dwSize = (uint)sizeof(FILE_ID_DESCRIPTOR),
                Type = FILE_ID_TYPE.ExtendedFileIdType,
            };
            fixed (byte* src = idBytes)
            {
                Buffer.MemoryCopy(src, &descriptor.Anonymous.ExtendedFileId, sizeof(FILE_ID_128), sizeof(FILE_ID_128));
            }

            using var file = PInvoke.OpenFileById(volume, descriptor, 0,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS);
            if (file.IsInvalid)
            {
                return null;
            }

            Span<char> buffer = stackalloc char[1024];
            var length = PInvoke.GetFinalPathNameByHandle(file, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED);
            if (length == 0 || length >= 1024)
            {
                return null;
            }

            var resolved = new string(buffer[..(int)length]);
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                resolved = @"\\" + resolved[8..];
            }
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                resolved = resolved[4..];
            }

            return resolved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    public string FileKind(string path)
    {
        var info = new SHFILEINFOW();
        var exists = File.Exists(path) || Directory.Exists(path);
        var flags = SHGFI_FLAGS.SHGFI_TYPENAME | (exists ? 0 : SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES);
        if (PInvoke.SHGetFileInfo(path, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, ref info, flags) == 0)
        {
            return string.Empty;
        }

        return info.szTypeName.ToString();
    }

    public PixelBuffer? Thumbnail(string path, int sizePx) => ShellImages.Thumbnail(path, sizePx);

    public PixelBuffer? Icon(string path, int sizePx) => ShellImages.Icon(path, sizePx);

    public unsafe IReadOnlyList<string>? ForegroundExplorerSelection()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return null;
        }

        var className = WindowsScratchpadPlatform.ClassName(foreground);
        var isDesktop = className is "Progman" or "WorkerW";
        if (className != "CabinetWClass" && !isDesktop)
        {
            return null;
        }

        var hwnd = (long)(nint)foreground.Value;
        IReadOnlyList<string>? result = null;
        var thread = new Thread(() => result = ReadSelection(hwnd, isDesktop)) { IsBackground = true, Name = "ExplorerSelection" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("shelf", "File Explorer did not answer in time.");
            return [];
        }

        return result ?? [];
    }

    public bool Share(nint ownerWindow, IReadOnlyList<string> paths, string title)
    {
        if (ownerWindow == 0 || paths.Count == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            return false;
        }

        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(ownerWindow);
            var files = paths.ToList();
            void OnRequested(DataTransferManager sender, DataRequestedEventArgs args)
            {
                sender.DataRequested -= OnRequested;
                var deferral = args.Request.GetDeferral();
                _ = FillAsync(args.Request, files, title, deferral);
            }

            manager.DataRequested += OnRequested;
            DataTransferManagerInterop.ShowShareUIForWindow(ownerWindow);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("shelf", "The share sheet is unavailable.", ex);
            return false;
        }
    }

    public void Reveal(IReadOnlyList<string> paths)
    {
        foreach (var group in paths.Where(p => File.Exists(p) || Directory.Exists(p)).GroupBy(p => Path.GetDirectoryName(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            RevealGroup(group.Key, group.ToList());
        }
    }

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);

    private static HWND RootAt(PixelPoint point)
    {
        var hwnd = PInvoke.WindowFromPoint(new System.Drawing.Point(point.X, point.Y));
        if (hwnd.IsNull)
        {
            return HWND.Null;
        }

        var root = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT);
        return root.IsNull ? hwnd : root;
    }

    private static SafeFileHandle? OpenForQuery(string path)
    {
        var handle = PInvoke.CreateFile(path, 0,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static async Task FillAsync(DataRequest request, IReadOnlyList<string> paths, string title, DataRequestDeferral deferral)
    {
        try
        {
            var items = new List<IStorageItem>();
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    items.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
                else if (File.Exists(path))
                {
                    items.Add(await StorageFile.GetFileFromPathAsync(path));
                }
            }

            request.Data.Properties.Title = title;
            request.Data.SetStorageItems(items);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            request.FailWithDisplayText(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static IReadOnlyList<string> ReadSelection(long foreground, bool desktop)
    {
        var selected = new List<string>();
        dynamic? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return selected;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return selected;
            }

            dynamic windows = shell.Windows();
            if (desktop)
            {
                // SWC_DESKTOP (8) with SWFO_NEEDDISPATCH (1).
                object location = 0; // CSIDL_DESKTOP
                object root = Type.Missing;
                int hwndOut = 0;
                dynamic? view = windows.FindWindowSW(ref location, ref root, 8, ref hwndOut, 1);
                if (view is not null)
                {
                    Collect(view.Document, selected);
                }

                return selected;
            }

            foreach (dynamic window in windows)
            {
                long windowHandle;
                try
                {
                    windowHandle = (long)window.HWND;
                }
                catch (Exception)
                {
                    continue;
                }

                if (windowHandle != foreground)
                {
                    continue;
                }

                Collect(window.Document, selected);
                if (selected.Count > 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Reflection.TargetInvocationException)
        {
            Log.Warn("shelf", "Reading the File Explorer selection failed.", ex);
        }
        finally
        {
            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        return selected;
    }

    private static void Collect(dynamic? document, List<string> selected)
    {
        if (document is null)
        {
            return;
        }

        dynamic items = document.SelectedItems();
        foreach (dynamic item in items)
        {
            string? path = item.Path;
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
            {
                selected.Add(path);
            }
        }
    }

    private static unsafe void RevealGroup(string folder, IReadOnlyList<string> items)
    {
        ITEMIDLIST* folderPidl = null;
        var children = new List<nint>();
        var full = new List<nint>();
        try
        {
            if (PInvoke.SHParseDisplayName(folder, null, out folderPidl, 0, out _).Failed || folderPidl == null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (PInvoke.SHParseDisplayName(item, null, out var pidl, 0, out _).Succeeded && pidl != null)
                {
                    full.Add((nint)pidl);
                    children.Add((nint)PInvoke.ILFindLastID(pidl));
                }
            }

            var array = new ITEMIDLIST*[children.Count];
            for (var i = 0; i < children.Count; i++)
            {
                array[i] = (ITEMIDLIST*)children[i];
            }

            fixed (ITEMIDLIST** pointers = array)
            {
                PInvoke.SHOpenFolderAndSelectItems(folderPidl, (uint)array.Length, pointers, 0);
            }
        }
        catch (COMException ex)
        {
            Log.Warn("shelf", "Reveal in File Explorer failed.", ex);
        }
        finally
        {
            foreach (var pidl in full)
            {
                PInvoke.ILFree((ITEMIDLIST*)pidl);
            }

            if (folderPidl != null)
            {
                PInvoke.ILFree(folderPidl);
            }
        }
    }

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

public sealed class ShelfWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<IShelfPlatform, WindowsShelfPlatform>();
}
