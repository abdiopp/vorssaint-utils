// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Imaging.Capture;
using PixelBuffer = Rivet.Core.Platform.PixelBuffer;
using PixelPoint = Avalonia.PixelPoint;

namespace Rivet.App.Features.Capture.Pins;

/// <summary>
/// A pinned capture (spec 01 §3.11): an always-on-top image window, aspect
/// locked, with 7-DIP rounded corners. Drag to move, drag an edge to resize,
/// Ctrl+wheel to zoom, double-click or Esc to close, arrows to nudge, Ctrl+C
/// to copy, right-click for the menu.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double MinWidthDip = 90;
    private const double MinHeightDip = 60;
    private const double EdgeGrip = 8;

    private readonly double _aspect;
    private readonly Image _image;
    private ResizeEdge _resize = ResizeEdge.None;
    private Point _resizeStart;
    private Size _resizeStartSize;
    private PixelPoint _resizeStartPosition;

    public PinWindow(PixelBuffer image, Size naturalSize)
    {
        Image = image;
        NaturalSize = naturalSize;
        _aspect = image.Width / (double)image.Height;
        CaptureUi.MakeFloating(this);
        ShowActivated = false;
        MinWidth = MinWidthDip;
        MinHeight = MinHeightDip;
        _image = new Image
        {
            Source = ImageInterop.ToBitmap(CaptureImaging.WithScale(image, 1)),
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetBitmapInterpolationMode(_image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        var frame = new Border
        {
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(102, 128, 128, 128)),
            Child = _image,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, OffsetY = 4, Color = Color.FromArgb(80, 0, 0, 0) }),
        };
        Content = frame;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        Focusable = true;
    }

    private enum ResizeEdge
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    public PixelBuffer Image { get; }

    /// <summary>The capture's natural size in DIPs (pixels / scale).</summary>
    public Size NaturalSize { get; }

    public bool IgnoresClicks { get; private set; }

    public event EventHandler<string>? MenuCommand;

    public void SetIgnoreClicks(bool ignore)
    {
        IgnoresClicks = ignore;
        var handle = Rivet.App.Shell.WindowInterop.Handle(this);
        if (handle != 0 && Rivet.App.Hosting.AppHost.Current?.Services.GetService(typeof(Rivet.Core.Capture.ICapturePlatform)) is Rivet.Core.Capture.ICapturePlatform platform)
        {
            platform.SetClickThrough(handle, ignore);
        }
    }

    /// <summary>Window alpha (100, 85, 70 or 50 %).</summary>
    public double PinOpacity
    {
        get => ((Border)Content!).Opacity;
        set => ((Border)Content!).Opacity = value;
    }

    /// <summary>Resizes around the centre keeping the aspect ratio (zoom).</summary>
    public void Zoom(double factor)
    {
        var width = Math.Clamp(Width * factor, MinWidthDip, 8000);
        ApplySize(width, centred: true);
    }

    public void ActualSize() => ApplySize(NaturalSize.Width, centred: true);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        Activate();
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        var edge = EdgeAt(point.Position);
        if (edge != ResizeEdge.None)
        {
            _resize = edge;
            _resizeStart = this.PointToScreen(point.Position).ToPoint(1);
            _resizeStartSize = new Size(Width, Height);
            _resizeStartPosition = Position;
            e.Pointer.Capture(this);
            return;
        }

        BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_resize == ResizeEdge.None)
        {
            Cursor = new Cursor(EdgeAt(position) switch
            {
                ResizeEdge.Left or ResizeEdge.Right => StandardCursorType.SizeWestEast,
                ResizeEdge.Top or ResizeEdge.Bottom => StandardCursorType.SizeNorthSouth,
                ResizeEdge.TopLeft or ResizeEdge.BottomRight => StandardCursorType.TopLeftCorner,
                ResizeEdge.TopRight or ResizeEdge.BottomLeft => StandardCursorType.TopRightCorner,
                _ => StandardCursorType.SizeAll,
            });
            return;
        }

        var scaling = RenderScaling;
        var screen = this.PointToScreen(position).ToPoint(1);
        var dx = (screen.X - _resizeStart.X) / scaling;
        var dy = (screen.Y - _resizeStart.Y) / scaling;
        var widthFromX = _resize switch
        {
            ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight => _resizeStartSize.Width + dx,
            ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft => _resizeStartSize.Width - dx,
            _ => double.NaN,
        };
        var widthFromY = _resize switch
        {
            ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight => (_resizeStartSize.Height + dy) * _aspect,
            ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight => (_resizeStartSize.Height - dy) * _aspect,
            _ => double.NaN,
        };
        var width = double.IsNaN(widthFromX) ? widthFromY : double.IsNaN(widthFromY) ? widthFromX : Math.Max(widthFromX, widthFromY);
        width = Math.Max(Math.Max(MinWidthDip, MinHeightDip * _aspect), width);
        var height = width / _aspect;
        var x = _resizeStartPosition.X;
        var y = _resizeStartPosition.Y;
        if (_resize is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            x = _resizeStartPosition.X + (int)Math.Round((_resizeStartSize.Width - width) * scaling);
        }

        if (_resize is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            y = _resizeStartPosition.Y + (int)Math.Round((_resizeStartSize.Height - height) * scaling);
        }

        Width = width;
        Height = height;
        Position = new PixelPoint(x, y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resize != ResizeEdge.None)
        {
            _resize = ResizeEdge.None;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers == KeyModifiers.None)
        {
            Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 12 : 1;
        var px = (int)Math.Round(step * RenderScaling);
        switch (e.Key)
        {
            case Key.Escape:
            case Key.W when ctrl:
                Close();
                break;
            case Key.C when ctrl:
                MenuCommand?.Invoke(this, "copy");
                break;
            case Key.S when ctrl:
                MenuCommand?.Invoke(this, "saveAs");
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                ActualSize();
                break;
            case Key.OemPlus or Key.Add when ctrl:
                Zoom(1.25);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Zoom(0.8);
                break;
            case Key.Left:
                Position = new PixelPoint(Position.X - px, Position.Y);
                break;
            case Key.Right:
                Position = new PixelPoint(Position.X + px, Position.Y);
                break;
            case Key.Up:
                Position = new PixelPoint(Position.X, Position.Y - px);
                break;
            case Key.Down:
                Position = new PixelPoint(Position.X, Position.Y + px);
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
    }

    private void ApplySize(double width, bool centred)
    {
        width = Math.Max(Math.Max(MinWidthDip, MinHeightDip * _aspect), width);
        var height = width / _aspect;
        if (centred)
        {
            var scaling = RenderScaling;
            var dx = (int)Math.Round((Width - width) * scaling / 2);
            var dy = (int)Math.Round((Height - height) * scaling / 2);
            Position = new PixelPoint(Position.X + dx, Position.Y + dy);
        }

        Width = width;
        Height = height;
    }

    private ResizeEdge EdgeAt(Point p)
    {
        var left = p.X <= EdgeGrip;
        var right = p.X >= Bounds.Width - EdgeGrip;
        var top = p.Y <= EdgeGrip;
        var bottom = p.Y >= Bounds.Height - EdgeGrip;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => ResizeEdge.TopLeft,
            (_, true, true, _) => ResizeEdge.TopRight,
            (true, _, _, true) => ResizeEdge.BottomLeft,
            (_, true, _, true) => ResizeEdge.BottomRight,
            (true, _, _, _) => ResizeEdge.Left,
            (_, true, _, _) => ResizeEdge.Right,
            (_, _, true, _) => ResizeEdge.Top,
            (_, _, _, true) => ResizeEdge.Bottom,
            _ => ResizeEdge.None,
        };
    }
}
