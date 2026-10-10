// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// GDI capture: BitBlt from the screen DC (works on every Windows version,
/// never shows a capture border, honours WDA_EXCLUDEFROMCAPTURE) and
/// PrintWindow for a window's own pixels. The process is per-monitor DPI
/// aware, so every coordinate is a physical pixel.
/// </summary>
internal static unsafe class GdiCapture
{
    /// <summary>PW_RENDERFULLCONTENT: renders DirectComposition/GPU content too (Windows 8.1+).</summary>
    private const PRINT_WINDOW_FLAGS RenderFullContent = (PRINT_WINDOW_FLAGS)2;

    /// <summary>Copies a rectangle of the virtual screen; null on failure.</summary>
    public static PixelBuffer? CaptureScreenRect(PixelRect rect, double scale, bool includeCursor)
    {
        if (rect.IsEmpty)
        {
            return null;
        }

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            return null;
        }

        try
        {
            return CopyFromDc(screen, rect.X, rect.Y, rect.Width, rect.Height, scale, dc =>
            {
                if (includeCursor)
                {
                    DrawCursor(dc, rect.X, rect.Y);
                }
            });
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    /// <summary>
    /// Renders a window with PrintWindow(PW_RENDERFULLCONTENT) and crops the
    /// visible frame (the window rectangle includes invisible resize borders).
    /// Null when the call fails or produces a blank image.
    /// </summary>
    public static PixelBuffer? CaptureWindow(nint handle, PixelRect visibleFrame, double scale)
    {
        var hwnd = new HWND((void*)handle);
        if (!PInvoke.GetWindowRect(hwnd, out var windowRect))
        {
            return null;
        }

        var width = windowRect.right - windowRect.left;
        var height = windowRect.bottom - windowRect.top;
        if (width <= 0 || height <= 0 || (long)width * height > 200_000_000)
        {
            return null;
        }

        var screen = PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            return null;
        }

        try
        {
            var full = CopyFromDc(screen, 0, 0, width, height, scale, dc => { }, render: dc => PInvoke.PrintWindow(hwnd, dc, RenderFullContent));
            if (full is null)
            {
                return null;
            }

            var crop = new PixelRect(visibleFrame.X - windowRect.left, visibleFrame.Y - windowRect.top, visibleFrame.Width, visibleFrame.Height);
            var result = Rivet.Imaging.Capture.CaptureImaging.Crop(full, crop);
            if (result is null || Rivet.Imaging.Capture.CaptureImaging.IsBlank(result))
            {
                return null;
            }

            Rivet.Imaging.Capture.CaptureImaging.MakeOpaque(result);
            return result;
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    /// <summary>
    /// Creates a top-down 32-bit DIB section, fills it with BitBlt from
    /// <paramref name="source"/> (or with <paramref name="render"/>), lets
    /// <paramref name="decorate"/> draw on it and copies the pixels out.
    /// </summary>
    private static PixelBuffer? CopyFromDc(HDC source, int x, int y, int width, int height, double scale, Action<HDC> decorate, Func<HDC, BOOL>? render = null)
    {
        var memory = PInvoke.CreateCompatibleDC(source);
        if (memory.IsNull)
        {
            return null;
        }

        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0; // BI_RGB

        void* bits;
        var bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (bitmap.IsNull || bits == null)
        {
            PInvoke.DeleteDC(memory);
            return null;
        }

        var previous = PInvoke.SelectObject(memory, new HGDIOBJ(bitmap.Value));
        try
        {
            bool ok;
            if (render is not null)
            {
                ok = render(memory);
            }
            else
            {
                // CAPTUREBLT includes layered windows (tooltips, menus, translucent apps).
                ok = PInvoke.BitBlt(memory, 0, 0, width, height, source, x, y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
            }

            if (!ok)
            {
                return null;
            }

            decorate(memory);
            var buffer = new PixelBuffer(width, height) { Scale = scale };
            var rowBytes = width * 4;
            fixed (byte* destination = buffer.Pixels)
            {
                Buffer.MemoryCopy(bits, destination, buffer.Pixels.Length, (long)rowBytes * height);
            }

            // GDI leaves alpha undefined (usually 0); a screen capture is opaque.
            var pixels = buffer.Pixels;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return buffer;
        }
        finally
        {
            PInvoke.SelectObject(memory, previous);
            PInvoke.DeleteObject(new HGDIOBJ(bitmap.Value));
            PInvoke.DeleteDC(memory);
        }
    }

    /// <summary>Draws the current cursor shape where it is on screen (GDI captures never contain it).</summary>
    private static void DrawCursor(HDC dc, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(&info) || (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || info.hCursor.IsNull)
        {
            return;
        }

        var icon = new HICON(info.hCursor.Value);
        ICONINFO iconInfo;
        if (!PInvoke.GetIconInfo(icon, &iconInfo))
        {
            return;
        }

        try
        {
            var x = info.ptScreenPos.X - (int)iconInfo.xHotspot - originX;
            var y = info.ptScreenPos.Y - (int)iconInfo.yHotspot - originY;
            PInvoke.DrawIconEx(dc, x, y, icon, 0, 0, 0, HBRUSH.Null, DI_FLAGS.DI_NORMAL);
        }
        finally
        {
            if (!iconInfo.hbmMask.IsNull) PInvoke.DeleteObject(new HGDIOBJ(iconInfo.hbmMask.Value));
            if (!iconInfo.hbmColor.IsNull) PInvoke.DeleteObject(new HGDIOBJ(iconInfo.hbmColor.Value));
        }
    }
}
