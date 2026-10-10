// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rivet.Core.Clipboard;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Clipboard;

/// <summary>
/// The clipboard lane on Windows (spec 06 §3.1.1, §7.2): one dedicated thread
/// owns a message-only window that opens the clipboard, listens with
/// AddClipboardFormatListener and pumps messages while idle (so apps
/// emptying a clipboard we own never wait on us). Every read and write runs
/// on that thread; the UI thread only posts work. Contents are never logged.
/// </summary>
public sealed unsafe class WindowsClipboardPlatform : IClipboardPlatform, IDisposable
{
    private const uint CfText = 1;
    private const uint CfBitmap = 2;
    private const uint CfMetafilePict = 3;
    private const uint CfOemText = 7;
    private const uint CfDib = 8;
    private const uint CfPalette = 9;
    private const uint CfUnicodeText = 13;
    private const uint CfEnhMetafile = 14;
    private const uint CfHdrop = 15;
    private const uint CfLocale = 16;
    private const uint CfDibV5 = 17;
    private const int MaxPngBytes = 16 * 1024 * 1024;
    private const int MaxDibBytes = 64 * 1024 * 1024;

    private static readonly Dictionary<uint, string> PredefinedNames = new()
    {
        [1] = "CF_TEXT", [2] = "CF_BITMAP", [3] = "CF_METAFILEPICT", [4] = "CF_SYLK", [5] = "CF_DIF", [6] = "CF_TIFF",
        [7] = "CF_OEMTEXT", [8] = "CF_DIB", [9] = "CF_PALETTE", [10] = "CF_PENDATA", [11] = "CF_RIFF", [12] = "CF_WAVE",
        [13] = "CF_UNICODETEXT", [14] = "CF_ENHMETAFILE", [15] = "CF_HDROP", [16] = "CF_LOCALE", [17] = "CF_DIBV5",
        [0x80] = "CF_OWNERDISPLAY", [0x81] = "CF_DSPTEXT", [0x82] = "CF_DSPBITMAP", [0x83] = "CF_DSPMETAFILEPICT", [0x8E] = "CF_DSPENHMETAFILE",
    };

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WNDPROC _wndProc;
    private readonly Thread _thread;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly Dictionary<string, uint> _registered = new(StringComparer.Ordinal);
    private HANDLE _wakeEvent;
    private HWND _hwnd;
    private volatile bool _stopping;
    private bool _listening;

    public WindowsClipboardPlatform()
    {
        _wndProc = WndProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClipboardLane" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public event EventHandler? Changed;

    public uint SequenceNumber => PInvoke.GetClipboardSequenceNumber();

    public void Post(Action work)
    {
        _work.Enqueue(work);
        PInvoke.SetEvent(_wakeEvent);
    }

    public void StartListening() => Post(() =>
    {
        if (!_listening && !_hwnd.IsNull)
        {
            _listening = PInvoke.AddClipboardFormatListener(_hwnd);
            if (!_listening)
            {
                Log.Warn("clipboard", $"AddClipboardFormatListener failed: {Marshal.GetLastWin32Error()}");
            }
        }
    });

    public void StopListening() => Post(() =>
    {
        if (_listening)
        {
            PInvoke.RemoveClipboardFormatListener(_hwnd);
            _listening = false;
        }
    });

    // ── The lane thread ────────────────────────────────────────────────

    private void Run()
    {
        _wakeEvent = PInvoke.CreateEvent((global::Windows.Win32.Security.SECURITY_ATTRIBUTES*)null, false, false, (PCWSTR)null);
        var className = $"{Rivet.Core.App.AppIdentity.Id}.ClipboardLane.{Environment.ProcessId}";
        var instance = new HINSTANCE(PInvoke.GetModuleHandle((PCWSTR)null).Value);
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = name,
            };
            PInvoke.RegisterClassEx(in wc);
            _hwnd = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, HMENU.Null, instance, null);
        }

        if (_hwnd.IsNull)
        {
            Log.Error("clipboard", $"The clipboard window could not be created: {Marshal.GetLastWin32Error()}");
        }

        _ready.Set();
        var handles = stackalloc HANDLE[1];
        handles[0] = _wakeEvent;
        while (!_stopping)
        {
            while (_work.TryDequeue(out var work))
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error("clipboard", "Clipboard work failed.", ex);
                }
            }

            PInvoke.MsgWaitForMultipleObjectsEx(1, handles, 1000, QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
            while (PInvoke.PeekMessage(out var msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }
        }

        if (_listening)
        {
            PInvoke.RemoveClipboardFormatListener(_hwnd);
        }

        PInvoke.DestroyWindow(_hwnd);
        fixed (char* name = className)
        {
            PInvoke.UnregisterClass(name, instance);
        }

        PInvoke.CloseHandle(_wakeEvent);
    }

    private LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_CLIPBOARDUPDATE)
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Error("clipboard", "Clipboard change handler failed.", ex);
            }

            return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // ── Reading ────────────────────────────────────────────────────────

    public ClipboardContent Read(ClipboardReadParts parts)
    {
        var sequence = SequenceNumber;
        using var session = Open();
        if (session is null)
        {
            return new ClipboardContent { Sequence = sequence };
        }

        var formats = EnumerateFormats();
        var names = formats.Select(f => f.Name).ToList();
        if (ClipboardFormats.IsConcealed(names, ReadDword))
        {
            // A secret: nothing beyond the format list is ever read.
            return new ClipboardContent { Sequence = sequence, Formats = names, IsConcealed = true };
        }

        var content = new ClipboardContent
        {
            Sequence = sequence,
            Formats = names,
            IsOwnWrite = names.Contains(ClipboardFormats.OwnSource, StringComparer.OrdinalIgnoreCase),
        };

        if (parts.HasFlag(ClipboardReadParts.Text) && Has(CfUnicodeText))
        {
            content = content with { Text = ReadUnicode(CfUnicodeText) };
        }

        if (parts.HasFlag(ClipboardReadParts.Html) && Format(ClipboardFormats.Html) is var html and not 0 && Has(html))
        {
            content = content with { Html = ReadBytes(html) is { } bytes ? Encoding.UTF8.GetString(TrimNull(bytes)) : null };
        }

        if (parts.HasFlag(ClipboardReadParts.Rtf) && Format(ClipboardFormats.Rtf) is var rtf and not 0 && Has(rtf))
        {
            content = content with { Rtf = ReadBytes(rtf) is { } bytes ? Encoding.Latin1.GetString(TrimNull(bytes)) : null };
        }

        if (parts.HasFlag(ClipboardReadParts.Url))
        {
            content = content with { Url = ReadUrl() };
        }

        if (parts.HasFlag(ClipboardReadParts.Files) && Has(CfHdrop))
        {
            content = content with { Files = ReadFiles() };
        }

        if (parts.HasFlag(ClipboardReadParts.Image) && (content.Files is null || content.Files.Count == 0 || content.Files.Count > 100))
        {
            var (png, width, height) = ReadImage();
            if (png is not null)
            {
                content = content with { Png = png, ImageWidth = width, ImageHeight = height };
            }
        }

        if (parts.HasFlag(ClipboardReadParts.Owner))
        {
            content = content with { OwnerApp = OwnerIdentity() };
        }

        return content;
    }

    private uint? ReadDword(string formatName)
    {
        var format = Format(formatName);
        if (format == 0 || !Has(format))
        {
            return null;
        }

        var bytes = ReadBytes(format);
        return bytes is { Length: >= 4 } ? BitConverter.ToUInt32(bytes, 0) : null;
    }

    private string? ReadUrl()
    {
        if (Format(ClipboardFormats.UrlW) is var urlW and not 0 && Has(urlW) && ReadUnicode(urlW) is { Length: > 0 } wide)
        {
            return wide.Trim();
        }

        if (Format(ClipboardFormats.Url) is var url and not 0 && Has(url) && ReadBytes(url) is { } ansi)
        {
            return Encoding.Default.GetString(TrimNull(ansi)).Trim();
        }

        if (Format(ClipboardFormats.MozUrl) is var moz and not 0 && Has(moz) && ReadBytes(moz) is { } mozBytes)
        {
            var text = Encoding.Unicode.GetString(mozBytes).TrimEnd('\0');
            var newline = text.IndexOf('\n');
            return (newline >= 0 ? text[..newline] : text).Trim();
        }

        return null;
    }

    private List<string>? ReadFiles()
    {
        var handle = PInvoke.GetClipboardData(CfHdrop);
        if (handle.IsNull)
        {
            return null;
        }

        var drop = new HDROP((void*)handle.Value);
        var count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, default, 0);
        var files = new List<string>((int)Math.Min(count, 101));
        var buffer = new char[32768];
        fixed (char* p = buffer)
        {
            for (uint i = 0; i < count && i < 101; i++)
            {
                var length = PInvoke.DragQueryFile(drop, i, new PWSTR(p), (uint)buffer.Length);
                if (length > 0)
                {
                    files.Add(new string(p, 0, (int)length));
                }
            }
        }

        return files;
    }

    private (byte[]? Png, int Width, int Height) ReadImage()
    {
        if (Format(ClipboardFormats.Png) is var pngFormat and not 0 && Has(pngFormat))
        {
            var png = ReadBytes(pngFormat, MaxPngBytes);
            if (png is not null && ImageSize(png) is { } size)
            {
                return (png, size.Width, size.Height);
            }
        }

        foreach (var format in new[] { CfDibV5, CfDib })
        {
            if (!Has(format))
            {
                continue;
            }

            var dib = ReadBytes(format, MaxDibBytes);
            if (dib is null || DibCodec.Decode(dib) is not { } buffer)
            {
                continue;
            }

            using var image = SkiaConvert.ToImage(buffer);
            var png = SkiaConvert.EncodePng(image);
            if (png.Length <= MaxPngBytes)
            {
                return (png, buffer.Width, buffer.Height);
            }
        }

        return (null, 0, 0);
    }

    private static (int Width, int Height)? ImageSize(byte[] png)
    {
        using var data = SKData.CreateCopy(png);
        using var codec = SKCodec.Create(data);
        return codec is { Info.Width: > 0, Info.Height: > 0 } ? (codec.Info.Width, codec.Info.Height) : null;
    }

    private string? OwnerIdentity()
    {
        var owner = PInvoke.GetClipboardOwner();
        if (owner.IsNull)
        {
            return null;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(owner, &pid);
        if (pid == 0 || pid == _ownProcessId)
        {
            return null;
        }

        return ProcessIdentity.Of((int)pid)?.Identity;
    }

    // ── Writing ────────────────────────────────────────────────────────

    public bool Write(ClipboardWriteData data, ClipboardWriteMarks marks)
    {
        // Prepare everything before the clipboard is opened (keeps it open briefly).
        var items = new List<(uint Format, byte[] Bytes)>();
        if (data.Text is { } text)
        {
            items.Add((CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0")));
        }

        if (data.Url is { } url)
        {
            if (Format(ClipboardFormats.UrlW) is var urlW and not 0)
            {
                items.Add((urlW, Encoding.Unicode.GetBytes(url + "\0")));
            }

            if (Format(ClipboardFormats.Url) is var urlA and not 0)
            {
                items.Add((urlA, Encoding.Default.GetBytes(url + "\0")));
            }
        }

        if (data.HtmlFragment is { } fragment && Format(ClipboardFormats.Html) is var html and not 0)
        {
            items.Add((html, CfHtml.Build(fragment)));
        }

        if (data.Rtf is { } rtf && Format(ClipboardFormats.Rtf) is var rtfFormat and not 0)
        {
            items.Add((rtfFormat, Encoding.Latin1.GetBytes(rtf + "\0")));
        }

        if (data.Png is { } png)
        {
            if (Format(ClipboardFormats.Png) is var pngFormat and not 0)
            {
                items.Add((pngFormat, png));
            }

            using var bitmap = SKBitmap.Decode(png);
            if (bitmap is not null)
            {
                items.Add((CfDibV5, DibCodec.EncodeV5(SkiaConvert.ToPixelBuffer(bitmap))));
            }
        }

        if (data.Files is { Count: > 0 } files)
        {
            items.Add((CfHdrop, DropFiles(files)));
            if (Format("Preferred DropEffect") is var effect and not 0)
            {
                items.Add((effect, BitConverter.GetBytes(1u))); // DROPEFFECT_COPY
            }
        }

        AddMarks(items, marks);
        if (items.Count == 0)
        {
            return false;
        }

        using var session = Open();
        if (session is null || !PInvoke.EmptyClipboard())
        {
            return false;
        }

        var ok = true;
        foreach (var (format, bytes) in items)
        {
            ok &= SetData(format, bytes);
        }

        return ok;
    }

    private void AddMarks(List<(uint Format, byte[] Bytes)> items, ClipboardWriteMarks marks)
    {
        if (marks.HasFlag(ClipboardWriteMarks.OwnSource) && Format(ClipboardFormats.OwnSource) is var own and not 0)
        {
            items.Add((own, [1]));
        }

        if (marks.HasFlag(ClipboardWriteMarks.Transient))
        {
            if (Format(ClipboardFormats.ExcludeFromMonitors) is var exclude and not 0)
            {
                items.Add((exclude, [0]));
            }

            if (Format(ClipboardFormats.CanIncludeInHistory) is var history and not 0)
            {
                items.Add((history, BitConverter.GetBytes(0u)));
            }

            if (Format(ClipboardFormats.CanUploadToCloud) is var cloud and not 0)
            {
                items.Add((cloud, BitConverter.GetBytes(0u)));
            }
        }
    }

    public bool Clear()
    {
        using var session = Open();
        return session is not null && PInvoke.EmptyClipboard();
    }

    // ── Snapshot and restore (transient paste) ─────────────────────────

    public ClipboardSnapshot? Snapshot(long maxBytes)
    {
        using var session = Open();
        if (session is null)
        {
            return null;
        }

        var formats = EnumerateFormats();
        var ids = formats.Select(f => f.Id).ToHashSet();
        var hasDib = ids.Contains(CfDib) || ids.Contains(CfDibV5);
        var items = new List<(uint Format, byte[] Bytes)>();
        long total = 0;
        foreach (var (id, _) in formats)
        {
            // The system re-synthesizes these from the formats we keep.
            if (id is CfText or CfOemText or CfLocale && ids.Contains(CfUnicodeText))
            {
                continue;
            }

            if (id is CfBitmap or CfPalette && hasDib)
            {
                continue;
            }

            if (id is CfDib && ids.Contains(CfDibV5))
            {
                continue;
            }

            // GDI and private handles cannot be copied as memory: fail open.
            if (id is CfBitmap or CfPalette or CfEnhMetafile or CfMetafilePict or 0x80 or >= 0x200 and <= 0x3FF)
            {
                return null;
            }

            var bytes = ReadBytes(id, int.MaxValue);
            if (bytes is null)
            {
                return null;
            }

            total += bytes.Length;
            if (total > maxBytes)
            {
                return null;
            }

            items.Add((id, bytes));
        }

        return new Win32Snapshot(items, total);
    }

    public bool Restore(ClipboardSnapshot snapshot, ClipboardWriteMarks marks)
    {
        if (snapshot is not Win32Snapshot saved)
        {
            return false;
        }

        var items = saved.Items.ToList();
        AddMarks(items, marks);
        using var session = Open();
        if (session is null || !PInvoke.EmptyClipboard())
        {
            return false;
        }

        var ok = true;
        foreach (var (format, bytes) in items)
        {
            ok &= SetData(format, bytes);
        }

        return ok;
    }

    private sealed class Win32Snapshot(List<(uint Format, byte[] Bytes)> items, long bytes) : ClipboardSnapshot
    {
        public IReadOnlyList<(uint Format, byte[] Bytes)> Items { get; } = items;

        public override long ByteCount { get; } = bytes;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private Session? Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (PInvoke.OpenClipboard(_hwnd))
            {
                return new Session();
            }

            Thread.Sleep(20);
        }

        Log.Warn("clipboard", "The clipboard stayed locked by another app.");
        return null;
    }

    private uint Format(string name)
    {
        if (_registered.TryGetValue(name, out var id))
        {
            return id;
        }

        id = PInvoke.RegisterClipboardFormat(name);
        _registered[name] = id;
        return id;
    }

    private static bool Has(uint format) => PInvoke.IsClipboardFormatAvailable(format);

    private List<(uint Id, string Name)> EnumerateFormats()
    {
        var result = new List<(uint, string)>();
        var buffer = stackalloc char[256];
        uint format = 0;
        while ((format = PInvoke.EnumClipboardFormats(format)) != 0)
        {
            string name;
            if (PredefinedNames.TryGetValue(format, out var predefined))
            {
                name = predefined;
            }
            else
            {
                var length = PInvoke.GetClipboardFormatName(format, new PWSTR(buffer), 256);
                name = length > 0 ? new string(buffer, 0, length) : $"#{format}";
            }

            result.Add((format, name));
        }

        return result;
    }

    private static string? ReadUnicode(uint format)
    {
        var bytes = ReadBytes(format);
        if (bytes is null)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static byte[]? ReadBytes(uint format, int max = MaxDibBytes)
    {
        var handle = PInvoke.GetClipboardData(format);
        if (handle.IsNull)
        {
            return null;
        }

        var global = new HGLOBAL(handle.Value);
        var size = (long)(ulong)PInvoke.GlobalSize(global);
        if (size <= 0 || size > max)
        {
            return size == 0 ? [] : null;
        }

        var pointer = PInvoke.GlobalLock(global);
        if (pointer == null)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy((nint)pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            PInvoke.GlobalUnlock(global);
        }
    }

    private static bool SetData(uint format, byte[] bytes)
    {
        var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)Math.Max(1, bytes.Length));
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

    private static byte[] TrimNull(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return end >= 0 ? bytes[..end] : bytes;
    }

    /// <summary>DROPFILES (20 bytes, wide) followed by a double-null-terminated path list.</summary>
    private static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var data = new byte[20 + list.Length];
        BitConverter.GetBytes(20).CopyTo(data, 0);
        BitConverter.GetBytes(1).CopyTo(data, 16);
        list.CopyTo(data, 20);
        return data;
    }

    public void Dispose()
    {
        _stopping = true;
        if (!_wakeEvent.IsNull)
        {
            PInvoke.SetEvent(_wakeEvent);
        }
    }

    private sealed class Session : IDisposable
    {
        public void Dispose() => PInvoke.CloseClipboard();
    }
}

/// <summary>The CF_HTML clipboard format: a header of byte offsets around the UTF-8 document.</summary>
internal static class CfHtml
{
    public static byte[] Build(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string prefix = "<html><body>\r\n<!--StartFragment-->";
        const string suffix = "<!--EndFragment-->\r\n</body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, startHtml, endHtml, startFragment, endFragment) + prefix + fragment + suffix;
        return Encoding.UTF8.GetBytes(text + "\0");
    }
}

/// <summary>DIB ⇄ pixel buffers (24/32-bit, BI_RGB and BI_BITFIELDS; bottom-up or top-down).</summary>
internal static class DibCodec
{
    public static PixelBuffer? Decode(byte[] dib)
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
        if (width <= 0 || rawHeight == 0 || width > 32768 || Math.Abs(rawHeight) > 32768 || bitCount is not (24 or 32) || compression is not (0 or 3))
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + (colorsUsed * 4);
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

    /// <summary>BITMAPV5HEADER + bottom-up 32-bit pixels with straight alpha.</summary>
    public static unsafe byte[] EncodeV5(PixelBuffer image)
    {
        var headerSize = sizeof(global::Windows.Win32.Graphics.Gdi.BITMAPV5HEADER);
        var rowBytes = image.Width * 4;
        var data = new byte[headerSize + (rowBytes * image.Height)];
        var header = new global::Windows.Win32.Graphics.Gdi.BITMAPV5HEADER
        {
            bV5Size = (uint)headerSize,
            bV5Width = image.Width,
            bV5Height = image.Height,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = global::Windows.Win32.Graphics.Gdi.BI_COMPRESSION.BI_BITFIELDS,
            bV5SizeImage = (uint)(rowBytes * image.Height),
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = 0x73524742,
            bV5Intent = 4,
        };
        fixed (byte* d = data)
        {
            *(global::Windows.Win32.Graphics.Gdi.BITMAPV5HEADER*)d = header;
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
}
