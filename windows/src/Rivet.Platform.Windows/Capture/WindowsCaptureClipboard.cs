// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Rivet.Core.App;
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// Clipboard for captures (spec 01 §3.8.3, §7): one clipboard session with
/// the registered "PNG" format (keeps transparency), CF_DIBV5 (what most apps
/// paste), CF_HDROP with the cached file (Explorer and chat apps paste a file)
/// and a "&lt;app&gt;.CaptureSource" marker that clipboard history can use to
/// recognize copied screenshots. Text goes as CF_UNICODETEXT plus the marker.
/// </summary>
public sealed unsafe class WindowsCaptureClipboard(NativeWindowHost host) : ICaptureClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const uint CfHdrop = 15;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif"];

    private static readonly uint PngFormat = PInvoke.RegisterClipboardFormat("PNG");
    private static readonly uint MarkerFormat = PInvoke.RegisterClipboardFormat($"{AppIdentity.Id}.CaptureSource");

    public long ChangeCount => PInvoke.GetClipboardSequenceNumber();

    public bool SetImage(PixelBuffer image, byte[] png, string? filePath)
    {
        var dib = EncodeDibV5(image);
        var drop = filePath is null ? null : DropFiles(filePath);
        using var session = Open();
        if (session is null)
        {
            return false;
        }

        if (!PInvoke.EmptyClipboard())
        {
            return false;
        }

        var ok = PngFormat != 0 && SetData(PngFormat, png);
        ok &= SetData(CfDibV5, dib);
        if (drop is not null)
        {
            SetData(CfHdrop, drop);
        }

        Mark("screenshot");
        return ok;
    }

    public bool SetText(string text)
    {
        using var session = Open();
        if (session is null || !PInvoke.EmptyClipboard())
        {
            return false;
        }

        var ok = SetData(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
        Mark("text");
        return ok;
    }

    public ClipboardImage? ReadImage(long maxPixels)
    {
        // Copy the data out first and decode after closing, so the clipboard is not held while decoding.
        IReadOnlyList<string> files = [];
        byte[]? png = null;
        byte[]? dib = null;
        using (var session = Open())
        {
            if (session is null)
            {
                return null;
            }

            if (PInvoke.IsClipboardFormatAvailable(CfHdrop))
            {
                files = ReadFiles();
            }

            if (PngFormat != 0 && PInvoke.IsClipboardFormatAvailable(PngFormat))
            {
                png = ReadBytes(PInvoke.GetClipboardData(PngFormat));
            }

            if (png is null)
            {
                foreach (var format in new[] { CfDibV5, CfDib })
                {
                    if (PInvoke.IsClipboardFormatAvailable(format))
                    {
                        dib = ReadBytes(PInvoke.GetClipboardData(format));
                        if (dib is not null)
                        {
                            break;
                        }
                    }
                }
            }
        }

        // A file copied in Explorer also carries a small icon image: the file wins.
        foreach (var file in files)
        {
            if (ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                && CaptureImaging.DecodeFile(file) is { } fromFile && (long)fromFile.Width * fromFile.Height <= maxPixels)
            {
                return new ClipboardImage(fromFile, file);
            }
        }

        if (png is not null && CaptureImaging.Decode(png) is { } fromPng)
        {
            return (long)fromPng.Width * fromPng.Height <= maxPixels ? new ClipboardImage(CaptureImaging.AtLeastOneX(fromPng), null) : null;
        }

        if (dib is not null && DecodeDib(dib, maxPixels) is { } fromDib)
        {
            return new ClipboardImage(fromDib, null);
        }

        return null;
    }

    private void Mark(string kind)
    {
        if (MarkerFormat != 0)
        {
            SetData(MarkerFormat, Encoding.UTF8.GetBytes(kind + "\0"));
        }
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

        Log.Warn("capture", "The clipboard stayed locked by another app.");
        return null;
    }

    private static bool SetData(uint format, byte[] bytes)
    {
        var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (global.IsNull)
        {
            return false;
        }

        var target = PInvoke.GlobalLock(global);
        if (target == null)
        {
            PInvoke.GlobalFree(global);
            return false;
        }

        Marshal.Copy(bytes, 0, (nint)target, bytes.Length);
        PInvoke.GlobalUnlock(global);
        if (PInvoke.SetClipboardData(format, new HANDLE((void*)global.Value)).IsNull)
        {
            PInvoke.GlobalFree(global);
            return false;
        }

        return true;
    }

    private static byte[]? ReadBytes(HANDLE handle)
    {
        if (handle.IsNull)
        {
            return null;
        }

        var global = new HGLOBAL(handle.Value);
        var size = (int)Math.Min(int.MaxValue, (long)PInvoke.GlobalSize(global));
        if (size <= 0)
        {
            return null;
        }

        var pointer = PInvoke.GlobalLock(global);
        if (pointer == null)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy((nint)pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            PInvoke.GlobalUnlock(global);
        }
    }

    private static List<string> ReadFiles()
    {
        var handle = PInvoke.GetClipboardData(CfHdrop);
        var files = new List<string>();
        if (handle.IsNull)
        {
            return files;
        }

        var drop = new HDROP((void*)handle.Value);
        var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            for (uint i = 0; i < count && i < 64; i++)
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

    /// <summary>DROPFILES header (20 bytes) and a double-null-terminated UTF-16 list.</summary>
    private static byte[] DropFiles(string path)
    {
        var list = Encoding.Unicode.GetBytes(Path.GetFullPath(path) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        list.CopyTo(data, 20);
        return data;
    }

    /// <summary>BITMAPV5HEADER + bottom-up 32-bit pixels with straight alpha; resolution carries the DPI.</summary>
    internal static byte[] EncodeDibV5(PixelBuffer image)
    {
        var headerSize = sizeof(BITMAPV5HEADER);
        var rowBytes = image.Width * 4;
        var data = new byte[headerSize + (rowBytes * image.Height)];
        var pelsPerMeter = (int)Math.Round(96 * image.Scale / 0.0254);
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)headerSize,
            bV5Width = image.Width,
            bV5Height = image.Height,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5SizeImage = (uint)(rowBytes * image.Height),
            bV5XPelsPerMeter = pelsPerMeter,
            bV5YPelsPerMeter = pelsPerMeter,
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

    /// <summary>24/32-bit uncompressed or bitfield DIBs; the scale comes from the stored resolution.</summary>
    internal static PixelBuffer? DecodeDib(byte[] dib, long maxPixels)
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
        var pelsPerMeter = BitConverter.ToInt32(dib, 24);
        if (width <= 0 || rawHeight == 0 || bitCount is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        if ((long)width * height > maxPixels)
        {
            return null;
        }

        var topDown = rawHeight < 0;
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        var rowBytes = ((width * bitCount) + 31) / 32 * 4;
        if (offset + ((long)rowBytes * height) > dib.Length)
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

        // Apps often stamp 72 DPI on ordinary bitmaps: only densities above 96 DPI mean a high-DPI image.
        var scale = pelsPerMeter > 0 ? Math.Max(1, ClipboardImageScale.FromDpi(pelsPerMeter * 0.0254) ?? 1) : 1;
        var buffer = new PixelBuffer(width, height) { Scale = scale };
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
