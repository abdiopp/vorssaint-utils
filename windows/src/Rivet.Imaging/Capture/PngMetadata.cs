// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Rivet.Imaging.Capture;

/// <summary>
/// Reads and writes the PNG <c>pHYs</c> chunk (physical pixel density).
/// SkiaSharp's encoder does not write one, so captures get it injected after
/// IHDR. Convention (spec 01 §6.15 [Win decision]): DPI = 96 × scale, read
/// back as scale = DPI / 96.
/// </summary>
public static class PngMetadata
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>Returns a copy of <paramref name="png"/> carrying <paramref name="dpi"/> (replacing an existing pHYs).</summary>
    public static byte[] WithDpi(byte[] png, double dpi)
    {
        if (!IsPng(png) || png.Length < 33 || !double.IsFinite(dpi) || dpi <= 0)
        {
            return png;
        }

        var pixelsPerMeter = (uint)Math.Round(dpi / 0.0254);
        Span<byte> chunk = stackalloc byte[4 + 4 + 9 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[8..], pixelsPerMeter);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[12..], pixelsPerMeter);
        chunk[16] = 1; // unit: metre
        BinaryPrimitives.WriteUInt32BigEndian(chunk[17..], Crc(chunk[4..17]));

        using var output = new MemoryStream(png.Length + chunk.Length);
        output.Write(png, 0, 8);
        var offset = 8;
        var inserted = false;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            if (length < 0 || offset + 12 + length > png.Length)
            {
                return png;
            }

            var type = png.AsSpan(offset + 4, 4);
            var isPhys = type.SequenceEqual("pHYs"u8);
            if (!isPhys)
            {
                output.Write(png, offset, 12 + length);
            }

            if (!inserted && type.SequenceEqual("IHDR"u8))
            {
                output.Write(chunk);
                inserted = true;
            }

            offset += 12 + length;
        }

        return inserted ? output.ToArray() : png;
    }

    /// <summary>The horizontal DPI stored in pHYs, or null when absent or not in metres.</summary>
    public static double? ReadDpi(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png))
        {
            return null;
        }

        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                return null;
            }

            var type = png.Slice(offset + 4, 4);
            if (type.SequenceEqual("pHYs"u8) && length == 9)
            {
                var x = BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8)..]);
                var unit = png[offset + 16];
                return unit == 1 && x > 0 ? x * 0.0254 : null;
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                return null;
            }

            offset += 12 + length;
        }

        return null;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
