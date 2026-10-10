// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Rivet.App.Controls;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The click ruler above the zoom lane (spec 02 §3.20.8): one tick per press,
/// the playhead, and dragging anywhere scrubs. The ticks double as zoom snap
/// targets and quietly explain why each automatic zoom exists.
/// </summary>
public sealed class ClickRuler : Control
{
    private readonly EditorSession _session;
    private readonly double[] _presses;
    private bool _dragging;

    public ClickRuler(EditorSession session)
    {
        _session = session;
        _presses = session.Take.Pointer.Clicks.Where(c => c.IsDown).Select(c => (double)c.Time).ToArray();
        Height = 12;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    public override void Render(DrawingContext context)
    {
        var palette = EditorPalette.For(this);
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(palette.InkBrush(0.05), null, new Rect(0, h - 1, w, 1));
        var tick = palette.InkBrush(0.35);
        foreach (var t in _presses)
        {
            var x = TimelineDraw.X(t, _session.Duration, w);
            context.DrawRectangle(tick, null, new Rect(x - 0.75, h - 7, 1.5, 7));
        }

        var playhead = TimelineDraw.X(_session.PlayheadSource, _session.Duration, w);
        context.DrawRectangle(palette.InkBrush(palette.Dark ? 1 : 0.85), null, new Rect(playhead - 1, 0, 2, h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_session.IsReady || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _session.Playback.Pause();   // scrubbing pauses (the audio device is not restarted per move)
        e.Pointer.Capture(this);
        Scrub(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Scrub(e.GetPosition(this).X);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Scrub(double x) => _session.SeekSource(TimelineDraw.Time(x, _session.Duration, Bounds.Width));
}

/// <summary>
/// The "Recording" row (spec 02 §3.20.7): 14 thumbnails, trim handles, cut
/// stretches with their seams, scrubbing, and Shift-drag to pick a stretch to cut.
/// </summary>
public sealed class Filmstrip : Control
{
    private const double HandleWidth = 12;
    private const double HandleSlop = 6;
    private const double SeamSlop = 4;
    private readonly EditorSession _session;
    private readonly Bitmap?[] _bitmaps = new Bitmap?[EditorSession.ThumbnailCount];
    private Mode _mode;
    private Point _press;
    private bool _shiftAtPress;
    private bool _interacting;

    public Filmstrip(EditorSession session)
    {
        _session = session;
        Height = 54;
        ClipToBounds = true;
    }

    private enum Mode
    {
        None,
        Pending,
        Scrub,
        Select,
        TrimStart,
        TrimEnd,
    }

    /// <summary>Converts newly loaded thumbnails to bitmaps (UI thread).</summary>
    public void RefreshThumbnails()
    {
        var thumbnails = _session.Thumbnails;
        for (var i = 0; i < _bitmaps.Length; i++)
        {
            if (_bitmaps[i] is null && thumbnails[i] is { } image)
            {
                _bitmaps[i] = ImageInterop.ToBitmap(image);
            }
        }

        InvalidateVisual();
    }

    private (double Start, double End) HandleXs()
    {
        var trim = _session.Timeline.Trim;
        var w = Bounds.Width;
        var start = TimelineDraw.X(trim.Start, _session.Duration, w);
        var end = Math.Max(start + 24, TimelineDraw.X(trim.End, _session.Duration, w));
        return (start, Math.Min(w, end));
    }

    public override void Render(DrawingContext context)
    {
        var palette = EditorPalette.For(this);
        var w = Bounds.Width;
        var h = Bounds.Height;
        var strip = new Rect(0, 0, w, h);
        var clip = new RoundedRect(strip, 8);
        using (context.PushClip(clip))
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x30)), null, strip);
            var slot = w / EditorSession.ThumbnailCount;
            for (var i = 0; i < _bitmaps.Length; i++)
            {
                var cell = new Rect(i * slot, 0, slot + 0.5, h);
                if (_bitmaps[i] is not { } bitmap)
                {
                    continue;
                }

                // Aspect-fill each slot.
                var scale = Math.Max(cell.Width / bitmap.Size.Width, cell.Height / bitmap.Size.Height);
                var sw = cell.Width / scale;
                var sh = cell.Height / scale;
                var source = new Rect((bitmap.Size.Width - sw) / 2, (bitmap.Size.Height - sh) / 2, sw, sh);
                context.DrawImage(bitmap, source, cell);
            }

            var dim = new SolidColorBrush(Color.FromArgb(158, 0, 0, 0));
            var (startX, endX) = HandleXs();
            if (startX > 0)
            {
                context.DrawRectangle(dim, null, new Rect(0, 0, startX, h));
            }

            if (endX < w)
            {
                context.DrawRectangle(dim, null, new Rect(endX, 0, w - endX, h));
            }

            var seam = TimelineDraw.Brush(EditorPalette.SeamOrange, 0.9);
            foreach (var cut in _session.Document.Cuts)
            {
                var x0 = TimelineDraw.X(cut.Start, _session.Duration, w);
                var x1 = TimelineDraw.X(cut.End, _session.Duration, w);
                context.DrawRectangle(dim, null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
                context.DrawRectangle(seam, null, new Rect(x0 - 1.5, 0, 3, h));
            }

            if (_session.CutSelection is { } selection)
            {
                var x0 = TimelineDraw.X(selection.Start, _session.Duration, w);
                var x1 = TimelineDraw.X(selection.End, _session.Duration, w);
                var rect = new Rect(x0, 0.75, Math.Max(1, x1 - x0), h - 1.5);
                context.DrawRectangle(TimelineDraw.Brush(EditorPalette.CutRed, 0.28), new Pen(TimelineDraw.Brush(EditorPalette.CutRed, 1), 1.5), rect);
            }

            var playhead = TimelineDraw.X(_session.PlayheadSource, _session.Duration, w);
            context.DrawRectangle(Brushes.White, null, new Rect(playhead - 1, 0, 2, h));
        }

        // The kept span outlined, with the trim handles at its ends.
        var (s, e) = HandleXs();
        var accent = palette.Accent;
        context.DrawRectangle(null, new Pen(new SolidColorBrush(accent), 2), new Rect(s + 1, 1, Math.Max(0, e - s - 2), h - 2), 8, 8);
        DrawHandle(context, s, accent, h);
        DrawHandle(context, e - HandleWidth, accent, h);
    }

    private static void DrawHandle(DrawingContext context, double x, Color accent, double h)
    {
        context.DrawRectangle(new SolidColorBrush(accent), null, new Rect(x, 0, HandleWidth, h), 4, 4);
        var gh = h * 0.36;
        context.DrawRectangle(Brushes.White, null, new Rect(x + (HandleWidth / 2) - 1, (h - gh) / 2, 2, gh), 1, 1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_session.IsReady || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        var (s, end) = HandleXs();
        _press = p;
        _shiftAtPress = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (p.X >= s - HandleSlop && p.X <= s + HandleWidth + HandleSlop)
        {
            _mode = Mode.TrimStart;
        }
        else if (p.X >= end - HandleWidth - HandleSlop && p.X <= end + HandleSlop)
        {
            _mode = Mode.TrimEnd;
        }
        else
        {
            _mode = Mode.Pending;
        }

        if (_mode is Mode.TrimStart or Mode.TrimEnd)
        {
            _interacting = true;
            _session.BeginInteraction();
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var t = TimelineDraw.Time(p.X, _session.Duration, Bounds.Width);
        if (_mode == Mode.None)
        {
            var (s, end) = HandleXs();
            var onHandle = (p.X >= s - HandleSlop && p.X <= s + HandleWidth + HandleSlop) || (p.X >= end - HandleWidth - HandleSlop && p.X <= end + HandleSlop);
            Cursor = new Cursor(onHandle ? StandardCursorType.SizeWestEast : StandardCursorType.Arrow);
            return;
        }

        switch (_mode)
        {
            case Mode.Pending when Math.Abs(p.X - _press.X) >= 2:
                _session.Playback.Pause();
                // Shift is decided on the first movement and kept for the whole drag.
                _mode = _shiftAtPress || (e.KeyModifiers & KeyModifiers.Shift) != 0 ? Mode.Select : Mode.Scrub;
                if (_mode == Mode.Select)
                {
                    _session.ClearSelection();
                }

                break;
            case Mode.TrimStart:
                _session.DragTrimStart(t);
                break;
            case Mode.TrimEnd:
                _session.DragTrimEnd(t);
                break;
        }

        if (_mode == Mode.Scrub)
        {
            _session.SeekSource(t);
        }
        else if (_mode == Mode.Select)
        {
            var from = TimelineDraw.Time(_press.X, _session.Duration, Bounds.Width);
            _session.SetCutSelection(new TimeRange(Math.Min(from, t), Math.Max(from, t)));
            _session.SeekSource(t);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Read the gesture before releasing capture: losing capture resets it.
        var mode = _mode;
        _mode = Mode.None;
        if (_interacting)
        {
            _interacting = false;
            _session.CommitInteraction();
        }

        e.Pointer.Capture(null);

        var p = e.GetPosition(this);
        var t = TimelineDraw.Time(p.X, _session.Duration, Bounds.Width);
        if (mode == Mode.Pending)
        {
            // A click: restore a cut whose seam is under the pointer, or clear the selection and seek.
            var seamHit = _session.Document.Cuts.Any(c => Math.Abs(TimelineDraw.X(c.Start, _session.Duration, Bounds.Width) - p.X) <= SeamSlop);
            if (seamHit && _session.RestoreCutAt(t))
            {
                return;
            }

            _session.SetCutSelection(null);
            _session.SeekSource(t);
        }
        else if (mode == Mode.Select && _session.CutSelection is { Length: < EditTimeline.MinimumCut })
        {
            _session.SetCutSelection(null);
        }

        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_interacting)
        {
            _interacting = false;
            _session.CommitInteraction();
        }

        _mode = Mode.None;
    }
}

/// <summary>An audio track's waveform (spec 02 §3.26): 220 peak bars, dimmed when the track is removed.</summary>
public sealed class WaveformView : Control
{
    private readonly EditorSession _session;
    private readonly bool _microphone;

    public WaveformView(EditorSession session, bool microphone)
    {
        _session = session;
        _microphone = microphone;
        Height = 32;
    }

    public override void Render(DrawingContext context)
    {
        
        var tint = _microphone ? EditorPalette.TextPurple : EditorPalette.SystemBlue;
        var removed = _microphone ? !_session.Document.KeepsMicrophone : !_session.Document.KeepsSystemAudio;
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(TimelineDraw.Brush(tint, removed ? 0.04 : 0.11), new Pen(TimelineDraw.Brush(tint, removed ? 0.10 : 0.28), 1), bounds.Deflate(0.5), 7, 7);
        var peaks = _microphone ? _session.MicrophoneWaveform : _session.SystemWaveform;
        var bar = TimelineDraw.Brush(tint, (removed ? 0.3 : 1) * 0.8);
        var h = bounds.Height;
        if (peaks is null)
        {
            context.DrawRectangle(bar, null, new Rect(4, (h / 2) - 0.5, bounds.Width - 8, 1));
            return;
        }

        var slot = bounds.Width / peaks.Length;
        for (var i = 0; i < peaks.Length; i++)
        {
            var height = Math.Max(1, peaks[i] * (h - 8));
            context.DrawRectangle(bar, null, new Rect((i * slot) + (slot * 0.16), (h - height) / 2, slot * 0.68, height), 0.7, 0.7);
        }

        // No playhead here: the waveform shares the row with its volume controls, so it is narrower
        // than the lanes (as on macOS) and a playhead would not line up with theirs.
    }
}
