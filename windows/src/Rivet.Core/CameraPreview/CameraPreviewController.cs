// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Modules.CameraPreview;

/// <summary>
/// The mirror's capture lifecycle (spec 07 §3.5.5–3.5.8): the camera runs only
/// while the window is shown, every start stops the previous session first,
/// late callbacks from an older start are dropped by a generation counter,
/// and hot-plugging is observed only while shown. Public members are called
/// on the UI thread; events are raised there too.
/// </summary>
public sealed class CameraPreviewController : IDisposable
{
    private readonly ICameraService _cameras;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private ICameraSession? _session;
    private IDisposable? _deviceWatch;
    private CancellationTokenSource? _startCancel;
    private int _generation;
    private PixelBuffer? _pendingFrame;
    private int _framePosted;

    public CameraPreviewController(ICameraService cameras, ISettingsStore settings)
    {
        _cameras = cameras;
        _settings = settings;
    }

    public event EventHandler? StateChanged;

    /// <summary>A new frame is ready in <see cref="TakeFrame"/> (coalesced: at most one pending notification).</summary>
    public event EventHandler? FrameReady;

    public CameraPreviewState State { get; private set; } = CameraPreviewState.Idle;

    public IReadOnlyList<CameraDevice> Cameras { get; private set; } = [];

    public string? CurrentDeviceId { get; private set; }

    public CameraDevice? CurrentCamera => Cameras.FirstOrDefault(c => c.Id == CurrentDeviceId);

    public bool IsShown { get; private set; }

    public string? FailureDetail { get; private set; }

    public string PrivacySettingsUri => _cameras.PrivacySettingsUri;

    /// <summary>Start capturing for a newly shown mirror.</summary>
    public void Start()
    {
        if (IsShown)
        {
            return;
        }

        IsShown = true;
        _deviceWatch = _cameras.WatchDevices(() => UiThread.Post(OnDevicesChanged));
        _ = StartCaptureAsync(preferredId: null);
    }

    /// <summary>Hide: drop late callbacks, stop the session, stop observing devices, back to idle.</summary>
    public void Stop()
    {
        if (!IsShown)
        {
            return;
        }

        IsShown = false;
        Interlocked.Increment(ref _generation);
        _startCancel?.Cancel();
        _deviceWatch?.Dispose();
        _deviceWatch = null;
        StopSession();
        Interlocked.Exchange(ref _pendingFrame, null);
        SetState(CameraPreviewState.Idle);
    }

    /// <summary>"Open camera" from the unavailable state: check again and start.</summary>
    public void Retry()
    {
        if (IsShown && State == CameraPreviewState.Unavailable)
        {
            _ = StartCaptureAsync(CurrentDeviceId);
        }
    }

    /// <summary>The user picked a camera in the menu: remember it and switch.</summary>
    public void SelectCamera(string deviceId)
    {
        if (!IsShown || deviceId == CurrentDeviceId)
        {
            return;
        }

        _settings.Set(CameraPreviewSettings.DeviceId, deviceId);
        _ = StartCaptureAsync(deviceId);
    }

    /// <summary>The latest frame, or null when none arrived since the last call.</summary>
    public PixelBuffer? TakeFrame()
    {
        Volatile.Write(ref _framePosted, 0);
        return Interlocked.Exchange(ref _pendingFrame, null);
    }

    public void Dispose()
    {
        Stop();
        _serial.Dispose();
    }

    /// <summary>The camera to use: the explicit pick if still present, otherwise the remembered one, otherwise the first.</summary>
    public static CameraDevice? Choose(IReadOnlyList<CameraDevice> cameras, string? preferredId, string? rememberedId) =>
        cameras.FirstOrDefault(c => c.Id == preferredId)
        ?? cameras.FirstOrDefault(c => c.Id == rememberedId)
        ?? cameras.FirstOrDefault();

    private async Task StartCaptureAsync(string? preferredId)
    {
        var generation = Interlocked.Increment(ref _generation);
        _startCancel?.Cancel();
        var cancel = _startCancel = new CancellationTokenSource();
        SetState(CameraPreviewState.Starting);
        FailureDetail = null;

        await _serial.WaitAsync().ConfigureAwait(true);
        try
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            // A start always stops the previous session first.
            StopSession();

            IReadOnlyList<CameraDevice> cameras;
            try
            {
                cameras = await _cameras.GetCamerasAsync(cancel.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn("camera", "Listing cameras failed.", ex);
                cameras = [];
            }

            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            Cameras = cameras;
            var camera = Choose(cameras, preferredId, _settings.Get(CameraPreviewSettings.DeviceId));
            if (camera is null)
            {
                CurrentDeviceId = null;
                SetState(CameraPreviewState.NoCamera);
                return;
            }

            CurrentDeviceId = camera.Id;
            CameraStartResult result;
            try
            {
                result = await _cameras.StartAsync(camera.Id, cancel.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("camera", "Starting the camera failed.", ex);
                result = CameraStartResult.Failed(CameraPreviewState.Unavailable, ex.Message);
            }

            if (generation != Volatile.Read(ref _generation) || !IsShown)
            {
                result.Session?.Dispose();
                return;
            }

            if (result.Session is not { } session)
            {
                FailureDetail = result.Detail;
                SetState(result.Failure);
                return;
            }

            _session = session;
            session.FrameArrived += (_, frame) => OnFrame(generation, frame);
            session.Faulted += (_, fault) => UiThread.Post(() => OnFault(generation, fault));
            SetState(CameraPreviewState.Running);
        }
        finally
        {
            _serial.Release();
        }
    }

    private void OnFrame(int generation, PixelBuffer frame)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        // Copy: the platform may reuse its buffer for the next frame.
        var copy = new PixelBuffer(frame.Width, frame.Height, (byte[])frame.Pixels.Clone(), frame.Stride);
        Interlocked.Exchange(ref _pendingFrame, copy);
        if (Interlocked.Exchange(ref _framePosted, 1) == 0)
        {
            UiThread.Post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    FrameReady?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Volatile.Write(ref _framePosted, 0);
                }
            });
        }
    }

    private void OnFault(int generation, CameraFault fault)
    {
        if (generation != Volatile.Read(ref _generation) || !IsShown)
        {
            return;
        }

        StopSession();
        SetState(fault == CameraFault.Denied ? CameraPreviewState.Denied : CameraPreviewState.Unavailable);
    }

    private async void OnDevicesChanged()
    {
        if (!IsShown)
        {
            return;
        }

        IReadOnlyList<CameraDevice> cameras;
        try
        {
            cameras = await _cameras.GetCamerasAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("camera", "Listing cameras after a device change failed.", ex);
            return;
        }

        if (!IsShown)
        {
            return;
        }

        Cameras = cameras;
        var selectedStillThere = CurrentDeviceId is not null && cameras.Any(c => c.Id == CurrentDeviceId);
        switch (State)
        {
            case CameraPreviewState.NoCamera when cameras.Count > 0:
                _ = StartCaptureAsync(null);
                break;
            case CameraPreviewState.Running or CameraPreviewState.Starting when !selectedStillThere:
                // Fall back to the first remaining camera; an automatic fallback is never remembered.
                if (cameras.Count > 0)
                {
                    _ = StartCaptureAsync(cameras[0].Id);
                }
                else
                {
                    Interlocked.Increment(ref _generation);
                    StopSession();
                    CurrentDeviceId = null;
                    SetState(CameraPreviewState.NoCamera);
                }

                break;
            default:
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void StopSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("camera", "Stopping the camera failed.", ex);
        }
    }

    private void SetState(CameraPreviewState state)
    {
        if (State == state)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
