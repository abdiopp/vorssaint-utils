// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Contracts;
using Rivet.Core.Platform;
using Rivet.Core.Shortcuts;
using SkiaSharp;

namespace Rivet.App.Features.Capture.Selector;

/// <summary>How a selector session is opened.</summary>
internal sealed record SelectorRequest
{
    /// <summary>Tools offered (installed features, fixed order).</summary>
    public required IReadOnlyList<CaptureTool> Tools { get; init; }

    public required CaptureTool Tool { get; init; }

    public bool ShowPalette { get; init; } = true;

    /// <summary>The single-plate variant (standalone scrolling capture).</summary>
    public bool Standalone { get; init; }

    /// <summary>Only drags confirm.</summary>
    public bool RegionOnly { get; init; }

    /// <summary>Return a region whatever the tool (scrolling capture).</summary>
    public bool ForceGeometry { get; init; }

    /// <summary>Never freeze (the standalone scrolling selector is live).</summary>
    public bool ForceLive { get; init; }

    public string? Purpose { get; init; }
}

/// <summary>What a selector session produced.</summary>
internal sealed record SelectorOutcome
{
    public required CaptureTool Tool { get; init; }

    public required SelectorActionKind Kind { get; init; }

    public required ScreenInfo Display { get; init; }

    /// <summary>The region in global physical pixels (snapped to even sizes in geometry mode).</summary>
    public PixelRect Region { get; init; }

    public CaptureWindowInfo? Window { get; init; }

    /// <summary>Pixels, in image mode.</summary>
    public CapturedImage? Image { get; init; }

    /// <summary>The picked colour (0xAARRGGBB), in colour mode.</summary>
    public uint? Color { get; init; }

    /// <summary>The region starts a scrolling capture.</summary>
    public bool Scrolling { get; init; }
}

/// <summary>A display's photograph: the raw pixels and a Skia image to draw.</summary>
internal sealed class DisplaySource(PixelBuffer pixels) : IDisposable
{
    public PixelBuffer Pixels { get; } = pixels;

    public SKImage Image { get; } = Rivet.Imaging.Skia.SkiaConvert.ToImage(pixels);

    public void Dispose() => Image.Dispose();
}

/// <summary>Virtual keys the selector reacts to.</summary>
internal static class SelectorKeys
{
    public static SelectorKey? Map(int virtualKey) => virtualKey switch
    {
        VirtualKeys.Escape => SelectorKey.Escape,
        VirtualKeys.Return => SelectorKey.Enter,
        VirtualKeys.Space => SelectorKey.Space,
        0x52 => SelectorKey.R,
        0x53 => SelectorKey.S,
        0x5A => SelectorKey.Z,
        0x43 => SelectorKey.C,
        0x31 or 0x61 => SelectorKey.Digit1,
        0x32 or 0x62 => SelectorKey.Digit2,
        0x33 or 0x63 => SelectorKey.Digit3,
        0x34 or 0x64 => SelectorKey.Digit4,
        VirtualKeys.Left => SelectorKey.Left,
        VirtualKeys.Right => SelectorKey.Right,
        VirtualKeys.Up => SelectorKey.Up,
        VirtualKeys.Down => SelectorKey.Down,
        _ => null,
    };

    /// <summary>The hook swallows Esc, Enter and Space always, other keys only without Ctrl, Alt or Win.</summary>
    public static bool ShouldSwallow(SelectorKey key, KeyModifiers modifiers) =>
        key is SelectorKey.Escape or SelectorKey.Enter or SelectorKey.Space
        || (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == 0;
}
