// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Modules.CameraPreview;
using Rivet.Core.Platform;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Rivet.Platform.Windows.CameraPreview;

/// <summary>
/// The live mirror on Windows (spec 07 §3.5.8): cameras from
/// DeviceInformation(VideoCapture), capture with MediaCapture in exclusive
/// control (falling back to shared read-only when another app streams), a
/// colour frame source at about 640×480, and a MediaFrameReader delivering
/// BGRA frames. Unpackaged desktop apps get no consent prompt: access denied
/// in Settings › Privacy &amp; security › Camera shows as the denied state.
/// Disposing the session stops the reader and releases the camera at once.
/// </summary>
public sealed class WindowsCameraService : ICameraService
{
    public string PrivacySettingsUri => "ms-settings:privacy-webcam";

    public CameraAccess Access
    {
        get
        {
            try
            {
                return DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture).CurrentStatus switch
                {
                    DeviceAccessStatus.Allowed => CameraAccess.Allowed,
                    DeviceAccessStatus.DeniedByUser or DeviceAccessStatus.DeniedBySystem => CameraAccess.Denied,
                    _ => CameraAccess.Unknown,
                };
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                return CameraAccess.Unknown;
            }
        }
    }

    public async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync(CancellationToken cancellationToken = default)
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask(cancellationToken).ConfigureAwait(false);
        return devices
            .Where(d => d.IsEnabled)
            .Select(d => new CameraDevice(d.Id, string.IsNullOrWhiteSpace(d.Name) ? "Camera" : d.Name))
            .ToList();
    }

    public async Task<CameraStartResult> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (Access == CameraAccess.Denied)
        {
            return CameraStartResult.Failed(CameraPreviewState.Denied);
        }

        MediaCapture? capture = null;
        try
        {
            capture = await InitializeAsync(deviceId, MediaCaptureSharingMode.ExclusiveControl).ConfigureAwait(true);
            var exclusive = true;
            if (capture is null)
            {
                // Another app is streaming: view alongside it (no format choice in shared mode).
                capture = await InitializeAsync(deviceId, MediaCaptureSharingMode.SharedReadOnly).ConfigureAwait(true);
                exclusive = false;
            }

            if (capture is null)
            {
                return CameraStartResult.Failed(CameraPreviewState.Unavailable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var source = PickColorSource(capture);
            if (source is null)
            {
                capture.Dispose();
                return CameraStartResult.Failed(CameraPreviewState.Unavailable, "No colour frame source.");
            }

            if (exclusive && PickFormat(source) is { } format)
            {
                try
                {
                    await source.SetFormatAsync(format).AsTask(cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    Log.Warn("camera", "Could not set the preview format; using the camera's default.", ex);
                }
            }

            var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask(cancellationToken).ConfigureAwait(true);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            var session = new Session(deviceId, capture, reader);
            var status = await reader.StartAsync().AsTask(cancellationToken).ConfigureAwait(true);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                Log.Warn("camera", $"Frame reader did not start: {status}.");
                session.Dispose();
                capture = null;
                return CameraStartResult.Failed(status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable
                    ? CameraPreviewState.Unavailable
                    : CameraPreviewState.Unavailable, status.ToString());
            }

            capture = null; // owned by the session now
            return CameraStartResult.Started(session);
        }
        catch (UnauthorizedAccessException)
        {
            return CameraStartResult.Failed(CameraPreviewState.Denied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            Log.Warn("camera", "The camera could not start.", ex);
            return CameraStartResult.Failed(CameraPreviewState.Unavailable, ex.Message);
        }
        finally
        {
            capture?.Dispose();
        }
    }

    public IDisposable WatchDevices(Action changed)
    {
        var watcher = DeviceInformation.CreateWatcher(DeviceClass.VideoCapture);
        var ready = false;
        watcher.EnumerationCompleted += (_, _) => ready = true;
        watcher.Added += (_, _) =>
        {
            if (ready)
            {
                changed();
            }
        };
        watcher.Removed += (_, _) =>
        {
            if (ready)
            {
                changed();
            }
        };
        watcher.Updated += (_, _) =>
        {
            if (ready)
            {
                changed();
            }
        };
        try
        {
            watcher.Start();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Warn("camera", "Camera hot-plug watching is unavailable.", ex);
        }

        return new Unwatch(watcher);
    }

    private static async Task<MediaCapture?> InitializeAsync(string deviceId, MediaCaptureSharingMode mode)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = mode,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            return capture;
        }
        catch (UnauthorizedAccessException)
        {
            capture.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Info("camera", $"MediaCapture init ({mode}) failed: 0x{ex.HResult:X8}.");
            capture.Dispose();
            return null;
        }
    }

    private static MediaFrameSource? PickColorSource(MediaCapture capture)
    {
        var colors = capture.FrameSources.Values.Where(s => s.Info.SourceKind == MediaFrameSourceKind.Color).ToList();
        return colors.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
               ?? colors.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
               ?? colors.FirstOrDefault();
    }

    /// <summary>The format closest to 640×480 (preferring at least that size and 15+ fps).</summary>
    private static MediaFrameFormat? PickFormat(MediaFrameSource source) => source.SupportedFormats
        .Where(f => f.VideoFormat is not null && f.VideoFormat.Width > 0)
        .OrderBy(f => f.VideoFormat.Width < CameraPreviewLayout.PreferredFrameWidth ? 1 : 0)
        .ThenBy(f => FrameRate(f) < 15 ? 1 : 0)
        .ThenBy(f => Math.Abs((int)f.VideoFormat.Width - CameraPreviewLayout.PreferredFrameWidth) + Math.Abs((int)f.VideoFormat.Height - CameraPreviewLayout.PreferredFrameHeight))
        .ThenByDescending(FrameRate)
        .FirstOrDefault();

    private static double FrameRate(MediaFrameFormat format) =>
        format.FrameRate is { Denominator: > 0 } rate ? rate.Numerator / (double)rate.Denominator : 0;

    private sealed class Session : ICameraSession
    {
        private readonly MediaCapture _capture;
        private readonly MediaFrameReader _reader;
        private byte[]? _pixels;
        private int _disposed;
        private int _faulted;

        public Session(string deviceId, MediaCapture capture, MediaFrameReader reader)
        {
            DeviceId = deviceId;
            _capture = capture;
            _reader = reader;
            _reader.FrameArrived += OnFrameArrived;
            _capture.Failed += (_, e) =>
            {
                Log.Warn("camera", $"Capture failed: {e.Message} (0x{e.Code:X8}).");
                Fault(CameraFault.Unavailable);
            };
            _capture.CameraStreamStateChanged += (sender, _) =>
            {
                try
                {
                    var state = sender.CameraStreamState;
                    if (state == global::Windows.Media.Devices.CameraStreamState.BlockedForPrivacy)
                    {
                        Fault(CameraFault.Denied);
                    }
                    else if (state == global::Windows.Media.Devices.CameraStreamState.Shutdown)
                    {
                        Fault(CameraFault.Unavailable);
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            };
        }

        public string DeviceId { get; }

        public event EventHandler<PixelBuffer>? FrameArrived;

        public event EventHandler<CameraFault>? Faulted;

        private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                using var frame = sender.TryAcquireLatestFrame();
                var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                if (bitmap is null)
                {
                    return;
                }

                SoftwareBitmap? converted = null;
                try
                {
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                    {
                        converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    }

                    var source = converted ?? bitmap;
                    var width = source.PixelWidth;
                    var height = source.PixelHeight;
                    var length = width * height * 4;
                    if (_pixels is null || _pixels.Length != length)
                    {
                        _pixels = new byte[length];
                    }

                    source.CopyToBuffer(_pixels.AsBuffer());

                    // Camera frames often carry an undefined alpha channel: make them opaque.
                    for (var i = 3; i < _pixels.Length; i += 4)
                    {
                        _pixels[i] = 255;
                    }

                    FrameArrived?.Invoke(this, new PixelBuffer(width, height, _pixels));
                }
                finally
                {
                    converted?.Dispose();
                    bitmap.Dispose();
                }
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException or ArgumentException)
            {
                Log.Warn("camera", "Reading a camera frame failed.", ex);
            }
        }

        private void Fault(CameraFault fault)
        {
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
            {
                Faulted?.Invoke(this, fault);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _reader.FrameArrived -= OnFrameArrived;

            // Stop and release off the UI thread; the camera light goes out as soon as this completes.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _reader.StopAsync();
                }
                catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
                {
                }

                _reader.Dispose();
                _capture.Dispose();
            });
        }
    }

    private sealed class Unwatch(DeviceWatcher watcher) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }
    }
}

public sealed class CameraPreviewWindowsRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) => services.AddSingleton<ICameraService, WindowsCameraService>();
}
