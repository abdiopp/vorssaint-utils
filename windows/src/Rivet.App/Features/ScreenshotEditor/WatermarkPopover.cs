// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.ScreenshotEditor;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The Watermark popover (spec 01 §3.10.11): none, text (eight colours) or a
/// picture; saved watermarks; position on a 3 × 3 grid, size, opacity and
/// rotation. It applies live and is remembered for the next capture.
/// </summary>
internal sealed class WatermarkPopover : UserControl
{
    private readonly EditorController _controller;
    private readonly ToggleButton _none = Segment(L.Get("screenshot.backdropNone"));
    private readonly ToggleButton _text = Segment(L.Get("screenshot.toolText"));
    private readonly ToggleButton _image = Segment(L.Get("screenshot.watermarkImageLabel"));
    private readonly TextBox _textBox = new() { MaxLength = WatermarkStyle.MaxTextLength, FontSize = 13 };
    private readonly StackPanel _textPanel = new() { Spacing = 8 };
    private readonly StackPanel _colorRow = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly Dictionary<AnnotationColor, ColorDot> _colorDots = [];
    private readonly Grid _imagePanel = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
    private readonly Image _thumbnail = new() { Width = 38, Height = 38, Stretch = Stretch.Uniform };
    private readonly TextBlock _fileName = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _presets = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _presetSection = new() { Spacing = 6 };
    private readonly UniformGrid _positions = new() { Columns = 3, Rows = 3, Width = 72, Height = 54, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Dictionary<WatermarkAnchor, Button> _positionButtons = [];
    private readonly Slider _size = new() { Minimum = 0, Maximum = 1, Focusable = false };
    private readonly Slider _opacity = new() { Minimum = WatermarkStyle.MinOpacity, Maximum = 1, Focusable = false };
    private readonly Slider _rotation = new() { Minimum = -90, Maximum = 90, SmallChange = 1, LargeChange = 15, TickFrequency = 1, IsSnapToTickEnabled = true, Focusable = false };
    private readonly TextBlock _rotationLabel = new() { FontSize = 12, Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _placement = new() { Spacing = 8 };
    private WatermarkStyle _draft;
    private WatermarkKind _kind;
    private string? _thumbnailPath;
    private bool _syncing;

    public WatermarkPopover(EditorController controller)
    {
        _controller = controller;
        _draft = controller.Watermark;
        _kind = _draft.Kind;
        Width = 292;

        _none.Click += (_, _) => SetKind(WatermarkKind.None);
        _text.Click += (_, _) => SetKind(WatermarkKind.Text);
        _image.Click += (_, _) => SetKind(WatermarkKind.Image);

        _textBox.PlaceholderText = L.Get("screenshot.watermarkTextPlaceholder");
        AutomationProperties.SetName(_textBox, L.Get("screenshot.watermarkTextPlaceholder"));
        _textBox.TextChanged += (_, _) =>
        {
            if (!_syncing)
            {
                Update(_draft with { Kind = WatermarkKind.Text, Text = _textBox.Text ?? string.Empty });
            }
        };
        foreach (var color in AnnotationColors.All)
        {
            var captured = color;
            var dot = new ColorDot(EditorChrome.ToAvalonia(color), L.Get(AnnotationColors.TitleKey(color)), () => Update(_draft with { Color = captured }));
            _colorDots[color] = dot;
            _colorRow.Children.Add(dot);
        }

        _textPanel.Children.Add(_textBox);
        _textPanel.Children.Add(_colorRow);

        var choose = new Button { Content = L.Get("screenshot.folderChoose"), Focusable = false, VerticalAlignment = VerticalAlignment.Center };
        choose.Click += (_, _) => _ = ChooseImageAsync();
        _imagePanel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x80)), CornerRadius = new CornerRadius(6), Child = _thumbnail });
        Grid.SetColumn(_fileName, 1);
        _imagePanel.Children.Add(_fileName);
        Grid.SetColumn(choose, 2);
        _imagePanel.Children.Add(choose);

        _presetSection.Children.Add(_presets);

        foreach (var anchor in Enum.GetValues<WatermarkAnchor>())
        {
            var captured = anchor;
            var button = new Button
            {
                Width = 22,
                Height = 16,
                Margin = new Thickness(1),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                Focusable = false,
                CornerRadius = new CornerRadius(3),
            };
            var name = L.Get(AnchorTitleKey(anchor));
            ToolTip.SetTip(button, name);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => Update(_draft with { Anchor = captured });
            _positionButtons[anchor] = button;
            _positions.Children.Add(button);
        }

        _size.ValueChanged += (_, e) => Slide(_draft with { Size = e.NewValue });
        _opacity.ValueChanged += (_, e) => Slide(_draft with { Opacity = e.NewValue });
        _rotation.ValueChanged += (_, e) => Slide(_draft with { Rotation = Math.Round(e.NewValue) });
        var rotationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        rotationRow.Children.Add(_rotation);
        Grid.SetColumn(_rotationLabel, 1);
        rotationRow.Children.Add(_rotationLabel);

        _placement.Children.Add(Row(L.Get("screenshot.watermarkPositionLabel"), _positions));
        _placement.Children.Add(Row(L.Get("screenshot.watermarkSizeLabel"), _size));
        _placement.Children.Add(Row(L.Get("screenshot.watermarkOpacityLabel"), _opacity));
        _placement.Children.Add(Row(L.Get("screenshot.watermarkRotationLabel"), rotationRow));

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(4),
            Children =
            {
                Title(L.Get("screenshot.watermarkLabel")),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _none, _text, _image } },
                _textPanel,
                _imagePanel,
                _presetSection,
                _placement,
            },
        };
        Sync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _draft = _controller.Watermark;
        _kind = _draft.Kind;
        Sync();
        if (_kind == WatermarkKind.Text && string.IsNullOrEmpty(_textBox.Text))
        {
            Dispatcher.UIThread.Post(() => _textBox.Focus(), DispatcherPriority.Input);
        }
    }

    private static TextBlock Title(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.75 };

    private static ToggleButton Segment(string text) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 12 },
        Padding = new Thickness(10, 3),
        Focusable = false,
        CornerRadius = new CornerRadius(6),
    };

    private static Control Row(string label, Control control)
    {
        if (control is Slider slider)
        {
            AutomationProperties.SetName(slider, label);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    public static string AnchorTitleKey(WatermarkAnchor anchor) => anchor switch
    {
        WatermarkAnchor.TopLeading => "screenshot.watermarkPositionTopLeading",
        WatermarkAnchor.Top => "screenshot.watermarkPositionTop",
        WatermarkAnchor.TopTrailing => "screenshot.watermarkPositionTopTrailing",
        WatermarkAnchor.Leading => "screenshot.watermarkPositionLeading",
        WatermarkAnchor.Center => "screenshot.watermarkPositionCenter",
        WatermarkAnchor.Trailing => "screenshot.watermarkPositionTrailing",
        WatermarkAnchor.BottomLeading => "screenshot.watermarkPositionBottomLeading",
        WatermarkAnchor.Bottom => "screenshot.watermarkPositionBottom",
        _ => "screenshot.watermarkPositionBottomTrailing",
    };

    /// <summary>"…" in the middle, so the extension stays visible.</summary>
    public static string MiddleTruncate(string text, int max = 26)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = max - 1;
        var head = (keep + 1) / 2;
        return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - (keep - head)));
    }

    private void SetKind(WatermarkKind kind)
    {
        _kind = kind;
        if (kind == WatermarkKind.Image && string.IsNullOrWhiteSpace(_draft.ImagePath))
        {
            // Choosing Image with no picture yet opens the file dialog.
            _ = ChooseImageAsync();
        }

        Update(_draft with { Kind = kind });
        if (kind == WatermarkKind.Text && string.IsNullOrEmpty(_textBox.Text))
        {
            Dispatcher.UIThread.Post(() => _textBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void Slide(WatermarkStyle next)
    {
        if (!_syncing)
        {
            Update(next);
        }
    }

    private void Update(WatermarkStyle next)
    {
        _draft = next;
        _controller.Watermark = next;
        Sync();
    }

    private async Task ChooseImageAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            _kind = WatermarkKind.Image;
            Update(_draft with { Kind = WatermarkKind.Image, ImagePath = path });
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            _none.IsChecked = _kind == WatermarkKind.None;
            _text.IsChecked = _kind == WatermarkKind.Text;
            _image.IsChecked = _kind == WatermarkKind.Image;
            _textPanel.IsVisible = _kind == WatermarkKind.Text;
            _imagePanel.IsVisible = _kind == WatermarkKind.Image;
            if (_textBox.Text != _draft.Text)
            {
                _textBox.Text = _draft.Text;
            }

            foreach (var (color, dot) in _colorDots)
            {
                dot.IsSelected = color == _draft.Color;
            }

            var path = _draft.ImagePath;
            _fileName.Text = string.IsNullOrWhiteSpace(path) ? L.Get("screenshot.backdropNone") : MiddleTruncate(Path.GetFileName(path));
            if (_thumbnailPath != path)
            {
                _thumbnailPath = path;
                _thumbnail.Source = string.IsNullOrWhiteSpace(path) ? null : ImageInterop.LoadThumbnail(path, 96);
            }

            var drawn = _controller.Watermark.IsDrawn;
            var saved = _controller.WatermarkPresets;
            _presetSection.IsVisible = drawn || saved.Count > 0;
            RebuildPresets(saved, drawn);

            _placement.IsEnabled = _kind != WatermarkKind.None;
            foreach (var (anchor, button) in _positionButtons)
            {
                var active = anchor == _draft.Anchor;
                button.Background = active ? EditorChrome.AccentBrush : new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
            }

            _size.Value = _draft.Size;
            _opacity.Value = _draft.Opacity;
            _rotation.Value = _draft.Rotation;
            _rotationLabel.Text = L.Format("win.screenshotEditor.degreesFormat", (int)Math.Round(_draft.Rotation));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RebuildPresets(IReadOnlyList<WatermarkStyle> saved, bool drawn)
    {
        _presets.Children.Clear();
        foreach (var preset in saved)
        {
            var mark = preset;
            Control content = mark.Kind == WatermarkKind.Image
                ? new Image { Source = ImageInterop.LoadThumbnail(mark.ImagePath!, 96), Stretch = Stretch.Uniform, Margin = new Thickness(4) }
                : new TextBlock
                {
                    Text = mark.Text,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(EditorChrome.ToAvalonia(mark.Color)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 0),
                };
            var tile = Tile(content, mark.Kind == WatermarkKind.Image ? Path.GetFileName(mark.ImagePath ?? string.Empty) : mark.Text, () =>
            {
                _kind = mark.Kind;
                Update(mark);
            });
            tile.Background = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x80));
            var remove = new MenuItem { Header = L.Get("screenshot.backdropDeletePreset") };
            remove.Click += (_, _) =>
            {
                _controller.RemoveWatermarkPreset(mark);
                Sync();
            };
            tile.ContextMenu = new ContextMenu { Items = { remove } };
            _presets.Children.Add(tile);
        }

        var dashed = new Avalonia.Controls.Shapes.Rectangle { Stroke = EditorChrome.Secondary, StrokeThickness = 1, StrokeDashArray = [3, 2], RadiusX = 8, RadiusY = 8 };
        var add = Tile(new Panel { Children = { dashed, new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Add, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } },
            L.Get("screenshot.watermarkSavePreset"), () =>
            {
                _controller.SaveWatermarkPreset(_controller.Watermark);
                Sync();
            });
        add.IsEnabled = drawn && !saved.Any(p => p.SameMark(_controller.Watermark));
        _presets.Children.Add(add);
    }

    private static Button Tile(Control content, string name, Action onClick)
    {
        var tile = new Button
        {
            Width = 64,
            Height = 38,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
        };
        ToolTip.SetTip(tile, name);
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => onClick();
        return tile;
    }
}
