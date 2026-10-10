// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Rivet.Core.Localization;
using Rivet.Core.RecordingEditor;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>
/// The right panel (spec 02 §3.27): what it shows follows the selection —
/// "This text", "This image", "This blur", "This zoom" — otherwise the
/// Look | Pointer | Zoom tabs. Panels update in place while their item stays
/// selected, so a slider being dragged is never rebuilt under the pointer.
/// </summary>
public sealed class InspectorView : Border
{
    private readonly EditorSession _session;
    private readonly StackPanel _content = new() { Spacing = 18 };
    private readonly List<Action> _updaters = [];
    private string _mode = string.Empty;
    private TextBox? _textEditor;

    public InspectorView(EditorSession session)
    {
        _session = session;
        Width = 272;
        this.Themed(BackgroundProperty, "EditorInspectorBrush");
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(16, 14), Child = _content },
        };
        session.Changed += OnChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _session.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(SessionChange change)
    {
        if ((change & (SessionChange.Document | SessionChange.Selection | SessionChange.Mode | SessionChange.Presets)) != 0)
        {
            Refresh();
        }
    }

    private string ModeKey() => _session.SelectedKind switch
    {
        LaneKind.Text when _session.SelectedText is { } t => "text:" + t.Id,
        LaneKind.Image when _session.SelectedImage is { } i => "image:" + i.Id,
        LaneKind.Blur when _session.SelectedBlur is { } b => "blur:" + b.Id,
        LaneKind.Zoom when _session.SelectedZoom is { } z => "zoom:" + z.Id,
        _ => "tab:" + _session.Tab + ":" + (_session.HasPointerTrack ? 1 : 0),
    };

    public void Refresh()
    {
        var mode = ModeKey();
        if (mode == _mode)
        {
            foreach (var update in _updaters)
            {
                update();
            }

            return;
        }

        if (_textEditor is not null && _session.History.InInteraction)
        {
            // The caption panel is going away: its typing lands as one undo step.
            _session.CommitInteraction();
        }

        _mode = mode;
        _updaters.Clear();
        _textEditor = null;
        _content.Children.Clear();
        switch (_session.SelectedKind)
        {
            case LaneKind.Text when _session.SelectedText is { } text:
                BuildText(text);
                break;
            case LaneKind.Image when _session.SelectedImage is { } image:
                BuildImage(image);
                break;
            case LaneKind.Blur when _session.SelectedBlur is { } blur:
                BuildBlur(blur);
                break;
            case LaneKind.Zoom when _session.SelectedZoom is { } zoom:
                BuildZoom(zoom);
                break;
            default:
                BuildTabs();
                break;
        }

        foreach (var update in _updaters)
        {
            update();
        }
    }

    // ── Tabs ──────────────────────────────────────────────────────────

    private void BuildTabs()
    {
        var tabs = new[] { InspectorTab.Look, InspectorTab.Pointer, InspectorTab.Zoom };
        _content.Children.Add(EditorUi.Segmented(
            [L.Get("recorder.lookLabel"), L.Get("recorder.pointerSectionLabel"), L.Get("recorder.zoomSectionLabel")],
            Array.IndexOf(tabs, _session.Tab),
            i => _session.SetTab(tabs[i]),
            out _));
        switch (_session.Tab)
        {
            case InspectorTab.Pointer:
                BuildPointerTab();
                break;
            case InspectorTab.Zoom:
                BuildZoomTab();
                break;
            default:
                BuildLookTab();
                break;
        }
    }

    private void BuildLookTab()
    {
        var looks = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 6 };
        var cards = new (RecorderLook Look, string Title, string Icon)[]
        {
            (RecorderLook.Original, L.Get("recorder.lookRaw"), "RectangleLandscape"),
            (RecorderLook.Smooth, L.Get("recorder.lookClean"), "CursorHover"),
            (RecorderLook.Studio, L.Get("recorder.lookStudio"), "Sparkle"),
        };
        for (var i = 0; i < cards.Length; i++)
        {
            var card = LookCard(cards[i].Look, cards[i].Title, cards[i].Icon);
            Grid.SetColumn(card, i);
            looks.Children.Add(card);
        }

        _content.Children.Add(new StackPanel { Spacing = 8, Children = { EditorUi.Section(L.Get("recorder.lookLabel")), looks } });

        // Background: a swatch button opening the popover, the shape, and the sliders when a background is set.
        var swatch = new Border { Width = 26, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) }.Themed(Border.BorderBrushProperty, "EditorButtonBorderBrush");
        var swatchLabel = EditorUi.Label(string.Empty, 12.5);
        var background = new Button
        {
            Classes = { "editor" },
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new DockPanel
            {
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { swatch, swatchLabel } },
                },
            },
        };
        var chevron = EditorUi.Icon("ChevronRight", 12);
        DockPanel.SetDock(chevron, Dock.Right);
        ((DockPanel)background.Content).Children.Insert(0, chevron);
        AutomationProperties.SetName(background, L.Get("screenshot.backdropLabel"));
        background.Click += (_, _) =>
        {
            var flyout = new Flyout { Placement = PlacementMode.LeftEdgeAlignedTop, Content = new BackgroundPicker(_session) };
            flyout.ShowAt(background);
        };

        var shapes = new[] { CanvasAspect.Original, CanvasAspect.Wide, CanvasAspect.Square, CanvasAspect.Vertical };
        var shape = new ComboBox
        {
            ItemsSource = new[] { L.Get("recorder.shapeOriginal"), L.Get("recorder.shapeWide"), L.Get("recorder.shapeSquare"), L.Get("recorder.shapeVertical") },
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(shape, L.Get("recorder.shapeLabel"));
        var updatingShape = false;
        shape.SelectionChanged += (_, _) =>
        {
            if (!updatingShape && shape.SelectedIndex >= 0)
            {
                _session.SetAspect(shapes[shape.SelectedIndex]);
            }
        };
        var shapeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { EditorUi.Label(L.Get("recorder.shapeLabel"), 12.5), shape } };
        Grid.SetColumn(shape, 1);

        var margin = new ValueSlider(L.Get("screenshot.backdropPaddingLabel"), 0, 1, 0.01, 0.5, 0.5, Percent,
            _session.BeginInteraction, v => _session.SetBackdropSliders(padding: v), _session.CommitInteraction);
        var corners = new ValueSlider(L.Get("screenshot.backdropCornersLabel"), 0, 1, 0.01, 0, 0, Percent,
            _session.BeginInteraction, v => _session.SetBackdropSliders(corners: v), _session.CommitInteraction);
        var blur = new ValueSlider(L.Get("screenshot.backdropBlurLabel"), 0, 1, 0.01, 0, 0, Percent,
            _session.BeginInteraction, v => _session.SetBackdropSliders(blur: v), _session.CommitInteraction);
        var sliders = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10),
            Child = new StackPanel { Spacing = 10, Children = { margin, corners, blur } },
        }.Themed(Border.BackgroundProperty, "EditorSubpanelBrush");

        _content.Children.Add(new StackPanel { Spacing = 8, Children = { EditorUi.Section(L.Get("recorder.backgroundSectionLabel")), background, shapeRow, sliders } });
        _updaters.Add(() =>
        {
            var style = _session.Document.BackdropStyle;
            swatch.Background = style.HasBackdrop ? BackgroundPicker.BrushFor(style) : null;
            swatch.Child = style.HasBackdrop ? null : EditorUi.Icon("SlashForward", 12);
            if (swatch.Child is Control slash)
            {
                slash.HorizontalAlignment = HorizontalAlignment.Center;
            }
            swatchLabel.Text = style.HasBackdrop ? L.Get("screenshot.backdropLabel") : L.Get("screenshot.backdropNone");
            updatingShape = true;
            shape.SelectedIndex = Array.IndexOf(shapes, _session.Document.Aspect);
            updatingShape = false;
            sliders.IsVisible = style.HasBackdrop;
            margin.Update(style.Padding);
            corners.Update(style.CornerRadius);
            blur.Update(style.Blur);
        });
    }

    private Control LookCard(RecorderLook look, string title, string icon)
    {
        var preview = new Border
        {
            Width = 48,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Child = EditorUi.Icon(icon, 18),
        };
        if (look != RecorderLook.Studio)
        {
            preview.Themed(Border.BackgroundProperty, "EditorSubpanelBrush");
        }
        else
        {
            preview.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0x4F, 0x46, 0xE5), 0), new GradientStop(Color.FromRgb(0xA8, 0x55, 0xF7), 1) },
            };
            ((Control)preview.Child!).SetValue(TextElement.ForegroundProperty, Brushes.White);
        }

        ((Control)preview.Child!).HorizontalAlignment = HorizontalAlignment.Center;
        var check = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -4, 0),
            Child = new FluentIcons.Avalonia.SymbolIcon { Symbol = Rivet.App.Controls.IconConverter.Parse("Checkmark"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var card = new Button
        {
            Padding = new Thickness(6, 8),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1.25),
            Content = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children = { preview, new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } },
                    },
                    check,
                },
            },
        };
        AutomationProperties.SetName(card, title);
        card.Click += (_, _) => _session.ApplyLook(look);
        _updaters.Add(() =>
        {
            var accent = EditorPalette.For(this).Accent;
            var selected = Looks.Matches(look, _session.Document);
            card.Background = selected ? new SolidColorBrush(EditorPalette.WithAlpha(accent, 0.10)) : Brushes.Transparent;
            card.BorderBrush = selected ? new SolidColorBrush(EditorPalette.WithAlpha(accent, 0.72)) : Brushes.Transparent;
            check.Background = new SolidColorBrush(accent);
            check.IsVisible = selected;
        });
        return card;
    }

    private void BuildPointerTab()
    {
        if (!_session.HasPointerTrack)
        {
            _content.Children.Add(EditorUi.Caption(L.Get("recorder.noPointerNote")));
            return;
        }

        var show = EditorUi.SwitchRow(L.Get("recorder.pointerShowToggle"), _session.Document.ShowsPointer, _session.SetShowsPointer, out var showToggle);
        var smoothingValues = new[] { PointerSmoothing.Off, PointerSmoothing.Light, PointerSmoothing.Smooth, PointerSmoothing.Cinematic };
        var smoothing = new ComboBox
        {
            ItemsSource = new[] { L.Get("recorder.pointerSmoothingOff"), L.Get("recorder.pointerSmoothingLight"), L.Get("recorder.pointerSmoothingSmooth"), L.Get("recorder.pointerSmoothingCinematic") },
            MinWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(smoothing, L.Get("recorder.pointerSmoothingLabel"));
        var updating = false;
        smoothing.SelectionChanged += (_, _) =>
        {
            if (!updating && smoothing.SelectedIndex >= 0)
            {
                _session.SetSmoothing(smoothingValues[smoothing.SelectedIndex]);
            }
        };
        var smoothingRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { EditorUi.Label(L.Get("recorder.pointerSmoothingLabel"), 12.5), smoothing } };
        Grid.SetColumn(smoothing, 1);
        var size = new ValueSlider(L.Get("recorder.pointerSizeLabel"), 0.5, 2, 0.05, _session.Document.PointerSize, 1,
            v => v.ToString("0.00", CultureInfo.CurrentUICulture) + "×", _session.BeginInteraction, _session.SetPointerSize, _session.CommitInteraction);
        var ring = EditorUi.SwitchRow(L.Get("recorder.clickRingToggle"), _session.Document.ShowsClickRing, _session.SetClickRing, out var ringToggle);
        var details = new StackPanel { Spacing = 12, Children = { smoothingRow, size, ring } };
        _content.Children.Add(new StackPanel { Spacing = 12, Children = { EditorUi.Section(L.Get("recorder.pointerSectionLabel")), show, details } });
        _updaters.Add(() =>
        {
            var doc = _session.Document;
            showToggle.IsChecked = doc.ShowsPointer;
            details.IsVisible = doc.ShowsPointer;
            updating = true;
            smoothing.SelectedIndex = Array.IndexOf(smoothingValues, doc.PointerSmoothing);
            updating = false;
            size.Update(doc.PointerSize);
            ringToggle.IsChecked = doc.ShowsClickRing;
        });
    }

    private void BuildZoomTab()
    {
        var section = new StackPanel { Spacing = 12, Children = { EditorUi.Section(L.Get("recorder.zoomSectionLabel")) } };
        ToggleSwitch? enabledToggle = null;
        if (_session.HasPointerTrack)
        {
            section.Children.Add(EditorUi.SwitchRow(L.Get("recorder.zoomToggle"), _session.Document.ZoomEnabled, _session.SetZoomEnabled, out var toggle));
            enabledToggle = toggle;
        }

        var typing = EditorUi.SwitchRow(L.Get("recorder.typingZoomToggle"), _session.Document.ZoomsOnTyping, _session.SetZoomsOnTyping, out var typingToggle);
        var typingBlock = new StackPanel { Spacing = 4, Children = { typing, EditorUi.Caption(L.Get("recorder.typingZoomCaption")) }, IsVisible = _session.HasPointerTrack };

        // Empty state.
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Center, Child = EditorUi.Icon("ZoomIn", 20) };
        ((Control)circle.Child).HorizontalAlignment = HorizontalAlignment.Center;
        var createButton = EditorUi.Button(string.Empty, null, null, () =>
        {
            if (_session.AutomaticZoomsWouldExist)
            {
                _session.RegenerateZooms();
            }
            else
            {
                _session.AddZoomAt(_session.PlayheadSource);
            }
        });
        createButton.HorizontalAlignment = HorizontalAlignment.Center;
        var empty = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                circle,
                new TextBlock { Text = L.Get("recorder.zoomEmptyTitle"), FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L.Get("recorder.zoomEmptyCaption"), Classes = { "caption" }, TextAlignment = TextAlignment.Center },
                createButton,
            },
        };

        var amount = new ValueSlider(L.Get("recorder.zoomAmountLabel"), ZoomSegment.MinAmount, ZoomSegment.MaxAmount, 0.1, _session.Document.ZoomAmount, ZoomSegment.DefaultAmount,
            v => v.ToString("0.0", CultureInfo.CurrentUICulture) + "×", _session.BeginInteraction, _session.SetGlobalZoomAmount, _session.CommitInteraction);
        var regenerate = new Button { Content = L.Get("recorder.regenerateZooms"), Classes = { "link" }, IsVisible = _session.HasPointerTrack };
        regenerate.Click += (_, _) => _session.RegenerateZooms();
        var panel = new StackPanel { Spacing = 10, Children = { amount, regenerate } };

        section.Children.Add(typingBlock);
        section.Children.Add(empty);
        section.Children.Add(panel);
        _content.Children.Add(section);
        _updaters.Add(() =>
        {
            var doc = _session.Document;
            var palette = EditorPalette.For(this);
            if (enabledToggle is not null)
            {
                enabledToggle.IsChecked = doc.ZoomEnabled;
            }

            typingToggle.IsChecked = doc.ZoomsOnTyping;
            var active = doc.ZoomEnabled || !_session.HasPointerTrack;
            typingBlock.IsVisible = _session.HasPointerTrack && doc.ZoomEnabled;
            empty.IsVisible = active && doc.ZoomSegments.Count == 0;
            panel.IsVisible = active && doc.ZoomSegments.Count > 0;
            circle.Background = new SolidColorBrush(EditorPalette.WithAlpha(palette.Accent, 0.16));
            ((Control)circle.Child!).SetValue(TextElement.ForegroundProperty, new SolidColorBrush(palette.Accent));
            createButton.Content = L.Get(_session.AutomaticZoomsWouldExist ? "recorder.createAutomaticZooms" : "recorder.addZoomButton");
            amount.Update(doc.ZoomAmount);
        });
    }

    // ── Context panels ────────────────────────────────────────────────

    private void AddHeader(string titleKey)
    {
        var back = new Button
        {
            Classes = { "link" },
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { EditorUi.Icon("ChevronLeft", 11), new TextBlock { Text = L.Get("recorder.backToOptions") } } },
        };
        ToolTip.SetTip(back, "Esc");
        AutomationProperties.SetName(back, L.Get("recorder.backToOptions"));
        back.Click += (_, _) => _session.ClearSelection();
        _content.Children.Add(new StackPanel { Spacing = 10, Children = { back, EditorUi.Label(L.Get(titleKey), 15, FontWeight.SemiBold) } });
    }

    private Button RemoveButton(Action remove)
    {
        var button = EditorUi.Button(L.Get("recorder.removeZoom"), "Delete", null, remove, "danger");
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private void BuildZoom(ZoomSegment zoom)
    {
        AddHeader("recorder.thisZoomLabel");
        var id = zoom.Id;
        var amount = new ValueSlider(L.Get("recorder.zoomAmountLabel"), ZoomSegment.MinAmount, ZoomSegment.MaxAmount, 0.1, zoom.Amount, ZoomSegment.DefaultAmount,
            v => v.ToString("0.0", CultureInfo.CurrentUICulture) + "×", _session.BeginInteraction, v => _session.SetZoomAmount(id, v), _session.CommitInteraction);
        ToggleButton[] where = [];
        var segmented = EditorUi.Segmented([L.Get("recorder.zoomFollowsPointer"), L.Get("recorder.zoomPickSpot")], zoom.IsAimed ? 1 : 0, i =>
        {
            if (i == 0)
            {
                _session.FollowPointer(id);
            }
            else
            {
                _session.StartAiming();
            }
        }, out where);
        var aimAgain = new Button { Content = L.Get("recorder.zoomPickSpotHint"), Classes = { "link" } };
        aimAgain.Click += (_, _) => _session.StartAiming();
        _content.Children.Add(amount);
        _content.Children.Add(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = L.Get("recorder.zoomWhereLabel"), Classes = { "sliderTitle" } }, segmented, aimAgain } });
        _content.Children.Add(RemoveButton(() => _session.Remove(LaneKind.Zoom, id)));
        _updaters.Add(() =>
        {
            if (_session.SelectedZoom is not { } current)
            {
                return;
            }

            amount.Update(current.Amount);
            where[0].IsChecked = !current.IsAimed && !_session.IsAiming;
            where[1].IsChecked = current.IsAimed || _session.IsAiming;
            aimAgain.IsVisible = current.IsAimed && !_session.IsAiming;
        });
    }

    private void BuildText(TextOverlay text)
    {
        AddHeader("recorder.thisTextLabel");
        var id = text.Id;
        var editor = new TextBox
        {
            Text = text.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 1,
            MaxLines = 3,
            PlaceholderText = L.Get("recorder.textPlaceholder"),
            MaxLength = TextOverlay.MaxLength,
        };
        AutomationProperties.SetName(editor, L.Get("recorder.textContentLabel"));
        var typing = false;
        editor.TextChanged += (_, _) =>
        {
            if (typing)
            {
                _session.SetText(id, editor.Text ?? string.Empty);
            }
        };
        editor.GotFocus += (_, _) =>
        {
            typing = true;
            _session.BeginInteraction();
        };
        editor.LostFocus += (_, _) =>
        {
            typing = false;
            _session.CommitInteraction();
        };
        _textEditor = editor;
        var size = new ValueSlider(L.Get("recorder.textSizeLabel"), TextOverlay.MinSize, TextOverlay.MaxSize, 0.005, text.Size, TextOverlay.DefaultSize, Percent,
            _session.BeginInteraction, v => _session.SetTextSize(id, v), _session.CommitInteraction);
        var anchors = AnchorGrid(a => _session.SetTextAnchor(id, a), out var anchorButtons);
        var palettes = PaletteRow(id, out var paletteButtons);
        _content.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.Get("recorder.textContentLabel"), Classes = { "sliderTitle" } }, editor } });
        _content.Children.Add(size);
        _content.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.Get("recorder.textPositionLabel"), Classes = { "sliderTitle" } }, anchors } });
        _content.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.Get("recorder.textColorLabel"), Classes = { "sliderTitle" } }, palettes } });
        _content.Children.Add(RemoveButton(() => _session.Remove(LaneKind.Text, id)));
        _updaters.Add(() =>
        {
            if (_session.SelectedText is not { } current)
            {
                return;
            }

            size.Update(current.Size);
            UpdateAnchors(anchorButtons, current.Anchor);
            UpdatePalettes(paletteButtons, current.Palette);
        });
    }

    private void BuildImage(ImageOverlay image)
    {
        AddHeader("recorder.thisImageLabel");
        var id = image.Id;
        var name = new TextBlock { Text = Path.GetFileName(image.Path), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "caption" } };
        var size = new ValueSlider(L.Get("recorder.imageSizeLabel"), ImageOverlay.MinSize, ImageOverlay.MaxSize, 0.01, image.Size, ImageOverlay.DefaultSize, Percent,
            _session.BeginInteraction, v => _session.SetImageSize(id, v), _session.CommitInteraction);
        var opacity = new ValueSlider(L.Get("recorder.imageOpacityLabel"), ImageOverlay.MinOpacity, 1, 0.01, image.Opacity, 1, Percent,
            _session.BeginInteraction, v => _session.SetImageOpacity(id, v), _session.CommitInteraction);
        var anchors = AnchorGrid(a => _session.SetImageAnchor(id, a), out var anchorButtons);
        _content.Children.Add(name);
        _content.Children.Add(size);
        _content.Children.Add(opacity);
        _content.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L.Get("recorder.imagePositionLabel"), Classes = { "sliderTitle" } }, anchors } });
        _content.Children.Add(RemoveButton(() => _session.Remove(LaneKind.Image, id)));
        _updaters.Add(() =>
        {
            if (_session.SelectedImage is not { } current)
            {
                return;
            }

            size.Update(current.Size);
            opacity.Update(current.Opacity);
            UpdateAnchors(anchorButtons, current.Anchor);
        });
    }

    private void BuildBlur(BlurRegion blur)
    {
        AddHeader("recorder.thisBlurLabel");
        var id = blur.Id;
        var choose = EditorUi.Button(L.Get("recorder.blurPickArea"), "Crop", null, _session.StartDrawingBlur, "primary");
        choose.HorizontalAlignment = HorizontalAlignment.Stretch;
        choose.HorizontalContentAlignment = HorizontalAlignment.Center;
        var caption = EditorUi.Caption(string.Empty);
        var strength = new ValueSlider(L.Get("screenshot.blurStrengthLabel"), 1, 5, 1, blur.Strength, BlurRegion.DefaultStrength,
            v => ((int)Math.Round(v)).ToString(CultureInfo.CurrentUICulture), _session.BeginInteraction, v => _session.SetBlurStrength(id, (int)Math.Round(v)), _session.CommitInteraction);
        _content.Children.Add(new StackPanel { Spacing = 8, Children = { choose, caption } });
        _content.Children.Add(strength);
        _content.Children.Add(RemoveButton(() => _session.Remove(LaneKind.Blur, id)));
        _updaters.Add(() =>
        {
            if (_session.SelectedBlur is not { } current)
            {
                return;
            }

            choose.IsEnabled = !_session.IsDrawingBlur;
            caption.Text = L.Get(_session.IsDrawingBlur ? "recorder.blurPickAreaHint" : "recorder.blurCaption");
            strength.Update(current.Strength);
        });
    }

    // ── Shared pieces ─────────────────────────────────────────────────

    private static readonly OverlayAnchor[] AnchorOrder =
    [
        OverlayAnchor.TopLeading, OverlayAnchor.Top, OverlayAnchor.TopTrailing,
        OverlayAnchor.Leading, OverlayAnchor.Center, OverlayAnchor.Trailing,
        OverlayAnchor.BottomLeading, OverlayAnchor.Bottom, OverlayAnchor.BottomTrailing,
    ];

    private Control AnchorGrid(Action<OverlayAnchor> picked, out Button[] buttons)
    {
        var grid = new UniformGrid { Columns = 3, Rows = 3 };
        var list = new Button[9];
        for (var i = 0; i < 9; i++)
        {
            var anchor = AnchorOrder[i];
            var cell = new Button
            {
                Height = 20,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(cell, anchor.ToString());
            cell.Click += (_, _) => picked(anchor);
            grid.Children.Add(cell);
            list[i] = cell;
        }

        buttons = list;
        return grid;
    }

    private void UpdateAnchors(Button[] buttons, OverlayAnchor selected)
    {
        var palette = EditorPalette.For(this);
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = AnchorOrder[i] == selected
                ? new SolidColorBrush(EditorPalette.WithAlpha(palette.Accent, 0.85))
                : palette.InkBrush(0.08);
        }
    }

    private static readonly CaptionPalette[] PaletteOrder =
        [CaptionPalette.White, CaptionPalette.Black, CaptionPalette.Accent, CaptionPalette.Yellow, CaptionPalette.Red, CaptionPalette.Green];

    private Control PaletteRow(string id, out Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var list = new Button[PaletteOrder.Length];
        for (var i = 0; i < PaletteOrder.Length; i++)
        {
            var palette = PaletteOrder[i];
            var color = EditNames.Color(palette);
            var dot = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(BackgroundPicker.ToColor(color)),
            };
            AutomationProperties.SetName(dot, palette.ToString());
            dot.Click += (_, _) => _session.SetTextPalette(id, palette);
            row.Children.Add(dot);
            list[i] = dot;
        }

        buttons = list;
        return row;
    }

    private void UpdatePalettes(Button[] buttons, CaptionPalette selected)
    {
        var accent = EditorPalette.For(this).Accent;
        for (var i = 0; i < buttons.Length; i++)
        {
            var isSelected = PaletteOrder[i] == selected;
            buttons[i].BorderThickness = new Thickness(isSelected ? 2.5 : 1);
            buttons[i].BorderBrush = isSelected ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        }
    }

    private static string Percent(double v) => (v * 100).ToString("0", CultureInfo.CurrentUICulture) + "%";

    /// <summary>Esc while a context panel shows: back to all options.</summary>
    public bool HandleEscape()
    {
        if (_session.SelectedKind is null)
        {
            return false;
        }

        _session.ClearSelection();
        return true;
    }
}
