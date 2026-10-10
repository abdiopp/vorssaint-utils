// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Imaging.MediaTools;

/// <summary>A GIF colour table (up to 255 colours; index 255 is reserved for transparency).</summary>
public sealed class GifPalette
{
    public const int TransparentIndex = 255;
    private readonly byte[] _lookup = new byte[32768];
    private readonly bool[] _known = new bool[32768];

    public GifPalette(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (colors.Count == 0 || colors.Count > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), "A palette holds 1–255 colours.");
        }

        Colors = colors;
    }

    public IReadOnlyList<(byte R, byte G, byte B)> Colors { get; }

    /// <summary>Nearest palette index for an RGB colour (cached on a 5-bit grid).</summary>
    public byte Map(int r, int g, int b)
    {
        var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
        if (_known[key])
        {
            return _lookup[key];
        }

        // Search from the centre of the 5-bit cell for stable results.
        var cr = ((r >> 3) << 3) + 4;
        var cg = ((g >> 3) << 3) + 4;
        var cb = ((b >> 3) << 3) + 4;
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < Colors.Count; i++)
        {
            var (pr, pg, pb) = Colors[i];
            var dr = pr - cr;
            var dg = pg - cg;
            var db = pb - cb;
            // Weighted for the eye's sensitivity (green > red > blue).
            var distance = (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        _lookup[key] = (byte)best;
        _known[key] = true;
        return (byte)best;
    }

    /// <summary>
    /// Median cut over a 5-bit RGB histogram of the sample frames (BGRA), then
    /// two k-means refinement passes. Fully transparent pixels are skipped.
    /// </summary>
    public static GifPalette Build(IEnumerable<PixelBuffer> samples, int maxColors = 255)
    {
        maxColors = Math.Clamp(maxColors, 2, 255);
        var histogram = new int[32768];
        var step = 1;
        foreach (var frame in samples)
        {
            // Sample about 250k pixels per frame at most.
            var pixels = (long)frame.Width * frame.Height;
            step = Math.Max(1, (int)(pixels / 250_000));
            for (var y = 0; y < frame.Height; y += 1)
            {
                var row = y * frame.Stride;
                for (var x = (y * 7) % step; x < frame.Width; x += step)
                {
                    var i = row + (x * 4);
                    if (frame.Pixels[i + 3] == 0)
                    {
                        continue;
                    }

                    var key = ((frame.Pixels[i + 2] >> 3) << 10) | ((frame.Pixels[i + 1] >> 3) << 5) | (frame.Pixels[i] >> 3);
                    histogram[key]++;
                }
            }
        }

        var entries = new List<(int Key, int Count)>();
        for (var k = 0; k < histogram.Length; k++)
        {
            if (histogram[k] > 0)
            {
                entries.Add((k, histogram[k]));
            }
        }

        if (entries.Count == 0)
        {
            return new GifPalette([(0, 0, 0), (255, 255, 255)]);
        }

        if (entries.Count <= maxColors)
        {
            return new GifPalette(entries.Select(e => Expand(e.Key)).ToList());
        }

        // Median cut: split the box with the largest (range × population) along its widest channel.
        var boxes = new List<List<(int Key, int Count)>> { entries };
        while (boxes.Count < maxColors)
        {
            var index = -1;
            long bestScore = -1;
            for (var b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2)
                {
                    continue;
                }

                var (range, _) = WidestChannel(boxes[b]);
                long population = boxes[b].Sum(e => (long)e.Count);
                var score = range * population;
                if (score > bestScore)
                {
                    bestScore = score;
                    index = b;
                }
            }

            if (index < 0)
            {
                break;
            }

            var box = boxes[index];
            var (_, channel) = WidestChannel(box);
            box.Sort((a, c) => Channel(a.Key, channel).CompareTo(Channel(c.Key, channel)));
            long total = box.Sum(e => (long)e.Count);
            long running = 0;
            var split = 1;
            for (var i = 0; i < box.Count - 1; i++)
            {
                running += box[i].Count;
                if (running * 2 >= total)
                {
                    split = i + 1;
                    break;
                }
            }

            boxes[index] = box.GetRange(0, split);
            boxes.Add(box.GetRange(split, box.Count - split));
        }

        var centres = boxes.Select(Mean).ToArray();

        // Two k-means passes over the histogram entries.
        for (var pass = 0; pass < 2; pass++)
        {
            var sums = new long[centres.Length, 4];
            foreach (var (key, count) in entries)
            {
                var (r, g, b) = Expand(key);
                var nearest = Nearest(centres, r, g, b);
                sums[nearest, 0] += r * (long)count;
                sums[nearest, 1] += g * (long)count;
                sums[nearest, 2] += b * (long)count;
                sums[nearest, 3] += count;
            }

            for (var c = 0; c < centres.Length; c++)
            {
                if (sums[c, 3] > 0)
                {
                    centres[c] = ((byte)(sums[c, 0] / sums[c, 3]), (byte)(sums[c, 1] / sums[c, 3]), (byte)(sums[c, 2] / sums[c, 3]));
                }
            }
        }

        return new GifPalette(centres.Distinct().ToList());
    }

    private static (byte R, byte G, byte B) Expand(int key) =>
        ((byte)(((key >> 10) & 31) << 3 | 4), (byte)(((key >> 5) & 31) << 3 | 4), (byte)((key & 31) << 3 | 4));

    private static int Channel(int key, int channel) => channel switch
    {
        0 => (key >> 10) & 31,
        1 => (key >> 5) & 31,
        _ => key & 31,
    };

    private static (int Range, int Channel) WidestChannel(List<(int Key, int Count)> box)
    {
        var best = (Range: -1, Channel: 0);
        for (var c = 0; c < 3; c++)
        {
            var min = 31;
            var max = 0;
            foreach (var (key, _) in box)
            {
                var v = Channel(key, c);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            // Green differences matter most to the eye.
            var range = (max - min) * (c == 1 ? 4 : c == 0 ? 3 : 2);
            if (range > best.Range)
            {
                best = (range, c);
            }
        }

        return best;
    }

    private static (byte R, byte G, byte B) Mean(List<(int Key, int Count)> box)
    {
        long r = 0, g = 0, b = 0, n = 0;
        foreach (var (key, count) in box)
        {
            var (er, eg, eb) = Expand(key);
            r += er * (long)count;
            g += eg * (long)count;
            b += eb * (long)count;
            n += count;
        }

        return n == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static int Nearest((byte R, byte G, byte B)[] centres, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < centres.Length; i++)
        {
            var dr = centres[i].R - r;
            var dg = centres[i].G - g;
            var db = centres[i].B - b;
            var distance = (2 * dr * dr) + (4 * dg * dg) + (3 * db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }
}

public sealed record GifEncoderOptions
{
    /// <summary>Loop forever (NETSCAPE2.0 block with count 0); false = play once (no block).</summary>
    public bool Loop { get; init; } = true;

    /// <summary>Floyd–Steinberg error diffusion strength (0 = none, 1 = full).</summary>
    public double Dither { get; init; } = 0.75;

    /// <summary>
    /// Per-channel difference (sum of |ΔR|+|ΔG|+|ΔB|) below which a pixel counts as
    /// unchanged from the previous frame and is written as transparent, so static
    /// areas cost nothing and do not shimmer.
    /// </summary>
    public int StillThreshold { get; init; } = 9;
}

/// <summary>
/// A streaming GIF89a encoder written for the media tools (no platform codec
/// needed): one global palette, frame differencing with a transparent index
/// (disposal "do not dispose" and the changed region cropped), Floyd–Steinberg
/// dithering of the changed pixels only, and variable-width LZW in 255-byte
/// sub-blocks. Frames are BGRA; alpha is ignored (frames are opaque video).
/// </summary>
public sealed class GifEncoder : IDisposable
{
    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private readonly GifPalette _palette;
    private readonly GifEncoderOptions _options;
    private byte[]? _previousSource;
    private int _frames;
    private bool _finished;

    public GifEncoder(Stream output, int width, int height, GifPalette palette, GifEncoderOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width > 65535 || height > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "GIF frames are at most 65535 pixels per side.");
        }

        _output = output;
        _width = width;
        _height = height;
        _palette = palette;
        _options = options ?? new GifEncoderOptions();
        WriteHeader();
    }

    public int FrameCount => _frames;

    public long BytesWritten => _output.CanSeek ? _output.Position : -1;

    /// <summary>Adds a frame shown for <paramref name="delayCentiseconds"/> (GIF stores 10 ms units).</summary>
    public void AddFrame(PixelBuffer frame, int delayCentiseconds)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The GIF is already finished.");
        }

        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"Frame is {frame.Width}×{frame.Height}, expected {_width}×{_height}.", nameof(frame));
        }

        var source = Pack(frame);
        var changed = new bool[_width * _height];
        int left = _width, top = _height, right = -1, bottom = -1;
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var p = (y * _width) + x;
                var isChanged = _previousSource is null || Difference(source, _previousSource, p) >= _options.StillThreshold;
                changed[p] = isChanged;
                if (isChanged)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0)
        {
            // Nothing changed: a 1×1 transparent frame keeps the timing.
            left = top = 0;
            right = bottom = 0;
        }

        var w = right - left + 1;
        var h = bottom - top + 1;
        var indices = Quantize(source, changed, left, top, w, h, out var anyTransparent);

        // Keep the previous source for unchanged pixels so slow drifts still update eventually.
        if (_previousSource is null)
        {
            _previousSource = source;
        }
        else
        {
            for (var p = 0; p < changed.Length; p++)
            {
                if (changed[p])
                {
                    _previousSource[(p * 3) + 0] = source[(p * 3) + 0];
                    _previousSource[(p * 3) + 1] = source[(p * 3) + 1];
                    _previousSource[(p * 3) + 2] = source[(p * 3) + 2];
                }
            }
        }

        WriteGraphicControl(Math.Clamp(delayCentiseconds, 1, 65535), anyTransparent || _frames > 0);
        WriteImage(left, top, w, h, indices);
        _frames++;
    }

    /// <summary>Writes the trailer. The stream stays open.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _output.WriteByte(0x3B);
        _output.Flush();
    }

    public void Dispose() => Finish();

    private byte[] Pack(PixelBuffer frame)
    {
        var rgb = new byte[_width * _height * 3];
        for (var y = 0; y < _height; y++)
        {
            var row = y * frame.Stride;
            for (var x = 0; x < _width; x++)
            {
                var s = row + (x * 4);
                var d = ((y * _width) + x) * 3;
                var a = frame.Pixels[s + 3];
                if (a == 255 || a == 0)
                {
                    rgb[d] = frame.Pixels[s + 2];
                    rgb[d + 1] = frame.Pixels[s + 1];
                    rgb[d + 2] = frame.Pixels[s];
                }
                else
                {
                    // Un-premultiply.
                    rgb[d] = (byte)Math.Min(255, frame.Pixels[s + 2] * 255 / a);
                    rgb[d + 1] = (byte)Math.Min(255, frame.Pixels[s + 1] * 255 / a);
                    rgb[d + 2] = (byte)Math.Min(255, frame.Pixels[s] * 255 / a);
                }
            }
        }

        return rgb;
    }

    private static int Difference(byte[] a, byte[] b, int p)
    {
        var i = p * 3;
        return Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
    }

    private byte[] Quantize(byte[] source, bool[] changed, int left, int top, int w, int h, out bool anyTransparent)
    {
        anyTransparent = false;
        var indices = new byte[w * h];
        var strength = Math.Clamp(_options.Dither, 0, 1);
        // Error rows for the current and next line (RGB, scaled by 16).
        var current = new int[(w + 2) * 3];
        var next = new int[(w + 2) * 3];
        for (var y = 0; y < h; y++)
        {
            Array.Clear(next);
            var leftToRight = (y & 1) == 0;
            for (var step = 0; step < w; step++)
            {
                var x = leftToRight ? step : w - 1 - step;
                var p = ((top + y) * _width) + left + x;
                if (!changed[p])
                {
                    indices[(y * w) + x] = GifPalette.TransparentIndex;
                    anyTransparent = true;
                    continue;
                }

                var e = (x + 1) * 3;
                var r = Math.Clamp(source[p * 3] + (current[e] >> 4), 0, 255);
                var g = Math.Clamp(source[(p * 3) + 1] + (current[e + 1] >> 4), 0, 255);
                var b = Math.Clamp(source[(p * 3) + 2] + (current[e + 2] >> 4), 0, 255);
                var index = _palette.Map(r, g, b);
                indices[(y * w) + x] = index;
                if (strength <= 0)
                {
                    continue;
                }

                var (pr, pg, pb) = _palette.Colors[index];
                var er = (int)((r - pr) * strength * 16);
                var eg = (int)((g - pg) * strength * 16);
                var eb = (int)((b - pb) * strength * 16);
                var forward = leftToRight ? 3 : -3;
                // Floyd–Steinberg: 7/16 ahead, 3/16 behind-below, 5/16 below, 1/16 ahead-below.
                Spread(current, e + forward, er, eg, eb, 7);
                Spread(next, e - forward, er, eg, eb, 3);
                Spread(next, e, er, eg, eb, 5);
                Spread(next, e + forward, er, eg, eb, 1);
            }

            (current, next) = (next, current);
        }

        return indices;
    }

    private static void Spread(int[] row, int i, int er, int eg, int eb, int weight)
    {
        if (i < 0 || i + 2 >= row.Length)
        {
            return;
        }

        row[i] += er * weight / 16;
        row[i + 1] += eg * weight / 16;
        row[i + 2] += eb * weight / 16;
    }

    private void WriteHeader()
    {
        Write("GIF89a"u8);
        WriteUInt16(_width);
        WriteUInt16(_height);
        _output.WriteByte(0xF7); // global table, 8-bit colour resolution, 256 entries
        _output.WriteByte(0);    // background index
        _output.WriteByte(0);    // pixel aspect
        for (var i = 0; i < 256; i++)
        {
            var (r, g, b) = i < _palette.Colors.Count ? _palette.Colors[i] : ((byte)0, (byte)0, (byte)0);
            _output.WriteByte(r);
            _output.WriteByte(g);
            _output.WriteByte(b);
        }

        if (_options.Loop)
        {
            Write([0x21, 0xFF, 0x0B]);
            Write("NETSCAPE2.0"u8);
            Write([0x03, 0x01, 0x00, 0x00, 0x00]); // loop count 0 = forever
        }
    }

    private void WriteGraphicControl(int delay, bool transparent)
    {
        // Disposal 1 (do not dispose) so unchanged pixels show through.
        var packed = (byte)((1 << 2) | (transparent ? 1 : 0));
        Write([0x21, 0xF9, 0x04, packed]);
        WriteUInt16(delay);
        _output.WriteByte(transparent ? (byte)GifPalette.TransparentIndex : (byte)0);
        _output.WriteByte(0);
    }

    private void WriteImage(int left, int top, int w, int h, byte[] indices)
    {
        _output.WriteByte(0x2C);
        WriteUInt16(left);
        WriteUInt16(top);
        WriteUInt16(w);
        WriteUInt16(h);
        _output.WriteByte(0); // no local table, not interlaced
        GifLzw.Encode(_output, indices, minCodeSize: 8);
    }

    private void WriteUInt16(int value)
    {
        _output.WriteByte((byte)(value & 0xFF));
        _output.WriteByte((byte)((value >> 8) & 0xFF));
    }

    private void Write(ReadOnlySpan<byte> bytes) => _output.Write(bytes);
}

/// <summary>GIF's variable-width LZW (codes up to 12 bits, clear at 4096), written in 255-byte sub-blocks.</summary>
public static class GifLzw
{
    public static void Encode(Stream output, ReadOnlySpan<byte> indices, int minCodeSize)
    {
        output.WriteByte((byte)minCodeSize);
        var writer = new SubBlockWriter(output);
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var codeSize = minCodeSize + 1;
        var nextCode = end + 1;
        var table = new Dictionary<int, int>(5003);

        writer.Write(clear, codeSize);
        if (indices.Length == 0)
        {
            writer.Write(end, codeSize);
            writer.Flush();
            output.WriteByte(0);
            return;
        }

        var prefix = (int)indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            var symbol = indices[i];
            var key = (prefix << 8) | symbol;
            if (table.TryGetValue(key, out var code))
            {
                prefix = code;
                continue;
            }

            writer.Write(prefix, codeSize);
            if (nextCode < 4096)
            {
                table[key] = nextCode++;
                if (nextCode > (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                writer.Write(clear, codeSize);
                table.Clear();
                codeSize = minCodeSize + 1;
                nextCode = end + 1;
            }

            prefix = symbol;
        }

        writer.Write(prefix, codeSize);

        // The decoder adds one more entry when it reads the last code, so the
        // end code may need the next width (the classic compress-style check).
        if (nextCode >= (1 << codeSize) && codeSize < 12)
        {
            codeSize++;
        }

        writer.Write(end, codeSize);
        writer.Flush();
        output.WriteByte(0);
    }

    private sealed class SubBlockWriter(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _count;
        private int _bits;
        private int _bitCount;

        public void Write(int code, int size)
        {
            _bits |= code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                Push((byte)(_bits & 0xFF));
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                Push((byte)(_bits & 0xFF));
                _bits = 0;
                _bitCount = 0;
            }

            if (_count > 0)
            {
                output.WriteByte((byte)_count);
                output.Write(_block, 0, _count);
                _count = 0;
            }
        }

        private void Push(byte value)
        {
            _block[_count++] = value;
            if (_count == 255)
            {
                output.WriteByte(255);
                output.Write(_block, 0, 255);
                _count = 0;
            }
        }
    }
}
