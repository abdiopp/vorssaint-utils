// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.CameraPreview;

/// <summary>The states of the mirror (spec 07 §3.5.5).</summary>
public enum CameraPreviewState
{
    Idle,

    /// <summary>Waiting for the user's answer to a permission prompt (packaged apps only; desktop apps get no prompt on Windows).</summary>
    WaitingPermission,

    Starting,
    Running,

    /// <summary>Camera access is off in Windows privacy settings (or blocked mid-stream).</summary>
    Denied,

    /// <summary>The camera could not start or stopped with an error; retry is offered.</summary>
    Unavailable,

    NoCamera,
}

public sealed record CameraDevice(string Id, string Name);

/// <summary>Why a running session stopped on its own.</summary>
public enum CameraFault
{
    /// <summary>Runtime error, another app pre-empted the camera, or the device went away.</summary>
    Unavailable,

    /// <summary>The privacy switch or a hardware shutter blocked the stream.</summary>
    Denied,
}

/// <summary>A running capture. Disposing it releases the camera (and its light) immediately.</summary>
public interface ICameraSession : IDisposable
{
    string DeviceId { get; }

    /// <summary>New frame (BGRA, premultiplied, top-down) on a worker thread. The buffer may be reused after the handler returns.</summary>
    event EventHandler<PixelBuffer>? FrameArrived;

    /// <summary>The session stopped by itself; raised once, on any thread.</summary>
    event EventHandler<CameraFault>? Faulted;
}

public sealed record CameraStartResult
{
    public ICameraSession? Session { get; init; }

    /// <summary>When <see cref="Session"/> is null: Denied, NoCamera or Unavailable.</summary>
    public CameraPreviewState Failure { get; init; } = CameraPreviewState.Unavailable;

    public string? Detail { get; init; }

    public static CameraStartResult Started(ICameraSession session) => new() { Session = session };

    public static CameraStartResult Failed(CameraPreviewState state, string? detail = null) => new() { Failure = state, Detail = detail };
}

/// <summary>What Windows privacy settings say about camera access for this app.</summary>
public enum CameraAccess
{
    Unknown,
    Allowed,

    /// <summary>Turned off in Settings › Privacy &amp; security › Camera (device, app or desktop-app switch).</summary>
    Denied,
}

/// <summary>Camera discovery and capture (MediaCapture + MediaFrameReader on Windows; generated frames in the fake).</summary>
public interface ICameraService
{
    /// <summary>Cheap check of the privacy switches, without starting a camera.</summary>
    CameraAccess Access { get; }

    Task<IReadOnlyList<CameraDevice>> GetCamerasAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts capturing from <paramref name="deviceId"/> at about 640×480.</summary>
    Task<CameraStartResult> StartAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>Calls back (any thread) when cameras are plugged in or removed. Observed only while the mirror is shown.</summary>
    IDisposable WatchDevices(Action changed);

    /// <summary>The Windows Settings page for camera privacy.</summary>
    string PrivacySettingsUri { get; }
}
