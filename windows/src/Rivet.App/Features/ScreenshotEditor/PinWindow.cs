// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.App.Shell;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Platform;
using PixelPoint = Avalonia.PixelPoint;
using PixelRect = Rivet.Core.Platform.PixelRect;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.ScreenshotEditor;
using Rivet.Imaging.Skia;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// A pinned capture (spec 01 §3.11): an always-on-top image with rounded
/// corners. Drag to move, drag an edge to resize (aspect locked), double-click
/// or Esc to close, arrow keys nudge, right-click for Copy, Save As, opacity,
/// click-through (Alt+click recovers it) and closing.
/// </summary>
internal sealed class PinWindow : Window
{
    private const double EdgeGrip = 8;
    private static readonly List<PinWindow> Open = [];
    private static int _cascade;
    private static IDisposable? _recoveryHook;

    private readonly IServiceProvider _services;
    private readonly PixelBuffer _image;
    private readonly double _aspect;
    private readonly Border _frame;
    private bool _clickThrough;
    private (Point Start, PixelPoint Position, Size Size, WindowEdge Edge)? _resize;

    private PinWindow(IServiceProvider services, PixelBuffer image)
    {
        _services = services;
        _image = image;
        _aspect = image.Width / (double)Math.Max(1, image.Height);
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.Manual;
        Title = L.Get("screenshot.pinButton");
        MinWidth = 90;
        MinHeight = 60;
        _frame = new Border
        {
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
            Child = new Image { Source = DisplayBitmap(image), Stretch = Stretch.Uniform },
        };
        Content = _frame;
        ContextMenu = BuildMenu();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            UpdateRecoveryHook();
        };
    }

    public static IReadOnlyList<PinWindow> All => Open;

    /// <summary>The pin's picture as a 96-DPI bitmap (the window, not the bitmap, carries the size).</summary>
    private static Avalonia.Media.Imaging.Bitmap DisplayBitmap(PixelBuffer image)
    {
        using var sk = SkiaConvert.ToImage(image);
        return ImageInterop.ToBitmap(sk, 1.0);
    }

    /// <summary>Opens a pin at the image's natural size (at most 55 % of the display's smaller side), cascading.</summary>
    public static void Show(IServiceProvider services, PixelBuffer image)
    {
        var pin = new PinWindow(services, image);
        var screens = services.GetService<IScreenService>();
        var screen = screens?.ScreenFromPoint(screens.CursorPosition);
        var scale = screen?.Scale ?? 1;
        var work = screen?.WorkArea ?? new PixelRect(0, 0, 1920, 1040);
        var visibleW = work.Width / scale;
        var visibleH = work.Height / scale;
        var natural = new Size(image.Width / Math.Max(0.1, image.Scale), image.Height / Math.Max(0.1, image.Scale));
        var limit = 0.55 * Math.Min(visibleW, visibleH);
        var shrink = Math.Min(1, Math.Min(limit / natural.Width, limit / natural.Height));
        var w = Math.Max(90, natural.Width * shrink);
        var h = Math.Max(60, natural.Height * shrink);
        pin.Width = w;
        pin.Height = h;
        var offset = (_cascade++ % 5) * 26 * scale;
        pin.Position = new PixelPoint(
            (int)(work.X + ((work.Width - (w * scale)) / 2) + offset),
            (int)(work.Y + ((work.Height - (h * scale)) / 2) + offset));
        pin.WindowStartupLocation = WindowStartupLocation.Manual;
        Open.Add(pin);
        pin.Show();
        WindowInterop.ApplyChrome(pin, WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost);
    }

    public static void CloseAll()
    {
        foreach (var pin in Open.ToList())
        {
            pin.Close();
        }

        _cascade = 0;
    }

    private ContextMenu BuildMenu()
    {
        var copy = new MenuItem { Header = L.Get("screenshot.copyButton"), InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copy.Click += (_, _) => CopyImage();
        var saveAs = new MenuItem { Header = L.Get("screenshot.saveAsButton") };
        saveAs.Click += (_, _) => _ = SaveAsAsync();
        var opacity = new MenuItem { Header = L.Get("screenshot.pinOpacity") };
        foreach (var value in new[] { 1.0, 0.85, 0.7, 0.5 })
        {
            var captured = value;
            var item = new MenuItem { Header = $"{value * 100:0} %", ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => _frame.Opacity = captured;
            opacity.Items.Add(item);
        }

        opacity.SubmenuOpened += (_, _) =>
        {
            foreach (var item in opacity.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Header is string text && text.StartsWith($"{_frame.Opacity * 100:0} ", StringComparison.Ordinal);
            }
        };
        var ignore = new MenuItem { Header = L.Get("screenshot.pinClickThrough"), ToggleType = MenuItemToggleType.CheckBox };
        ignore.Click += (_, _) => SetClickThrough(!_clickThrough);
        var close = new MenuItem { Header = L.Get("Strings.menuClose") };
        close.Click += (_, _) => Close();
        var closeAll = new MenuItem { Header = L.Get("screenshot.pinCloseAll") };
        closeAll.Click += (_, _) => CloseAll();
        var menu = new ContextMenu { Items = { copy, saveAs, new Separator(), opacity, ignore, new Separator(), close, closeAll } };
        menu.Opening += (_, _) => ignore.IsChecked = _clickThrough;
        return menu;
    }

    private void CopyImage()
    {
        if (_services.GetService<ICaptureOutput>() is { } output)
        {
            output.Copy(_image);
        }
        else
        {
            _services.GetRequiredService<IClipboardService>().SetImage(_image);
        }

        _services.GetService<IHud>()?.Show(L.Get("screenshot.copiedHUD"), HudStyle.Success, "Copy");
    }

    private async Task SaveAsAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = EditorFiles.DefaultName(DateTime.Now, L.Get("screenshot.fileNamePrefix")),
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"], MimeTypes = ["image/png"] }],
        });
        if (file is null)
        {
            return;
        }

        using var image = SkiaConvert.ToImage(_image);
        var bytes = PngWriter.Encode(image, _image.Scale);
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(bytes);
    }

    private void SetClickThrough(bool on)
    {
        _clickThrough = on;
        var chrome = _services.GetService<IWindowChrome>();
        var handle = WindowInterop.Handle(this);
        if (chrome is not null && handle != 0)
        {
            if (on)
            {
                chrome.Apply(handle, WindowChromeOptions.ClickThrough);
            }
            else
            {
                chrome.Remove(handle, WindowChromeOptions.ClickThrough);
            }
        }

        UpdateRecoveryHook();
    }

    /// <summary>While any pin ignores clicks, an Alt+click inside it turns click-through off again.</summary>
    private static void UpdateRecoveryHook()
    {
        var needed = Open.Any(p => p._clickThrough);
        if (needed && _recoveryHook is null && Open.FirstOrDefault()?._services.GetService<IInputHooks>() is { } hooks)
        {
            _recoveryHook = hooks.SubscribeMouse((ref MouseHookEvent e) =>
            {
                if (e.Kind != MouseHookKind.LeftDown || !e.Modifiers.HasFlag(Rivet.Core.Shortcuts.KeyModifiers.Alt))
                {
                    return false;
                }

                var position = e.Position;
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var pin in Open.Where(p => p._clickThrough))
                    {
                        var origin = pin.Position;
                        var scaling = pin.RenderScaling;
                        var bounds = new PixelRect(origin.X, origin.Y, (int)(pin.Bounds.Width * scaling), (int)(pin.Bounds.Height * scaling));
                        if (bounds.Contains(position))
                        {
                            pin.SetClickThrough(false);
                        }
                    }
                });
                return false;
            });
        }
        else if (!needed && _recoveryHook is not null)
        {
            _recoveryHook.Dispose();
            _recoveryHook = null;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
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
                CopyImage();
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
                return;
        }

        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }

        if (EdgeAt(point.Position) is { } edge)
        {
            _resize = (PointToScreenDip(point.Position), Position, Bounds.Size, edge);
            e.Pointer.Capture(this);
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_resize is not { } r)
        {
            Cursor = EdgeAt(p) switch
            {
                WindowEdge.NorthWest or WindowEdge.SouthEast => new Cursor(StandardCursorType.TopLeftCorner),
                WindowEdge.NorthEast or WindowEdge.SouthWest => new Cursor(StandardCursorType.TopRightCorner),
                WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
                WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
                _ => Cursor.Default,
            };
            return;
        }

        // Aspect-locked resize from the dragged edge; the opposite edge stays put.
        var now = PointToScreenDip(p);
        var dx = now.X - r.Start.X;
        var dy = now.Y - r.Start.Y;
        var grow = r.Edge switch
        {
            WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast => dx,
            WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest => -dx,
            WindowEdge.South => dy * _aspect,
            _ => -dy * _aspect,
        };
        var width = Math.Max(Math.Max(90, 60 * _aspect), r.Size.Width + grow);
        var height = width / _aspect;
        var scaling = RenderScaling;
        var x = r.Edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest
            ? r.Position.X + (int)Math.Round((r.Size.Width - width) * scaling)
            : r.Position.X;
        var y = r.Edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast
            ? r.Position.Y + (int)Math.Round((r.Size.Height - height) * scaling)
            : r.Position.Y;
        Width = width;
        Height = height;
        Position = new PixelPoint(x, y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resize is not null)
        {
            _resize = null;
            e.Pointer.Capture(null);
        }
    }

    private Point PointToScreenDip(Point local)
    {
        var screen = this.PointToScreen(local);
        return new Point(screen.X / RenderScaling, screen.Y / RenderScaling);
    }

    private WindowEdge? EdgeAt(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var left = p.X <= EdgeGrip;
        var right = p.X >= w - EdgeGrip;
        var top = p.Y <= EdgeGrip;
        var bottom = p.Y >= h - EdgeGrip;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }
}
