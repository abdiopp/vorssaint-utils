// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Localization;
using Rivet.Core.ScreenshotEditor;
using Rivet.Imaging.ScreenshotEditor;
using SkiaSharp;

namespace Rivet.App.Features.ScreenshotEditor;

/// <summary>
/// The contextual style bar (spec 01 §3.10.5). Its groups follow the active
/// tool, or the selected mark while Select is active; in crop mode it becomes
/// the crop bar (Cancel, Crop).
/// </summary>
internal sealed class StyleBar : UserControl
{
    private readonly EditorController _controller;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _cropBar = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Button _arrowMenu;
    private readonly Image _arrowSample = new() { Width = 36, Height = 16 };
    private readonly Button _stickerMenu;
    private readonly TextBlock _stickerGlyph = new() { FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _blurGroup = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Button _blurStyleMenu;
    private readonly TextBlock _blurStyleLabel = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = EditorChrome.Primary, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _textOnly;
    private readonly StackPanel _strength = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Slider _strengthSlider = new() { Minimum = 1, Maximum = 5, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 84, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _colors = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly Dictionary<AnnotationColor, ColorDot> _colorDots = [];
    private readonly StackPanel _thickness = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Dictionary<StrokeWidth, Button> _thicknessButtons = [];
    private readonly StackPanel _textSize = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _smaller;
    private readonly Button _larger;
    private readonly Button _sizeMenu;
    private readonly StackPanel _layers = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _backward;
    private readonly Button _forward;
    private readonly Button _shadows;
    private readonly Button _background;
    private readonly Button _watermark;
    private readonly List<Control> _dividers = [];
    private readonly Dictionary<ArrowStyle, Avalonia.Media.Imaging.Bitmap> _arrowSamples = [];
    private bool _syncing;

    public StyleBar(EditorController controller, Func<Control> backdropPopover, Func<Control> watermarkPopover)
    {
        _controller = controller;
        var session = controller.Session;

        _arrowMenu = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _arrowSample, Chevron() } }, L.Get("screenshot.arrowStyleLabel"));
        _arrowMenu.Flyout = BuildArrowMenu();

        _stickerMenu = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _stickerGlyph, Label(L.Get("screenshot.toolSticker")), Chevron() } }, L.Get("screenshot.toolSticker"));
        _stickerMenu.Flyout = BuildStickerMenu();

        _blurStyleMenu = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { EditorChrome.Icon("Blur", 15), _blurStyleLabel, Chevron() } }, L.Get("screenshot.blurStyleLabel"));
        _blurStyleMenu.Flyout = BuildBlurMenu();
        _textOnly = EditorChrome.ToggleButton("TextT", L.Get("screenshot.blurTextOnly"), () => session.SetTextOnly(!session.Style.TextOnly), L.Get("screenshot.blurTextOnly"));
        _strengthSlider.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                session.SetBlurLevel((int)Math.Round(e.NewValue));
            }
        };
        ToolTip.SetTip(_strengthSlider, L.Get("screenshot.blurStrengthLabel"));
        AutomationProperties.SetName(_strengthSlider, L.Get("screenshot.blurStrengthLabel"));
        _strength.Children.Add(EditorChrome.Icon("BrightnessLow", 13));
        _strength.Children.Add(_strengthSlider);
        _strength.Children.Add(EditorChrome.Icon("BrightnessHigh", 15));
        _blurGroup.Children.Add(_blurStyleMenu);
        _blurGroup.Children.Add(_textOnly);
        _blurGroup.Children.Add(_strength);

        foreach (var color in AnnotationColors.All)
        {
            var captured = color;
            var dot = new ColorDot(EditorChrome.ToAvalonia(color), L.Get(AnnotationColors.TitleKey(color)), () => session.SetColor(captured));
            _colorDots[color] = dot;
            _colors.Children.Add(dot);
        }

        foreach (var stroke in StrokeWidths.All)
        {
            var captured = stroke;
            var glyph = new Border
            {
                Width = 15,
                Height = stroke switch { StrokeWidth.Small => 1.8, StrokeWidth.Large => 5.4, _ => 3.4 },
                CornerRadius = new CornerRadius(3),
                Background = EditorChrome.Primary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Button
            {
                Content = glyph,
                Width = 26,
                Height = 24,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Focusable = false,
                Classes = { "editorIcon" },
            };
            var name = $"{L.Get("screenshot.strokeLabel")}: {L.Get(StrokeWidths.TitleKey(stroke))}";
            ToolTip.SetTip(button, name);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => session.SetStroke(captured);
            _thicknessButtons[stroke] = button;
            _thickness.Children.Add(button);
        }

        _smaller = EditorChrome.IconButton("FontDecrease", L.Get("win.screenshotEditor.textSmaller"), () =>
        {
            if (TextSizes.Smaller(session.Style.TextSize) is { } s)
            {
                session.SetTextSize(s);
            }
        }, 15, 28, 26);
        _larger = EditorChrome.IconButton("FontIncrease", L.Get("win.screenshotEditor.textLarger"), () =>
        {
            if (TextSizes.Larger(session.Style.TextSize) is { } s)
            {
                session.SetTextSize(s);
            }
        }, 15, 28, 26);
        _sizeMenu = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { Label(string.Empty), Chevron() } }, L.Get("screenshot.fontSizeLabel"));
        _sizeMenu.Flyout = BuildSizeMenu();
        _textSize.Children.Add(_smaller);
        _textSize.Children.Add(_sizeMenu);
        _textSize.Children.Add(_larger);

        _backward = EditorChrome.IconButton("PositionBackward", L.Get("screenshot.sendBackward"), session.SendBackward, 15, 28, 26);
        _forward = EditorChrome.IconButton("PositionForward", L.Get("screenshot.bringForward"), session.BringForward, 15, 28, 26);
        _layers.Children.Add(_backward);
        _layers.Children.Add(_forward);

        _shadows = EditorChrome.ToggleButton("SquareShadow", L.Get("screenshot.shadowLabel"), () => controller.Shadows = !controller.Shadows);

        _background = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { EditorChrome.Icon("ImageShadow", 15), Label(L.Get("screenshot.backdropLabel")) } }, L.Get("screenshot.backdropLabel"));
        _background.Flyout = new Flyout { Placement = PlacementMode.Top, Content = new LazyContent(backdropPopover) };
        _watermark = MenuButton(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { EditorChrome.Icon("SlideText", 15), Label(L.Get("screenshot.watermarkLabel")) } }, L.Get("screenshot.watermarkLabel"));
        _watermark.Flyout = new Flyout { Placement = PlacementMode.Top, Content = new LazyContent(watermarkPopover) };

        foreach (var control in new Control[] { _arrowMenu, _stickerMenu, _blurGroup, _colors, _thickness, _textSize, _layers })
        {
            _row.Children.Add(control);
            var divider = EditorChrome.VerticalDivider();
            _dividers.Add(divider);
            _row.Children.Add(divider);
        }

        _row.Children.Add(_shadows);
        _row.Children.Add(_background);
        _row.Children.Add(_watermark);

        var cancel = EditorChrome.TextButton(L.Get("screenshot.cancel"), session.CancelCrop, "Esc", 13);
        var crop = new Button
        {
            Content = L.Get("screenshot.cropApply"),
            Classes = { "accent" },
            Focusable = false,
            Padding = new Thickness(14, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(crop, L.Get("win.screenshotEditor.enterKey"));
        crop.Click += (_, _) => CropRequested?.Invoke(this, EventArgs.Empty);
        _cropBar.Children.Add(cancel);
        _cropBar.Children.Add(crop);

        Content = EditorChrome.Capsule(new Panel { Children = { _row, _cropBar } }, 13, new Thickness(12, 6));
        HorizontalAlignment = HorizontalAlignment.Center;

        session.Changed += (_, _) => Sync();
        session.StyleChanged += (_, _) => Sync();
        session.ToolChanged += (_, _) => Sync();
        controller.Changed += (_, _) => Sync();
        Sync();
    }

    /// <summary>The Crop button of the crop bar (the window applies the crop).</summary>
    public event EventHandler? CropRequested;

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = EditorChrome.Primary, VerticalAlignment = VerticalAlignment.Center };

    private static SymbolIcon Chevron() => EditorChrome.Icon("ChevronDown", 10);

    private static Button MenuButton(Control content, string name)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(7, 3),
            MinHeight = 26,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "editorIcon" },
        };
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private MenuFlyout BuildArrowMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var current = _controller.Session.Style.ArrowStyle;
            foreach (var style in ArrowStyles.All)
            {
                var captured = style;
                var item = new MenuItem
                {
                    Header = L.Get(ArrowStyles.TitleKey(style)),
                    Icon = new Image { Source = ArrowSample(style), Width = 36, Height = 16 },
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = style == current,
                };
                item.Click += (_, _) => _controller.Session.SetArrowStyle(captured);
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    private MenuFlyout BuildStickerMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var current = _controller.Session.Style.Sticker;
            foreach (var sticker in Stickers.All)
            {
                var captured = sticker;
                var item = new MenuItem
                {
                    Header = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { new TextBlock { Text = Stickers.Glyph(sticker), FontSize = 16 }, new TextBlock { Text = L.Get(Stickers.TitleKey(sticker)), VerticalAlignment = VerticalAlignment.Center } },
                    },
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = sticker == current,
                };
                item.Click += (_, _) => _controller.Session.SetSticker(captured);
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    private MenuFlyout BuildBlurMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var current = _controller.Session.Style.BlurStyle;
            foreach (var style in BlurStyles.All)
            {
                var captured = style;
                var item = new MenuItem { Header = L.Get(BlurStyles.TitleKey(style)), ToggleType = MenuItemToggleType.Radio, IsChecked = style == current };
                item.Click += (_, _) => _controller.Session.SetBlurStyle(captured);
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    private MenuFlyout BuildSizeMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.Top };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var current = _controller.Session.Style.TextSize;
            foreach (var size in TextSizes.Presets)
            {
                var captured = size;
                var item = new MenuItem { Header = L.Format("win.screenshotEditor.pointSizeFormat", size), ToggleType = MenuItemToggleType.Radio, IsChecked = size == current };
                item.Click += (_, _) => _controller.Session.SetTextSize(captured);
                menu.Items.Add(item);
            }
        };
        return menu;
    }

    /// <summary>The style menu sample, rendered once per style by the real renderer.</summary>
    private Avalonia.Media.Imaging.Bitmap ArrowSample(ArrowStyle style)
    {
        if (!_arrowSamples.TryGetValue(style, out var bitmap))
        {
            using var sample = EditorSamples.ArrowStyle(style, 2, SKColors.White);
            _arrowSamples[style] = bitmap = EditorChrome.ToBitmap(sample);
        }

        return bitmap;
    }

    /// <summary>The kind whose styles the bar shows: the selection with Select, else the active tool's.</summary>
    private AnnotationKind? ContextKind()
    {
        var session = _controller.Session;
        if (session.Tool == EditorTool.Select)
        {
            return session.Selected?.Kind;
        }

        return EditorTools.IsCreation(session.Tool) ? EditorTools.KindFor(session.Tool) : null;
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var session = _controller.Session;
            var cropping = session.Tool == EditorTool.Crop && session.CropDraft is not null;
            _cropBar.IsVisible = cropping;
            _row.IsVisible = !cropping;
            var kind = ContextKind();
            var style = session.Style;

            _arrowMenu.IsVisible = kind == AnnotationKind.Arrow;
            if (_arrowMenu.IsVisible)
            {
                _arrowSample.Source = ArrowSample(style.ArrowStyle);
            }

            _stickerMenu.IsVisible = kind == AnnotationKind.Sticker;
            _stickerGlyph.Text = Stickers.Glyph(style.Sticker);

            _blurGroup.IsVisible = kind == AnnotationKind.Blur;
            _blurStyleLabel.Text = L.Get(BlurStyles.TitleKey(style.BlurStyle));
            EditorChrome.SetToggled(_textOnly, style.TextOnly);
            _strength.IsVisible = style.BlurStyle != BlurStyle.Erase;
            _strengthSlider.Value = style.BlurLevel;

            var showColors = kind is not null and not (AnnotationKind.Sticker or AnnotationKind.Blur);
            _colors.IsVisible = showColors;
            foreach (var (color, dot) in _colorDots)
            {
                dot.IsSelected = color == style.Color;
            }

            _textSize.IsVisible = kind == AnnotationKind.Text;
            if (_sizeMenu.Content is StackPanel { Children: [TextBlock sizeLabel, ..] })
            {
                sizeLabel.Text = L.Format("win.screenshotEditor.pointSizeFormat", style.TextSize);
            }

            _smaller.IsEnabled = TextSizes.Smaller(style.TextSize) is not null;
            _larger.IsEnabled = TextSizes.Larger(style.TextSize) is not null;

            // Thickness has no visible effect on highlights, counters and solid blocks, so it is hidden there.
            _thickness.IsVisible = showColors && kind is not (AnnotationKind.Text or AnnotationKind.Highlight or AnnotationKind.Counter or AnnotationKind.Redact);
            foreach (var (stroke, button) in _thicknessButtons)
            {
                var active = stroke == style.Stroke;
                button.Background = active ? EditorChrome.AccentTint(0.18) : Brushes.Transparent;
                if (button.Content is Border glyph)
                {
                    glyph.Background = active ? EditorChrome.AccentBrush : EditorChrome.Primary;
                }
            }

            _layers.IsVisible = session.ShowsLayerButtons;
            _backward.IsEnabled = session.CanSendBackward;
            _forward.IsEnabled = session.CanBringForward;

            EditorChrome.SetToggled(_shadows, _controller.Shadows);

            var watermarkOn = _controller.Watermark.IsDrawn;
            _watermark.Background = watermarkOn ? EditorChrome.AccentTint(0.22) : Brushes.Transparent;

            // A divider follows each visible group.
            var groups = new Control[] { _arrowMenu, _stickerMenu, _blurGroup, _colors, _thickness, _textSize, _layers };
            for (var i = 0; i < groups.Length; i++)
            {
                _dividers[i].IsVisible = groups[i].IsVisible;
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
