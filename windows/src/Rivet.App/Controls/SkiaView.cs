// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Rivet.App.Controls;

public sealed class SkiaDrawEventArgs(SKCanvas canvas, Size size, double scaling) : EventArgs
{
    /// <summary>Canvas in DIP units (already scaled to the window's render scaling).</summary>
    public SKCanvas Canvas { get; } = canvas;

    /// <summary>Control size in DIPs.</summary>
    public Size Size { get; } = size;

    /// <summary>Render scaling of the window (1.5 at 150 %), to pick crisp bitmap sizes.</summary>
    public double Scaling { get; } = scaling;
}

/// <summary>
/// Draws with SkiaSharp directly inside Avalonia's render pass. The screenshot
/// editor and the recorder use it so the canvas and the exported file go
/// through the same renderer. Call <see cref="Visual.InvalidateVisual"/> to redraw.
/// </summary>
public class SkiaView : Control
{
    public event EventHandler<SkiaDrawEventArgs>? Draw;

    /// <summary>Alternative to the event for subclasses.</summary>
    protected virtual void OnDraw(SkiaDrawEventArgs e) => Draw?.Invoke(this, e);

    public override void Render(DrawingContext context)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        context.Custom(new DrawOperation(this, new Rect(Bounds.Size), scaling));
    }

    private sealed class DrawOperation(SkiaView owner, Rect bounds, double scaling) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null)
            {
                return;
            }

            using var api = lease.Lease();
            var canvas = api.SkCanvas;
            var count = canvas.Save();
            try
            {
                canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
                owner.OnDraw(new SkiaDrawEventArgs(canvas, bounds.Size, scaling));
            }
            finally
            {
                canvas.RestoreToCount(count);
            }
        }

        public void Dispose()
        {
        }
    }
}
