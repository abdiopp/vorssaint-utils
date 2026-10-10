// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Rivet.Imaging.Recording;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// The real cursor, for the pointer track. Position and visibility come from
/// <c>GetCursorInfo</c> (physical pixels: the process is per-monitor DPI aware;
/// touch and pen input suppress the cursor, which counts as hidden). Shapes are
/// read with <c>GetIconInfoEx</c> + <c>GetDIBits</c> and decoded by
/// <see cref="CursorBitmapDecoder"/> (alpha, masked and monochrome cursors;
/// inverting pixels become black with a white outline). A sharper rendition
/// for zooms is requested from the cursor's resource with
/// <c>CopyImage(LR_COPYFROMRESOURCE)</c>; cursors not loaded from a resource
/// keep their base picture. The accessibility pointer size is already part of
/// the bitmaps Windows returns, so the track's system scale is 1.
/// </summary>
public sealed unsafe class WindowsCursorProbe : ICursorProbe
{
    /// <summary>The sharper copy targets the maximum editor zoom (3×) at the drawn size.</summary>
    private const double SharperFactor = 3.0;

    public CursorReading Read()
    {
        var info = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(ref info))
        {
            return PInvoke.GetCursorPos(out var point)
                ? new CursorReading(new PixelPoint(point.X, point.Y), true, 0)
                : default;
        }

        var showing = (info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) != 0;
        var suppressed = (info.flags & CURSORINFO_FLAGS.CURSOR_SUPPRESSED) != 0;
        return new CursorReading(new PixelPoint(info.ptScreenPos.X, info.ptScreenPos.Y), showing && !suppressed, (nint)info.hCursor.Value);
    }

    public CursorShapeSnapshot? CaptureShape(nint handle, double monitorScale)
    {
        if (handle == 0)
        {
            return null;
        }

        var cursor = new HCURSOR((void*)handle);
        var baseImage = Read(cursor, out var hotX, out var hotY);
        if (baseImage is null)
        {
            return null;
        }

        // Cursor bitmaps are made for the system DPI; on a monitor with another
        // scale Windows draws them proportionally larger or smaller.
        var systemScale = Math.Max(1, PInvoke.GetDpiForSystem()) / 96.0;
        var factor = Math.Clamp(monitorScale / systemScale, 0.25, 8);

        PixelBuffer? sharper = null;
        var targetWidth = (int)Math.Ceiling(baseImage.Width * factor * SharperFactor);
        var targetHeight = (int)Math.Ceiling(baseImage.Height * factor * SharperFactor);
        if (targetWidth <= CursorShapeFactory.MaxSharperSide && targetHeight <= CursorShapeFactory.MaxSharperSide)
        {
            var copy = PInvoke.CopyImage(new HANDLE((void*)handle), GDI_IMAGE_TYPE.IMAGE_CURSOR, targetWidth, targetHeight, IMAGE_FLAGS.LR_COPYFROMRESOURCE);
            if (!copy.IsNull)
            {
                try
                {
                    sharper = Read(new HCURSOR(copy.Value), out _, out _);
                }
                finally
                {
                    PInvoke.DestroyCursor(new HCURSOR(copy.Value));
                }
            }
        }

        return CursorShapeFactory.Create(baseImage, hotX, hotY, factor, sharper);
    }

    /// <summary>The cursor's bitmaps decoded to premultiplied BGRA, or null.</summary>
    private static PixelBuffer? Read(HCURSOR cursor, out int hotX, out int hotY)
    {
        hotX = hotY = 0;
        var info = new ICONINFOEXW { cbSize = (uint)sizeof(ICONINFOEXW) };
        if (!PInvoke.GetIconInfoEx(new HICON(cursor.Value), &info))
        {
            return null;
        }

        try
        {
            hotX = (int)info.xHotspot;
            hotY = (int)info.yHotspot;
            if (info.hbmMask.IsNull || !TryGetSize(info.hbmMask, out var maskWidth, out var maskHeight))
            {
                return null;
            }

            byte[]? color = null;
            int height;
            if (!info.hbmColor.IsNull && TryGetSize(info.hbmColor, out var colorWidth, out var colorHeight))
            {
                height = colorHeight;
                color = ReadPixels(info.hbmColor, colorWidth, colorHeight);
                if (color is null || colorWidth != maskWidth)
                {
                    return null;
                }
            }
            else
            {
                height = maskHeight / 2;
            }

            var mask = ReadPixels(info.hbmMask, maskWidth, color is null ? height * 2 : height);
            if (mask is null || height <= 0)
            {
                return null;
            }

            return CursorBitmapDecoder.Decode(new CursorBitmapSource { Width = maskWidth, Height = height, Color = color, Mask = mask });
        }
        finally
        {
            if (!info.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(info.hbmColor.Value));
            }

            if (!info.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(new HGDIOBJ(info.hbmMask.Value));
            }
        }
    }

    private static bool TryGetSize(HBITMAP bitmap, out int width, out int height)
    {
        BITMAP bm;
        if (PInvoke.GetObject(new HGDIOBJ(bitmap.Value), sizeof(BITMAP), &bm) == 0)
        {
            width = height = 0;
            return false;
        }

        width = bm.bmWidth;
        height = Math.Abs(bm.bmHeight);
        return width is > 0 and <= CursorBitmapDecoder.MaxSide && height is > 0 and <= CursorBitmapDecoder.MaxSide * 2;
    }

    /// <summary>Any bitmap as 32-bit top-down BGRA (1-bit masks become black and white).</summary>
    private static byte[]? ReadPixels(HBITMAP bitmap, int width, int height)
    {
        var dc = PInvoke.GetDC(HWND.Null);
        try
        {
            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0; // BI_RGB
            var pixels = new byte[width * height * 4];
            fixed (byte* p = pixels)
            {
                var lines = PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
                return lines == height ? pixels : null;
            }
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, dc);
        }
    }
}
