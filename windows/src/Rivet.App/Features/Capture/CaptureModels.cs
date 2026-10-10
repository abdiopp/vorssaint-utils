// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Rivet.App.Hosting;
using Rivet.App.Shell;
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;

namespace Rivet.App.Features.Capture;

/// <summary>
/// A finished screenshot moving through the pipeline (spec 01 §3.1): the
/// pixels (with their DPI scale) and the captured area on screen, used only
/// to place the preview (empty for captures that do not come from the screen).
/// </summary>
internal sealed record CapturedImage(PixelBuffer Image, PixelRect Anchor)
{
    public double Scale => Image.Scale;

    public CaptureTargetKind? Kind { get; init; }

    public string? WindowTitle { get; init; }
}

/// <summary>Placement helpers for the capture tools' floating windows.</summary>
internal static class CaptureWindows
{
    /// <summary>
    /// Shows a window over an exact physical-pixel rectangle. Avalonia positions
    /// in physical pixels but sizes in DIPs, and a move to a monitor with another
    /// DPI rescales the window; the native placement afterwards makes it exact.
    /// </summary>
    public static void PlaceExactly(Window window, PixelRect bounds, double scale)
    {
        window.Position = new Avalonia.PixelPoint(bounds.X, bounds.Y);
        window.Width = bounds.Width / scale;
        window.Height = bounds.Height / scale;
        var handle = WindowInterop.Handle(window);
        if (handle != 0)
        {
            AppHost.Current?.Services.GetService<ICapturePlatform>()?.PlaceWindow(handle, bounds);
        }
    }

    /// <summary>Workflow surfaces: no taskbar button, top-most, never in a capture.</summary>
    public static void ApplyWorkflowChrome(Window window, bool clickThrough = false, bool noActivate = false)
    {
        var options = WindowChromeOptions.ToolWindow | WindowChromeOptions.Topmost | WindowChromeOptions.ExcludeFromCapture;
        if (clickThrough)
        {
            options |= WindowChromeOptions.ClickThrough;
        }

        if (noActivate)
        {
            options |= WindowChromeOptions.NoActivate;
        }

        WindowInterop.ApplyChrome(window, options);
    }

    /// <summary>The display for a capture anchor: the largest overlap, else the pointer's display.</summary>
    public static ScreenInfo DisplayFor(IScreenService screens, PixelRect anchor)
    {
        var pointer = screens.CursorPosition;
        return CaptureGeometry.DisplayWithLargestOverlap(screens.Screens, anchor, pointer) ?? screens.ScreenFromPoint(pointer);
    }
}
