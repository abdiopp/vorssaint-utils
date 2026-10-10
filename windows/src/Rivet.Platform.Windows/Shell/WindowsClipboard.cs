// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using Rivet.Platform.Windows.Interop;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;

namespace Rivet.Platform.Windows.Shell;

/// <summary>
/// Basic Win32 clipboard access owned by the host window. Images are written
/// both as "PNG" (keeps transparency) and CF_DIBV5; reading prefers PNG.
/// </summary>
public sealed unsafe class WindowsClipboard(NativeWindowHost host) : IClipboardService
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint CfHdrop = 15;

    private static readonly uint PngFormat = PInvoke.RegisterClipboardFormat("PNG");

    public string? GetText()
    {
        using var session = Open();
        if (session is null || !PInvoke.IsClipboardFormatAvailable(CfUnicodeText))
        {
            return null;
        }

        var handle = PInvoke.GetClipboardData(CfUnicodeText);
        return ReadGlobal(handle, ptr => Marshal.PtrToStringUni(ptr));
    }

    public void SetText(string text)
    {
        using var session = Open();
        if (session is null)
        {
            return;
        }

        PInvoke.EmptyClipboard();
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        SetData(CfUnicodeText, bytes);
    }

    public PixelBuffer? GetImage()
    {
        using var session = Open();
        if (session is null)
        {
            return null;
        }

        if (PngFormat != 0 && PInvoke.IsClipboardFormatAvailable(PngFormat))
        {
            var png = ReadGlobalBytes(PInvoke.GetClipboardData(PngFormat));
            if (png is not null)
            {
                using var bitmap = SKBitmap.Decode(png);
                if (bitmap is not null)
                {
                    return SkiaConvert.ToPixelBuffer(bitmap);
                }
            }
        }

        foreach (var format in new[] { CfDibV5, CfDib })
        {
            if (PInvoke.IsClipboardFormatAvailable(format))
            {
                var dib = ReadGlobalBytes(PInvoke.GetClipboardData(format));
                if (dib is not null && DecodeDib(dib) is { } image)
                {
                    return image;
                }
            }
        }

        return null;
    }

    public void SetImage(PixelBuffer image)
    {
        using var skImage = SkiaConvert.ToImage(image);
        var png = SkiaConvert.EncodePng(skImage);
        var dib = EncodeDibV5(image);

        using var session = Open();
        if (session is null)
        {
            return;
        }

        PInvoke.EmptyClipboard();
        if (PngFormat != 0)
        {
            SetData(PngFormat, png);
        }

        SetData(CfDibV5, dib);
    }

    public void SetFiles(IReadOnlyList<string> paths)
    {
        // DROPFILES header (20 bytes) followed by a double-null-terminated UTF-16 list.
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);   // pFiles
        BitConverter.GetBytes(1).CopyTo(data, 16);   // fWide
        list.CopyTo(data, 20);

        using var session = Open();
        if (session is null)
        {
            return;
        }

        PInvoke.EmptyClipboard();
        SetData(CfHdrop, data);
    }

    public IReadOnlyList<string> GetFiles()
    {
        using var session = Open();
        if (session is null || !PInvoke.IsClipboardFormatAvailable(CfHdrop))
        {
            return [];
        }

        var handle = PInvoke.GetClipboardData(CfHdrop);
        if (handle.IsNull)
        {
            return [];
        }

        var drop = new HDROP((void*)handle.Value);
        var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var files = new List<string>((int)count);
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            for (uint i = 0; i < count; i++)
            {
                var length = PInvoke.DragQueryFile(drop, i, p, (uint)buffer.Length);
                if (length > 0)
                {
                    files.Add(new string(p, 0, (int)length));
                }
            }
        }

        return files;
    }

    private Session? Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (PInvoke.OpenClipboard(new HWND((void*)host.Handle)))
            {
                return new Session();
            }

            Thread.Sleep(15);
        }

        Log.Warn("clipboard", "The clipboard stayed locked by another app.");
        return null;
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (global.IsNull)
        {
            return;
        }

        var target = PInvoke.GlobalLock(global);
        Marshal.Copy(bytes, 0, (nint)target, bytes.Length);
        PInvoke.GlobalUnlock(global);
        if (PInvoke.SetClipboardData(format, new HANDLE((void*)global.Value)).IsNull)
        {
            PInvoke.GlobalFree(global);
        }
    }

    private static T? ReadGlobal<T>(HANDLE handle, Func<nint, T?> read)
    {
        if (handle.IsNull)
        {
            return default;
        }

        var global = new HGLOBAL(handle.Value);
        var pointer = PInvoke.GlobalLock(global);
        try
        {
            return pointer == null ? default : read((nint)pointer);
        }
        finally
        {
            PInvoke.GlobalUnlock(global);
        }
    }

    private static byte[]? ReadGlobalBytes(HANDLE handle)
    {
        if (handle.IsNull)
        {
            return null;
        }

        var global = new HGLOBAL(handle.Value);
        var size = (int)PInvoke.GlobalSize(global);
        return size <= 0 ? null : ReadGlobal(handle, ptr =>
        {
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        });
    }

    /// <summary>BITMAPV5HEADER + bottom-up 32-bit pixels with straight alpha.</summary>
    private static byte[] EncodeDibV5(PixelBuffer image)
    {
        var headerSize = sizeof(BITMAPV5HEADER);
        var rowBytes = image.Width * 4;
        var data = new byte[headerSize + (rowBytes * image.Height)];
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)headerSize,
            bV5Width = image.Width,
            bV5Height = image.Height,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5SizeImage = (uint)(rowBytes * image.Height),
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = 0x73524742, // 'sRGB'
            bV5Intent = 4,          // LCS_GM_IMAGES
        };
        fixed (byte* d = data)
        {
            *(BITMAPV5HEADER*)d = header;
        }

        for (var y = 0; y < image.Height; y++)
        {
            var source = y * image.Stride;
            var destination = headerSize + ((image.Height - 1 - y) * rowBytes);
            for (var x = 0; x < image.Width; x++)
            {
                var s = source + (x * 4);
                var t = destination + (x * 4);
                var a = image.Pixels[s + 3];
                if (a is 0 or 255)
                {
                    data[t] = image.Pixels[s];
                    data[t + 1] = image.Pixels[s + 1];
                    data[t + 2] = image.Pixels[s + 2];
                }
                else
                {
                    data[t] = (byte)Math.Min(255, image.Pixels[s] * 255 / a);
                    data[t + 1] = (byte)Math.Min(255, image.Pixels[s + 1] * 255 / a);
                    data[t + 2] = (byte)Math.Min(255, image.Pixels[s + 2] * 255 / a);
                }

                data[t + 3] = a;
            }
        }

        return data;
    }

    /// <summary>Reads 24/32-bit uncompressed or bitfield DIBs into a premultiplied buffer.</summary>
    private static PixelBuffer? DecodeDib(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var rawHeight = BitConverter.ToInt32(dib, 8);
        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        if (width <= 0 || rawHeight == 0 || bitCount is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        var rowBytes = ((width * bitCount) + 31) / 32 * 4;
        if (offset + (rowBytes * height) > dib.Length)
        {
            return null;
        }

        var hasAlpha = false;
        if (bitCount == 32)
        {
            for (var i = offset + 3; i < offset + (rowBytes * height); i += 4)
            {
                if (dib[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }
        }

        var buffer = new PixelBuffer(width, height);
        for (var y = 0; y < height; y++)
        {
            var sourceRow = offset + ((topDown ? y : height - 1 - y) * rowBytes);
            var destinationRow = y * buffer.Stride;
            for (var x = 0; x < width; x++)
            {
                var s = sourceRow + (x * (bitCount / 8));
                var t = destinationRow + (x * 4);
                var a = bitCount == 32 && hasAlpha ? dib[s + 3] : (byte)255;
                buffer.Pixels[t] = (byte)(dib[s] * a / 255);
                buffer.Pixels[t + 1] = (byte)(dib[s + 1] * a / 255);
                buffer.Pixels[t + 2] = (byte)(dib[s + 2] * a / 255);
                buffer.Pixels[t + 3] = a;
            }
        }

        return buffer;
    }

    private sealed class Session : IDisposable
    {
        public void Dispose() => PInvoke.CloseClipboard();
    }
}
