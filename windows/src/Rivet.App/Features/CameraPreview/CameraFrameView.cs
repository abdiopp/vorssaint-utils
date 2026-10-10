// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;

namespace Rivet.App.Features.CameraPreview;

/// <summary>
/// Draws the latest camera frame aspect-filled (cropped evenly on the long
/// side) and mirrored. One bitmap is reused while the frame size stays the
/// same, so a running preview does not allocate per frame.
/// </summary>
public sealed class CameraFrameView : Control
{
    private WriteableBitmap? _bitmap;

    public bool Mirrored { get; set; } = true;

    public bool HasFrame => _bitmap is not null;

    public void SetFrame(PixelBuffer frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var locked = _bitmap.Lock())
        {
            var rowBytes = frame.Width * 4;
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Pixels, y * frame.Stride, locked.Address + (y * locked.RowBytes), rowBytes);
            }
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var size = Bounds.Size;
        var (x, y, width, height) = CameraPreviewLayout.AspectFillSource(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height, size.Width, size.Height);
        var mirror = Mirrored ? Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(size.Width, 0) : Matrix.Identity;
        using (context.PushTransform(mirror))
        {
            context.DrawImage(_bitmap, new Rect(x, y, width, height), new Rect(size));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Clear();
    }
}

/// <summary>A small white activity spinner (a rotating arc), animated only while visible and attached.</summary>
public sealed class CameraSpinner : Control
{
    // Immutable: shared by every window without being tied to one compositor.
    private static readonly IPen Track = new Avalonia.Media.Immutable.ImmutablePen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 2.4);
    private static readonly IPen Arc = new Avalonia.Media.Immutable.ImmutablePen(new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Colors.White), 2.4, lineCap: PenLineCap.Round);
    private DispatcherTimer? _timer;
    private double _angle;

    static CameraSpinner()
    {
        AffectsRender<CameraSpinner>(IsVisibleProperty);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }

        var radius = (size / 2) - 1.5;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, Track, center, radius, radius);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var start = _angle * Math.PI / 180;
            var end = start + (Math.PI * 1.5);
            g.BeginFigure(new Point(center.X + (radius * Math.Cos(start)), center.Y + (radius * Math.Sin(start))), false);
            g.ArcTo(new Point(center.X + (radius * Math.Cos(end)), center.Y + (radius * Math.Sin(end))), new Size(radius, radius), 0, true, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, Arc, geometry);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (!IsEffectivelyVisible)
            {
                return;
            }

            _angle = (_angle + 12) % 360;
            InvalidateVisual();
        });
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        base.OnDetachedFromVisualTree(e);
    }
}
