// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rivet.App.Controls;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.Backdrop;
using Rivet.Imaging.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The editor canvas (spec 01 §3.10.3): draws through the shared renderer at
/// display scale and turns pointer input into image-space gestures. Zoom is
/// view DIPs per image pixel; fit mode never enlarges past one DIP per pixel.
/// </summary>
internal sealed class EditorCanvas : SkiaView
{
    private readonly EditorController _controller;
    private Size _viewport = new(800, 600);
    private Size? _pendingViewport;
    private Vector _scrollOffset;
    private double _zoom = 1;
    private bool _pressed;

    public EditorCanvas(EditorController controller)
    {
        _controller = controller;
        ClipToBounds = true;
        Focusable = false;
        Cursor = CursorFor(StandardCursorType.Cross);
    }

    /// <summary>Raised before a press is handled, so the window can commit the inline text editor.</summary>
    public event EventHandler? PressStarting;

    /// <summary>The zoom or the fit/1:1 state changed.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>The scroll offset should move (zoom anchoring).</summary>
    public event EventHandler<Vector>? ScrollRequested;

    public bool FitMode { get; private set; } = true;

    public double Zoom => FitMode ? FitZoom() : _zoom;

    public bool IsDragging => _pressed;

    public bool IsActualSize => !FitMode && Math.Abs(_zoom - EditorLayoutMath.ActualSizeZoom(_controller.Scale)) < 1e-6;

    public int ZoomPercent => EditorLayoutMath.ZoomPercent(Zoom, _controller.Scale);

    /// <summary>The backdrop margin in image pixels (0 without a backdrop).</summary>
    public double Padding => BackdropRenderer.Measure(_controller.BaseImage.Width, _controller.BaseImage.Height, _controller.Backdrop).Padding;

    public Size ContentPixels
    {
        get
        {
            var g = BackdropRenderer.Measure(_controller.BaseImage.Width, _controller.BaseImage.Height, _controller.Backdrop);
            return new Size(g.CanvasWidth, g.CanvasHeight);
        }
    }

    /// <summary>The scroll viewport (available area) changed.</summary>
    public void SetViewport(Size viewport, Vector offset)
    {
        _scrollOffset = offset;
        if (_pressed)
        {
            // During a drag the canvas keeps the layout it had when the drag started.
            _pendingViewport = viewport;
        }
        else if (viewport != _viewport)
        {
            _viewport = viewport;
            InvalidateMeasure();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }

        InvalidateVisual();
    }

    public void SetScrollOffset(Vector offset)
    {
        _scrollOffset = offset;
        InvalidateVisual();
    }

    public void Fit()
    {
        FitMode = true;
        Relayout(null);
    }

    public void ActualSize() => SetZoom(EditorLayoutMath.ActualSizeZoom(_controller.Scale), null);

    public void ZoomBy(double factor, Point? anchor = null) => SetZoom(Zoom * factor, anchor);

    public void SetZoom(double zoom, Point? anchor)
    {
        var clamped = EditorLayoutMath.ClampZoom(zoom);
        // Keyboard zoom keeps the middle of the visible area in place.
        anchor ??= new Point(_scrollOffset.X + (_viewport.Width / 2), _scrollOffset.Y + (_viewport.Height / 2));
        var before = anchor is { } a ? ViewToImage(a) : (ImgPoint?)null;
        var anchorInViewport = anchor is { } p ? p - _scrollOffset : default;
        FitMode = false;
        _zoom = clamped;
        Relayout(before is { } img ? (img, anchorInViewport) : null);
    }

    /// <summary>Content size changed (backdrop margin, crop): keep the current mode.</summary>
    public void ContentChanged()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Relayout((ImgPoint Image, Vector AnchorInViewport)? keep)
    {
        InvalidateMeasure();
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        if (keep is { } k)
        {
            var size = MeasureSize();
            var origin = OriginFor(size);
            var z = Zoom;
            var pad = Padding;
            var view = new Point(origin.X + ((k.Image.X + pad) * z), origin.Y + ((k.Image.Y + pad) * z));
            ScrollRequested?.Invoke(this, new Vector(view.X - k.AnchorInViewport.X, view.Y - k.AnchorInViewport.Y));
        }
    }

    private double FitZoom()
    {
        var content = ContentPixels;
        return EditorLayoutMath.FitZoom(_viewport.Width, _viewport.Height, content.Width, content.Height);
    }

    private Size MeasureSize()
    {
        var content = ContentPixels;
        var z = Zoom;
        var margin = 2 * EditorLayoutMath.CanvasMargin;
        return new Size(Math.Max(_viewport.Width, (content.Width * z) + margin), Math.Max(_viewport.Height, (content.Height * z) + margin));
    }

    protected override Size MeasureOverride(Size availableSize) => MeasureSize();

    /// <summary>Top-left of the content in canvas DIPs: centred when smaller than the viewport, else at the 14-point margin.</summary>
    private Point OriginFor(Size bounds)
    {
        var content = ContentPixels;
        var z = Zoom;
        var w = content.Width * z;
        var h = content.Height * z;
        var x = Math.Max(EditorLayoutMath.CanvasMargin, (bounds.Width - w) / 2);
        var y = Math.Max(EditorLayoutMath.CanvasMargin, (bounds.Height - h) / 2);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return new Point(Math.Round(x * scaling) / scaling, Math.Round(y * scaling) / scaling);
    }

    public Point Origin => OriginFor(Bounds.Size);

    public ImgPoint ViewToImage(Point p)
    {
        var o = Origin;
        return EditorLayoutMath.ViewToImage(p.X - o.X, p.Y - o.Y, Zoom, Padding);
    }

    public Point ImageToView(ImgPoint p)
    {
        var o = Origin;
        var z = Zoom;
        var pad = Padding;
        return new Point(o.X + ((p.X + pad) * z), o.Y + ((p.Y + pad) * z));
    }

    /// <summary>
    /// Snapshots everything the frame needs on the UI thread: the draw operation itself
    /// runs later on Avalonia's render thread, which must never read live editor state.
    /// The snapshot is immutable (the session replaces its lists instead of changing them).
    /// </summary>
    public override void Render(DrawingContext context)
    {
        var session = _controller.Session;
        var origin = Origin;
        _frame = new CanvasFrame
        {
            State = _controller.RenderState(),
            Zoom = Zoom,
            Origin = new SKPoint((float)origin.X, (float)origin.Y),
            RenderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1,
            Backdrop = _controller.Backdrop,
            Selected = session.Selected,
            CropDraft = session.Tool == EditorTool.Crop ? session.CropDraft : null,
            CropLoupePoint = session.CropLoupePoint,
            Words = session.Recognition.Words,
            SelectedWords = session.SelectedWords,
            Viewport = SKRect.Create((float)_scrollOffset.X, (float)_scrollOffset.Y, (float)_viewport.Width, (float)_viewport.Height),
        };
        base.Render(context);
    }

    private volatile CanvasFrame? _frame;

    protected override void OnDraw(SkiaDrawEventArgs e)
    {
        if (_frame is { } frame)
        {
            _controller.Painter.Paint(e.Canvas, frame with { RenderScaling = e.Scaling });
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || _controller.IsBusy)
        {
            return;
        }

        PressStarting?.Invoke(this, EventArgs.Empty);
        _pressed = true;
        e.Pointer.Capture(this);
        var p = point.Position;
        _controller.Session.PointerDown(ViewToImage(p), new ImgPoint(p.X, p.Y));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var image = ViewToImage(p);
        if (_pressed)
        {
            _controller.Session.PointerMove(image, new ImgPoint(p.X, p.Y));
            e.Handled = true;
        }
        else
        {
            UpdateCursor(image);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        var p = e.GetPosition(this);
        _controller.Session.PointerUp(ViewToImage(p), new ImgPoint(p.X, p.Y));
        e.Pointer.Capture(null);
        e.Handled = true;
        ApplyPendingViewport();
        UpdateCursor(ViewToImage(p));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressed)
        {
            _pressed = false;
            _controller.Session.CancelGesture();
            ApplyPendingViewport();
        }
    }

    private void ApplyPendingViewport()
    {
        if (_pendingViewport is { } pending)
        {
            _pendingViewport = null;
            SetViewport(pending, _scrollOffset);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // A notch is ±1 here; ten units per notch gives ×1.14, and precision touchpads send small fractions.
            ZoomBy(EditorLayoutMath.WheelFactor(e.Delta.Y * 10), e.GetPosition(this));
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    private void UpdateCursor(ImgPoint image)
    {
        var session = _controller.Session;
        var hover = session.HoverAt(image);
        var type = hover.Kind switch
        {
            HoverKind.Handle or HoverKind.CropHandle => HandleCursor(hover.Handle),
            HoverKind.Endpoint => StandardCursorType.Hand,
            HoverKind.SelectedBody or HoverKind.CropInside => StandardCursorType.SizeAll,
            HoverKind.Word => StandardCursorType.Ibeam,
            HoverKind.CropNew => StandardCursorType.Cross,
            _ => EditorTools.IsCreation(session.Tool) ? StandardCursorType.Cross : StandardCursorType.Arrow,
        };
        if (Cursor is null || _cursorType != type)
        {
            _cursorType = type;
            Cursor = CursorFor(type);
        }
    }

    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = [];

    /// <summary>One cursor object per shape for the whole app (cursors hold native handles).</summary>
    private static Cursor CursorFor(StandardCursorType type)
    {
        if (!Cursors.TryGetValue(type, out var cursor))
        {
            Cursors[type] = cursor = new Cursor(type);
        }

        return cursor;
    }

    private StandardCursorType _cursorType = StandardCursorType.Cross;

    private static StandardCursorType HandleCursor(ResizeHandle? handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => StandardCursorType.TopLeftCorner,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => StandardCursorType.TopRightCorner,
        ResizeHandle.Top or ResizeHandle.Bottom => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };
}
