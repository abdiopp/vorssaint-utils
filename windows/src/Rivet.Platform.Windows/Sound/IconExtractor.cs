// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Platform;

namespace Rivet.Platform.Windows.Sound;

/// <summary>Reads an executable's icon into a premultiplied BGRA <see cref="PixelBuffer"/>.</summary>
internal static unsafe class IconExtractor
{
    /// <summary>
    /// The icon of <paramref name="location"/>: a file path, optionally with
    /// ",index" (the form of session icon paths). Indirect "@…" strings are
    /// not resolved. Null when nothing could be read.
    /// </summary>
    public static PixelBuffer? FromLocation(string? location, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(location) || location.StartsWith('@'))
        {
            return null;
        }

        var (file, index) = Split(Environment.ExpandEnvironmentVariables(location.Trim().Trim('"')));
        if (!File.Exists(file))
        {
            return null;
        }

        var hr = SoundNative.SHDefExtractIcon(file, index, 0, out var large, out var small, (uint)(size | (16 << 16)));
        if (small != 0)
        {
            SoundNative.DestroyIcon(small);
        }

        if (hr != SoundNative.SOk || large == 0)
        {
            return null;
        }

        try
        {
            return FromIcon(large);
        }
        finally
        {
            SoundNative.DestroyIcon(large);
        }
    }

    /// <summary>Copies an HICON's pixels (32-bit colour with alpha, or colour plus the AND mask).</summary>
    public static PixelBuffer? FromIcon(nint icon)
    {
        if (!SoundNative.GetIconInfo(icon, out var info))
        {
            return null;
        }

        try
        {
            if (info.ColorBitmap == 0)
            {
                return null;
            }

            SoundNative.Bitmap bitmap;
            if (SoundNative.GetObjectW(info.ColorBitmap, sizeof(SoundNative.Bitmap), &bitmap) == 0 || bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Width > 256 || bitmap.Height > 256)
            {
                return null;
            }

            var width = bitmap.Width;
            var height = bitmap.Height;
            var pixels = new byte[width * height * 4];
            var dc = SoundNative.CreateCompatibleDC(0);
            if (dc == 0)
            {
                return null;
            }

            try
            {
                if (!ReadBits(dc, info.ColorBitmap, width, height, pixels))
                {
                    return null;
                }

                var hasAlpha = false;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha)
                {
                    // Old-style icon: transparency comes from the AND mask (black = opaque).
                    var mask = new byte[pixels.Length];
                    var haveMask = info.MaskBitmap != 0 && ReadBits(dc, info.MaskBitmap, width, height, mask);
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        pixels[i + 3] = haveMask && mask[i] != 0 ? (byte)0 : (byte)255;
                    }
                }
            }
            finally
            {
                SoundNative.DeleteDC(dc);
            }

            Premultiply(pixels);
            return new PixelBuffer(width, height, pixels);
        }
        finally
        {
            if (info.ColorBitmap != 0)
            {
                SoundNative.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != 0)
            {
                SoundNative.DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static bool ReadBits(nint dc, nint bitmap, int width, int height, byte[] target)
    {
        // Room for a colour table in case the driver writes one.
        var infoBuffer = stackalloc byte[sizeof(SoundNative.BitmapInfoHeader) + (256 * 4)];
        var header = (SoundNative.BitmapInfoHeader*)infoBuffer;
        *header = new SoundNative.BitmapInfoHeader
        {
            Size = (uint)sizeof(SoundNative.BitmapInfoHeader),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };

        fixed (byte* bits = target)
        {
            return SoundNative.GetDIBits(dc, bitmap, 0, (uint)height, bits, header, 0) == height;
        }
    }

    private static void Premultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }
    }

    private static (string File, int Index) Split(string location)
    {
        var comma = location.LastIndexOf(',');
        if (comma > 0 && int.TryParse(location.AsSpan(comma + 1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            return (location[..comma].Trim().Trim('"'), index);
        }

        return (location, 0);
    }
}
