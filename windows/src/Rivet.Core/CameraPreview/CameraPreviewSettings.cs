// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Settings;

namespace Rivet.Core.Modules.CameraPreview;

/// <summary>Camera preview preferences. Nothing about frames is ever stored.</summary>
public static class CameraPreviewSettings
{
    public static readonly Setting<bool> ShortcutEnabled = new("cameraPreviewShortcutEnabled", false);

    /// <summary>Toggle hotkey (VK storage, empty = the default Ctrl+Alt+Win+W).</summary>
    public static readonly Setting<string> Shortcut = new("cameraPreviewShortcut", string.Empty);

    /// <summary>
    /// New on Windows: the camera the user explicitly picked (macOS keeps this as the
    /// system's per-app preferred camera). Written only on an explicit pick, never on a
    /// fallback. Device ids are machine-specific, so it never travels in backups.
    /// </summary>
    public static readonly Setting<string> DeviceId = new("cameraPreviewDeviceId", string.Empty, machineState: true);
}

/// <summary>Sizes and timings (spec 07 §6.5).</summary>
public static class CameraPreviewLayout
{
    public const double Width = 320;
    public const double Height = 240;
    public const double CornerRadius = 14;
    public const double TopOffset = 48;
    public const double SideClamp = 16;
    public const double BottomClamp = 16;
    public const double OutsideClickTolerance = 2;
    public static readonly TimeSpan FadeIn = TimeSpan.FromSeconds(0.13);
    public static readonly TimeSpan HoverAnimation = TimeSpan.FromSeconds(0.15);
    public static readonly TimeSpan PermissionGrace = TimeSpan.FromSeconds(1.0);
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(0.15);

    /// <summary>Preferred capture size ("medium" quality on macOS).</summary>
    public const int PreferredFrameWidth = 640;
    public const int PreferredFrameHeight = 480;

    /// <summary>
    /// Top-left of the mirror in physical pixels: horizontally centred on the
    /// pointer's monitor, 48 DIP below the top of its work area, kept 16 DIP
    /// from the sides and 16 DIP above the bottom of the work area.
    /// </summary>
    public static PixelPoint Position(ScreenInfo screen)
    {
        var scale = screen.Scale <= 0 ? 1.0 : screen.Scale;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var side = (int)Math.Round(SideClamp * scale);
        var bounds = screen.Bounds;
        var work = screen.WorkArea;
        var left = work.X + ((work.Width - width) / 2);
        left = Math.Clamp(left, bounds.X + side, Math.Max(bounds.X + side, bounds.Right - width - side));
        var top = work.Y + (int)Math.Round(TopOffset * scale);
        var maxTop = work.Bottom - height - (int)Math.Round(BottomClamp * scale);
        top = Math.Min(top, Math.Max(work.Y, maxTop));
        return new PixelPoint(left, top);
    }

    /// <summary>
    /// Aspect-fill: the source rectangle (in frame pixels) that covers the
    /// view without distortion, cropped evenly on the long side.
    /// </summary>
    public static (double X, double Y, double Width, double Height) AspectFillSource(int frameWidth, int frameHeight, double viewWidth, double viewHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
        {
            return (0, 0, Math.Max(0, frameWidth), Math.Max(0, frameHeight));
        }

        var frameAspect = frameWidth / (double)frameHeight;
        var viewAspect = viewWidth / viewHeight;
        if (frameAspect > viewAspect)
        {
            var width = frameHeight * viewAspect;
            return ((frameWidth - width) / 2, 0, width, frameHeight);
        }

        var height = frameWidth / viewAspect;
        return (0, (frameHeight - height) / 2, frameWidth, height);
    }
}
