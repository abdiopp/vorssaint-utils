// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using Rivet.App.Controls;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Localization;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Capture.Selector;

/// <summary>Everything the hint bar shows; it rebuilds only when this changes.</summary>
internal sealed record HintState(
    IReadOnlyList<CaptureTool> Tools,
    CaptureTool Tool,
    SelectorMode Mode,
    bool ShowPalette,
    bool Standalone,
    bool RegionOnly,
    string? Purpose,
    bool Scrolling,
    bool LoupeOn,
    bool RepeatAvailable,
    bool WindowClicks)
{
    public bool Equals(HintState? other) =>
        other is not null && Tools.SequenceEqual(other.Tools) && Tool == other.Tool && Mode == other.Mode && ShowPalette == other.ShowPalette
        && Standalone == other.Standalone && RegionOnly == other.RegionOnly && Purpose == other.Purpose && Scrolling == other.Scrolling
        && LoupeOn == other.LoupeOn && RepeatAvailable == other.RepeatAvailable && WindowClicks == other.WindowClicks;

    public override int GetHashCode() => HashCode.Combine(Tool, Mode, ShowPalette, Scrolling, LoupeOn, RepeatAvailable, WindowClicks, Standalone);
}

/// <summary>
/// The chooser's hint bar (spec 01 §3.4.3): the 1–4 mode palette, a capsule
/// with the tool's subtitle and key chips, and the recording audio row whose
/// space is always reserved so the bar never changes height.
/// </summary>
internal sealed class SelectorHintBar : UserControl
{
    public const double WidthMax = 620;
    public const double StandaloneWidth = 680;

    /// <summary>Four buttons, the Esc chip and spacing fill the 620-DIP bar.</summary>
    private const double ToolButtonWidth = 132;

    private readonly ISettingsStore _settings;
    private HintState? _state;

    public SelectorHintBar(ISettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Raised when a palette button is clicked.</summary>
    public event EventHandler<CaptureTool>? ToolRequested;

    /// <summary>Raised when the Esc hint next to the palette is clicked.</summary>
    public event EventHandler? CancelRequested;

    public static double HeightFor(HintState state) => state.Standalone ? 72 : state.ShowPalette && state.Tools.Count > 1 ? 146 : 82;

    public static double WidthFor(HintState state, double displayWidthDip) =>
        state.Standalone ? Math.Min(StandaloneWidth, displayWidthDip - 32) : Math.Min(WidthMax, Math.Max(280, displayWidthDip - 32));

    public static string ToolTitle(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => L.Get("recorder.pageTitle"),
        CaptureTool.Text => L.Get("Strings.ocrName"),
        CaptureTool.Color => L.Get("Strings.colorPickerName"),
        _ => L.Get("screenshot.pageTitle"),
    };

    public static string ToolIcon(CaptureTool tool) => tool switch
    {
        CaptureTool.Recording => "Record",
        CaptureTool.Text => "ScanText",
        CaptureTool.Color => "Eyedropper",
        _ => "Screenshot",
    };

    public static string Subtitle(HintState state)
    {
        const string dot = "  ·  ";
        if (state.Standalone)
        {
            if (state.RegionOnly || state.Scrolling)
            {
                return L.Get("screenshot.scrollingCaptureSelectionHint");
            }

            return state.Purpose is null ? L.Get("screenshot.hintClick") : L.Get("screenshot.hintDrag") + dot + L.Get("screenshot.hintClick");
        }

        return state.Tool switch
        {
            CaptureTool.Recording => L.Get("recorder.selectionPurpose") + dot + L.Get("screenshot.hintDrag") + dot + L.Get("screenshot.hintClick"),
            CaptureTool.Text => L.Get("Strings.ocrCaption"),
            CaptureTool.Color => L.Get("Strings.colorPickerCaption"),
            _ when state.Scrolling => L.Get("screenshot.scrollingCaptureSelectionHint") + dot + L.Get("screenshot.scrollingCaptureHintOn"),
            _ => L.Get("screenshot.hintDrag") + dot + L.Get("screenshot.hintClick") + dot + L.Get("screenshot.scrollingCaptureHintOff"),
        };
    }

    public void Update(HintState state)
    {
        if (Equals(state, _state))
        {
            return;
        }

        _state = state;
        Content = state.Standalone ? BuildStandalone(state) : BuildUnified(state);
    }

    private Control BuildUnified(HintState state)
    {
        var rows = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var palette = state.ShowPalette && state.Tools.Count > 1;
        if (palette)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            for (var i = 0; i < 4; i++)
            {
                var tool = (CaptureTool)i;
                if (state.Tools.Contains(tool))
                {
                    buttons.Children.Add(ToolButton(tool, i + 1, tool == state.Tool));
                }
            }

            var paletteRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { Plate(buttons, 14, new Thickness(4)), EscButton() },
            };
            rows.Children.Add(paletteRow);
        }

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foreach (var chip in Chips(state))
        {
            chips.Children.Add(chip);
        }

        if (!palette)
        {
            chips.Children.Add(EscChip(null));
        }

        var subtitle = new TextBlock
        {
            Text = Subtitle(state),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        subtitle.Bind(TextBlock.ForegroundProperty, subtitle.GetResourceObservable("TextPrimaryBrush").ToBinding());
        ToolTip.SetTip(subtitle, subtitle.Text);
        var capsuleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        capsuleGrid.Children.Add(subtitle);
        Grid.SetColumn(chips, 1);
        capsuleGrid.Children.Add(chips);
        var capsule = Plate(capsuleGrid, 15, new Thickness(12, 0));
        capsule.Height = 30;
        rows.Children.Add(capsule);

        // The audio row's space is always reserved; only Recording shows it.
        var audio = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Height = 28 };
        audio.Children.Add(AudioToggle(CaptureSettings.RecorderSystemAudio, "Speaker2", L.Get("win.capture.systemAudio")));
        audio.Children.Add(AudioToggle(CaptureSettings.RecorderMicrophone, "Mic", L.Get("recorder.microphoneTrackLabel")));
        var audioPlate = Plate(audio, 14, new Thickness(4, 0));
        audioPlate.HorizontalAlignment = HorizontalAlignment.Center;
        audioPlate.Opacity = state.Tool == CaptureTool.Recording ? 1 : 0;
        audioPlate.IsHitTestVisible = state.Tool == CaptureTool.Recording;
        rows.Children.Add(audioPlate);
        return rows;
    }

    private Control BuildStandalone(HintState state)
    {
        var icon = new SymbolIcon { Symbol = FluentIcons.Common.Symbol.ScanDash, FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush").ToBinding());
        var title = new TextBlock { Text = state.Purpose ?? L.Get("screenshot.hintDrag"), FontWeight = FontWeight.SemiBold, FontSize = 14 };
        var subtitle = new TextBlock { Text = Subtitle(state), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.Bind(TextBlock.ForegroundProperty, subtitle.GetResourceObservable("TextSecondaryBrush").ToBinding());
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foreach (var chip in Chips(state))
        {
            chips.Children.Add(chip);
        }

        chips.Children.Add(EscChip(null));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);
        var plate = Plate(grid, 16, new Thickness(16, 0));
        plate.Height = 72;
        plate.IsHitTestVisible = false; // clicks pass through to the overlay
        return plate;
    }

    private IEnumerable<Control> Chips(HintState state)
    {
        // The palette buttons already show their digits; the chip reminds of them when the palette is hidden.
        if (state.Tools.Count > 1 && !state.Standalone && !(state.ShowPalette && state.Tools.Count > 1))
        {
            yield return CaptureUi.KeyChip($"1–{state.Tools.Count}", "Keyboard");
        }

        if (state.Mode == SelectorMode.Color)
        {
            yield break;
        }

        if (state.WindowClicks)
        {
            yield return CaptureUi.KeyChip(L.Get("win.capture.keyEnter"));
        }

        if (state.Tool == CaptureTool.Screenshot && state.Mode == SelectorMode.Image && !state.Standalone)
        {
            yield return CaptureUi.KeyChip(state.Scrolling ? L.Format("win.capture.keyOnFormat", "S") : "S", active: state.Scrolling);
        }

        yield return CaptureUi.KeyChip(state.LoupeOn ? L.Format("win.capture.keyOnFormat", "Z") : "Z", active: state.LoupeOn);
        if (state.LoupeOn)
        {
            yield return CaptureUi.KeyChip("C");
        }

        if (state.RepeatAvailable)
        {
            yield return CaptureUi.KeyChip("R");
        }
    }

    private Control ToolButton(CaptureTool tool, int digit, bool selected)
    {
        var badge = new Border
        {
            Width = 19,
            Height = 19,
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = digit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (selected)
        {
            badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("AccentBrush").ToBinding());
            ((TextBlock)badge.Child).Foreground = Brushes.White;
        }
        else
        {
            badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("ChipBrush").ToBinding());
        }

        var icon = new SymbolIcon { Symbol = IconConverter.Parse(ToolIcon(tool)), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        if (selected)
        {
            icon.Bind(SymbolIcon.ForegroundProperty, icon.GetResourceObservable("AccentBrush").ToBinding());
        }

        var title = new TextBlock
        {
            Text = ToolTitle(tool),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 13,
        };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
        content.Children.Add(badge);
        Grid.SetColumn(icon, 1);
        content.Children.Add(icon);
        Grid.SetColumn(title, 2);
        content.Children.Add(title);

        var button = new Button
        {
            Focusable = false,
            Width = ToolButtonWidth,
            Height = 48,
            Padding = new Thickness(7, 4),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = content,
        };
        if (selected)
        {
            button.Bind(Button.BackgroundProperty, button.GetResourceObservable("AccentSoftBrush").ToBinding());
            button.Bind(Button.BorderBrushProperty, button.GetResourceObservable("AccentBrush").ToBinding());
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
        }

        var tip = L.Format("win.capture.withShortcutFormat", ToolTitle(tool), digit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => ToolRequested?.Invoke(this, tool);
        return button;
    }

    private Control AudioToggle(Setting<bool> setting, string icon, string label)
    {
        var toggle = new ToggleButton
        {
            Focusable = false,
            IsChecked = _settings.Get(setting),
            Padding = new Thickness(10, 3),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new SymbolIcon { Symbol = IconConverter.Parse(icon), FontSize = 14 },
                    new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(toggle, label);
        toggle.IsCheckedChanged += (_, _) => _settings.Set(setting, toggle.IsChecked == true);
        return toggle;
    }

    private static Border EscChip(double? height)
    {
        var chip = CaptureUi.KeyChip(L.Get("win.capture.keyEsc"), "Dismiss");
        ToolTip.SetTip(chip, L.Get("screenshot.hintCancel"));
        return chip;
    }

    /// <summary>The tall "× Esc" next to the palette; it looks like a button, so it acts as one.</summary>
    private Button EscButton()
    {
        var button = new Button
        {
            Focusable = false,
            Height = 56,
            MinWidth = 56,
            Padding = new Thickness(10, 0),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new SymbolIcon { Symbol = FluentIcons.Common.Symbol.Dismiss, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = L.Get("win.capture.keyEsc"), FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        button.Bind(Button.BackgroundProperty, button.GetResourceObservable("HudBackgroundBrush").ToBinding());
        button.Bind(Button.BorderBrushProperty, button.GetResourceObservable("PanelBorderBrush").ToBinding());
        ToolTip.SetTip(button, L.Get("screenshot.hintCancel"));
        AutomationProperties.SetName(button, L.Get("screenshot.hintCancel"));
        button.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    /// <summary>
    /// A chrome plate: the overlay is opaque when frozen, so a live blur would
    /// show the wrong pixels; an opaque contrast plate keeps text readable.
    /// </summary>
    private static Border Plate(Control child, double radius, Thickness padding)
    {
        var plate = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Padding = padding,
            BorderThickness = new Thickness(1),
            Child = child,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(70, 0, 0, 0) }),
        };
        plate.Bind(Border.BackgroundProperty, plate.GetResourceObservable("HudBackgroundBrush").ToBinding());
        plate.Bind(Border.BorderBrushProperty, plate.GetResourceObservable("PanelBorderBrush").ToBinding());
        return plate;
    }
}
