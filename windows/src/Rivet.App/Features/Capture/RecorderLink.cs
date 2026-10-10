// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Contracts;
using Rivet.Core.Diagnostics;
using Rivet.Core.Recording.Engine;

namespace Rivet.App.Features.Capture;

/// <summary>What the capture chooser needs from the screen recorder.</summary>
internal interface IRecorderLink
{
    /// <summary>
    /// A recording is running, counting down, starting or still being written
    /// (spec 01 §3.3 step 2): the chooser then opens only from another tool's
    /// own shortcut without the menu, offering only that tool.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Hands a Recording selection made in a chooser another tool opened to the
    /// recorder, which runs its checks and countdown as usual. False when the
    /// recorder can't be reached this way.
    /// </summary>
    Task<bool> TryRecordSelectionAsync(CaptureSelection selection);
}

/// <summary>
/// The recording module's <see cref="IScreenRecorder"/>, resolved lazily so the
/// two modules stay independent. Without the recorder it reports idle and hands
/// nothing over.
/// </summary>
internal sealed class RecorderLink(IServiceProvider services) : IRecorderLink
{
    private IScreenRecorder? Recorder => services.GetService<IScreenRecorder>();

    public bool IsBusy => Recorder?.IsBusy ?? false;

    public async Task<bool> TryRecordSelectionAsync(CaptureSelection selection)
    {
        if (Recorder is not { } recorder)
        {
            return false;
        }

        try
        {
            await recorder.RecordSelectionAsync(selection);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The recorder took the selection; its own start failed (it reports that itself).
            Log.Warn("capture", "The recorder could not start from the handed-over selection.", ex);
        }

        return true;
    }
}
