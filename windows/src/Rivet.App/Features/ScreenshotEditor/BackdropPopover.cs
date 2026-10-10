// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Imaging.Backdrop;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The Background popover (spec 01 §3.10.10): swatches (none, presets, saved
/// looks, desktop wallpapers, Image…), custom solid or gradient wells with a
/// 20-colour palette, and the Margin / Corners / Blur sliders. Everything
/// applies live and is remembered for the next capture.
/// </summary>
internal sealed class BackdropPopover : UserControl
{
    private const double TileHeight = 38;

    private static readonly ConcurrentDictionary<string, Bitmap?> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    private readonly EditorController _controller;
    private readonly WrapPanel _swatches = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _wells = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ToggleButton _solid = Segment(L.Get("screenshot.backdropSolidLabel"));
    private readonly ToggleButton _gradient = Segment(L.Get("screenshot.backdropGradientLabel"));
    private readonly Button _save;
    private readonly Slider _margin = NewSlider();
    private readonly Slider _corners = NewSlider();
    private readonly Slider _blur = NewSlider();
    private readonly List<RgbColor> _wellColors = [];
    private int _activeWell;
    private bool _syncing;

    public BackdropPopover(EditorController controller)
    {
        _controller = controller;
        Width = 292;
        LoadWells();

        _solid.Click += (_, _) => SetCustomKind(gradient: false);
        _gradient.Click += (_, _) => SetCustomKind(gradient: true);
        _save = new Button
        {
            Content = new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Add, FontSize = 14 },
            Width = 30,
            Height = 26,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        ToolTip.SetTip(_save, L.Get("screenshot.backdropSavePreset"));
        _save.Click += (_, _) =>
        {
            controller.SaveBackdropPreset(controller.Backdrop);
            Rebuild();
        };
        AutomationProperties.SetName(_save, L.Get("screenshot.backdropSavePreset"));

        var palette = new UniformGrid { Columns = 10, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var color in BackdropPresets.Palette)
        {
            var captured = color;
            palette.Children.Add(new ColorDot(EditorChrome.ToAvalonia(color), Hex(color), () => PaintWell(captured), size: 17));
        }

        _margin.ValueChanged += (_, e) => SetSlider(b => b with { Padding = e.NewValue });
        _corners.ValueChanged += (_, e) => SetSlider(b => b with { CornerRadius = e.NewValue });
        _blur.ValueChanged += (_, e) => SetSlider(b => b with { Blur = e.NewValue });

        var segmented = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _solid, _gradient } };
        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        customRow.Children.Add(segmented);
        Grid.SetColumn(_wells, 1);
        _wells.HorizontalAlignment = HorizontalAlignment.Center;
        customRow.Children.Add(_wells);
        Grid.SetColumn(_save, 2);
        customRow.Children.Add(_save);

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(4),
            Children =
            {
                Title(L.Get("screenshot.backdropLabel")),
                _swatches,
                Title(L.Get("screenshot.backdropCustomLabel")),
                customRow,
                palette,
                SliderRow(L.Get("screenshot.backdropPaddingLabel"), _margin),
                SliderRow(L.Get("screenshot.backdropCornersLabel"), _corners),
                SliderRow(L.Get("screenshot.backdropBlurLabel"), _blur),
            },
        };
        controller.BackdropChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        Rebuild();
    }

    private static TextBlock Title(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.75 };

    private static Slider NewSlider() => new() { Minimum = 0, Maximum = 1, Focusable = false, VerticalAlignment = VerticalAlignment.Center };

    private static ToggleButton Segment(string text) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 12 },
        Padding = new Thickness(9, 3),
        Focusable = false,
        CornerRadius = new CornerRadius(6),
    };

    private static Control SliderRow(string label, Slider slider)
    {
        AutomationProperties.SetName(slider, label);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*") };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        return grid;
    }

    private static string Hex(RgbColor c) => "#" + (c.ToArgb() & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    private void LoadWells()
    {
        var current = _controller.Backdrop;
        _wellColors.Clear();
        if (current.Kind is BackdropKind.Solid or BackdropKind.Gradient && current.Colors is { Count: > 0 } colors)
        {
            _wellColors.AddRange(colors);
        }

        if (_wellColors.Count == 0)
        {
            _wellColors.Add(BackdropPresets.DefaultSolid);
        }

        if (_wellColors.Count == 1)
        {
            _wellColors.Add(BackdropPresets.DefaultGradientEnd);
        }

        _activeWell = 0;
    }

    private void SetCustomKind(bool gradient)
    {
        _activeWell = Math.Min(_activeWell, gradient ? 1 : 0);
        ApplyCustom(gradient);
    }

    private void PaintWell(RgbColor color)
    {
        var gradient = _gradient.IsChecked == true;
        _wellColors[_activeWell] = color;
        ApplyCustom(gradient);
    }

    private void ApplyCustom(bool gradient)
    {
        var look = gradient
            ? new BackdropStyle { Kind = BackdropKind.Gradient, Colors = [_wellColors[0], _wellColors[1]] }
            : new BackdropStyle { Kind = BackdropKind.Solid, Colors = [_wellColors[0]] };
        Apply(look);
    }

    /// <summary>Applying a look keeps the current slider values.</summary>
    private void Apply(BackdropStyle look)
    {
        _controller.Backdrop = _controller.Backdrop.WithLook(look);
        Rebuild();
    }

    private void SetSlider(Func<BackdropStyle, BackdropStyle> change)
    {
        if (!_syncing)
        {
            _controller.Backdrop = change(_controller.Backdrop);
        }
    }

    private void Rebuild()
    {
        var current = _controller.Backdrop;
        _swatches.Children.Clear();
        AddTile(EditorChrome.Icon("Prohibited", 18), L.Get("screenshot.backdropNone"), current.Kind == BackdropKind.None, () => Apply(BackdropStyle.None));
        foreach (var preset in BackdropPresets.All)
        {
            var look = new BackdropStyle { Kind = BackdropKind.Preset, PresetId = preset.Id };
            AddTile(Fill(look), L.Get(PresetTitleKey(preset.Id)), current.SameLook(look), () => Apply(look));
        }

        foreach (var saved in _controller.BackdropPresets)
        {
            var look = saved;
            var tile = AddTile(Fill(look), L.Get("screenshot.backdropCustomLabel"), current.SameLook(look), () => Apply(look));
            var remove = new MenuItem { Header = L.Get("screenshot.backdropDeletePreset") };
            remove.Click += (_, _) =>
            {
                _controller.RemoveBackdropPreset(look);
                Rebuild();
            };
            tile.ContextMenu = new ContextMenu { Items = { remove } };
        }

        foreach (var path in _controller.Wallpapers.GetWallpaperPaths())
        {
            var look = new BackdropStyle { Kind = BackdropKind.Image, ImagePath = path };
            var image = new Image { Stretch = Stretch.UniformToFill };
            LoadThumbnail(path, image);
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Desktop, FontSize = 10, Foreground = Brushes.White },
            };
            AddTile(new Panel { Children = { image, badge } }, L.Get("screenshot.backdropWallpaperLabel"), current.SameLook(look), () => Apply(look));
        }

        if (current.Kind == BackdropKind.Image && current.ImagePath is { } chosen && !_controller.Wallpapers.GetWallpaperPaths().Contains(chosen, StringComparer.OrdinalIgnoreCase))
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            LoadThumbnail(chosen, image);
            AddTile(image, Path.GetFileName(chosen), true, () => { });
        }

        var dashed = new Avalonia.Controls.Shapes.Rectangle
        {
            Stroke = EditorChrome.Secondary,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            RadiusX = 8,
            RadiusY = 8,
        };
        var label = new TextBlock { Text = L.Get("screenshot.backdropImageButton"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AddTile(new Panel { Children = { dashed, label } }, L.Get("screenshot.backdropImageButton"), false, () => _ = PickImageAsync());

        _wells.Children.Clear();
        var gradient = current.Kind == BackdropKind.Gradient || (current.Kind != BackdropKind.Solid && _gradient.IsChecked == true);
        for (var i = 0; i < (gradient ? 2 : 1); i++)
        {
            var index = i;
            var well = new ColorDot(EditorChrome.ToAvalonia(_wellColors[i]), Hex(_wellColors[i]), () =>
            {
                _activeWell = index;
                Rebuild();
            }, size: 22)
            {
                IsSelected = index == _activeWell,
            };
            _wells.Children.Add(well);
        }

        Sync();
    }

    private Button AddTile(Control content, string name, bool selected, Action onClick)
    {
        var tile = new Button
        {
            Width = 64,
            Height = TileHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderBrush = selected ? EditorChrome.AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2.5 : 0),
            Content = new Border { CornerRadius = new CornerRadius(selected ? 5.5 : 8), ClipToBounds = true, Child = content },
        };
        ToolTip.SetTip(tile, name);
        AutomationProperties.SetName(tile, name);
        tile.Click += (_, _) => onClick();
        _swatches.Children.Add(tile);
        return tile;
    }

    private static string PresetTitleKey(string id) => id switch
    {
        "ocean" => "win.screenshotEditor.presetOcean",
        "sunset" => "win.screenshotEditor.presetSunset",
        "forest" => "win.screenshotEditor.presetForest",
        "candy" => "win.screenshotEditor.presetCandy",
        _ => "win.screenshotEditor.presetGraphite",
    };

    /// <summary>A tile filled with the look (diagonal gradient, top-left to bottom-right).</summary>
    private static Control Fill(BackdropStyle look)
    {
        var colors = look.ResolvedColors();
        IBrush brush = colors.Count >= 2
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(EditorChrome.ToAvalonia(colors[0]), 0), new GradientStop(EditorChrome.ToAvalonia(colors[1]), 1) },
            }
            : new SolidColorBrush(colors.Count == 1 ? EditorChrome.ToAvalonia(colors[0]) : Colors.Transparent);
        return new Border { Background = brush };
    }

    /// <summary>Thumbnails are at most 220 px, decoded off the UI thread, 24 kept.</summary>
    private static void LoadThumbnail(string path, Image target)
    {
        if (Thumbnails.TryGetValue(path, out var cached))
        {
            target.Source = cached;
            return;
        }

        _ = Task.Run(() =>
        {
            var bitmap = ImageInterop.LoadThumbnail(path, 220);
            if (Thumbnails.Count >= 24)
            {
                Thumbnails.Clear();
            }

            Thumbnails[path] = bitmap;
            Dispatcher.UIThread.Post(() => target.Source = bitmap);
        });
    }

    private async Task PickImageAsync()
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
            Apply(new BackdropStyle { Kind = BackdropKind.Image, ImagePath = path });
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var current = _controller.Backdrop;
            var has = current.HasBackdrop;
            _margin.Value = current.Padding;
            _corners.Value = current.CornerRadius;
            _blur.Value = current.Blur;
            _margin.IsEnabled = has;
            _blur.IsEnabled = has;
            var gradient = current.Kind == BackdropKind.Gradient || (current.Kind != BackdropKind.Solid && _gradient.IsChecked == true);
            _solid.IsChecked = !gradient;
            _gradient.IsChecked = gradient;
            _save.IsEnabled = current.Kind is BackdropKind.Solid or BackdropKind.Gradient or BackdropKind.Image
                              && !_controller.BackdropPresets.Any(p => p.SameLook(current));
        }
        finally
        {
            _syncing = false;
        }
    }
}
