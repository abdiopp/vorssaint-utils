// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Contracts;

namespace Rivet.Core.Recording.Engine;

public enum ScreenRecorderState
{
    Idle,

    /// <summary>Choosing what to record, checking the microphone or counting down.</summary>
    Preparing,

    /// <summary>The take is being opened; the indicator is already visible.</summary>
    Starting,

    Recording,

    Paused,

    /// <summary>Stopping and writing the take; new recordings are refused until it is done.</summary>
    Finishing,
}

/// <summary>
/// The screen recorder as other modules see it (the capture chooser blocks
/// while a recording runs and hands Recording selections over; the command
/// bar shows "Stop recording"). Implemented by the recording module; resolve
/// with <c>GetService</c> and cope with it being absent.
/// </summary>
public interface IScreenRecorder
{
    ScreenRecorderState State { get; }

    /// <summary>Anything but idle: a toggle would stop or cancel.</summary>
    bool IsBusy { get; }

    /// <summary>Recording seconds (pauses removed); 0 when not recording.</summary>
    double ElapsedSeconds { get; }

    /// <summary>Raised on the UI thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>The one toggle every entry point uses (spec 02 §3.1): start, stop, or cancel a countdown.</summary>
    Task ToggleAsync(bool fromShortcut = false);

    /// <summary>
    /// Records a selection made in a chooser another tool opened (the user
    /// switched to Recording there). Runs the pre-flight checks, the
    /// microphone check and the countdown as usual.
    /// </summary>
    Task RecordSelectionAsync(CaptureSelection selection);

    /// <summary>Stops a running recording (delivers it); cancels a pending start.</summary>
    void Stop();

    /// <summary>Pauses or resumes; false when not recording.</summary>
    bool TogglePause();
}
