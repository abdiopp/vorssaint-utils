// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Contracts;

/// <summary>The tools that share the capture selector.</summary>
public enum CaptureTool
{
    Screenshot,
    Recording,
    Text,
    Color,
}

public enum CaptureTargetKind
{
    Area,
    Window,
    Display,
}

public sealed record CaptureSelectorRequest
{
    public required CaptureTool Tool { get; init; }

    /// <summary>Let the user switch tools from the selector's hint bar.</summary>
    public bool AllowToolSwitching { get; init; } = true;

    /// <summary>Start in window-picking mode instead of area selection.</summary>
    public bool StartWithWindowPicking { get; init; }
}

/// <summary>What the user chose in the selector.</summary>
public sealed record CaptureSelection
{
    /// <summary>The tool active when the user confirmed (they may have switched).</summary>
    public required CaptureTool Tool { get; init; }

    public required CaptureTargetKind Kind { get; init; }

    /// <summary>Selected rectangle in physical virtual-screen pixels.</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>The monitor that holds most of the selection.</summary>
    public required string ScreenId { get; init; }

    /// <summary>The picked window, for <see cref="CaptureTargetKind.Window"/>.</summary>
    public nint WindowHandle { get; init; }

    /// <summary>Frozen pixels of the selection when the selector froze the screen (screenshots).</summary>
    public PixelBuffer? FrozenImage { get; init; }

    /// <summary>For the colour tool: the colour under the loupe, 0xAARRGGBB.</summary>
    public uint? PickedColor { get; init; }
}

/// <summary>
/// The unified full-screen selector with its pixel loupe, shared by
/// screenshots, recording, text recognition and colour picking. Owned by the
/// screenshot module.
/// </summary>
public interface ICaptureSelector
{
    bool IsActive { get; }

    /// <summary>Shows the selector; returns null when the user cancels.</summary>
    Task<CaptureSelection?> SelectAsync(CaptureSelectorRequest request, CancellationToken cancellationToken = default);
}
