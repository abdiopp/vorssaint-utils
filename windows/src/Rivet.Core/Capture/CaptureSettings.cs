// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Capture;

/// <summary>After-capture default action (<c>screenshotDefaultAction</c>).</summary>
public enum ScreenshotDefaultAction
{
    /// <summary>Stored as an empty string: no automatic action, the preview asks.</summary>
    Ask,
    Save,
    SaveAndCopy,
    Copy,
    Edit,
}

/// <summary>Where the quick preview appears (<c>screenshotPreviewPosition</c>).</summary>
public enum PreviewPosition
{
    Automatic,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Settings of the capture tools (spec 01 §4.1). Keys match the macOS app;
/// shortcut values use the Windows <see cref="Shortcuts.KeyChord"/> storage
/// format (empty = the role's default), so macOS values are not portable.
/// </summary>
public static class CaptureSettings
{
    // ── Global shortcuts ────────────────────────────────────────────────
    public static readonly Setting<bool> ScreenshotShortcutEnabled = new("screenshotShortcutEnabled", false);
    public static readonly Setting<string> ScreenshotShortcut = new("screenshotShortcut", string.Empty);
    public static readonly Setting<bool> ScreenshotShowCaptureMenu = new("screenshotShowCaptureMenuOnShortcut", true);

    public static readonly Setting<bool> ScreenOcrShortcutEnabled = new("screenOCRShortcutEnabled", false);
    public static readonly Setting<string> ScreenOcrShortcut = new("screenOCRShortcut", string.Empty);
    public static readonly Setting<bool> ScreenOcrShowCaptureMenu = new("screenOCRShowCaptureMenuOnShortcut", true);

    public static readonly Setting<bool> ColorPickerShortcutEnabled = new("colorPickerShortcutEnabled", false);
    public static readonly Setting<string> ColorPickerShortcut = new("colorPickerShortcut", string.Empty);
    public static readonly Setting<bool> ColorPickerShowCaptureMenu = new("colorPickerShowCaptureMenuOnShortcut", true);

    public static readonly Setting<bool> FullScreenShortcutEnabled = new("screenshotFullScreenShortcutEnabled", false);
    public static readonly Setting<string> FullScreenShortcut = new("screenshotFullScreenShortcut", string.Empty);

    public static readonly Setting<bool> LastCaptureShortcutEnabled = new("screenshotLastCaptureShortcutEnabled", false);
    public static readonly Setting<string> LastCaptureShortcut = new("screenshotLastCaptureShortcut", string.Empty);

    public static readonly Setting<bool> ClipboardShortcutEnabled = new("screenshotClipboardShortcutEnabled", false);
    public static readonly Setting<string> ClipboardShortcut = new("screenshotClipboardShortcut", string.Empty);

    public static readonly Setting<bool> RecentCapturesShortcutEnabled = new("recentCapturesShortcutEnabled", false);
    public static readonly Setting<string> RecentCapturesShortcut = new("recentCapturesShortcut", string.Empty);

    /// <summary>Windows only: Print Screen opens the capture (taken over from the Snipping Tool with the shared hook).</summary>
    public static readonly Setting<bool> PrintScreenEnabled = new("screenshotPrintScreenEnabled", false);
    public static readonly Setting<string> PrintScreenShortcut = new("screenshotPrintScreenShortcut", string.Empty);

    /// <summary>The recorder's own "show capture menu" switch (read when the recorder opens the chooser).</summary>
    public static readonly Setting<bool> RecorderShowCaptureMenu = new("recorderShowCaptureMenuOnShortcut", true);

    /// <summary>Recorder audio switches, written by the chooser's audio row (owned by the recorder; same keys and defaults).</summary>
    public static readonly Setting<bool> RecorderSystemAudio = new("recorderSystemAudio", true);
    public static readonly Setting<bool> RecorderMicrophone = new("recorderMicrophone", false);

    // ── Selection ───────────────────────────────────────────────────────
    public static readonly Setting<bool> Freeze = new("screenshotFreeze", true);

    /// <summary>Keeps every window of this app out of captures and window picking (macOS key name kept).</summary>
    public static readonly Setting<bool> HideOwnWindows = new("screenshotHideVorssaintWindows", true);

    /// <summary>Countdown before the Screenshot chooser or a full-screen capture: 0, 3, 5 or 10 s (anything else means 0).</summary>
    public static readonly Setting<int> Delay = new("screenshotDelay", 0, Sanitize.OneOf(0, 0, 3, 5, 10));

    public static readonly Setting<bool> IncludePointer = new("screenshotIncludePointer", false);
    public static readonly Setting<bool> ShowLastRegion = new("screenshotShowLastRegion", true);

    public static readonly Setting<bool> LoupeStartsOn = new("screenshotLoupeStartsOn", false);
    public static readonly Setting<bool> LoupeRememberZoom = new("screenshotLoupeRememberZoom", false);
    public static readonly Setting<double> LoupeDefaultZoom = new("screenshotLoupeDefaultZoom", 1.0, LoupeMath.SanitizeZoom);
    public static readonly Setting<double> LoupeLastZoom = new("screenshotLoupeLastZoom", 1.0, LoupeMath.SanitizeZoom, machineState: true);

    /// <summary>false = Fast, true = Step by step (Alt swaps while held).</summary>
    public static readonly Setting<bool> LoupeSteppedZoomByDefault = new("screenshotLoupeSteppedZoomByDefault", false);

    // ── After capture ───────────────────────────────────────────────────
    public static readonly Setting<string> DefaultAction =
        new("screenshotDefaultAction", string.Empty, Sanitize.OneOfStrings(string.Empty, string.Empty, "save", "saveAndCopy", "copy", "edit"));

    public static readonly Setting<bool> PreviewEnabled = new("screenshotPreviewEnabled", true);

    /// <summary>Confirmation duration in seconds; 0 = until dismissed; invalid values mean 3.</summary>
    public static readonly Setting<int> PreviewDuration = new("screenshotPreviewDuration", 3, Sanitize.OneOf(3, 1, 2, 3, 5, 10, 0));

    public static readonly Setting<string> PreviewPositionValue =
        new("screenshotPreviewPosition", string.Empty, Sanitize.OneOfStrings(string.Empty, string.Empty, "topLeft", "topRight", "bottomLeft", "bottomRight"));

    public static readonly Setting<bool> PreviewTakesFocus = new("screenshotPreviewTakesFocus", true);

    public static readonly Setting<bool> CopyToClipboard = new("screenshotCopyToClipboard", false);
    public static readonly Setting<bool> AddToShelf = new("screenshotAddToShelf", false);

    // ── Output ──────────────────────────────────────────────────────────
    /// <summary>Absolute folder (or "~\…"); empty = the Windows Screenshots folder. Machine-local.</summary>
    public static readonly Setting<string> SaveFolder = new("screenshotSaveFolder", string.Empty, machineState: true);

    public static readonly Setting<string> SaveSubfolder = new("screenshotSaveSubfolder", string.Empty, Sanitize.MaxLength(200));
    public static readonly Setting<string> FileNamePattern = new("screenshotFileNamePattern", string.Empty, Sanitize.MaxLength(200));
    public static readonly Setting<int> FileNumberStart = new("screenshotFileNumberStart", 1, Sanitize.Clamp(0, 999_999));
    public static readonly Setting<int> FileNumberNext = new("screenshotFileNumberNext", 1, v => Math.Max(0, v));

    /// <summary>"Save at 1x size": high-DPI captures are scaled to their 100 % size on export.</summary>
    public static readonly Setting<bool> Downscale = new("screenshotDownscale", false);

    // ── Copy text from screen ───────────────────────────────────────────
    public static readonly Setting<bool> OcrRemoveLineBreaks = new("screenOCRRemoveLineBreaks", false);
    public static readonly Setting<bool> OcrDetectQrCodes = new("screenOCRDetectQRCodes", true);

    // ── Colour picker ───────────────────────────────────────────────────
    /// <summary>hex, rgb, hsl or csharp. The macOS "swiftui" value maps to its Windows counterpart, C#.</summary>
    public static readonly Setting<string> ColorPickerFormat = new("colorPickerFormat", "hex", ColorFormatter.SanitizeFormatKey);
    public static readonly Setting<bool> ColorPickerBareHex = new("colorPickerBareHex", false);

    public static ScreenshotDefaultAction ParseDefaultAction(string value) => value switch
    {
        "save" => ScreenshotDefaultAction.Save,
        "saveAndCopy" => ScreenshotDefaultAction.SaveAndCopy,
        "copy" => ScreenshotDefaultAction.Copy,
        "edit" => ScreenshotDefaultAction.Edit,
        _ => ScreenshotDefaultAction.Ask,
    };

    public static string ToStorage(ScreenshotDefaultAction action) => action switch
    {
        ScreenshotDefaultAction.Save => "save",
        ScreenshotDefaultAction.SaveAndCopy => "saveAndCopy",
        ScreenshotDefaultAction.Copy => "copy",
        ScreenshotDefaultAction.Edit => "edit",
        _ => string.Empty,
    };

    public static PreviewPosition ParsePreviewPosition(string value) => value switch
    {
        "topLeft" => PreviewPosition.TopLeft,
        "topRight" => PreviewPosition.TopRight,
        "bottomLeft" => PreviewPosition.BottomLeft,
        "bottomRight" => PreviewPosition.BottomRight,
        _ => PreviewPosition.Automatic,
    };

    public static string ToStorage(PreviewPosition position) => position switch
    {
        PreviewPosition.TopLeft => "topLeft",
        PreviewPosition.TopRight => "topRight",
        PreviewPosition.BottomLeft => "bottomLeft",
        PreviewPosition.BottomRight => "bottomRight",
        _ => string.Empty,
    };

    /// <summary>
    /// Turning automatic copy off also strips the copy half from the default
    /// action (Copy becomes Ask, Save &amp; Copy becomes Save), spec §4.0.
    /// </summary>
    public static ScreenshotDefaultAction WithoutCopy(ScreenshotDefaultAction action) => action switch
    {
        ScreenshotDefaultAction.Copy => ScreenshotDefaultAction.Ask,
        ScreenshotDefaultAction.SaveAndCopy => ScreenshotDefaultAction.Save,
        _ => action,
    };
}
