// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Capture;
using Rivet.Core.Localization;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Capture.Settings;

/// <summary>Settings → Copy text from screen (spec 01 §3.16), with the Windows OCR language status.</summary>
internal sealed class ScreenOcrSettingsPage : CaptureSettingsPage
{
    public ScreenOcrSettingsPage(IServiceProvider services)
        : base(services)
    {
        var showMenu = GatedToggle(CaptureSettings.ScreenOcrShowCaptureMenu, CaptureSettings.ScreenOcrShortcutEnabled, null, "screenshot.showCaptureMenuOnShortcut");
        Content = Stack(
            Header("Strings.ocrName", "Strings.ocrCaption"),
            Card(null,
                ActionsRow("ScanText", L.Get("Strings.ocrName"), L.Get("Strings.ocrCaption"),
                    (L.Get("Strings.ocrName"), CaptureModule.OcrActionId, "ScanText", true))),
            Card("win.capture.sectionShortcuts",
                ShortcutRow(CaptureModule.OcrRoleId, CaptureSettings.ScreenOcrShortcutEnabled),
                Indented(showMenu)),
            Card(null,
                Toggle(CaptureSettings.OcrRemoveLineBreaks, "TextWrap", "Strings.ocrRemoveLineBreaksToggle", "Strings.ocrRemoveLineBreaksCaption"),
                Toggle(CaptureSettings.OcrDetectQrCodes, "QrCode", "Strings.ocrQRToggle", "Strings.ocrQRCaption")),
            LanguagesCard());
    }

    private Control LanguagesCard()
    {
        var status = Services.GetRequiredService<IOcrEngine>().GetStatus();
        var shell = Services.GetRequiredService<IShellService>();
        var open = ActionButton(L.Get("win.capture.openLanguageSettings"), () => shell.OpenSystemSettings("ms-settings:regionlanguage"), "LocalLanguage");
        if (status.IsAvailable)
        {
            var languages = string.Join(", ", status.LanguageNames);
            return Card("win.capture.ocrStatusTitle",
                Row("LocalLanguage", L.Format("win.capture.ocrLanguagesFormat", languages), L.Get("win.capture.ocrLanguagesCaption"), open));
        }

        var warning = Note(L.Get("win.capture.ocrUnavailableBody"), "WarningBrush");
        return Card("win.capture.ocrStatusTitle",
            Row("Warning", L.Get("win.capture.ocrUnavailableTitle"), null, open),
            Indented(warning));
    }
}

/// <summary>Settings → Color picker (spec 01 §3.18): the copied format with a live example.</summary>
internal sealed class ColorPickerSettingsPage : CaptureSettingsPage
{
    /// <summary>Dodger blue, the spec's example colour.</summary>
    private const uint ExampleColor = 0xFF1E90FF;

    public ColorPickerSettingsPage(IServiceProvider services)
        : base(services)
    {
        var showMenu = GatedToggle(CaptureSettings.ColorPickerShowCaptureMenu, CaptureSettings.ColorPickerShortcutEnabled, null, "screenshot.showCaptureMenuOnShortcut");
        var bareHex = Toggle(CaptureSettings.ColorPickerBareHex, null, "Strings.colorPickerBareHexToggle");
        var bareIndented = Indented(bareHex);
        When(() => bareIndented.IsVisible = ColorFormatter.Parse(Settings.Get(CaptureSettings.ColorPickerFormat)) == ColorFormat.Hex, CaptureSettings.ColorPickerFormat);
        Content = Stack(
            Header("Strings.colorPickerName", "Strings.colorPickerCaption"),
            Card(null,
                ActionsRow("Eyedropper", L.Get("Strings.colorPickerName"), L.Get("Strings.colorPickerCaption"),
                    (L.Get("Strings.colorPickerPickNow"), CaptureModule.ColorActionId, "Eyedropper", true))),
            Card("win.capture.sectionShortcuts",
                ShortcutRow(CaptureModule.ColorRoleId, CaptureSettings.ColorPickerShortcutEnabled),
                Indented(showMenu)),
            LiveCard(null, FormatRow(), bareIndented));
    }

    public static string FormatLabel(ColorFormat format) => format switch
    {
        ColorFormat.Rgb => "RGB",
        ColorFormat.Hsl => "HSL",
        ColorFormat.CSharp => L.Get("win.capture.colorFormatCSharp"),
        _ => "HEX",
    };

    private Control FormatRow()
    {
        var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var example = new TextBlock { Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Avalonia.Media.Color.FromUInt32(ExampleColor)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var format in Enum.GetValues<ColorFormat>())
        {
            var radio = new RadioButton { Content = FormatLabel(format), GroupName = "colorPickerFormat", Margin = new Thickness(0, 0, 8, 0) };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    Settings.Set(CaptureSettings.ColorPickerFormat, ColorFormatter.ToKey(format));
                }
            };
            AutomationProperties.SetName(radio, FormatLabel(format));
            radios.Children.Add(radio);
        }

        When(() =>
        {
            var current = ColorFormatter.Parse(Settings.Get(CaptureSettings.ColorPickerFormat));
            var values = Enum.GetValues<ColorFormat>();
            for (var i = 0; i < values.Length; i++)
            {
                ((RadioButton)radios.Children[i]).IsChecked = values[i] == current;
            }

            example.Text = ColorFormatter.Format(ExampleColor, current, Settings.Get(CaptureSettings.ColorPickerBareHex));
        }, CaptureSettings.ColorPickerFormat, CaptureSettings.ColorPickerBareHex);

        var exampleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { swatch, example } };
        var row = Row("Color", L.Get("Strings.colorPickerFormatLabel"), null, radios);
        return new StackPanel { Spacing = 4, Children = { row, Indented(exampleRow) } };
    }
}
