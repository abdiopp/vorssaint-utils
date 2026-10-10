// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Imaging.Capture;

public enum ScrollCaptureOutcome
{
    /// <summary>Finished normally.</summary>
    Success,

    /// <summary>The page changed in a way that could not be joined, or a frame failed: the completed part is kept.</summary>
    Partial,

    /// <summary>A safety limit stopped it: the completed part is kept.</summary>
    Limited,

    Cancelled,

    /// <summary>No first frame, or an internal inconsistency.</summary>
    Failed,
}

public sealed record ScrollCaptureResult(ScrollCaptureOutcome Outcome, PixelBuffer? Image);

/// <summary>What one fed frame did.</summary>
public enum ScrollFeedKind
{
    Stable,
    Appended,
    Backward,
    Unmatched,
    Limited,
    Failed,
}

/// <summary>
/// Accepts successive frames of a scrolling region and keeps the newly
/// revealed strips (spec 01 §6.16): the first forward advance fixes the
/// moving columns (fixed side columns are cut off) and a fixed footer (kept
/// once, appended at the very end). Backward movement never duplicates.
/// </summary>
public sealed class ScrollStitcher
{
    public const int MaxStrips = 512;
    public const long MaxTotalPixels = 60_000_000;
    public const long MaxRetainedPixels = 60_000_000;

    private readonly List<PixelBuffer> _slices = [];
    private readonly PixelBuffer _first;
    private readonly ScrollSample _firstSample;
    private ScrollSample _previous;
    private ScrollSample _lastObserved;
    private PixelBuffer? _footerSlice;
    private int? _contentStart;
    private int? _contentEnd;
    private int _pixelLeft;
    private int _pixelRight;
    private int _footer;
    private long _retained;

    private ScrollStitcher(PixelBuffer first, ScrollSample sample)
    {
        _first = first;
        _firstSample = sample;
        _previous = sample;
        _lastObserved = sample;
        var trimmed = CaptureImaging.Crop(first, sample.Trim) ?? first;
        _slices.Add(trimmed);
        _retained = (long)trimmed.Width * trimmed.Height;
        TotalHeight = trimmed.Height;
    }

    /// <summary>Stitched height so far (pixels, without the footer).</summary>
    public int TotalHeight { get; private set; }

    public bool HadUnmatched { get; private set; }

    /// <summary>Whether the last frame equalled the one before (the page has settled).</summary>
    public bool LastFrameStable { get; private set; } = true;

    public int StripCount => _slices.Count;

    public static ScrollStitcher? Start(PixelBuffer first)
    {
        var sample = ScrollSample.From(first);
        return sample is null ? null : new ScrollStitcher(first, sample);
    }

    public ScrollFeedKind Feed(PixelBuffer frame)
    {
        var sample = ScrollSample.From(frame);
        if (sample is null)
        {
            return ScrollFeedKind.Failed;
        }

        var c0 = _contentStart ?? 0;
        var c1 = _contentEnd ?? sample.Width;
        LastFrameStable = sample.Width == _lastObserved.Width && sample.Height == _lastObserved.Height
                          && ScrollMatcher.IsStable(_lastObserved, sample, c0, Math.Min(c1, sample.Width));
        _lastObserved = sample;

        var transition = ScrollMatcher.Transition(_previous, sample, _contentStart, _contentEnd);
        switch (transition.Kind)
        {
            case ScrollTransitionKind.End:
                HadUnmatched = false;
                return ScrollFeedKind.Stable;
            case ScrollTransitionKind.Unmatched:
                HadUnmatched = true;
                return ScrollFeedKind.Unmatched;
        }

        if (!transition.Forward)
        {
            // Back up the page: keep the furthest accepted frame as the reference.
            HadUnmatched = false;
            return ScrollFeedKind.Backward;
        }

        var h = sample.Height;
        var firstAdvance = _contentStart is null;
        if (firstAdvance)
        {
            var width = sample.Trim.Width;
            _pixelLeft = sample.Trim.X + (int)Math.Floor(transition.ColumnStart * width / (double)sample.Width);
            _pixelRight = sample.Trim.X + (int)Math.Ceiling(transition.ColumnEnd * width / (double)sample.Width);
            _footer = DetectFooter(_previous, sample, transition.ColumnStart, transition.ColumnEnd, transition.Overlap);

            // The first slice keeps only the moving columns, without the footer rows.
            var firstTrim = _firstSample.Trim;
            var firstRect = new PixelRect(_pixelLeft, firstTrim.Y, _pixelRight - _pixelLeft, firstTrim.Height - _footer);
            var firstSlice = CaptureImaging.Crop(_first, firstRect);
            if (firstSlice is null)
            {
                return ScrollFeedKind.Failed;
            }

            _retained -= (long)_slices[0].Width * _slices[0].Height;
            _slices[0] = firstSlice;
            _retained += (long)firstSlice.Width * firstSlice.Height;
            TotalHeight = firstSlice.Height;
            if (_footer > 0)
            {
                _footerSlice = CaptureImaging.Crop(frame, new PixelRect(_pixelLeft, sample.Trim.Bottom - _footer, _pixelRight - _pixelLeft, _footer));
            }

            _contentStart = transition.ColumnStart;
            _contentEnd = transition.ColumnEnd;
        }

        var pixelColumns = Math.Max(1, _pixelRight - _pixelLeft);
        var rowStart = transition.Overlap - _footer;
        var rowEnd = h - _footer;
        var rows = rowEnd - rowStart;
        if (rows <= 0)
        {
            _previous = sample;
            return ScrollFeedKind.Stable;
        }

        if (_slices.Count >= MaxStrips || TotalHeight + rows > MaxTotalPixels / pixelColumns)
        {
            return ScrollFeedKind.Limited;
        }

        var strip = CaptureImaging.Crop(frame, new PixelRect(_pixelLeft, sample.Trim.Y + rowStart, pixelColumns, rows));
        if (strip is null)
        {
            return ScrollFeedKind.Failed;
        }

        if (_retained + ((long)strip.Width * strip.Height) > MaxRetainedPixels)
        {
            return ScrollFeedKind.Limited;
        }

        if (_footer > 0 && !firstAdvance)
        {
            _footerSlice = CaptureImaging.Crop(frame, new PixelRect(_pixelLeft, sample.Trim.Bottom - _footer, pixelColumns, _footer));
        }

        _slices.Add(strip);
        _retained += (long)strip.Width * strip.Height;
        TotalHeight += rows;
        _previous = sample;
        HadUnmatched = false;
        return ScrollFeedKind.Appended;
    }

    /// <summary>Stacks the strips top to bottom at the narrowest width, footer last.</summary>
    public PixelBuffer Stitch()
    {
        var parts = new List<PixelBuffer>(_slices);
        if (_footerSlice is not null && _contentStart is not null)
        {
            parts.Add(_footerSlice);
        }

        if (parts.Count == 1)
        {
            return parts[0];
        }

        var width = parts.Min(p => p.Width);
        var height = parts.Sum(p => p.Height);
        var result = new PixelBuffer(width, height) { Scale = _first.Scale };
        var y = 0;
        foreach (var part in parts)
        {
            for (var row = 0; row < part.Height; row++)
            {
                Buffer.BlockCopy(part.Pixels, row * part.Stride, result.Pixels, (y + row) * result.Stride, width * 4);
            }

            y += part.Height;
        }

        return result;
    }

    /// <summary>
    /// Rows at the bottom identical across both frames (mean difference ≤ 2
    /// over the moving columns) form a fixed footer when there are at least
    /// max(4, min(12, H/100)) of them and fewer than the overlap.
    /// </summary>
    internal static int DetectFooter(ScrollSample previous, ScrollSample current, int c0, int c1, int overlap)
    {
        var h = current.Height;
        var count = 0;
        for (var y = h - 1; y >= 0; y--)
        {
            var a = previous.Row(y);
            var b = current.Row(y);
            var sum = 0;
            for (var c = c0; c < c1; c++)
            {
                sum += Math.Abs(a[c] - b[c]);
            }

            if (sum / (double)Math.Max(1, c1 - c0) > 2)
            {
                break;
            }

            count++;
        }

        var minimum = Math.Max(4, Math.Min(12, h / 100));
        return count >= minimum && count < overlap ? count : 0;
    }
}

/// <summary>
/// The scrolling-capture loop (spec 01 §6.16): polls frames every 90 ms,
/// never watches input. Finishing takes one more frame and waits up to
/// 0.85 s for the page to settle (0.22 s without change).
/// </summary>
public sealed class ScrollCaptureEngine
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(90);
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan FinishGrace = TimeSpan.FromMilliseconds(850);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120);

    private readonly Func<PixelBuffer?> _grab;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private volatile bool _finishRequested;

    public ScrollCaptureEngine(Func<PixelBuffer?> grab, Func<TimeSpan>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _grab = grab;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Total stitched height after each appended strip.</summary>
    public event Action<int>? Progress;

    /// <summary>Called once per frame (for automatic scrolling): true when the page was stable.</summary>
    public event Action<bool>? FrameProcessed;

    public void RequestFinish() => _finishRequested = true;

    public async Task<ScrollCaptureResult> RunAsync(CancellationToken cancellationToken)
    {
        var first = _grab();
        var stitcher = first is null ? null : ScrollStitcher.Start(first);
        if (stitcher is null)
        {
            return new ScrollCaptureResult(ScrollCaptureOutcome.Failed, null);
        }

        Progress?.Invoke(stitcher.TotalHeight);
        var start = _clock();
        var lastFrame = start;
        var lastChange = start;
        TimeSpan? finishAt = null;
        var pending = false;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScrollCaptureResult(ScrollCaptureOutcome.Cancelled, null);
            }

            var now = _clock();
            if (_finishRequested && finishAt is null)
            {
                finishAt = now;
                pending = true;
            }

            if (finishAt is { } f && (!pending || now - f >= FinishGrace))
            {
                return Complete(stitcher, stitcher.HadUnmatched ? ScrollCaptureOutcome.Partial : ScrollCaptureOutcome.Success);
            }

            if (now - start >= MaxDuration || stitcher.StripCount >= ScrollStitcher.MaxStrips)
            {
                return Complete(stitcher, ScrollCaptureOutcome.Limited);
            }

            var wait = SampleInterval - (now - lastFrame);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new ScrollCaptureResult(ScrollCaptureOutcome.Cancelled, null);
                }
            }

            lastFrame = _clock();
            var frame = _grab();
            if (frame is null)
            {
                return Complete(stitcher, ScrollCaptureOutcome.Partial);
            }

            var kind = stitcher.Feed(frame);
            now = _clock();
            if (!stitcher.LastFrameStable)
            {
                lastChange = now;
            }

            switch (kind)
            {
                case ScrollFeedKind.Failed:
                    return Complete(stitcher, ScrollCaptureOutcome.Partial);
                case ScrollFeedKind.Limited:
                    return Complete(stitcher, ScrollCaptureOutcome.Limited);
                case ScrollFeedKind.Appended:
                    Progress?.Invoke(stitcher.TotalHeight);
                    break;
            }

            FrameProcessed?.Invoke(stitcher.LastFrameStable);
            pending = !stitcher.LastFrameStable || now - lastChange < Settle;
        }
    }

    private static ScrollCaptureResult Complete(ScrollStitcher stitcher, ScrollCaptureOutcome outcome) =>
        new(outcome, stitcher.Stitch());
}
