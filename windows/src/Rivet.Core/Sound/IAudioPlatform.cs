// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Sound;

/// <summary>
/// The audio system (Windows: MMDevice API, WASAPI sessions and the policy
/// interfaces). Implementations own one serial "audio thread": every device
/// read and write runs there, notifications only schedule a coalesced
/// refresh, and nothing ever blocks the UI thread.
///
/// Events are raised on the audio thread (or any thread); consumers marshal to
/// the UI thread themselves. <see cref="InvokeAsync{T}"/> works at any time,
/// whether or not observation is running.
/// </summary>
public interface IAudioPlatform
{
    /// <summary>What works on this PC. Becomes accurate once observation has started once.</summary>
    AudioCapabilities Capabilities { get; }

    /// <summary>
    /// Starts or stops observation: device list, defaults and endpoint volumes
    /// (<paramref name="devices"/>), and per-app sessions (<paramref name="sessions"/>,
    /// which implies devices). Idempotent. Stopping releases every system object.
    /// </summary>
    void SetObservation(bool devices, bool sessions);

    /// <summary>The latest device snapshot; <see cref="AudioSnapshot.Empty"/> while not observing.</summary>
    AudioSnapshot Snapshot { get; }

    /// <summary>The latest sessions; empty while sessions are not observed.</summary>
    IReadOnlyList<AudioSessionInfo> Sessions { get; }

    /// <summary>Raised after <see cref="Snapshot"/> or <see cref="Capabilities"/> changed (coalesced, 0.2 s).</summary>
    event EventHandler? SnapshotChanged;

    /// <summary>Raised after <see cref="Sessions"/> changed.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>A session's volume or mute was changed by someone else (never by this app's own writes).</summary>
    event EventHandler<AudioSessionVolumeEventArgs>? SessionVolumeChanged;

    /// <summary>
    /// Runs <paramref name="work"/> on the audio thread with synchronous access
    /// to the endpoints. Work items run one at a time, in order, so a sequence
    /// of reads and writes inside one item is never interleaved with others.
    /// </summary>
    Task<T> InvokeAsync<T>(Func<IAudioEndpointAccess, T> work);

    /// <summary>Sets the volume (0…1) of these sessions. Rapid calls coalesce: only the newest value is written.</summary>
    void SetSessionVolume(IReadOnlyList<string> sessionKeys, float volume);

    void SetSessionMute(IReadOnlyList<string> sessionKeys, bool muted);

    /// <summary>
    /// Routes the apps running as <paramref name="processIds"/> to an output
    /// (null = follow the default). Windows persists the choice per app.
    /// Returns false when routing is unsupported or failed.
    /// </summary>
    Task<bool> SetAppOutputAsync(IReadOnlyList<int> processIds, string? deviceId);

    /// <summary>Clears every app's persisted output (the "all apps" switch).</summary>
    Task<bool> ClearAppOutputsAsync();
}

/// <summary>
/// Synchronous endpoint operations, valid only inside
/// <see cref="IAudioPlatform.InvokeAsync{T}"/>. Writes carry the app's own
/// event context, so they never come back as external changes.
/// </summary>
public interface IAudioEndpointAccess
{
    /// <summary>Active endpoints of one direction, read now.</summary>
    IReadOnlyList<AudioDevice> ActiveDevices(AudioFlow flow);

    /// <summary>The default endpoint (console role), read now.</summary>
    string? DefaultDeviceId(AudioFlow flow);

    /// <summary>Reads the endpoint's volume and mute; null when the device is gone or unreadable.</summary>
    EndpointVolume? ReadVolume(string deviceId);

    bool TrySetVolume(string deviceId, float scalar);

    bool TrySetMute(string deviceId, bool muted);

    /// <summary>Per-channel levels (0…1), or null when unreadable.</summary>
    IReadOnlyList<float>? ReadChannelVolumes(string deviceId);

    bool TrySetChannelVolume(string deviceId, int channel, float scalar);

    /// <summary>Makes the endpoint the default for every role and confirms it by reading back.</summary>
    bool TrySetDefaultDevice(string deviceId, AudioFlow flow);

    /// <summary>Some app currently records from this capture endpoint (best effort; false when unknown).</summary>
    bool IsCapturing(string deviceId);
}
