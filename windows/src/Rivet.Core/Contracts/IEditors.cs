// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Contracts;

/// <summary>Where a screenshot came from (affects naming and the editor's defaults).</summary>
public sealed record ScreenshotEditRequest
{
    public required PixelBuffer Image { get; init; }

    /// <summary>The file the image was already saved to, when there is one (re-editing a capture).</summary>
    public string? SourcePath { get; init; }

    public CaptureTargetKind? Kind { get; init; }

    /// <summary>Title of the captured window, for file names.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>The recent-captures record this image belongs to, if any.</summary>
    public string? RecentCaptureId { get; init; }
}

/// <summary>The screenshot editor window. Owned by the screenshot editor module.</summary>
public interface IScreenshotEditor
{
    Task OpenAsync(ScreenshotEditRequest request);
}

/// <summary>The recording editor window for a take folder. Owned by the recording editor module.</summary>
public interface IRecordingEditor
{
    Task OpenAsync(string takeFolder);

    /// <summary>Whether an editor window has this take open (the take sweep must leave it alone).</summary>
    bool IsOpen(string takeFolder) => false;
}

public sealed record CaptureSaveResult(string Path, CaptureRecord Record);

/// <summary>
/// Saving and copying screenshots with the user's folder and naming settings,
/// and recording them in recent captures. Owned by the capture module; the
/// screenshot editor uses it for Save, Save As and Copy.
/// </summary>
public interface ICaptureOutput
{
    /// <summary>Saves to the configured folder with the configured name; returns the record.</summary>
    Task<CaptureSaveResult?> SaveAsync(PixelBuffer image, string? windowTitle = null, string? existingRecordId = null);

    /// <summary>Copies the image (honouring the "copy at 1x size" setting).</summary>
    void Copy(PixelBuffer image);

    /// <summary>The file name a new capture would get now (without folder).</summary>
    string SuggestFileName(string? windowTitle = null);

    /// <summary>The folder captures are saved to.</summary>
    string SaveFolder { get; }
}
