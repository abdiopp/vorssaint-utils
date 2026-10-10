// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Imaging.RecordingEditor;

/// <summary>One decoded GIF frame: BGRA (opaque or transparent where the GIF says so) and its delay.</summary>
public sealed record GifFrame(int Width, int Height, byte[] Bgra, int DelayCentiseconds);

/// <summary>
/// A small GIF89a/87a reader. "Copy as GIF" uses it to verify the file is a
/// GIF with at least one frame before the clipboard is touched (spec 02
/// §3.34); tests use it for encode/decode round trips. Frames are composited
/// onto the logical screen honouring disposal methods.
/// </summary>
public static class GifDecoder
{
    public sealed record Result(int Width, int Height, int LoopCount, IReadOnlyList<GifFrame> Frames);

    /// <summary>Header and at least one image descriptor present, without decoding pixels.</summary>
    public static bool LooksLikeGif(ReadOnlySpan<byte> data, out int frameCount)
    {
        frameCount = 0;
        try
        {
            frameCount = Decode(data.ToArray(), decodePixels: false)?.Frames.Count ?? 0;
            return frameCount > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Null when the data is not a GIF.</summary>
    public static Result? Decode(byte[] data, bool decodePixels = true)
    {
        if (data.Length < 13 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F')
        {
            return null;
        }

        var pos = 6;
        var width = data[pos] | (data[pos + 1] << 8);
        var height = data[pos + 2] | (data[pos + 3] << 8);
        var packed = data[pos + 4];
        pos += 7;
        byte[]? globalTable = null;
        if ((packed & 0x80) != 0)
        {
            var size = 3 * (1 << ((packed & 7) + 1));
            globalTable = data.AsSpan(pos, size).ToArray();
            pos += size;
        }

        var frames = new List<GifFrame>();
        var loop = 1;
        var delay = 0;
        var disposal = 0;
        var transparent = -1;
        byte[]? screen = decodePixels ? new byte[Math.Max(1, width * height * 4)] : null;
        while (pos < data.Length)
        {
            var block = data[pos++];
            if (block == 0x3B)
            {
                break;
            }

            if (block == 0x21)
            {
                var label = data[pos++];
                if (label == 0xF9 && data[pos] >= 4)
                {
                    var flags = data[pos + 1];
                    disposal = (flags >> 2) & 7;
                    delay = data[pos + 2] | (data[pos + 3] << 8);
                    transparent = (flags & 1) != 0 ? data[pos + 4] : -1;
                }
                else if (label == 0xFF && data[pos] == 11 && data.AsSpan(pos + 1, 11).SequenceEqual("NETSCAPE2.0"u8))
                {
                    var sub = pos + 12;
                    if (data[sub] >= 3 && data[sub + 1] == 1)
                    {
                        loop = data[sub + 2] | (data[sub + 3] << 8);
                    }
                }

                pos = SkipSubBlocks(data, pos);
                continue;
            }

            if (block != 0x2C)
            {
                throw new InvalidDataException("Unknown GIF block.");
            }

            var left = data[pos] | (data[pos + 1] << 8);
            var top = data[pos + 2] | (data[pos + 3] << 8);
            var w = data[pos + 4] | (data[pos + 5] << 8);
            var h = data[pos + 6] | (data[pos + 7] << 8);
            var imagePacked = data[pos + 8];
            pos += 9;
            var table = globalTable;
            if ((imagePacked & 0x80) != 0)
            {
                var size = 3 * (1 << ((imagePacked & 7) + 1));
                table = data.AsSpan(pos, size).ToArray();
                pos += size;
            }

            var interlaced = (imagePacked & 0x40) != 0;
            var minCodeSize = data[pos++];
            var start = pos;
            pos = SkipSubBlocks(data, pos);
            if (!decodePixels)
            {
                frames.Add(new GifFrame(w, h, [], delay));
                continue;
            }

            if (table is null)
            {
                throw new InvalidDataException("A GIF frame without a colour table.");
            }

            var indices = DecodeLzw(data, start, minCodeSize, w * h);
            var previous = disposal == 3 ? (byte[])screen!.Clone() : null;
            for (var i = 0; i < w * h; i++)
            {
                var row = interlaced ? InterlacedRow(i / Math.Max(1, w), h) : i / Math.Max(1, w);
                var x = left + (i % Math.Max(1, w));
                var y = top + row;
                if (x >= width || y >= height)
                {
                    continue;
                }

                var index = indices[i];
                if (index == transparent)
                {
                    continue;
                }

                var o = ((y * width) + x) * 4;
                var t = index * 3;
                if (t + 2 < table.Length)
                {
                    screen![o] = table[t + 2];
                    screen[o + 1] = table[t + 1];
                    screen[o + 2] = table[t];
                    screen[o + 3] = 255;
                }
            }

            frames.Add(new GifFrame(width, height, (byte[])screen!.Clone(), delay));
            if (disposal == 2)
            {
                for (var y = top; y < Math.Min(height, top + h); y++)
                {
                    Array.Clear(screen!, ((y * width) + left) * 4, Math.Min(w, width - left) * 4);
                }
            }
            else if (disposal == 3 && previous is not null)
            {
                screen = previous;
            }

            transparent = -1;
            disposal = 0;
            delay = 0;
        }

        return new Result(width, height, loop, frames);
    }

    private static int InterlacedRow(int pass, int height)
    {
        // Rows are stored in the order: every 8th from 0, every 8th from 4, every 4th from 2, every 2nd from 1.
        int[] starts = [0, 4, 2, 1];
        int[] steps = [8, 8, 4, 2];
        var index = pass;
        for (var p = 0; p < 4; p++)
        {
            var count = (height - starts[p] + steps[p] - 1) / steps[p];
            if (index < count)
            {
                return starts[p] + (index * steps[p]);
            }

            index -= count;
        }

        return height - 1;
    }

    private static int SkipSubBlocks(byte[] data, int pos)
    {
        while (pos < data.Length)
        {
            var size = data[pos++];
            if (size == 0)
            {
                break;
            }

            pos += size;
        }

        return pos;
    }

    private static byte[] DecodeLzw(byte[] data, int pos, int minCodeSize, int pixelCount)
    {
        var output = new byte[pixelCount];
        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var codeSize = minCodeSize + 1;
        var next = clear + 2;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var length = new int[4096];
        for (var i = 0; i < clear; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            length[i] = 1;
        }

        var stack = new byte[4097];
        var outIndex = 0;
        var old = -1;
        var bitBuffer = 0;
        var bitCount = 0;
        var blockRemaining = 0;
        while (outIndex < pixelCount)
        {
            while (bitCount < codeSize)
            {
                if (blockRemaining == 0)
                {
                    if (pos >= data.Length)
                    {
                        return output;
                    }

                    blockRemaining = data[pos++];
                    if (blockRemaining == 0)
                    {
                        return output;
                    }
                }

                bitBuffer |= data[pos++] << bitCount;
                bitCount += 8;
                blockRemaining--;
            }

            var code = bitBuffer & ((1 << codeSize) - 1);
            bitBuffer >>= codeSize;
            bitCount -= codeSize;
            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = clear + 2;
                old = -1;
                continue;
            }

            if (code == eoi)
            {
                break;
            }

            int first;
            if (old == -1)
            {
                if (code >= clear)
                {
                    throw new InvalidDataException("Bad first LZW code.");
                }

                output[outIndex++] = suffix[code];
                old = code;
                continue;
            }

            int emit;
            if (code < next)
            {
                emit = code;
            }
            else if (code == next)
            {
                emit = old;
            }
            else
            {
                throw new InvalidDataException("Bad LZW code.");
            }

            // Unwind the string for `emit`.
            var sp = 0;
            var c = emit;
            while (c >= 0 && sp < stack.Length)
            {
                stack[sp++] = suffix[c];
                c = prefix[c];
            }

            first = stack[sp - 1];
            for (var i = sp - 1; i >= 0 && outIndex < pixelCount; i--)
            {
                output[outIndex++] = stack[i];
            }

            if (code == next && outIndex < pixelCount)
            {
                output[outIndex++] = (byte)first;
            }

            if (next < 4096)
            {
                prefix[next] = old;
                suffix[next] = (byte)first;
                length[next] = length[old] + 1;
                next++;
                if (next == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            old = code;
        }

        return output;
    }
}
