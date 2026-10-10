// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.App.Features.Capture.Hud;
using Rivet.App.Features.Capture.Output;
using Rivet.Core.Capture;
using Rivet.Core.Settings;

namespace Rivet.App.Features.Capture.ColorPicking;

/// <summary>
/// The colour picker's output (spec 01 §3.18): the picked pixel formatted
/// as HEX, RGB, HSL or C#, copied as text, with a swatch toast.
/// </summary>
internal sealed class ColorPickService(ISettingsStore settings, CaptureOutputService output)
{
    private ColorToastWindow? _toast;

    public ColorFormat Format => ColorFormatter.Parse(settings.Get(CaptureSettings.ColorPickerFormat));

    public string Formatted(uint argb) => ColorFormatter.Format(argb, Format, settings.Get(CaptureSettings.ColorPickerBareHex));

    /// <summary>Copies the colour; with <paramref name="toast"/> the swatch toast confirms it.</summary>
    public bool Copy(uint argb, bool toast = true)
    {
        var value = Formatted(argb);
        if (!output.CopyText(value))
        {
            return false;
        }

        if (toast)
        {
            _toast ??= new ColorToastWindow();
            _toast.Show(argb, value);
        }

        return true;
    }
}
