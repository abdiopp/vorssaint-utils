// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Shared drawing helpers for the timeline rows.</summary>
internal static class TimelineDraw
{
    public static readonly Typeface UiFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    public static readonly Typeface MediumFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);

    public static double X(double t, double duration, double width) =>
        duration <= 0 ? 0 : Math.Clamp(t / duration, 0, 1) * width;

    public static double Time(double x, double duration, double width) =>
        width <= 0 ? 0 : Math.Clamp(x / width, 0, 1) * duration;

    public static FormattedText Text(string text, double size, IBrush brush, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface ?? UiFont, size, brush);

    public static SolidColorBrush Brush(Color color, double alpha) => new(EditorPalette.WithAlpha(color, alpha));
}

/// <summary>
/// One lane of blocks — zooms, captions, images or blurs (spec 02 §3.21).
/// One press decides select / move / resize; a release on empty space puts
/// the selection down or adds a block; every release is one undo step.
/// </summary>
public sealed class LaneControl : Control
{
    private const double EdgeZone = 11;
    private const double HitSlop = 2;
    private readonly EditorSession _session;
    private readonly LaneKind _kind;
    private readonly Func<double, Task>? _addImage;
    private Gesture _gesture;
    private string? _gestureId;
    private Point _pressPoint;
    private double _grabOffset;
    private bool _interacting;
    private double? _hoverX;

    public LaneControl(EditorSession session, LaneKind kind, Func<double, Task>? addImage = null)
    {
        _session = session;
        _kind = kind;
        _addImage = addImage;
        Height = 30;
        Focusable = true;
        ClipToBounds = true;
    }

    private enum Gesture
    {
        None,
        PendingBody,
        PendingStartEdge,
        PendingEndEdge,
        Moving,
        ResizingStart,
        ResizingEnd,
        Empty,
    }

    public LaneKind Kind => _kind;

    public static double RampFor(LaneKind kind) => kind switch
    {
        LaneKind.Zoom => ZoomRamps.RampIn,
        LaneKind.Blur => 0,
        _ => OverlayLayout.Ramp,
    };

    public static string EmptyHintKey(LaneKind kind) => kind switch
    {
        LaneKind.Zoom => "recorder.zoomLaneEmptyHint",
        LaneKind.Text => "recorder.textLaneEmptyHint",
        LaneKind.Image => "recorder.imageLaneEmptyHint",
        _ => "recorder.blurLaneEmptyHint",
    };

    public override void Render(DrawingContext context)
    {
        var palette = EditorPalette.For(this);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(palette.InkBrush(0.04), null, bounds, 6, 6);
        var items = _session.Items(_kind);
        var duration = _session.Duration;
        if (items.Count == 0)
        {
            var dash = new Pen(palette.InkBrush(0.22), 1, new DashStyle([5, 4], 0));
            context.DrawRectangle(null, dash, bounds.Deflate(1), 6, 6);
            var hint = TimelineDraw.Text(L.Get(EmptyHintKey(_kind)), 10.5, palette.InkBrush(0.4), TimelineDraw.MediumFont);
            context.DrawText(hint, new Point((bounds.Width - hint.Width) / 2, (bounds.Height - hint.Height) / 2));
        }

        var accent = palette.KindAccent(_kind);
        foreach (var (id, start, end) in items)
        {
            var rect = BlockRect(start, end);
            var selected = _session.SelectedKind == _kind && _session.SelectedId == id;
            context.DrawRectangle(TimelineDraw.Brush(accent, 0.22), null, rect, 6, 6);

            var ramp = Math.Min(RampFor(_kind), (end - start) / 2);
            var holdStart = TimelineDraw.X(start + ramp, duration, Bounds.Width);
            if (rect.Right - holdStart > 1)
            {
                using (context.PushClip(new RoundedRect(rect, 6)))
                {
                    context.DrawRectangle(TimelineDraw.Brush(accent, 0.16), null, new Rect(holdStart, rect.Y, rect.Right - holdStart, rect.Height));
                }
            }

            var pen = new Pen(TimelineDraw.Brush(accent, selected ? 1 : 0.65), selected ? 2 : 1);
            context.DrawRectangle(null, pen, selected ? rect.Deflate(1) : rect.Deflate(0.5), 6, 6);

            if (rect.Width > 34)
            {
                var grip = TimelineDraw.Brush(accent, 0.9);
                var gh = rect.Height * 0.36;
                context.DrawRectangle(grip, null, new Rect(rect.X + 4, rect.Center.Y - (gh / 2), 2, gh), 1, 1);
                context.DrawRectangle(grip, null, new Rect(rect.Right - 6, rect.Center.Y - (gh / 2), 2, gh), 1, 1);
            }

            DrawLabel(context, palette, rect, id);
        }

        if (_hoverX is { } hx)
        {
            context.DrawRectangle(palette.InkBrush(0.12), null, new Rect(hx, 0, 1, Bounds.Height));
        }

        var playhead = TimelineDraw.X(_session.PlayheadSource, duration, Bounds.Width);
        context.DrawRectangle(palette.InkBrush(0.55), null, new Rect(playhead - 0.5, 0, 1, Bounds.Height));
    }

    private Rect BlockRect(double start, double end)
    {
        var x0 = TimelineDraw.X(start, _session.Duration, Bounds.Width);
        var x1 = TimelineDraw.X(end, _session.Duration, Bounds.Width);
        var width = Math.Max(6, x1 - x0);
        return new Rect(x0, 2, width, Math.Max(0, Bounds.Height - 4));
    }

    private void DrawLabel(DrawingContext context, EditorPalette palette, Rect rect, string id)
    {
        var (label, glyph) = LabelFor(id);
        var textBrush = palette.Dark ? new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(220, 0, 0, 0));
        const double glyphSize = 9;
        if (rect.Width < glyphSize + 10)
        {
            return;
        }

        FormattedText? text = null;
        if (rect.Width > 56 && label.Length > 0)
        {
            text = TimelineDraw.Text(label, 10, textBrush);
            if (glyphSize + 4 + text.Width + 10 > rect.Width)
            {
                text = null;
            }
        }

        var total = glyphSize + (text is null ? 0 : 4 + text.Width);
        var x = rect.X + ((rect.Width - total) / 2);
        var cy = rect.Center.Y;
        DrawGlyph(context, glyph, new Rect(x, cy - (glyphSize / 2), glyphSize, glyphSize), textBrush);
        if (text is not null)
        {
            context.DrawText(text, new Point(x + glyphSize + 4, cy - (text.Height / 2)));
        }
    }

    private (string Label, char Glyph) LabelFor(string id)
    {
        var doc = _session.Document;
        switch (_kind)
        {
            case LaneKind.Zoom:
                var zoom = doc.ZoomSegments.FirstOrDefault(z => z.Id == id);
                return zoom is null ? (string.Empty, 'f') : (zoom.Amount.ToString("0.0", CultureInfo.CurrentUICulture) + "×", zoom.IsAimed ? 'a' : 'f');
            case LaneKind.Text:
                var text = doc.Texts.FirstOrDefault(t => t.Id == id)?.Text.Trim() ?? string.Empty;
                return (text.Length > 18 ? text[..18] : text, 'T');
            case LaneKind.Image:
                var name = Path.GetFileNameWithoutExtension(doc.Images.FirstOrDefault(i => i.Id == id)?.Path ?? string.Empty);
                return (name.Length > 18 ? name[..18] : name, 'i');
            default:
                return (L.Get("recorder.blurLaneLabel"), 'b');
        }
    }

    /// <summary>Small vector glyphs (↗ follows, ◉ aimed, T, ▣ image, ▦ blur) so no symbol font is needed.</summary>
    internal static void DrawGlyph(DrawingContext context, char glyph, Rect r, IBrush brush)
    {
        var pen = new Pen(brush, 1.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        switch (glyph)
        {
            case 'f':
                context.DrawLine(pen, r.BottomLeft + new Vector(1, -1), r.TopRight + new Vector(-1, 1));
                context.DrawLine(pen, r.TopRight + new Vector(-1, 1), r.TopRight + new Vector(-5, 1));
                context.DrawLine(pen, r.TopRight + new Vector(-1, 1), r.TopRight + new Vector(-1, 5));
                break;
            case 'a':
                context.DrawEllipse(null, pen, r.Center, r.Width / 2 - 0.5, r.Height / 2 - 0.5);
                context.DrawEllipse(brush, null, r.Center, 1.8, 1.8);
                break;
            case 'T':
                context.DrawLine(pen, r.TopLeft + new Vector(0.5, 1), r.TopRight + new Vector(-0.5, 1));
                context.DrawLine(pen, new Point(r.Center.X, r.Top + 1), new Point(r.Center.X, r.Bottom));
                break;
            case 'i':
                context.DrawRectangle(null, pen, r.Deflate(0.5), 1.5, 1.5);
                context.DrawRectangle(brush, null, r.Deflate(3), 0.5, 0.5);
                break;
            default:
                context.DrawRectangle(null, pen, r.Deflate(0.5), 1.5, 1.5);
                context.DrawLine(pen, new Point(r.Center.X, r.Top + 1), new Point(r.Center.X, r.Bottom - 1));
                context.DrawLine(pen, new Point(r.Left + 1, r.Center.Y), new Point(r.Right - 1, r.Center.Y));
                break;
        }
    }

    // ── Gestures ──────────────────────────────────────────────────────

    private (string Id, Gesture Zone)? HitTest(Point p)
    {
        // Topmost (last drawn) first.
        foreach (var (id, start, end) in _session.Items(_kind).Reverse())
        {
            var rect = BlockRect(start, end).Inflate(HitSlop);
            if (!rect.Contains(p))
            {
                continue;
            }

            var edge = Math.Min(EdgeZone, (rect.Width - (2 * HitSlop)) * 0.4);
            if (p.X <= rect.X + HitSlop + edge)
            {
                return (id, Gesture.PendingStartEdge);
            }

            if (p.X >= rect.Right - HitSlop - edge)
            {
                return (id, Gesture.PendingEndEdge);
            }

            return (id, Gesture.PendingBody);
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _hoverX = p.X;
        if (_gesture == Gesture.None)
        {
            Cursor = HitTest(p) is { } hit
                ? new Cursor(hit.Zone == Gesture.PendingBody ? StandardCursorType.Hand : StandardCursorType.SizeWestEast)
                : new Cursor(StandardCursorType.Arrow);
            InvalidateVisual();
            return;
        }

        var dx = Math.Abs(p.X - _pressPoint.X);
        switch (_gesture)
        {
            case Gesture.PendingBody when dx >= 4:
                _gesture = Gesture.Moving;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                Begin();
                break;
            case Gesture.PendingStartEdge when dx >= 3:
                _gesture = Gesture.ResizingStart;
                Begin();
                break;
            case Gesture.PendingEndEdge when dx >= 3:
                _gesture = Gesture.ResizingEnd;
                Begin();
                break;
        }

        if (_gestureId is { } id)
        {
            var t = TimelineDraw.Time(p.X, _session.Duration, Bounds.Width);
            switch (_gesture)
            {
                case Gesture.Moving:
                    _session.MoveItem(_kind, id, Snap(t - _grabOffset, id));
                    break;
                case Gesture.ResizingStart:
                    _session.ResizeItem(_kind, id, startEdge: true, Snap(t, id));
                    break;
                case Gesture.ResizingEnd:
                    _session.ResizeItem(_kind, id, startEdge: false, Snap(t, id));
                    break;
            }
        }

        InvalidateVisual();
    }

    private double Snap(double t, string? exceptId) =>
        LaneMath.Snap(t, _session.SnapCandidates(_kind, exceptId), Bounds.Width, _session.Duration);

    private void Begin()
    {
        if (!_interacting)
        {
            _interacting = true;
            _session.BeginInteraction();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_session.IsReady)
        {
            return;
        }

        Focus();
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        var hit = HitTest(p);
        if (point.Properties.IsRightButtonPressed)
        {
            if (hit is { } target)
            {
                _session.Select(_kind, target.Id);
                ShowContextMenu(target.Id);
            }

            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressPoint = p;
        if (hit is { } h)
        {
            _session.Select(_kind, h.Id);
            _gesture = h.Zone;
            _gestureId = h.Id;
            var item = _session.Items(_kind).First(i => i.Id == h.Id);
            _grabOffset = TimelineDraw.Time(p.X, _session.Duration, Bounds.Width) - item.Start;
        }
        else
        {
            _gesture = Gesture.Empty;
            _gestureId = null;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var gesture = _gesture;
        _gesture = Gesture.None;
        _gestureId = null;
        e.Pointer.Capture(null);
        if (_interacting)
        {
            _interacting = false;
            _session.CommitInteraction();
        }
        else if (gesture == Gesture.Empty)
        {
            // First click on empty space puts a selection down; otherwise it adds a block.
            if (_session.SelectedKind == _kind)
            {
                _session.ClearSelection();
            }
            else
            {
                var t = Snap(TimelineDraw.Time(e.GetPosition(this).X, _session.Duration, Bounds.Width), null);
                if (_kind == LaneKind.Image)
                {
                    _ = _addImage?.Invoke(t);
                }
                else
                {
                    _session.AddAt(_kind, t);
                }
            }
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
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

        _gesture = Gesture.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && _session.SelectedKind == _kind)
        {
            _session.RemoveSelected();
            e.Handled = true;
        }
    }

    private void ShowContextMenu(string id)
    {
        var remove = new MenuItem { Header = L.Get("recorder.removeZoom") };
        remove.Click += (_, _) => _session.Remove(_kind, id);
        var menu = new ContextMenu { Items = { remove } };
        menu.Open(this);
    }
}
