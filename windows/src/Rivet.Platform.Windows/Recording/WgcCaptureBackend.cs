// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Recording.Engine;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// Creates Windows Graphics Capture sessions for a monitor (area and display
/// recordings, cropped on the GPU) or a window (the window's own buffer,
/// independent of position and occlusion).
/// </summary>
public sealed class WgcCaptureBackend : IVideoCaptureBackend
{
    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private bool? _borderlessAllowed;

    public RecorderAvailability CheckAvailability()
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported())
            {
                return RecorderAvailability.CaptureUnsupported;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Windows Graphics Capture is unavailable.", ex);
            return RecorderAvailability.CaptureUnsupported;
        }

        return MediaFoundationRuntime.Probe();
    }

    public async Task<IVideoCaptureSession> CreateAsync(VideoCaptureRequest request, CancellationToken cancellationToken)
    {
        await EnsureBorderlessAccessAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Create(request), cancellationToken).ConfigureAwait(false);
    }

    private WgcCaptureSession Create(VideoCaptureRequest request)
    {
        var (device, context, videoSupport) = CreateDevice();
        try
        {
            var target = request.Target;
            var item = target.Kind == RecordingTargetKind.Window
                ? CaptureInterop.CreateItemForWindow(target.Window)
                : CaptureInterop.CreateItemForMonitor(MonitorHandle(target.Monitor));
            return new WgcCaptureSession(request, device, context, videoSupport, item, _borderlessAllowed == true);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A hardware device with video support (needed to share it with Media
    /// Foundation's GPU encoder); without video support the CPU encoder path
    /// is used; WARP is the last resort (virtual machines, Remote Desktop).
    /// </summary>
    private static (ID3D11Device Device, ID3D11DeviceContext Context, bool VideoSupport) CreateDevice()
    {
        var attempts = new (DriverType Driver, DeviceCreationFlags Flags, bool Video)[]
        {
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, true),
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport, false),
            (DriverType.Warp, DeviceCreationFlags.BgraSupport, false),
        };
        Exception? last = null;
        foreach (var (driver, flags, video) in attempts)
        {
            try
            {
                var result = D3D11.D3D11CreateDevice(IntPtr.Zero, driver, flags, FeatureLevels, out var device, out var context);
                if (result.Success && device is not null && context is not null)
                {
                    using (var multithread = device.QueryInterface<ID3D11Multithread>())
                    {
                        // Media Foundation and the capture callback use the device from other threads.
                        multithread.SetMultithreadProtected(true);
                    }

                    if (driver != DriverType.Hardware || !video)
                    {
                        Log.Info("recorder", $"Capture device: {driver}, video support {video}.");
                    }

                    return (device, context, video);
                }

                device?.Dispose();
                context?.Dispose();
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("No Direct3D 11 device is available for capture.", last);
    }

    private static unsafe nint MonitorHandle(ScreenInfo monitor)
    {
        var center = new System.Drawing.Point(monitor.Bounds.X + (monitor.Bounds.Width / 2), monitor.Bounds.Y + (monitor.Bounds.Height / 2));
        var handle = PInvoke.MonitorFromPoint(center, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return (nint)handle.Value;
    }

    /// <summary>
    /// Windows 11 can drop the yellow capture border; unpackaged apps are
    /// normally allowed, but asking once keeps the behaviour explicit. Never
    /// fatal: on Windows 10 the border simply stays.
    /// </summary>
    private async Task EnsureBorderlessAccessAsync()
    {
        if (_borderlessAllowed.HasValue)
        {
            return;
        }

        try
        {
            if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                _borderlessAllowed = false;
                return;
            }

            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            _borderlessAllowed = status == global::Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
            Log.Info("recorder", $"Borderless capture: {status}.");
        }
        catch (Exception ex)
        {
            Log.Info("recorder", $"Borderless capture unavailable: {ex.Message}");
            _borderlessAllowed = false;
        }
    }
}
