// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.ScreenshotEditor;

/// <summary>A small grid of interpolated colours and where to stretch it.</summary>
public sealed class ErasePatch(SKImage image, SKRect destination) : IDisposable
{
    public SKImage Image { get; } = image;

    /// <summary>The area grown by half a cell, so grid nodes land exactly on the area's edges.</summary>
    public SKRect Destination { get; } = destination;

    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Content-aware fill from the surroundings (spec 01 §6.11): medians of thin
/// strips just outside each edge, blended across the area with inverse
/// distance weights. A flat background is reproduced exactly, and so is a
/// straight gradient.
/// </summary>
public static class EraseFill
{
    private enum Side
    {
        Top,
        Bottom,
        Left,
        Right,
    }

    private sealed class SideColors(Side side, double[][] medians, double alongStart, double length, double offset)
    {
        public Side Side { get; } = side;

        /// <summary>Per segment: premultiplied B, G, R, A.</summary>
        public double[][] Medians { get; } = medians;

        public double AlongStart { get; } = alongStart;

        public double Length { get; } = length;

        public double Offset { get; } = offset;

        /// <summary>Linear interpolation between neighbouring segment medians: place = clamp(pos/length·count − 0.5, 0, count − 1).</summary>
        public double[] At(double position)
        {
            var count = Medians.Length;
            var place = Math.Clamp(((position - AlongStart) / Math.Max(1e-9, Length) * count) - 0.5, 0, count - 1);
            var i0 = (int)Math.Floor(place);
            var i1 = Math.Min(count - 1, i0 + 1);
            var t = place - i0;
            var result = new double[4];
            for (var c = 0; c < 4; c++)
            {
                result[c] = Medians[i0][c] + ((Medians[i1][c] - Medians[i0][c]) * t);
            }

            return result;
        }
    }

    /// <summary>
    /// Builds the fill for <paramref name="run"/>; pixels inside <paramref name="skip"/>
    /// rectangles (other text runs) are not sampled in the first pass. Null when the area is under a pixel.
    /// </summary>
    public static ErasePatch? Build(SkiaEditorImage image, ImgRect run, IReadOnlyList<ImgRect>? skip)
    {
        var bounds = new ImgRect(0, 0, image.Width, image.Height);
        var area = run.Integral().Intersect(bounds);
        if (area.Width < 1 || area.Height < 1)
        {
            return null;
        }

        var pixmap = image.PeekPixels();
        var band = Math.Clamp(SpecMath.RoundToInt(0.12 * Math.Min(area.Width, area.Height)), 2, 8);
        List<SideColors>? sides = null;
        var skipRects = skip is { Count: > 0 } ? skip : null;
        if (skipRects is not null)
        {
            sides = Sample(pixmap, area, bounds, band, outside: true, skipRects);
        }

        if (sides is null || sides.Count == 0)
        {
            sides = Sample(pixmap, area, bounds, band, outside: true, null);
        }

        if (sides.Count == 0)
        {
            var depth = (int)Math.Min(band, Math.Min(area.Width, area.Height));
            sides = Sample(pixmap, area, bounds, Math.Max(1, depth), outside: false, null);
        }

        if (sides.Count == 0)
        {
            return null;
        }

        var cell = Math.Max(4, Math.Max(area.Width, area.Height) / 128);
        var cols = (int)Math.Ceiling(area.Width / cell) + 1;
        var rows = (int)Math.Ceiling(area.Height / cell) + 1;
        using var bitmap = new SKBitmap(SkiaConvert.InfoFor(cols, rows));
        unsafe
        {
            var dst = (byte*)bitmap.GetPixels();
            var row = bitmap.RowBytes;
            for (var j = 0; j < rows; j++)
            {
                var y = (double)j / (rows - 1) * area.Height;
                for (var i = 0; i < cols; i++)
                {
                    var x = (double)i / (cols - 1) * area.Width;
                    double sumW = 0;
                    var acc = new double[4];
                    foreach (var side in sides)
                    {
                        double weight;
                        double[] color;
                        switch (side.Side)
                        {
                            case Side.Top:
                                weight = 1 / Math.Max(0.5, y + side.Offset);
                                color = side.At(area.X + x);
                                break;
                            case Side.Bottom:
                                weight = 1 / Math.Max(0.5, area.Height - y + side.Offset);
                                color = side.At(area.X + x);
                                break;
                            case Side.Left:
                                weight = 1 / Math.Max(0.5, x + side.Offset);
                                color = side.At(area.Y + y);
                                break;
                            default:
                                weight = 1 / Math.Max(0.5, area.Width - x + side.Offset);
                                color = side.At(area.Y + y);
                                break;
                        }

                        sumW += weight;
                        for (var c = 0; c < 4; c++)
                        {
                            acc[c] += weight * color[c];
                        }
                    }

                    var o = dst + ((long)j * row) + (i * 4);
                    var alpha = Math.Clamp(SpecMath.Round(acc[3] / sumW), 0, 255);
                    o[3] = (byte)alpha;
                    for (var c = 0; c < 3; c++)
                    {
                        o[c] = (byte)Math.Clamp(SpecMath.Round(acc[c] / sumW), 0, alpha);
                    }
                }
            }
        }

        bitmap.NotifyPixelsChanged();
        bitmap.SetImmutable();
        var cellW = area.Width / (cols - 1);
        var cellH = area.Height / (rows - 1);
        var destination = SKRect.Create(
            (float)(area.X - (cellW / 2)), (float)(area.Y - (cellH / 2)), (float)(cols * cellW), (float)(rows * cellH));
        return new ErasePatch(SKImage.FromBitmap(bitmap), destination);
    }

    private static List<SideColors> Sample(SKPixmap pixmap, ImgRect area, ImgRect bounds, int band, bool outside, IReadOnlyList<ImgRect>? skip)
    {
        var list = new List<SideColors>(4);
        foreach (var side in Enum.GetValues<Side>())
        {
            var strip = (side, outside) switch
            {
                (Side.Top, true) => new ImgRect(area.X, area.Y - band, area.Width, band),
                (Side.Bottom, true) => new ImgRect(area.X, area.MaxY, area.Width, band),
                (Side.Left, true) => new ImgRect(area.X - band, area.Y, band, area.Height),
                (Side.Right, true) => new ImgRect(area.MaxX, area.Y, band, area.Height),
                (Side.Top, false) => new ImgRect(area.X, area.Y, area.Width, band),
                (Side.Bottom, false) => new ImgRect(area.X, area.MaxY - band, area.Width, band),
                (Side.Left, false) => new ImgRect(area.X, area.Y, band, area.Height),
                _ => new ImgRect(area.MaxX - band, area.Y, band, area.Height),
            };
            var visible = strip.Intersect(bounds);
            if (visible.Width < 1 || visible.Height < 1)
            {
                continue;
            }

            if (SampleSide(pixmap, side, visible, band, outside, skip) is { } colors)
            {
                list.Add(colors);
            }
        }

        return list;
    }

    private static SideColors? SampleSide(SKPixmap pixmap, Side side, ImgRect strip, int band, bool outside, IReadOnlyList<ImgRect>? skip)
    {
        var horizontal = side is Side.Top or Side.Bottom;
        var length = horizontal ? strip.Width : strip.Height;
        var depth = Math.Max(1, (int)(horizontal ? strip.Height : strip.Width));
        var alongStart = horizontal ? strip.X : strip.Y;
        var segments = Math.Clamp((int)(length / Math.Max(12, 4 * band)), 1, 48);
        var medians = new double[]?[segments];
        const int attempts = 32;
        var samples = new List<byte[]>(attempts);
        for (var s = 0; s < segments; s++)
        {
            var start = alongStart + (s * length / segments);
            var span = length / segments;
            samples.Clear();
            for (var k = 0; k < attempts; k++)
            {
                var along = (int)Math.Floor(start + (k * span / attempts));
                var across = k % depth;
                var x = horizontal ? along : (int)strip.X + across;
                var y = horizontal ? (int)strip.Y + across : along;
                if (x < 0 || y < 0 || x >= pixmap.Width || y >= pixmap.Height)
                {
                    continue;
                }

                if (skip is not null && Inside(skip, x + 0.5, y + 0.5))
                {
                    continue;
                }

                samples.Add(Read(pixmap, x, y));
            }

            if (samples.Count > 0)
            {
                medians[s] = Median(samples);
            }
        }

        if (medians.All(m => m is null))
        {
            return null;
        }

        // Segments without samples copy the nearest one that has them.
        var filled = new double[segments][];
        for (var s = 0; s < segments; s++)
        {
            if (medians[s] is { } m)
            {
                filled[s] = m;
                continue;
            }

            for (var d = 1; d < segments; d++)
            {
                if (s - d >= 0 && medians[s - d] is { } before)
                {
                    filled[s] = before;
                    break;
                }

                if (s + d < segments && medians[s + d] is { } after)
                {
                    filled[s] = after;
                    break;
                }
            }
        }

        return new SideColors(side, filled, alongStart, length, outside ? depth / 2.0 : 0);
    }

    private static bool Inside(IReadOnlyList<ImgRect> rects, double x, double y)
    {
        foreach (var r in rects)
        {
            if (r.Contains(new ImgPoint(x, y)))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe byte[] Read(SKPixmap pixmap, int x, int y)
    {
        var p = (byte*)pixmap.GetPixels() + ((long)y * pixmap.RowBytes) + (x * 4);
        return [p[0], p[1], p[2], p[3]];
    }

    /// <summary>Median per channel (exact value when all samples agree).</summary>
    private static double[] Median(List<byte[]> samples)
    {
        var result = new double[4];
        var channel = new byte[samples.Count];
        for (var c = 0; c < 4; c++)
        {
            for (var i = 0; i < samples.Count; i++)
            {
                channel[i] = samples[i][c];
            }

            Array.Sort(channel);
            var n = channel.Length;
            result[c] = n % 2 == 1 ? channel[n / 2] : (channel[(n / 2) - 1] + channel[n / 2]) / 2.0;
        }

        return result;
    }
}

/// <summary>
/// Erase fills for the current base image and text runs, keyed by (integral
/// rect, skips text). After 256 entries only the fills used in the last
/// redraw are kept.
/// </summary>
public sealed class EraseCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(int X, int Y, int W, int H, bool Skips), ErasePatch?> _fills = [];
    private readonly HashSet<(int, int, int, int, bool)> _used = [];
    private SkiaEditorImage? _image;
    private IReadOnlyList<ImgRect>? _runs;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _fills.Count;
            }
        }
    }

    public ErasePatch? Get(SkiaEditorImage image, ImgRect run, IReadOnlyList<ImgRect>? textRuns)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(image, _image) || !ReferenceEquals(textRuns, _runs))
            {
                ClearLocked();
                _image = image;
                _runs = textRuns;
            }
        }

        var integral = run.Integral();
        var skip = textRuns?.Where(r => !r.NearlyEquals(run)).ToList();
        var key = ((int)integral.X, (int)integral.Y, (int)integral.Width, (int)integral.Height, skip is { Count: > 0 });
        lock (_gate)
        {
            _used.Add(key);
            if (_fills.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var patch = EraseFill.Build(image, run, skip);
        lock (_gate)
        {
            if (_fills.TryGetValue(key, out var raced))
            {
                patch?.Dispose();
                return raced;
            }

            _fills[key] = patch;
            return patch;
        }
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            _used.Clear();
        }
    }

    public void EndFrame()
    {
        lock (_gate)
        {
            if (_fills.Count <= 256)
            {
                return;
            }

            foreach (var key in _fills.Keys.Where(k => !_used.Contains(k)).ToList())
            {
                _fills[key]?.Dispose();
                _fills.Remove(key);
            }
        }
    }

    private void ClearLocked()
    {
        foreach (var patch in _fills.Values)
        {
            patch?.Dispose();
        }

        _fills.Clear();
        _used.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ClearLocked();
        }
    }
}
