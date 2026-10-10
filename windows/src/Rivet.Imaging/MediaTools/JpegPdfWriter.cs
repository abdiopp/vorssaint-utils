// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Rivet.Imaging.MediaTools;

/// <summary>
/// A one-page PDF that embeds a JPEG as-is (DCTDecode), with the page size
/// equal to the image's pixel size in points (spec 07 §3.6.6, "Convert to PDF").
/// </summary>
public static class JpegPdfWriter
{
    public static void Write(Stream output, byte[] jpeg, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var offsets = new List<long>();
        var writer = new CountingWriter(output);

        writer.Ascii("%PDF-1.4\n%âãÏÓ\n");

        offsets.Add(writer.Position);
        writer.Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add(writer.Position);
        writer.Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets.Add(writer.Position);
        writer.Ascii(string.Create(CultureInfo.InvariantCulture,
            $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n"));

        offsets.Add(writer.Position);
        writer.Ascii(string.Create(CultureInfo.InvariantCulture,
            $"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"));
        writer.Bytes(jpeg);
        writer.Ascii("\nendstream\nendobj\n");

        var content = string.Create(CultureInfo.InvariantCulture, $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n");
        offsets.Add(writer.Position);
        writer.Ascii(string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n"));
        writer.Ascii(content);
        writer.Ascii("endstream\nendobj\n");

        var xref = writer.Position;
        writer.Ascii(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets)
        {
            writer.Ascii(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        }

        writer.Ascii(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        output.Flush();
    }

    private sealed class CountingWriter(Stream stream)
    {
        public long Position { get; private set; }

        public void Ascii(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            Bytes(bytes);
        }

        public void Bytes(byte[] bytes)
        {
            stream.Write(bytes);
            Position += bytes.Length;
        }
    }
}

/// <summary>
/// Keeps camera metadata when "Remove metadata" is off and both the source and
/// the output are JPEG: the source's Exif APP1 segment is copied into the new
/// file with the orientation reset to 1 (the pixels are already upright) and
/// the stored pixel dimensions updated. Other formats get no metadata.
/// </summary>
public static class JpegExif
{
    /// <summary>The Exif APP1 segment (including marker and length) of a JPEG, or null.</summary>
    public static byte[]? ExtractApp1(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length)
            {
                return null;
            }

            if (marker == 0xE1 && length > 8 && jpeg.Slice(i + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                return jpeg.Slice(i, length + 2).ToArray();
            }

            i += 2 + length;
        }

        return null;
    }

    /// <summary>Inserts <paramref name="app1"/> right after SOI (and any JFIF APP0) of <paramref name="jpeg"/>.</summary>
    public static byte[] Insert(byte[] jpeg, byte[] app1, int width, int height)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return jpeg;
        }

        var patched = (byte[])app1.Clone();
        Patch(patched, width, height);
        var insertAt = 2;
        if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
        {
            insertAt = 4 + ((jpeg[4] << 8) | jpeg[5]);
        }

        var result = new byte[jpeg.Length + patched.Length];
        Buffer.BlockCopy(jpeg, 0, result, 0, insertAt);
        Buffer.BlockCopy(patched, 0, result, insertAt, patched.Length);
        Buffer.BlockCopy(jpeg, insertAt, result, insertAt + patched.Length, jpeg.Length - insertAt);
        return result;
    }

    /// <summary>Sets Orientation (0x0112) to 1 and PixelX/YDimension (0xA002/0xA003) to the new size, in place.</summary>
    public static void Patch(byte[] app1, int width, int height)
    {
        const int tiff = 10; // FF E1 len(2) "Exif\0\0"
        if (app1.Length < tiff + 8)
        {
            return;
        }

        var little = app1[tiff] == (byte)'I';
        int U16(int at) => little ? app1[at] | (app1[at + 1] << 8) : (app1[at] << 8) | app1[at + 1];
        int U32(int at) => little
            ? app1[at] | (app1[at + 1] << 8) | (app1[at + 2] << 16) | (app1[at + 3] << 24)
            : (app1[at] << 24) | (app1[at + 1] << 16) | (app1[at + 2] << 8) | app1[at + 3];
        void W16(int at, int value)
        {
            if (little) { app1[at] = (byte)value; app1[at + 1] = (byte)(value >> 8); }
            else { app1[at] = (byte)(value >> 8); app1[at + 1] = (byte)value; }
        }

        void W32(int at, int value)
        {
            if (little) { app1[at] = (byte)value; app1[at + 1] = (byte)(value >> 8); app1[at + 2] = (byte)(value >> 16); app1[at + 3] = (byte)(value >> 24); }
            else { app1[at] = (byte)(value >> 24); app1[at + 1] = (byte)(value >> 16); app1[at + 2] = (byte)(value >> 8); app1[at + 3] = (byte)value; }
        }

        void VisitIfd(int ifdOffset, bool exifIfd)
        {
            var at = tiff + ifdOffset;
            if (ifdOffset <= 0 || at + 2 > app1.Length)
            {
                return;
            }

            var count = U16(at);
            for (var n = 0; n < count; n++)
            {
                var entry = at + 2 + (n * 12);
                if (entry + 12 > app1.Length)
                {
                    return;
                }

                var tag = U16(entry);
                var type = U16(entry + 2);
                switch (tag)
                {
                    case 0x0112 when !exifIfd:
                        W16(entry + 8, 1);
                        break;
                    case 0x8769 when !exifIfd:
                        VisitIfd(U32(entry + 8), exifIfd: true);
                        break;
                    case 0xA002 when exifIfd:
                        if (type == 3) W16(entry + 8, width); else W32(entry + 8, width);
                        break;
                    case 0xA003 when exifIfd:
                        if (type == 3) W16(entry + 8, height); else W32(entry + 8, height);
                        break;
                }
            }
        }

        VisitIfd(U32(tiff + 4), exifIfd: false);
    }
}
