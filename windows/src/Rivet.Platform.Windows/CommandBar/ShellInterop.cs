// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Rivet.Platform.Windows.Launcher;

/// <summary>
/// Shell helpers for the Command Bar: shortcut targets (IShellLinkW, cached
/// by file time), a window's AppUserModelID (to match running packaged apps)
/// and shell icons (IShellItemImageFactory on one STA worker thread).
/// </summary>
internal static class ShellInterop
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, string? Target)> Targets = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<StaWorker> IconWorker = new(() => new StaWorker("Rivet shell icons"));

    /// <summary>The lower-case target path of a .lnk file, or null (advertised shortcuts have none).</summary>
    public static unsafe string? ShortcutTarget(string lnk)
    {
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(lnk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (Targets.TryGetValue(lnk, out var cached) && cached.Written == written)
        {
            return cached.Target;
        }

        string? target = null;
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, STGM.STGM_READ);
                var buffer = stackalloc char[1024];
                link.GetPath(new PWSTR(buffer), 1024, null, 0);
                var path = new string(buffer);
                if (path.Length > 0)
                {
                    target = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\').ToLowerInvariant();
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException)
        {
            target = null;
        }

        Targets[lnk] = (written, target);
        return target;
    }

    /// <summary>The AppUserModelID of a window (its own property, else its process's package identity).</summary>
    public static unsafe string? WindowAppId(HWND hwnd, uint processId)
    {
        try
        {
            var storeId = typeof(IPropertyStore).GUID;
            if (PInvoke.SHGetPropertyStoreForWindow(hwnd, &storeId, out var store).Succeeded && store is IPropertyStore properties)
            {
                try
                {
                    var key = PInvoke.PKEY_AppUserModel_ID;
                    properties.GetValue(&key, out var value);
                    try
                    {
                        if (PInvoke.PropVariantToStringAlloc(in value, out var text).Succeeded)
                        {
                            try
                            {
                                var id = text.ToString();
                                if (!string.IsNullOrEmpty(id))
                                {
                                    return id;
                                }
                            }
                            finally
                            {
                                PInvoke.CoTaskMemFree(text.Value);
                            }
                        }
                    }
                    finally
                    {
                        PInvoke.PropVariantClear(ref value);
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(properties);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // Some windows refuse; fall back to the process.
        }

        var process = PInvoke.OpenProcess(global::Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            using var safe = new Microsoft.Win32.SafeHandles.SafeProcessHandle((nint)process.Value, ownsHandle: false);
            Span<char> buffer = stackalloc char[256];
            uint length = 256;
            return PInvoke.GetApplicationUserModelId(safe, ref length, buffer) == WIN32_ERROR.ERROR_SUCCESS && length > 1
                ? new string(buffer[..((int)length - 1)])
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>The shell's icon of a file, folder, shortcut or <c>shell:AppsFolder\AUMID</c>, as premultiplied BGRA.</summary>
    public static Task<PixelBuffer?> IconAsync(string path, int size, CancellationToken cancellationToken) =>
        IconWorker.Value.Run(() => cancellationToken.IsCancellationRequested ? null : Icon(path, size));

    private static unsafe PixelBuffer? Icon(string path, int size)
    {
        object? item = null;
        try
        {
            var factoryId = typeof(IShellItemImageFactory).GUID;
            HRESULT created;
            fixed (char* text = path)
            {
                created = PInvoke.SHCreateItemFromParsingName(text, null, &factoryId, out item);
            }

            if (created.Failed || item is not IShellItemImageFactory factory)
            {
                return null;
            }

            factory.GetImage(new SIZE(size, size), SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out var bitmap);
            using (bitmap)
            {
                return Pixels(new HBITMAP(bitmap.DangerousGetHandle()));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Info("commandBar", $"No shell icon ({ex.GetType().Name}).");
            return null;
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item))
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
    }

    /// <summary>Copies a 32-bit HBITMAP top-down; images without alpha get an opaque one.</summary>
    private static unsafe PixelBuffer? Pixels(HBITMAP bitmap)
    {
        BITMAP info;
        if (PInvoke.GetObject(bitmap, sizeof(BITMAP), &info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0)
        {
            return null;
        }

        var width = info.bmWidth;
        var height = Math.Abs(info.bmHeight);
        var header = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        var pixels = new byte[width * height * 4];
        var dc = PInvoke.GetDC(HWND.Null);
        try
        {
            fixed (byte* p = pixels)
            {
                if (PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &header, DIB_USAGE.DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = PInvoke.ReleaseDC(HWND.Null, dc);
        }

        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                anyAlpha = true;
                break;
            }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new PixelBuffer(width, height, pixels);
    }

    /// <summary>One single-threaded-apartment thread running queued shell calls in order.</summary>
    private sealed class StaWorker
    {
        private readonly BlockingCollection<Action> _queue = new();

        public StaWorker(string name)
        {
            var thread = new Thread(() =>
            {
                PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }
    }
}
