// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Imaging.Capture;

namespace Rivet.App.Features.Capture.Preview;

/// <summary>What the preview's buttons and keys ask for.</summary>
internal enum PreviewCommand
{
    Edit,
    Save,
    Copy,
    Discard,
    Dismiss,
    Pin,
    Share,
    Qr,
}

/// <summary>
/// The floating thumbnail after a capture (spec 01 §3.9): 350 × 210 DIPs,
/// thumbnail (click to edit, drag out as a PNG), Share and Pin over its
/// corner, then Discard, QR, Save, Copy and Edit. Behaviour lives in
/// <see cref="QuickPreviewController"/>.
/// </summary>
internal sealed class QuickPreviewWindow : Window
{
    public const double ContentWidth = 350;
    public const double ContentHeight = 210;
    private const double Shadow = 12;

    private readonly Button _save;
    private readonly Button _copy;
    private readonly Button _qr;
    private readonly Button _edit;
    private readonly Border _thumbnailFrame;
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private bool _dragStarted;

    public QuickPreviewWindow(CapturedImage capture, bool persistent, bool editAvailable)
    {
        CaptureUi.MakeFloating(this);
        Width = ContentWidth + (2 * Shadow);
        Height = ContentHeight + (2 * Shadow);
        ShowActivated = false;
        Focusable = true;

        var thumbnail = CaptureImaging.Thumbnail(capture.Image, 1200);
        var image = new Image
        {
            Source = ImageInterop.ToBitmap(CaptureImaging.WithScale(thumbnail, 1)),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

        var overlay = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6) };
        if (persistent)
        {
            var close = CaptureUi.RoundOverlayButton("Dismiss", L.Get("win.capture.doneTooltip"), () => Raise(PreviewCommand.Dismiss));
            close.VerticalAlignment = VerticalAlignment.Top;
            overlay.Children.Add(close);
        }

        var corner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
        corner.Children.Add(CaptureUi.RoundOverlayButton("Share", L.Get("screenshot.shareButton"), () => Raise(PreviewCommand.Share)));
        corner.Children.Add(CaptureUi.RoundOverlayButton("Pin", L.Get("screenshot.pinButton"), () => Raise(PreviewCommand.Pin)));
        Grid.SetColumn(corner, 2);
        overlay.Children.Add(corner);

        _thumbnailFrame = new Border
        {
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(31, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel { Children = { image, overlay } },
        };
        _thumbnailFrame.Bind(Border.BorderBrushProperty, _thumbnailFrame.GetResourceObservable("PanelBorderBrush").ToBinding());
        AutomationProperties.SetName(_thumbnailFrame, L.Get("screenshot.editButton"));
        ToolTip.SetTip(_thumbnailFrame, L.Get("screenshot.dragOutHandleLabel"));
        _thumbnailFrame.PointerPressed += OnThumbnailPressed;
        _thumbnailFrame.PointerMoved += OnThumbnailMoved;
        _thumbnailFrame.PointerReleased += OnThumbnailReleased;

        var discard = CaptureUi.IconButton("Delete", Shortcut(L.Get("screenshot.discardConfirm"), "Del"), () => Raise(PreviewCommand.Discard));
        _qr = CaptureUi.IconButton("QrCode", L.Get("Strings.qrResultTitle"), () => Raise(PreviewCommand.Qr));
        _qr.IsVisible = false;
        _save = CaptureUi.TextButton(L.Get("screenshot.saveButton"), () => Raise(PreviewCommand.Save), "Save", tooltip: "Ctrl+S");
        _copy = CaptureUi.TextButton(L.Get("screenshot.copyButton"), () => Raise(PreviewCommand.Copy), "Copy", tooltip: "Ctrl+C");
        _edit = CaptureUi.TextButton(L.Get("screenshot.editButton"), () => Raise(PreviewCommand.Edit), "Edit", accent: true, tooltip: L.Get("win.capture.keyEnter"));
        _edit.IsVisible = editAvailable;

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), ColumnSpacing = 6 };
        bar.Children.Add(discard);
        Grid.SetColumn(_qr, 1);
        bar.Children.Add(_qr);
        Grid.SetColumn(_save, 2);
        bar.Children.Add(_save);
        Grid.SetColumn(_copy, 3);
        bar.Children.Add(_copy);
        Grid.SetColumn(_edit, 5);
        bar.Children.Add(_edit);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 10 };
        layout.Children.Add(_thumbnailFrame);
        Grid.SetRow(bar, 1);
        layout.Children.Add(bar);

        var plate = CaptureUi.Plate(layout, radius: 16);
        plate.Margin = new Thickness(Shadow);
        Content = plate;
    }

    public event EventHandler<PreviewCommand>? Command;

    /// <summary>Starts a drag of the capture as a file (the controller writes the file).</summary>
    public Func<PointerPressedEventArgs, Task>? DragOut { get; set; }

    /// <summary>Save and Copy dim to 40 % once the automatic action already did them.</summary>
    public void SetCompleted(bool saved, bool copied)
    {
        _save.IsEnabled = !saved;
        _save.Opacity = saved ? 0.4 : 1;
        _copy.IsEnabled = !copied;
        _copy.Opacity = copied ? 0.4 : 1;
    }

    public void ShowQrButton() => _qr.IsVisible = true;

    public bool SaveEnabled => _save.IsEnabled;

    public bool CopyEnabled => _copy.IsEnabled;

    /// <summary>The outer margin reserved for the shadow, in DIPs.</summary>
    public static double ShadowMargin => Shadow;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var others = e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
        PreviewCommand? command = (e.Key, ctrl, others) switch
        {
            (Key.W, true, KeyModifiers.None) => PreviewCommand.Dismiss,
            (Key.C, true, KeyModifiers.None) when _copy.IsEnabled => PreviewCommand.Copy,
            (Key.S, true, KeyModifiers.None) when _save.IsEnabled => PreviewCommand.Save,
            (Key.Back or Key.Delete, _, KeyModifiers.None) => PreviewCommand.Discard,
            (Key.Enter or Key.E, false, KeyModifiers.None) when _edit.IsVisible => PreviewCommand.Edit,
            (Key.Escape, false, _) => PreviewCommand.Dismiss,
            _ => null,
        };
        if (command is { } c)
        {
            e.Handled = true;
            Raise(c);
            return;
        }

        base.OnKeyDown(e);
    }

    private void Raise(PreviewCommand command) => Command?.Invoke(this, command);

    private static string Shortcut(string text, string keys) => L.Format("win.capture.withShortcutFormat", text, keys);

    private void OnThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_thumbnailFrame).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _press = e;
        _pressPoint = e.GetPosition(_thumbnailFrame);
        _dragStarted = false;
    }

    private async void OnThumbnailMoved(object? sender, PointerEventArgs e)
    {
        if (_press is null || _dragStarted)
        {
            return;
        }

        var point = e.GetPosition(_thumbnailFrame);
        if (Math.Abs(point.X - _pressPoint.X) < 6 && Math.Abs(point.Y - _pressPoint.Y) < 6)
        {
            return;
        }

        _dragStarted = true;
        var press = _press;
        _press = null;
        if (DragOut is { } drag)
        {
            await drag(press);
        }
    }

    private void OnThumbnailReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasClick = _press is not null && !_dragStarted;
        _press = null;
        if (wasClick && e.InitialPressMouseButton == MouseButton.Left)
        {
            Raise(PreviewCommand.Edit);
        }
    }
}
