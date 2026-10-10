// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Recording.Engine;

/// <summary>
/// Screen recorder preferences that belong to the capture side. Keys match
/// the macOS app where the setting exists there (spec 02 §4); the editor's
/// own keys (quality, automatic zooms, GIF options, presets, links) belong to
/// the recording editor module.
/// </summary>
public static class RecorderSettings
{
    /// <summary>The dedicated "Screen recording" shortcut is registered (off by default, as on macOS).</summary>
    public static readonly Setting<bool> ShortcutEnabled = new("recorderShortcutEnabled", false);

    /// <summary>Chord of the <c>screenRecorder</c> shortcut role; empty means the default (Ctrl+Alt+Win+5).</summary>
    public static readonly Setting<string> Shortcut = new("recorderShortcut", string.Empty, Sanitize.MaxLength(64));

    /// <summary>Show the 1–4 tool palette when the chooser is opened from the shortcut.</summary>
    public static readonly Setting<bool> ShowCaptureMenuOnShortcut = new("recorderShowCaptureMenuOnShortcut", true);

    /// <summary>Seconds before capture starts: 0, 3, 5 or 10 (anything else counts as 0).</summary>
    public static readonly Setting<int> Countdown = new("recorderCountdown", 3, v => v is 0 or 3 or 5 or 10 ? v : 0);

    /// <summary>Maximum capture frame rate: 30 or 60 (anything else counts as 60).</summary>
    public static readonly Setting<int> FrameRate = new("recorderFrameRate", 60, v => v is 30 or 60 ? v : 60);

    /// <summary>Record what the PC plays (WASAPI loopback) on its own track.</summary>
    public static readonly Setting<bool> SystemAudio = new("recorderSystemAudio", true);

    /// <summary>Record the microphone on its own track.</summary>
    public static readonly Setting<bool> Microphone = new("recorderMicrophone", false);

    /// <summary>
    /// Windows only: the capture endpoint id to record; empty follows the
    /// Windows default input (and its changes). Endpoint ids are per PC.
    /// </summary>
    public static readonly Setting<string> MicrophoneDevice = new("recorderMicrophoneDevice", string.Empty, Sanitize.MaxLength(512), machineState: true);

    /// <summary>Open the recording editor after a normal stop; otherwise save the file straight away.</summary>
    public static readonly Setting<bool> OpenEditor = new("recorderOpenEditor", true);

    /// <summary>Folder for recordings saved straight away; empty means the Videos folder. Machine-local.</summary>
    public static readonly Setting<string> SaveFolder = new("recorderSaveFolder", string.Empty, Sanitize.MaxLength(1024), machineState: true);

    /// <summary>Windows only: show the floating recording controls (the tray icon and shortcut still stop).</summary>
    public static readonly Setting<bool> ShowIndicator = new("recorderShowIndicator", true);

    /// <summary>Allowed countdown values, in menu order.</summary>
    public static IReadOnlyList<int> CountdownChoices { get; } = [0, 3, 5, 10];

    /// <summary>Allowed frame rates, in menu order.</summary>
    public static IReadOnlyList<int> FrameRateChoices { get; } = [30, 60];
}
