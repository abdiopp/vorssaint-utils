// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The background popover, shared in look and data with the screenshot editor
/// (spec 02 §3.29): None, the five gradients, saved customs, the desktop
/// wallpapers and "Image…", then a custom solid/gradient with a palette.
/// Saved customs live in <c>screenshotBackdropPresets</c>.
/// </summary>
internal sealed class BackgroundPicker : StackPanel
{
    private static readonly RgbValue[] Palette =
    [
        new(0.96, 0.26, 0.21), new(1.00, 0.58, 0.00), new(1.00, 0.80, 0.00), new(0.55, 0.86, 0.25), new(0.20, 0.78, 0.35),
        new(0.10, 0.74, 0.61), new(0.15, 0.78, 0.85), new(0.04, 0.52, 1.00), new(0.35, 0.34, 0.84), new(0.69, 0.32, 0.87),
        new(1.00, 0.45, 0.66), new(0.91, 0.12, 0.39), new(0.55, 0.39, 0.29), new(0.11, 0.16, 0.32), new(0.05, 0.05, 0.06),
        new(0.25, 0.25, 0.28), new(0.55, 0.55, 0.58), new(0.85, 0.85, 0.87), new(1, 1, 1), new(0.99, 0.93, 0.85),
    ];

    private static readonly RgbValue DefaultStart = new(0.20, 0.47, 0.96);
    private static readonly RgbValue DefaultEnd = new(0.45, 0.83, 0.98);

    private readonly EditorSession _session;
    private readonly WrapPanel _swatches = new() { ItemWidth = 67, ItemHeight = 44 };
    private readonly StackPanel _wells = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _save;
    private bool _gradient;
    private int _activeWell;
    private RgbValue _start = DefaultStart;
    private RgbValue _end = DefaultEnd;

    public BackgroundPicker(EditorSession session)
    {
        _session = session;
        Width = 292;
        Spacing = 12;
        var current = session.Document.BackdropStyle;
        if (current.Kind is RecorderBackdropKind.Solid or RecorderBackdropKind.Gradient && current.Colors is { Count: > 0 } colors)
        {
            _gradient = current.Kind == RecorderBackdropKind.Gradient;
            _start = colors[0];
            _end = colors.Count > 1 ? colors[1] : DefaultEnd;
        }

        Children.Add(_swatches);
        Children.Add(new TextBlock { Text = L.Get("screenshot.backdropCustomLabel").ToUpper(System.Globalization.CultureInfo.CurrentUICulture), Classes = { "inspectorSection" } });
        Children.Add(EditorUi.Segmented([L.Get("screenshot.backdropSolidLabel"), L.Get("screenshot.backdropGradientLabel")], _gradient ? 1 : 0, i =>
        {
            _gradient = i == 1;
            _activeWell = 0;
            ApplyCustom();
        }, out _));
        _save = EditorUi.Button(null, "Add", L.Get("screenshot.backdropSavePreset"), () => _session.SaveBackdropCustom(_session.Document.BackdropStyle));
        var wellRow = new DockPanel { Children = { _wells } };
        DockPanel.SetDock(_save, Dock.Right);
        wellRow.Children.Insert(0, _save);
        Children.Add(wellRow);
        var palette = new UniformGrid { Columns = 10 };
        foreach (var color in Palette)
        {
            var dot = new Button
            {
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 3),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ToColor(color)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            };
            AutomationProperties.SetName(dot, L.Get("screenshot.backdropCustomLabel"));
            var picked = color;
            dot.Click += (_, _) =>
            {
                if (_gradient && _activeWell == 1)
                {
                    _end = picked;
                }
                else
                {
                    _start = picked;
                }

                ApplyCustom();
            };
            palette.Children.Add(dot);
        }

        Children.Add(palette);
        session.Changed += OnChanged;
        Rebuild();
    }

    private void OnChanged(SessionChange change)
    {
        if ((change & (SessionChange.Document | SessionChange.Presets)) != 0)
        {
            Rebuild();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _session.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void ApplyCustom()
    {
        _session.SetBackdropLook(_gradient
            ? new RecorderBackdrop { Kind = RecorderBackdropKind.Gradient, Colors = [_start, _end] }
            : new RecorderBackdrop { Kind = RecorderBackdropKind.Solid, Colors = [_start] });
        Rebuild();
    }

    private void Rebuild()
    {
        var current = _session.Document.BackdropStyle;
        _swatches.Children.Clear();
        _swatches.Children.Add(Swatch(RecorderBackdrop.None, current, null));
        foreach (var id in RecorderBackdrop.PresetIds)
        {
            _swatches.Children.Add(Swatch(new RecorderBackdrop { Kind = RecorderBackdropKind.Preset, PresetId = id }, current, null));
        }

        foreach (var custom in _session.BackdropCustoms)
        {
            _swatches.Children.Add(Swatch(custom, current, custom));
        }

        foreach (var wallpaper in Wallpapers())
        {
            _swatches.Children.Add(Swatch(new RecorderBackdrop { Kind = RecorderBackdropKind.Image, ImagePath = wallpaper }, current, null, wallpaperBadge: true));
        }

        _swatches.Children.Add(ImageTile());

        _wells.Children.Clear();
        _wells.Children.Add(Well(_start, 0));
        if (_gradient)
        {
            _wells.Children.Add(Well(_end, 1));
        }

        _save.IsEnabled = current.Kind is RecorderBackdropKind.Solid or RecorderBackdropKind.Gradient or RecorderBackdropKind.Image
                          && !_session.BackdropCustoms.Any(c => c.SameLook(current));
    }

    private IReadOnlyList<string> Wallpapers()
    {
        try
        {
            return _session.Services.GetService<IRecordingEditorShell>()?.CurrentWallpapers() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private Control Swatch(RecorderBackdrop look, RecorderBackdrop current, RecorderBackdrop? removable, bool wallpaperBadge = false)
    {
        var selected = look.SameLook(current);
        var fill = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = BrushFor(look) };
        if (!look.HasBackdrop)
        {
            fill.Child = EditorUi.Icon("SlashForward", 18);
            fill.Child.HorizontalAlignment = HorizontalAlignment.Center;
            fill.Themed(Border.BackgroundProperty, "EditorSubpanelBrush");
        }

        var grid = new Grid { Children = { fill } };
        if (wallpaperBadge)
        {
            grid.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3),
                Child = new FluentIcons.Avalonia.SymbolIcon { Symbol = IconConverter.Parse("Desktop"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }

        var button = new Button
        {
            Padding = new Thickness(2),
            Margin = new Thickness(3),
            Height = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(selected ? 2.5 : 0),
            BorderBrush = selected ? new SolidColorBrush(EditorPalette.For(this).Accent) : Brushes.Transparent,
            Content = grid,
        };
        AutomationProperties.SetName(button, look.HasBackdrop ? L.Get("screenshot.backdropLabel") : L.Get("screenshot.backdropNone"));
        button.Click += (_, _) => _session.SetBackdropLook(look);
        if (removable is not null)
        {
            var remove = new MenuItem { Header = L.Get("screenshot.backdropDeletePreset") };
            remove.Click += (_, _) => _session.RemoveBackdropCustom(removable);
            button.ContextMenu = new ContextMenu { Items = { remove } };
        }

        return button;
    }

    private Control ImageTile()
    {
        var label = new TextBlock { Text = L.Get("screenshot.backdropImageButton"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = label,
        }.Themed(Border.BorderBrushProperty, "EditorButtonBorderBrush");
        var button = new Button
        {
            Margin = new Thickness(3),
            Height = 38,
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            Content = border,
        };
        AutomationProperties.SetName(button, L.Get("screenshot.backdropImageButton"));
        button.Click += async (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null)
            {
                return;
            }

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [ImageFileTypes],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                _session.SetBackdropLook(new RecorderBackdrop { Kind = RecorderBackdropKind.Image, ImagePath = path });
            }
        };
        return button;
    }

    private Control Well(RgbValue color, int index)
    {
        var active = index == _activeWell;
        var well = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(ToColor(color)),
            BorderThickness = new Thickness(active ? 2.5 : 1),
            BorderBrush = active ? new SolidColorBrush(EditorPalette.For(this).Accent) : new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
        };
        AutomationProperties.SetName(well, L.Get("screenshot.backdropCustomLabel"));
        well.Click += (_, _) =>
        {
            _activeWell = index;
            Rebuild();
        };
        return well;
    }

    internal static FilePickerFileType ImageFileTypes { get; } = new(L.Get("win.recordingEditor.imageFilter"))
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.heic", "*.tif", "*.tiff"],
        MimeTypes = ["image/*"],
    };

    private static readonly Dictionary<string, Bitmap?> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The swatch fill for a look (gradient top-left → bottom-right, or an image thumbnail).</summary>
    public static IBrush? BrushFor(RecorderBackdrop look)
    {
        var colors = look.ResolvedColors();
        if (look.Kind == RecorderBackdropKind.Image && look.ImagePath is { } path)
        {
            if (!Thumbnails.TryGetValue(path, out var bitmap))
            {
                bitmap = ImageInterop.LoadThumbnail(path, 120);
                Thumbnails[path] = bitmap;
            }

            return bitmap is null ? Brushes.Gray : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        }

        return colors.Count switch
        {
            >= 2 => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(ToColor(colors[0]), 0), new GradientStop(ToColor(colors[1]), 1) },
            },
            1 => new SolidColorBrush(ToColor(colors[0])),
            _ => null,
        };
    }

    public static Color ToColor(RgbValue c) =>
        Color.FromRgb((byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255), (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));
}
