// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;

namespace Rivet.Core.ScreenshotEditor;

/// <summary>A word the recognizer read, with its box in the recognized image's pixels (top-left origin).</summary>
public sealed record OcrWord(string Text, ImgRect? Box);

/// <summary>A recognized line; <see cref="Box"/> is the line's box when the recognizer reports one.</summary>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words, ImgRect? Box = null);

/// <summary>Everything read from one image.</summary>
public sealed record OcrPage(IReadOnlyList<OcrLine> Lines);

/// <summary>
/// On-device text recognition of one image (Windows.Media.Ocr on Windows).
/// Returns null when recognition failed, which keeps text-only blurs covering
/// their whole area.
/// </summary>
public interface ITextRecognizer
{
    /// <summary>False when no recognition language is installed (the editor then offers no word selection).</summary>
    bool IsAvailable { get; }

    /// <summary>Largest side the engine accepts, used to limit band heights; null for no limit.</summary>
    int? MaxImageDimension { get; }

    Task<OcrPage?> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken);
}

/// <summary>A word on the canvas: text, box in image pixels (may be missing), reading-order line index.</summary>
public sealed record RecognizedWord(string Text, ImgRect? Rect, int Line, ImgRect? LineBox = null)
{
    public double MidY => Rect is { } r ? r.MidY : LineBox?.MidY ?? 0;
}

/// <summary>
/// The recognition state of one base image: words for selection, and padded
/// text runs for text-only blurring. <see cref="Runs"/> is null while
/// recognition is pending or when any band failed, so text-only areas cover
/// everything (an early export never leaks text).
/// </summary>
public sealed record TextRecognition(IReadOnlyList<RecognizedWord> Words, IReadOnlyList<ImgRect>? Runs)
{
    public static TextRecognition Pending { get; } = new([], null);

    /// <summary>Shifts into a cropped image's coordinates, dropping what falls outside (null runs stay null).</summary>
    public TextRecognition Cropped(ImgRect crop)
    {
        var bounds = new ImgRect(0, 0, crop.Width, crop.Height);
        var words = Words
            .Select(w => w with
            {
                Rect = w.Rect?.Offset(-crop.X, -crop.Y),
                LineBox = w.LineBox?.Offset(-crop.X, -crop.Y),
            })
            .Where(w => (w.Rect ?? w.LineBox) is { } r && r.Intersects(bounds))
            .ToList();
        return new TextRecognition(words, TextRuns.Cropped(Runs, crop));
    }
}

/// <summary>A horizontal band of the image and the rows it owns after merging.</summary>
public readonly record struct TextBand(int Y, int Height, int OwnedStart, int OwnedEnd)
{
    public int Bottom => Y + Height;
}

/// <summary>Bands, so a tall capture is never one huge recognition request (spec 01 §6.12).</summary>
public static class TextBands
{
    public const int MaxTilePixels = 12_000_000;
    public const int MaxTileHeight = 4096;

    /// <summary>
    /// <code>
    /// tileH = min(H, max(512, min(4096, maxTilePixels / W)))
    /// overlap = tileH &lt; H ? even(min(256, tileH / 4)) : 0
    /// owned = [y == 0 ? 0 : y + overlap/2, last ? H : y + bandH − overlap/2)
    /// </code>
    /// </summary>
    public static IReadOnlyList<TextBand> Compute(int width, int height, int maxTilePixels = MaxTilePixels, int maxTileHeight = MaxTileHeight)
    {
        var bands = new List<TextBand>();
        if (width <= 0 || height <= 0)
        {
            return bands;
        }

        var tileH = Math.Min(height, Math.Max(512, Math.Min(maxTileHeight, maxTilePixels / width)));
        var overlap = tileH < height ? Math.Min(256, tileH / 4) / 2 * 2 : 0;
        var y = 0;
        while (true)
        {
            var bandH = Math.Min(tileH, height - y);
            var last = y + bandH >= height;
            var ownedStart = y == 0 ? 0 : y + (overlap / 2);
            var ownedEnd = last ? height : y + bandH - (overlap / 2);
            bands.Add(new TextBand(y, bandH, ownedStart, ownedEnd));
            if (last)
            {
                break;
            }

            y += bandH - overlap;
        }

        return bands;
    }
}

/// <summary>One band's words in image coordinates, and whether the band was read.</summary>
public sealed record BandWords(TextBand Band, IReadOnlyList<RecognizedWord> Words, bool Succeeded);

/// <summary>Merges overlapping bands: seam duplicates are kept once and reading order is preserved.</summary>
public static class TextBandMerger
{
    public static TextRecognition Merge(IReadOnlyList<BandWords> bands)
    {
        if (bands.Count == 0)
        {
            return new TextRecognition([], []);
        }

        // Words are referenced by (band, index) so duplicates with the same text stay distinct.
        var dropped = new HashSet<(int Band, int Index)>();
        for (var b = 0; b + 1 < bands.Count; b++)
        {
            var upper = bands[b];
            var lower = bands[b + 1];
            var sharedStart = lower.Band.Y;
            var sharedEnd = upper.Band.Bottom;
            if (sharedEnd <= sharedStart)
            {
                continue;
            }

            var seam = upper.Band.OwnedEnd;
            var upperCandidates = Candidates(upper, b, sharedStart, sharedEnd, dropped);
            var lowerCandidates = Candidates(lower, b + 1, sharedStart, sharedEnd, dropped);
            var paired = new HashSet<int>();
            foreach (var (ui, uw, ur) in upperCandidates)
            {
                var bestScore = double.NegativeInfinity;
                var best = -1;
                foreach (var (li, lw, lr) in lowerCandidates)
                {
                    if (paired.Contains(li))
                    {
                        continue;
                    }

                    var inter = ur.Intersect(lr);
                    var interArea = inter.Width * inter.Height;
                    var minArea = Math.Min(ur.Width * ur.Height, lr.Width * lr.Height);
                    var share = minArea > 0 ? interArea / minArea : 0;
                    var sameText = string.Equals(uw.Text, lw.Text, StringComparison.Ordinal);
                    var qualifies = (sameText && inter.Height >= Math.Min(ur.Height, lr.Height) / 2) || share >= 0.5;
                    if (!qualifies)
                    {
                        continue;
                    }

                    var score = share + (sameText ? 1 : 0);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = li;
                    }
                }

                if (best < 0)
                {
                    continue;
                }

                paired.Add(best);
                var lowerRect = lower.Words[best].Rect!.Value;
                var averageMid = (ur.MidY + lowerRect.MidY) / 2;
                if (averageMid < seam)
                {
                    dropped.Add((b + 1, best));
                }
                else
                {
                    dropped.Add((b, ui));
                }
            }
        }

        // Kept words go to the band that owns their midY, concatenated band by band.
        var perBand = bands.Select(_ => new List<RecognizedWord>()).ToList();
        for (var b = 0; b < bands.Count; b++)
        {
            for (var i = 0; i < bands[b].Words.Count; i++)
            {
                if (dropped.Contains((b, i)))
                {
                    continue;
                }

                var word = bands[b].Words[i];
                var owner = OwnerOf(bands, word.MidY, b);
                perBand[owner].Add(word);
            }
        }

        var kept = perBand.SelectMany(w => w).ToList();
        if (bands.Any(b => !b.Succeeded))
        {
            return new TextRecognition(kept, null);
        }

        // Runs: kept words, dropped duplicates close to their band's owned range, and line boxes of box-less words.
        var runSources = new List<RecognizedWord>(kept);
        foreach (var (b, i) in dropped.OrderBy(d => d.Band).ThenBy(d => d.Index))
        {
            var word = bands[b].Words[i];
            var band = bands[b].Band;
            var shared = SharedRows(bands, b);
            var reach = shared / 4.0;
            var mid = word.MidY;
            if ((mid >= band.OwnedEnd && mid < band.OwnedEnd + reach) || (mid < band.OwnedStart && mid >= band.OwnedStart - reach))
            {
                runSources.Add(word);
            }
        }

        return new TextRecognition(kept, TextRuns.FromWords(runSources));
    }

    private static List<(int Index, RecognizedWord Word, ImgRect Rect)> Candidates(BandWords band, int bandIndex, int sharedStart, int sharedEnd, HashSet<(int, int)> dropped)
    {
        var list = new List<(int, RecognizedWord, ImgRect)>();
        for (var i = 0; i < band.Words.Count; i++)
        {
            if (band.Words[i].Rect is { } r && r.MinY < sharedEnd && r.MaxY > sharedStart && !dropped.Contains((bandIndex, i)))
            {
                list.Add((i, band.Words[i], r));
            }
        }

        return list;
    }

    private static int OwnerOf(IReadOnlyList<BandWords> bands, double midY, int fallback)
    {
        for (var b = 0; b < bands.Count; b++)
        {
            if (midY >= bands[b].Band.OwnedStart && midY < bands[b].Band.OwnedEnd)
            {
                return b;
            }
        }

        return fallback;
    }

    private static int SharedRows(IReadOnlyList<BandWords> bands, int b)
    {
        var shared = 0;
        if (b > 0)
        {
            shared = Math.Max(shared, bands[b - 1].Band.Bottom - bands[b].Band.Y);
        }

        if (b + 1 < bands.Count)
        {
            shared = Math.Max(shared, bands[b].Band.Bottom - bands[b + 1].Band.Y);
        }

        return Math.Max(0, shared);
    }
}

/// <summary>Text runs: padded line pieces used by text-only blurs and erase skipping (spec 01 §6.12).</summary>
public static class TextRuns
{
    /// <summary>
    /// Joins words of the same line while the gap is at most 1.5 × height and
    /// they overlap vertically by half the smaller height, then pads each run
    /// by (max(1, 0.6·h), max(1, 0.3·h)).
    /// </summary>
    public static IReadOnlyList<ImgRect> FromWords(IEnumerable<RecognizedWord> words)
    {
        var runs = new List<ImgRect>();
        ImgRect? current = null;
        var currentLine = int.MinValue;
        foreach (var word in words)
        {
            var box = word.Rect ?? word.LineBox;
            if (box is not { Width: > 0, Height: > 0 } r)
            {
                continue;
            }

            if (current is { } run && word.Line == currentLine)
            {
                var h = Math.Max(run.Height, r.Height);
                var gap = Math.Max(Math.Max(r.MinX - run.MaxX, run.MinX - r.MaxX), 0);
                var overlap = Math.Min(run.MaxY, r.MaxY) - Math.Max(run.MinY, r.MinY);
                if (gap <= 1.5 * h && overlap >= Math.Min(run.Height, r.Height) / 2)
                {
                    current = run.Union(r);
                    continue;
                }
            }

            if (current is { } finished)
            {
                runs.Add(Pad(finished));
            }

            current = r;
            currentLine = word.Line;
        }

        if (current is { } last)
        {
            runs.Add(Pad(last));
        }

        return runs;
    }

    public static ImgRect Pad(ImgRect run) =>
        run.Inflate(Math.Max(1, 0.6 * run.Height), Math.Max(1, 0.3 * run.Height));

    /// <summary>What a text-only area covers: the whole area while runs are unknown, else each run that intersects it, clipped.</summary>
    public static IReadOnlyList<ImgRect> Covering(IReadOnlyList<ImgRect>? runs, ImgRect area)
    {
        if (runs is null)
        {
            return [area];
        }

        var list = new List<ImgRect>();
        foreach (var run in runs)
        {
            var clipped = run.Intersect(area);
            if (!clipped.IsEmpty)
            {
                list.Add(clipped);
            }
        }

        return list;
    }

    /// <summary>After a crop: offset by −origin and keep runs intersecting the new bounds; null stays null.</summary>
    public static IReadOnlyList<ImgRect>? Cropped(IReadOnlyList<ImgRect>? runs, ImgRect crop)
    {
        if (runs is null)
        {
            return null;
        }

        var bounds = new ImgRect(0, 0, crop.Width, crop.Height);
        return runs.Select(r => r.Offset(-crop.X, -crop.Y)).Where(r => r.Intersects(bounds)).ToList();
    }
}

/// <summary>Selecting recognized words on the canvas and copying them.</summary>
public static class WordSelection
{
    /// <summary>Indices of words whose box intersects the drag rectangle grown by 1 px (a hairline drag still selects).</summary>
    public static IReadOnlyList<int> Select(IReadOnlyList<RecognizedWord> words, ImgPoint anchor, ImgPoint current)
    {
        var area = ImgRect.FromPoints(anchor, current).Inflate(1, 1);
        var result = new List<int>();
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Rect is { } r && r.Intersects(area))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>The word under <paramref name="p"/> (box grown by 2·scale).</summary>
    public static int? WordAt(IReadOnlyList<RecognizedWord> words, ImgPoint p, double scale)
    {
        var tol = HitTesting.WordTolerance(scale);
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Rect is { } r && r.Inflate(tol, tol).Contains(p))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Selected words in index order: a space within a line, a newline between lines.</summary>
    public static string JoinedText(IReadOnlyList<RecognizedWord> words, IEnumerable<int> indices)
    {
        var builder = new StringBuilder();
        int? previousLine = null;
        foreach (var index in indices.Distinct().Order())
        {
            if (index < 0 || index >= words.Count || words[index].Text.Length == 0)
            {
                continue;
            }

            var word = words[index];
            if (previousLine is { } line)
            {
                builder.Append(line == word.Line ? ' ' : '\n');
            }

            builder.Append(word.Text);
            previousLine = word.Line;
        }

        return builder.ToString();
    }
}

/// <summary>Runs a recognizer band by band and merges the results.</summary>
public sealed class TextRecognitionRunner(ITextRecognizer recognizer)
{
    public bool IsAvailable => recognizer.IsAvailable;

    public async Task<TextRecognition> RecognizeAsync(PixelBuffer image, CancellationToken cancellationToken)
    {
        if (!recognizer.IsAvailable)
        {
            return TextRecognition.Pending;
        }

        var maxHeight = recognizer.MaxImageDimension is int limit && limit > 0
            ? Math.Min(TextBands.MaxTileHeight, limit)
            : TextBands.MaxTileHeight;
        var bands = TextBands.Compute(image.Width, image.Height, TextBands.MaxTilePixels, maxHeight);
        var results = new List<BandWords>(bands.Count);
        var lineOffset = 0;
        foreach (var band in bands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrPage? page;
            try
            {
                page = await recognizer.RecognizeAsync(Slice(image, band), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("screenshotEditor", "Text recognition of a band failed.", ex);
                page = null;
            }

            var words = new List<RecognizedWord>();
            if (page is not null)
            {
                for (var li = 0; li < page.Lines.Count; li++)
                {
                    var line = page.Lines[li];
                    var lineIndex = lineOffset + li;
                    var lineBox = line.Box?.Offset(0, band.Y) ?? UnionOf(line.Words.Select(w => w.Box?.Offset(0, band.Y)));
                    var real = line.Words.Where(w => w.Text.Length > 0).ToList();
                    if (real.Count == 0)
                    {
                        words.Add(new RecognizedWord(string.Empty, null, lineIndex, lineBox));
                        continue;
                    }

                    foreach (var word in real)
                    {
                        words.Add(new RecognizedWord(word.Text, word.Box?.Offset(0, band.Y), lineIndex, lineBox));
                    }
                }

                lineOffset += page.Lines.Count;
            }

            results.Add(new BandWords(band, words, page is not null));
        }

        return TextBandMerger.Merge(results);
    }

    private static ImgRect? UnionOf(IEnumerable<ImgRect?> rects)
    {
        ImgRect? union = null;
        foreach (var r in rects)
        {
            if (r is { } rect)
            {
                union = union is { } u ? u.Union(rect) : rect;
            }
        }

        return union;
    }

    /// <summary>A copy of the band's rows (full width), so the recognizer never sees other rows.</summary>
    public static PixelBuffer Slice(PixelBuffer image, TextBand band)
    {
        var rowBytes = image.Width * 4;
        var pixels = new byte[rowBytes * band.Height];
        for (var y = 0; y < band.Height; y++)
        {
            Buffer.BlockCopy(image.Pixels, (band.Y + y) * image.Stride, pixels, y * rowBytes, rowBytes);
        }

        return new PixelBuffer(image.Width, band.Height, pixels) { Scale = image.Scale };
    }
}
