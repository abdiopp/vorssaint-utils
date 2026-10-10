// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// Per-frame adaptive palette (median cut over a 15-bit colour histogram)
/// with optional Floyd–Steinberg error diffusion (spec 02 §6.18: "per-frame
/// 256-colour adaptive palette + error-diffusion dithering").
/// </summary>
public sealed class GifQuantizer
{
    private const int Bins = 32 * 32 * 32;
    private readonly int[] _count = new int[Bins];
    private readonly long[] _sumR = new long[Bins];
    private readonly long[] _sumG = new long[Bins];
    private readonly long[] _sumB = new long[Bins];
    private readonly int[] _lookup = new int[Bins];
    private float[] _errorA = [];
    private float[] _errorB = [];

    /// <summary>Palette as RGB triples (≤ 256 entries).</summary>
    public byte[] Palette { get; private set; } = [];

    public int PaletteSize => Palette.Length / 3;

    /// <summary>Quantizes opaque BGRA pixels to palette indices.</summary>
    public byte[] Quantize(ReadOnlySpan<byte> bgra, int width, int height, int stride, bool dither, int maxColors = 256)
    {
        maxColors = Math.Clamp(maxColors, 2, 256);
        if (TryExactPalette(bgra, width, height, stride, maxColors) is { } exact)
        {
            return exact;
        }

        BuildPalette(bgra, width, height, stride, maxColors);
        Array.Fill(_lookup, -1);
        var indices = new byte[width * height];
        if (!dither)
        {
            for (var y = 0; y < height; y++)
            {
                var row = bgra.Slice(y * stride, width * 4);
                for (var x = 0; x < width; x++)
                {
                    indices[(y * width) + x] = (byte)Nearest(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4]);
                }
            }

            return indices;
        }

        var rowLength = (width + 2) * 3;
        if (_errorA.Length < rowLength)
        {
            _errorA = new float[rowLength];
            _errorB = new float[rowLength];
        }

        var current = _errorA;
        var next = _errorB;
        Array.Clear(current, 0, rowLength);
        Array.Clear(next, 0, rowLength);
        var palette = Palette;
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var e = (x + 1) * 3;
                var r = Math.Clamp(row[(x * 4) + 2] + current[e], 0f, 255f);
                var g = Math.Clamp(row[(x * 4) + 1] + current[e + 1], 0f, 255f);
                var b = Math.Clamp(row[x * 4] + current[e + 2], 0f, 255f);
                var index = Nearest((int)(r + 0.5f), (int)(g + 0.5f), (int)(b + 0.5f));
                indices[(y * width) + x] = (byte)index;
                var er = r - palette[index * 3];
                var eg = g - palette[(index * 3) + 1];
                var eb = b - palette[(index * 3) + 2];
                current[e + 3] += er * 7 / 16f;
                current[e + 4] += eg * 7 / 16f;
                current[e + 5] += eb * 7 / 16f;
                next[e - 3] += er * 3 / 16f;
                next[e - 2] += eg * 3 / 16f;
                next[e - 1] += eb * 3 / 16f;
                next[e] += er * 5 / 16f;
                next[e + 1] += eg * 5 / 16f;
                next[e + 2] += eb * 5 / 16f;
                next[e + 3] += er / 16f;
                next[e + 4] += eg / 16f;
                next[e + 5] += eb / 16f;
            }

            (current, next) = (next, current);
            Array.Clear(next, 0, rowLength);
        }

        return indices;
    }

    /// <summary>A frame with few distinct colours (flat UI) is stored losslessly.</summary>
    private byte[]? TryExactPalette(ReadOnlySpan<byte> bgra, int width, int height, int stride, int maxColors)
    {
        var colors = new Dictionary<int, byte>();
        var indices = new byte[width * height];
        var palette = new List<byte>(maxColors * 3);
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var key = (row[(x * 4) + 2] << 16) | (row[(x * 4) + 1] << 8) | row[x * 4];
                if (!colors.TryGetValue(key, out var index))
                {
                    if (colors.Count >= maxColors)
                    {
                        return null;
                    }

                    index = (byte)colors.Count;
                    colors[key] = index;
                    palette.Add(row[(x * 4) + 2]);
                    palette.Add(row[(x * 4) + 1]);
                    palette.Add(row[x * 4]);
                }

                indices[(y * width) + x] = index;
            }
        }

        Palette = palette.ToArray();
        return indices;
    }

    private int Nearest(int r, int g, int b)
    {
        var bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
        var cached = _lookup[bin];
        if (cached >= 0)
        {
            return cached;
        }

        // Search with the bin's centre so cached answers are consistent.
        var cr = ((r >> 3) << 3) + 4;
        var cg = ((g >> 3) << 3) + 4;
        var cb = ((b >> 3) << 3) + 4;
        var best = 0;
        var bestDistance = int.MaxValue;
        var palette = Palette;
        for (var i = 0; i < palette.Length / 3; i++)
        {
            var dr = cr - palette[i * 3];
            var dg = cg - palette[(i * 3) + 1];
            var db = cb - palette[(i * 3) + 2];
            var d = (dr * dr * 3) + (dg * dg * 4) + (db * db * 2);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        _lookup[bin] = best;
        return best;
    }

    private void BuildPalette(ReadOnlySpan<byte> bgra, int width, int height, int stride, int maxColors)
    {
        Array.Clear(_count);
        Array.Clear(_sumR);
        Array.Clear(_sumG);
        Array.Clear(_sumB);
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                int b = row[x * 4], g = row[(x * 4) + 1], r = row[(x * 4) + 2];
                var bin = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                _count[bin]++;
                _sumR[bin] += r;
                _sumG[bin] += g;
                _sumB[bin] += b;
            }
        }

        var used = new List<int>();
        for (var i = 0; i < Bins; i++)
        {
            if (_count[i] > 0)
            {
                used.Add(i);
            }
        }

        var boxes = new List<Box> { new(used.ToArray(), this) };
        while (boxes.Count < maxColors)
        {
            var best = -1;
            double bestScore = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var score = boxes[i].Score;
                if (boxes[i].Bins.Length > 1 && score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            if (best < 0)
            {
                break;
            }

            var (a, b) = boxes[best].Split(this);
            boxes[best] = a;
            boxes.Add(b);
        }

        var palette = new byte[boxes.Count * 3];
        for (var i = 0; i < boxes.Count; i++)
        {
            long n = 0, r = 0, g = 0, bl = 0;
            foreach (var bin in boxes[i].Bins)
            {
                n += _count[bin];
                r += _sumR[bin];
                g += _sumG[bin];
                bl += _sumB[bin];
            }

            n = Math.Max(1, n);
            palette[i * 3] = (byte)Math.Clamp((r + (n / 2)) / n, 0, 255);
            palette[(i * 3) + 1] = (byte)Math.Clamp((g + (n / 2)) / n, 0, 255);
            palette[(i * 3) + 2] = (byte)Math.Clamp((bl + (n / 2)) / n, 0, 255);
        }

        if (palette.Length == 0)
        {
            palette = [0, 0, 0];
        }

        Palette = palette;
    }

    private static int Channel(int bin, int axis) => axis switch
    {
        0 => (bin >> 10) & 31,
        1 => (bin >> 5) & 31,
        _ => bin & 31,
    };

    /// <summary>A box of histogram bins.</summary>
    private readonly struct Box
    {
        public Box(int[] bins, GifQuantizer q)
        {
            Bins = bins;
            long pixels = 0;
            int r0 = 31, r1 = 0, g0 = 31, g1 = 0, b0 = 31, b1 = 0;
            foreach (var bin in bins)
            {
                pixels += q._count[bin];
                var r = Channel(bin, 0);
                var g = Channel(bin, 1);
                var b = Channel(bin, 2);
                r0 = Math.Min(r0, r);
                r1 = Math.Max(r1, r);
                g0 = Math.Min(g0, g);
                g1 = Math.Max(g1, g);
                b0 = Math.Min(b0, b);
                b1 = Math.Max(b1, b);
            }

            // Perceptual weights: green matters most, blue least.
            var rr = (r1 - r0) * 3;
            var gr = (g1 - g0) * 4;
            var br = (b1 - b0) * 2;
            Axis = gr >= rr && gr >= br ? 1 : rr >= br ? 0 : 2;
            var range = Math.Max(rr, Math.Max(gr, br));
            Score = range * Math.Sqrt(pixels);
        }

        public int[] Bins { get; }

        public int Axis { get; }

        public double Score { get; }

        /// <summary>Splits at the pixel-count median along the widest (weighted) axis.</summary>
        public (Box, Box) Split(GifQuantizer q)
        {
            var axis = Axis;
            var sorted = Bins.OrderBy(b => Channel(b, axis)).ThenBy(b => b).ToArray();
            long total = 0;
            foreach (var bin in sorted)
            {
                total += q._count[bin];
            }

            long acc = 0;
            var cut = 1;
            for (var i = 0; i < sorted.Length - 1; i++)
            {
                acc += q._count[sorted[i]];
                cut = i + 1;
                if (acc * 2 >= total)
                {
                    break;
                }
            }

            return (new Box(sorted[..cut], q), new Box(sorted[cut..], q));
        }
    }
}
