// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;
using SkiaSharp;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The stage (spec 02 §3.20.4): the composed preview aspect-fit on a dark
/// rounded well. A click toggles playback (or puts down a selected zoom or
/// blur); while aiming it sets the zoom focus, while drawing a blur it drags
/// out the area on the raw recording.
/// </summary>
public sealed class StageView : Panel
{
    private readonly EditorSession _session;
    private readonly FrameSurface _surface;
    private readonly Border _hint;
    private readonly TextBlock _hintText;
    private readonly TextBlock _message;

    public StageView(EditorSession session)
    {
        _session = session;
        _surface = new FrameSurface(this);
        _hintText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White };
        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            IsHitTestVisible = false,
            IsVisible = false,
            Child = _hintText,
        };
        _message = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            [!Border.BackgroundProperty] = this.GetResourceObservable("EditorStageBrush").ToBinding(),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("EditorStageBorderBrush").ToBinding(),
            Child = _surface,
        });
        Children.Add(_hint);
        Children.Add(_message);
        Cursor = new Cursor(StandardCursorType.Arrow);
        _session.Changed += OnChanged;
        _session.Playback.FrameReady += _surface.InvalidateVisual;
        Refresh();
    }

    /// <summary>The blur area being dragged, in surface DIPs.</summary>
    internal Rect? DraftArea { get; private set; }

    private Point? _press;

    private void OnChanged(SessionChange change)
    {
        if ((change & (SessionChange.Mode | SessionChange.Selection)) != 0)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_session.LoadError is not null)
        {
            _message.Text = L.Get("win.recordingEditor.openFailed");
            _message.IsVisible = true;
        }

        _hint.IsVisible = _session.IsAiming || _session.IsDrawingBlur;
        _hintText.Text = _session.IsAiming ? L.Get("recorder.zoomPickSpotHint") : L.Get("recorder.blurPickAreaHint");
        Cursor = new Cursor(_session.IsAiming || _session.IsDrawingBlur ? StandardCursorType.Cross : StandardCursorType.Arrow);
        _surface.InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _session.Playback.SetViewport(e.NewSize.Width * scaling, e.NewSize.Height * scaling);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !_session.IsReady)
        {
            return;
        }

        _press = e.GetPosition(_surface);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } start || !_session.IsDrawingBlur)
        {
            return;
        }

        var p = e.GetPosition(_surface);
        if (Math.Abs(p.X - start.X) >= 2 || Math.Abs(p.Y - start.Y) >= 2)
        {
            DraftArea = new Rect(start, p).Normalize();
            _surface.InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is not { } start)
        {
            return;
        }

        _press = null;
        e.Pointer.Capture(null);
        var end = e.GetPosition(_surface);
        var size = _surface.Bounds.Size;
        if (_session.IsDrawingBlur && _session.SelectedId is { } blurId)
        {
            var area = DraftArea;
            DraftArea = null;
            if (area is { } a)
            {
                var (u0, v0) = StageMapping.ToPicture(a.Left, a.Top, size.Width, size.Height, _session.SourceWidth, _session.SourceHeight);
                var (u1, v1) = StageMapping.ToPicture(a.Right, a.Bottom, size.Width, size.Height, _session.SourceWidth, _session.SourceHeight);
                _session.SetBlurArea(blurId, u0, v0, u1, v1);
            }
            else
            {
                _session.EndModes();
            }

            _surface.InvalidateVisual();
            return;
        }

        if (_session.IsAiming && _session.SelectedId is { } zoomId)
        {
            var (u, v) = StageMapping.ToPicture(end.X, end.Y, size.Width, size.Height, _session.SourceWidth, _session.SourceHeight);
            if (u is >= 0 and <= 1 && v is >= 0 and <= 1)
            {
                _session.SetZoomFocus(zoomId, u, v);
            }

            _session.EndModes();
            return;
        }

        if (Math.Abs(end.X - start.X) > 4 || Math.Abs(end.Y - start.Y) > 4)
        {
            return;
        }

        if (_session.SelectedKind is LaneKind.Zoom or LaneKind.Blur)
        {
            _session.ClearSelection();
        }
        else
        {
            _session.TogglePlay();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _session.Changed -= OnChanged;
        _session.Playback.FrameReady -= _surface.InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Draws the latest frame aspect-fit, plus the blur area being drawn.</summary>
    private sealed class FrameSurface(StageView owner) : SkiaView
    {
        protected override void OnDraw(SkiaDrawEventArgs e)
        {
            var canvas = e.Canvas;
            var size = e.Size;
            owner._session.Playback.WithFrame((bitmap, _) =>
            {
                if (bitmap is null)
                {
                    return;
                }

                var (x, y, w, h) = StageMapping.FitRect(size.Width, size.Height, bitmap.Width, bitmap.Height);
                // Copied under the frame lock: the GPU upload may happen after the lock is released,
                // when the worker is already drawing the next frame into this buffer.
                using var pixmap = bitmap.PeekPixels();
                using var image = SKImage.FromPixelCopy(pixmap);
                if (image is not null)
                {
                    canvas.DrawImage(image, SKRect.Create((float)x, (float)y, (float)w, (float)h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                }
            });

            if (owner.DraftArea is { } area)
            {
                var rect = SKRect.Create((float)area.X, (float)area.Y, (float)area.Width, (float)area.Height);
                using var fill = new SKPaint { Color = new SKColor(0x30, 0xB0, 0xC7, 56), IsAntialias = true };
                using var border = new SKPaint { Color = new SKColor(0x30, 0xB0, 0xC7), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, border);
            }
        }
    }
}
