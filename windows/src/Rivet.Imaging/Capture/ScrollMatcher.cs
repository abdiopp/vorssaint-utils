// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Rivet.Core.Platform;

namespace Rivet.Imaging.Capture;

/// <summary>
/// A grayscale summary of a frame for overlap matching: at most 32 columns
/// wide (horizontal area-average only) and full height, so an integer scroll
/// stays an integer row shift (spec 01 §6.16).
/// </summary>
public sealed class ScrollSample
{
    public const int MaxWidth = 32;

    public ScrollSample(int width, int height, byte[] data, PixelRect trim)
    {
        Width = width;
        Height = height;
        Data = data;
        Trim = trim;
    }

    /// <summary>Sample columns (≤ 32).</summary>
    public int Width { get; }

    /// <summary>Rows (same as the trimmed frame).</summary>
    public int Height { get; }

    /// <summary>Row-major luminance, <see cref="Width"/> bytes per row.</summary>
    public byte[] Data { get; }

    /// <summary>The frame area the sample covers (frame pixels, after trimming transparent edges).</summary>
    public PixelRect Trim { get; }

    public ReadOnlySpan<byte> Row(int y) => Data.AsSpan(y * Width, Width);

    /// <summary>Trims fully transparent outer rows and columns, then samples; null when nothing opaque remains.</summary>
    public static ScrollSample? From(PixelBuffer frame)
    {
        var trim = TrimTransparent(frame);
        if (trim is not { } r)
        {
            return null;
        }

        var sw = Math.Min(MaxWidth, r.Width);
        var data = new byte[sw * r.Height];
        var pixels = frame.Pixels;
        Span<int> starts = stackalloc int[sw + 1];
        for (var c = 0; c <= sw; c++)
        {
            starts[c] = r.X + (int)((long)c * r.Width / sw);
        }

        for (var y = 0; y < r.Height; y++)
        {
            var row = (r.Y + y) * frame.Stride;
            for (var c = 0; c < sw; c++)
            {
                var from = starts[c];
                var to = Math.Max(from + 1, starts[c + 1]);
                var sum = 0;
                for (var x = from; x < to; x++)
                {
                    var i = row + (x * 4);
                    // Rec. 601 luma in fixed point (premultiplied channels are fine for matching).
                    sum += ((pixels[i + 2] * 77) + (pixels[i + 1] * 150) + (pixels[i] * 29)) >> 8;
                }

                data[(y * sw) + c] = (byte)(sum / (to - from));
            }
        }

        return new ScrollSample(sw, r.Height, data, r);
    }

    /// <summary>Removes fully transparent rows (top, bottom) and columns (left, right).</summary>
    public static PixelRect? TrimTransparent(PixelBuffer frame)
    {
        int top = 0, bottom = frame.Height, left = 0, right = frame.Width;
        while (top < bottom && RowTransparent(frame, top, left, right)) top++;
        while (bottom > top && RowTransparent(frame, bottom - 1, left, right)) bottom--;
        if (top >= bottom)
        {
            return null;
        }

        while (left < right && ColumnTransparent(frame, left, top, bottom)) left++;
        while (right > left && ColumnTransparent(frame, right - 1, top, bottom)) right--;
        return left < right ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    private static bool RowTransparent(PixelBuffer frame, int y, int left, int right)
    {
        var row = y * frame.Stride;
        for (var x = left; x < right; x++)
        {
            if (frame.Pixels[row + (x * 4) + 3] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ColumnTransparent(PixelBuffer frame, int x, int top, int bottom)
    {
        for (var y = top; y < bottom; y++)
        {
            if (frame.Pixels[(y * frame.Stride) + (x * 4) + 3] != 0)
            {
                return false;
            }
        }

        return true;
    }
}

public enum ScrollTransitionKind
{
    /// <summary>Nothing moved (the frames are stable).</summary>
    End,

    /// <summary>A unique overlap was found (forward or backward).</summary>
    Advanced,

    /// <summary>Content changed in a way no unique overlap explains.</summary>
    Unmatched,
}

/// <summary>Result of comparing two samples.</summary>
public readonly record struct ScrollTransition(ScrollTransitionKind Kind, int Overlap = 0, bool Forward = true, int ColumnStart = 0, int ColumnEnd = 0)
{
    public static ScrollTransition End => new(ScrollTransitionKind.End);

    public static ScrollTransition Unmatched => new(ScrollTransitionKind.Unmatched);

    /// <summary>Rows the content moved (sample height minus overlap).</summary>
    public int Advance(int height) => height - Overlap;
}

/// <summary>
/// The exact overlap policy of spec 01 §6.16. It never invents a seam:
/// ambiguous matches (repeated blank bands, two plausible offsets) are
/// reported as unmatched and the caller keeps what it already has.
/// </summary>
public static class ScrollMatcher
{
    public const double StableThreshold = 1.5;
    public const double MovingThreshold = 1.5;
    public const int RowTolerancePerColumn = 8;

    /// <summary>Mean |A − B| over rows [H/24, H − H/24) and columns [c0, c1).</summary>
    public static double Difference(ScrollSample a, ScrollSample b, int c0, int c1)
    {
        var h = a.Height;
        var margin = h / 24;
        long sum = 0;
        long count = 0;
        for (var y = margin; y < h - margin; y++)
        {
            var ra = a.Row(y);
            var rb = b.Row(y);
            for (var c = c0; c < c1; c++)
            {
                sum += Math.Abs(ra[c] - rb[c]);
            }

            count += c1 - c0;
        }

        return count == 0 ? 0 : sum / (double)count;
    }

    public static bool IsStable(ScrollSample a, ScrollSample b, int c0, int c1) =>
        a.Width == b.Width && a.Height == b.Height && Difference(a, b, c0, c1) <= StableThreshold;

    public static ScrollTransition Transition(ScrollSample prev, ScrollSample cur, int? contentStart = null, int? contentEnd = null)
    {
        if (prev.Width != cur.Width || prev.Height != cur.Height || cur.Height < 24)
        {
            return ScrollTransition.Unmatched;
        }

        var sw = cur.Width;
        var c0 = Math.Clamp(contentStart ?? 0, 0, sw);
        var c1 = Math.Clamp(contentEnd ?? sw, c0, sw);
        if (c1 - c0 < 1)
        {
            return ScrollTransition.Unmatched;
        }

        if (IsStable(prev, cur, c0, c1))
        {
            return ScrollTransition.End;
        }

        var h = cur.Height;
        var minA = Math.Max(2, (int)Math.Round(0.01 * h, MidpointRounding.AwayFromZero));
        var maxA = Math.Min(h - 8, (int)Math.Round(0.88 * h, MidpointRounding.AwayFromZero));
        if (minA > maxA)
        {
            return ScrollTransition.Unmatched;
        }

        var tiles = Tiles(c0, c1);
        var moving = new List<int>();
        for (var t = 0; t < tiles.Count; t++)
        {
            if (Difference(prev, cur, tiles[t].Start, tiles[t].End) > MovingThreshold)
            {
                moving.Add(t);
            }
        }

        if (moving.Count == 0)
        {
            return ScrollTransition.Unmatched;
        }

        var required = Math.Max(8, Math.Min(28, h / 12));
        var edge = Math.Max(2, h / 10);
        var minGroupTiles = moving.Count >= 3 ? 2 : 1;
        var candidates = new List<Candidate>();
        var rowSums = new int[tiles.Count * h];
        Span<byte> diff = stackalloc byte[ScrollSample.MaxWidth];

        for (var a = minA; a <= maxA; a++)
        {
            var rows = h - a;
            if (rows - edge <= edge)
            {
                continue;
            }

            for (var direction = 0; direction < 2; direction++)
            {
                var forward = direction == 0;
                // Per-row sums of |cur − prev shifted| for every tile.
                for (var r = 0; r < rows; r++)
                {
                    var curRow = cur.Row(r + (forward ? 0 : a));
                    var prevRow = prev.Row(r + (forward ? a : 0));
                    AbsDiff(curRow, prevRow, diff[..sw]);
                    for (var t = 0; t < tiles.Count; t++)
                    {
                        var s = 0;
                        for (var c = tiles[t].Start; c < tiles[t].End; c++)
                        {
                            s += diff[c];
                        }

                        rowSums[(t * h) + r] = s;
                    }
                }

                Candidate? best = null;
                foreach (var group in Groups(moving, tiles, rowSums, h, edge, rows, required, minGroupTiles))
                {
                    var stats = Match(rowSums, h, group.FirstTile, group.LastTile, tiles, edge, rows);
                    if (!stats.Supports(required))
                    {
                        continue;
                    }

                    var candidate = new Candidate(a, forward, tiles[group.FirstTile].Start, tiles[group.LastTile].End, group.TileCount, stats);
                    if (best is null || candidate.CompareWithinOffset(best.Value) < 0)
                    {
                        best = candidate;
                    }
                }

                if (best is { } found)
                {
                    candidates.Add(found);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return ScrollTransition.Unmatched;
        }

        candidates.Sort((x, y) => x.CompareOverall(y));
        var winner = candidates[0];
        if (winner.Stats.Longest < required)
        {
            return ScrollTransition.Unmatched;
        }

        foreach (var rival in candidates.Skip(1))
        {
            if (rival.Forward == winner.Forward && Math.Abs(rival.Offset - winner.Offset) <= 2)
            {
                continue;
            }

            if (rival.Columns >= winner.Columns - 1
                && rival.Tiles >= winner.Tiles - 1
                && rival.Stats.Longest >= winner.Stats.Longest - 2
                && rival.Stats.Matching >= winner.Stats.Matching - Math.Max(3, 3 * winner.Tiles)
                && rival.Stats.Difference <= winner.Stats.Difference + 0.75)
            {
                return ScrollTransition.Unmatched;
            }

            break;
        }

        return new ScrollTransition(ScrollTransitionKind.Advanced, h - winner.Offset, winner.Forward, winner.ColumnStart, winner.ColumnEnd);
    }

    /// <summary>Consecutive column chunks of max(2, width / 8); a trailing chunk narrower than 2 is dropped unless it is the only one.</summary>
    internal static List<(int Start, int End)> Tiles(int c0, int c1)
    {
        var width = c1 - c0;
        var tileWidth = Math.Max(2, width / 8);
        var tiles = new List<(int Start, int End)>();
        for (var start = c0; start < c1; start += tileWidth)
        {
            var end = Math.Min(c1, start + tileWidth);
            if (end - start < 2 && tiles.Count > 0)
            {
                continue;
            }

            tiles.Add((start, end));
        }

        return tiles;
    }

    private static IEnumerable<(int FirstTile, int LastTile, int TileCount)> Groups(
        List<int> moving, List<(int Start, int End)> tiles, int[] rowSums, int h, int edge, int rows, int required, int minTiles)
    {
        var groups = new List<(int, int, int)>();
        var first = -1;
        var last = -1;
        var count = 0;
        var skipped = false;
        var previousIndex = -1;

        void Close()
        {
            if (first >= 0 && count >= minTiles)
            {
                groups.Add((first, last, count));
            }

            first = -1;
            last = -1;
            count = 0;
            skipped = false;
        }

        foreach (var t in moving)
        {
            // Groups are contiguous in column space; a gap of one tile counts as the allowed skip.
            var gap = previousIndex >= 0 ? t - previousIndex - 1 : 0;
            previousIndex = t;
            if (first >= 0 && gap > 0)
            {
                if (gap == 1 && !skipped)
                {
                    skipped = true;
                }
                else
                {
                    Close();
                }
            }

            var supports = Match(rowSums, h, t, t, tiles, edge, rows).Supports(required);
            if (supports)
            {
                if (first < 0)
                {
                    first = t;
                }

                last = t;
                count++;
            }
            else if (first >= 0 && !skipped)
            {
                skipped = true;
            }
            else
            {
                Close();
            }
        }

        Close();
        return groups;
    }

    private static MatchStats Match(int[] rowSums, int h, int firstTile, int lastTile, List<(int Start, int End)> tiles, int edge, int rows)
    {
        var columns = tiles[lastTile].End - tiles[firstTile].Start;
        var limit = RowTolerancePerColumn * columns;
        var longest = 0;
        var streak = 0;
        var matching = 0;
        var compared = 0;
        long total = 0;
        for (var r = edge; r < rows - edge; r++)
        {
            var sum = 0;
            for (var t = firstTile; t <= lastTile; t++)
            {
                sum += rowSums[(t * h) + r];
            }

            compared++;
            total += sum;
            if (sum <= limit)
            {
                matching++;
                streak++;
                longest = Math.Max(longest, streak);
            }
            else
            {
                streak = 0;
            }
        }

        var difference = compared == 0 ? double.MaxValue : total / (double)(compared * columns);
        return new MatchStats(longest, matching, compared, difference);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> result)
    {
        var i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 16 <= a.Length; i += 16)
            {
                var va = Vector128.Create(a.Slice(i, 16));
                var vb = Vector128.Create(b.Slice(i, 16));
                (Vector128.Max(va, vb) - Vector128.Min(va, vb)).CopyTo(result.Slice(i, 16));
            }
        }

        for (; i < a.Length; i++)
        {
            result[i] = (byte)Math.Abs(a[i] - b[i]);
        }
    }

    private readonly record struct MatchStats(int Longest, int Matching, int Compared, double Difference)
    {
        public bool Supports(int required) => Longest >= required && Matching >= Math.Max(required, Compared / 3);
    }

    private readonly record struct Candidate(int Offset, bool Forward, int ColumnStart, int ColumnEnd, int Tiles, MatchStats Stats)
    {
        public int Columns => ColumnEnd - ColumnStart;

        /// <summary>Best group for one offset: widest, then most matching rows, then smallest difference.</summary>
        public int CompareWithinOffset(Candidate other)
        {
            var c = other.Columns.CompareTo(Columns);
            if (c != 0) return c;
            c = other.Stats.Matching.CompareTo(Stats.Matching);
            return c != 0 ? c : Stats.Difference.CompareTo(other.Stats.Difference);
        }

        /// <summary>Overall ranking: width, tiles, longest streak, matching rows, difference.</summary>
        public int CompareOverall(Candidate other)
        {
            var c = other.Columns.CompareTo(Columns);
            if (c != 0) return c;
            c = other.Tiles.CompareTo(Tiles);
            if (c != 0) return c;
            c = other.Stats.Longest.CompareTo(Stats.Longest);
            if (c != 0) return c;
            c = other.Stats.Matching.CompareTo(Stats.Matching);
            return c != 0 ? c : Stats.Difference.CompareTo(other.Stats.Difference);
        }
    }
}
