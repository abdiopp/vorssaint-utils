// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Capture;

/// <summary>Known folders, cursor, exact window placement, the error beep and the share sheet.</summary>
public sealed class WindowsCapturePlatform : ICapturePlatform
{
    private string? _defaultFolder;

    public string DefaultScreenshotFolder => _defaultFolder ??= ResolveScreenshotsFolder();

    public void SetCursorPosition(PixelPoint point) => PInvoke.SetCursorPos(point.X, point.Y);

    public unsafe void PlaceWindow(nint hwnd, PixelRect bounds) =>
        PInvoke.SetWindowPos(new HWND((void*)hwnd), HWND.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

    public void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);

    public bool ShareFile(nint ownerWindow, string filePath, string title, Action<bool>? completed)
    {
        if (ownerWindow == 0 || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(ownerWindow);
            var finished = 0;
            void Finish(bool chosen)
            {
                if (Interlocked.Exchange(ref finished, 1) == 0)
                {
                    completed?.Invoke(chosen);
                }
            }

            global::Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? requested = null;
            global::Windows.Foundation.TypedEventHandler<DataTransferManager, TargetApplicationChosenEventArgs>? chosen = null;
            requested = async (sender, args) =>
            {
                sender.DataRequested -= requested;
                var deferral = args.Request.GetDeferral();
                try
                {
                    args.Request.Data.Properties.Title = title;
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    args.Request.Data.SetStorageItems([file]);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
                {
                    Log.Warn("capture", "Could not prepare the shared file.", ex);
                    args.Request.FailWithDisplayText(title);
                    Finish(false);
                }
                finally
                {
                    deferral.Complete();
                }
            };
            chosen = (sender, _) =>
            {
                sender.TargetApplicationChosen -= chosen;
                Finish(true);
            };
            manager.DataRequested += requested;
            manager.TargetApplicationChosen += chosen;
            DataTransferManagerInterop.ShowShareUIForWindow(ownerWindow);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            Log.Warn("capture", "The Windows share sheet is unavailable.", ex);
            return false;
        }
    }

    public unsafe void SetClickThrough(nint hwnd, bool enabled)
    {
        var handle = new HWND((void*)hwnd);
        var style = (WINDOW_EX_STYLE)(nint)PInvoke.GetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var updated = enabled
            ? style | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED
            : style & ~WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        if (updated != style)
        {
            PInvoke.SetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)updated);
        }

        if ((updated & WINDOW_EX_STYLE.WS_EX_LAYERED) != 0)
        {
            // A layered window stays invisible until its attributes are set: keep it fully opaque.
            PInvoke.SetLayeredWindowAttributes(handle, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }
    }

    private static unsafe string ResolveScreenshotsFolder()
    {
        try
        {
            var id = PInvoke.FOLDERID_Screenshots;
            if (PInvoke.SHGetKnownFolderPath(in id, KNOWN_FOLDER_FLAG.KF_FLAG_CREATE, null, out var path).Succeeded)
            {
                try
                {
                    var value = path.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
                finally
                {
                    PInvoke.CoTaskMemFree(path.Value);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Log.Warn("capture", "The Screenshots known folder could not be resolved.", ex);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }
}
