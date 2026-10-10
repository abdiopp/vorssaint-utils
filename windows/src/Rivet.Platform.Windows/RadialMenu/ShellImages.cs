// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Rivet.Platform.Windows.RadialMenu;

/// <summary>
/// Shell icons and thumbnails through IShellItemImageFactory (the same images
/// File Explorer shows), returned as premultiplied BGRA pixel buffers. Shared
/// by the radial menu (app and file icons) and the shelf (thumbnails).
/// </summary>
internal static unsafe class ShellImages
{
    public static PixelBuffer? Icon(string path, int sizePx) => Get(path, sizePx, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK);

    public static PixelBuffer? Thumbnail(string path, int sizePx) => Get(path, sizePx, SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK);

    public static PixelBuffer? Get(string path, int sizePx, SIIGBF flags)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var hr = PInvoke.SHCreateItemFromParsingName<IShellItemImageFactory>(path, null, out var factory);
            if (hr.Failed || factory is null)
            {
                return null;
            }

            try
            {
                factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, flags, out var hbitmap);
                using (hbitmap)
                {
                    return ToPixelBuffer(new HBITMAP(hbitmap.DangerousGetHandle()));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or FileNotFoundException)
        {
            Log.Debug("shell", $"No shell image for {path}: {ex.Message}");
            return null;
        }
    }

    private static PixelBuffer? ToPixelBuffer(HBITMAP hbitmap)
    {
        BITMAP bitmap;
        if (PInvoke.GetObject(new HGDIOBJ(hbitmap.Value), sizeof(BITMAP), &bitmap) == 0 || bitmap.bmWidth <= 0 || bitmap.bmHeight == 0)
        {
            return null;
        }

        var width = bitmap.bmWidth;
        var height = Math.Abs(bitmap.bmHeight);
        var header = new BITMAPINFO();
        header.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        header.bmiHeader.biWidth = width;
        header.bmiHeader.biHeight = -height; // top-down
        header.bmiHeader.biPlanes = 1;
        header.bmiHeader.biBitCount = 32;
        header.bmiHeader.biCompression = 0; // BI_RGB
        var pixels = new byte[width * height * 4];
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        try
        {
            fixed (byte* p = pixels)
            {
                if (PInvoke.GetDIBits(dc, hbitmap, 0, (uint)height, p, &header, DIB_USAGE.DIB_RGB_COLORS) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            PInvoke.DeleteDC(dc);
        }

        // Icons come with alpha; some thumbnails have none (all zero): make those opaque.
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
}
