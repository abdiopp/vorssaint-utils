// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Capture;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Imaging.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Rivet.Platform.Windows.Capture;

/// <summary>
/// Pixel acquisition on Windows (spec 01 §3.5).
/// <list type="bullet">
/// <item>Displays: Windows.Graphics.Capture where it is border-free (Windows 11)
/// or needed (HDR monitors, captured in FP16 and tone-mapped), otherwise GDI
/// BitBlt, which never flashes the Windows 10 yellow capture border.</item>
/// <item>Windows: the window's own pixels even when covered — WGC CreateForWindow
/// on Windows 11, PrintWindow(PW_RENDERFULLCONTENT) first on Windows 10 —
/// falling back to cropping the screen. Attached dialogs are drawn over it.</item>
/// <item>This app's windows are kept out with WDA_EXCLUDEFROMCAPTURE for the
/// duration of a capture when the policy asks for it.</item>
/// </list>
/// </summary>
public sealed class WindowsScreenCapturer : IScreenCapturer, IDisposable
{
    private static readonly TimeSpan WindowRenderTimeout = TimeSpan.FromSeconds(2.5);

    private readonly WgcCapture _wgc = new();
    private readonly bool _wgcSupported = WgcCapture.IsSupported;

    public async Task<IReadOnlyDictionary<string, PixelBuffer>> CaptureDisplaysAsync(IReadOnlyList<ScreenInfo> displays, DisplayCaptureOptions options, CancellationToken cancellationToken = default)
    {
        // Exclusion is applied and removed on the calling (UI) thread, which owns our windows.
        using var exclusion = OwnWindowExclusion.Apply(options);
        if (exclusion.Changed)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }

        return await Task.Run(() => CaptureAllAsync(displays, options, cancellationToken), cancellationToken).ConfigureAwait(true);
    }

    public async Task<PixelBuffer?> CaptureWindowAsync(CaptureWindowInfo window, IReadOnlyList<CaptureWindowInfo> attached, DisplayCaptureOptions options, CancellationToken cancellationToken = default)
    {
        using var exclusion = OwnWindowExclusion.Apply(options);
        if (exclusion.Changed)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }

        return await Task.Run(async () =>
        {
            var main = await CaptureOneWindowAsync(window, options, cancellationToken).ConfigureAwait(false);
            if (main is null)
            {
                return null;
            }

            var layers = new List<(PixelBuffer Image, PixelPoint Offset)>();
            foreach (var dialog in attached)
            {
                var image = await CaptureOneWindowAsync(dialog, options, cancellationToken).ConfigureAwait(false);
                if (image is not null)
                {
                    layers.Add((image, new PixelPoint(dialog.Bounds.X - window.Bounds.X, dialog.Bounds.Y - window.Bounds.Y)));
                }
            }

            return CaptureImaging.Composite(main, layers);
        }, cancellationToken).ConfigureAwait(true);
    }

    public IRegionCapture? CreateRegionCapture(ScreenInfo display, PixelRect rect, DisplayCaptureOptions options)
    {
        var clamped = rect.Intersect(display.Bounds);
        return clamped.IsEmpty ? null : new GdiRegionCapture(clamped, display.Scale, OwnWindowExclusion.Apply(options));
    }

    private async Task<IReadOnlyDictionary<string, PixelBuffer>> CaptureAllAsync(IReadOnlyList<ScreenInfo> displays, DisplayCaptureOptions options, CancellationToken cancellationToken)
    {
        var colors = DisplayColorInfo.Query();
        var borderless = _wgcSupported && await _wgc.CanCaptureBorderlessAsync().ConfigureAwait(false);
        var results = new Dictionary<string, PixelBuffer>(StringComparer.Ordinal);
        foreach (var display in displays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            colors.TryGetValue(display.Id, out var color);
            PixelBuffer? image = null;
            var hdr = color?.IsHdr == true;
            if (_wgcSupported && (hdr || borderless))
            {
                try
                {
                    var monitor = color?.Monitor is { } m && m != 0 ? m : MonitorAt(display.Bounds);
                    image = await _wgc.CaptureMonitorAsync(monitor, display.Scale, options.IncludeCursor, borderless, hdr ? color!.SdrWhiteScale : null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("capture", $"Windows.Graphics.Capture failed for {display.Id}; using GDI.", ex);
                }
            }

            image ??= GdiCapture.CaptureScreenRect(display.Bounds, display.Scale, options.IncludeCursor);
            if (image is not null)
            {
                results[display.Id] = image;
            }
            else
            {
                Log.Warn("capture", $"Display {display.Id} could not be captured.");
            }
        }

        return results;
    }

    private async Task<PixelBuffer?> CaptureOneWindowAsync(CaptureWindowInfo window, DisplayCaptureOptions options, CancellationToken cancellationToken)
    {
        var borderless = _wgcSupported && await _wgc.CanCaptureBorderlessAsync().ConfigureAwait(false);
        if (borderless && await TryWgcWindowAsync(window, borderless: true, cancellationToken).ConfigureAwait(false) is { } fromWgc)
        {
            return fromWgc;
        }

        // PrintWindow sends WM_PRINT to the window's thread: never wait on a hung app forever.
        var render = Task.Run(() => GdiCapture.CaptureWindow(window.Handle, window.Bounds, window.Scale), cancellationToken);
        try
        {
            if (await render.WaitAsync(WindowRenderTimeout, cancellationToken).ConfigureAwait(false) is { } printed)
            {
                return printed;
            }
        }
        catch (TimeoutException)
        {
            Log.Warn("capture", "PrintWindow timed out.");
        }

        if (!borderless && _wgcSupported && await TryWgcWindowAsync(window, borderless: false, cancellationToken).ConfigureAwait(false) is { } fallback)
        {
            return fallback;
        }

        // Last resort: the window's frame as it appears on screen (may include what covers it).
        return GdiCapture.CaptureScreenRect(window.Bounds, window.Scale, options.IncludeCursor);
    }

    private async Task<PixelBuffer?> TryWgcWindowAsync(CaptureWindowInfo window, bool borderless, CancellationToken cancellationToken)
    {
        try
        {
            var image = await _wgc.CaptureWindowAsync(window.Handle, window.Scale, borderless, cancellationToken).ConfigureAwait(false);
            return image is null ? null : FitToFrame(image, window);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("capture", "Window capture with Windows.Graphics.Capture failed.", ex);
            return null;
        }
    }

    /// <summary>
    /// WGC delivers the window's visual; when it includes the invisible resize
    /// borders of the window rectangle, crop to the visible frame.
    /// </summary>
    private static unsafe PixelBuffer? FitToFrame(PixelBuffer image, CaptureWindowInfo window)
    {
        var frame = window.Bounds;
        if (Math.Abs(image.Width - frame.Width) <= 1 && Math.Abs(image.Height - frame.Height) <= 1)
        {
            return CaptureImaging.IsBlank(image) ? null : image;
        }

        var hwnd = new HWND((void*)window.Handle);
        if (PInvoke.GetWindowRect(hwnd, out var rect) && image.Width == rect.right - rect.left && image.Height == rect.bottom - rect.top)
        {
            var cropped = CaptureImaging.Crop(image, new PixelRect(frame.X - rect.left, frame.Y - rect.top, frame.Width, frame.Height));
            return cropped is null || CaptureImaging.IsBlank(cropped) ? null : cropped;
        }

        if (image.Width >= frame.Width && image.Height >= frame.Height)
        {
            var cropped = CaptureImaging.Crop(image, new PixelRect((image.Width - frame.Width) / 2, 0, frame.Width, frame.Height));
            return cropped is null || CaptureImaging.IsBlank(cropped) ? null : cropped;
        }

        // A clipped buffer (window partly off-screen): accept it only when the aspect still matches.
        var rx = image.Width / (double)frame.Width;
        var ry = image.Height / (double)frame.Height;
        return Math.Max(rx, ry) / Math.Min(rx, ry) > 1.08 || CaptureImaging.IsBlank(image) ? null : image;
    }

    private static unsafe nint MonitorAt(PixelRect bounds)
    {
        var center = new System.Drawing.Point(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        return (nint)PInvoke.MonitorFromPoint(center, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST).Value;
    }

    public void Dispose() => _wgc.Dispose();

    /// <summary>Repeated BitBlt of a fixed rectangle (cheap, no capture border); keeps the exclusion until disposed.</summary>
    private sealed class GdiRegionCapture(PixelRect rect, double scale, OwnWindowExclusion exclusion) : IRegionCapture
    {
        public PixelBuffer? CaptureFrame() => GdiCapture.CaptureScreenRect(rect, scale, includeCursor: false);

        public void Dispose() => Rivet.Core.Util.UiThread.Run(exclusion.Dispose);
    }
}
