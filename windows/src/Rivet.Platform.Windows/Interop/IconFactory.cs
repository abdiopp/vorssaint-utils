// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Interop;

/// <summary>Builds HICONs from BGRA pixel buffers (32-bit colour with alpha plus an empty mask).</summary>
internal static unsafe class IconFactory
{
    /// <summary>Creates an icon the caller owns (release it with DestroyIcon).</summary>
    public static HICON Create(PixelBuffer image)
    {
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = image.Width,
            bV5Height = -image.Height,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };

        var screen = PInvoke.GetDC(HWND.Null);
        using var color = PInvoke.CreateDIBSection(screen, (BITMAPINFO*)&header, DIB_USAGE.DIB_RGB_COLORS, out var bits, null, 0);
        PInvoke.ReleaseDC(HWND.Null, screen);
        if (color.IsInvalid || bits == null)
        {
            return default;
        }

        var rowBytes = image.Width * 4;
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Pixels, y * image.Stride, (nint)bits + (y * rowBytes), rowBytes);
        }

        var mask = PInvoke.CreateBitmap(image.Width, image.Height, 1, 1, null);
        try
        {
            var info = new ICONINFO
            {
                fIcon = true,
                hbmColor = new HBITMAP((void*)color.DangerousGetHandle()),
                hbmMask = mask,
            };

            using var icon = PInvoke.CreateIconIndirect(in info);
            var handle = icon.DangerousGetHandle();
            icon.SetHandleAsInvalid();
            return new HICON((void*)handle);
        }
        finally
        {
            PInvoke.DeleteObject(new HGDIOBJ(mask.Value));
        }
    }
}
