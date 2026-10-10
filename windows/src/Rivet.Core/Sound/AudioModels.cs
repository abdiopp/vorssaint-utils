// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Sound;

/// <summary>Direction of an audio endpoint.</summary>
public enum AudioFlow
{
    /// <summary>Speakers, headphones, displays: where sound plays.</summary>
    Render,

    /// <summary>Microphones and line inputs.</summary>
    Capture,
}

/// <summary>
/// The endpoint's physical shape, as Windows reports it in
/// <c>PKEY_AudioEndpoint_FormFactor</c> (same numbering).
/// </summary>
public enum AudioFormFactor
{
    RemoteNetworkDevice = 0,
    Speakers = 1,
    LineLevel = 2,
    Headphones = 3,
    Microphone = 4,
    Headset = 5,
    Handset = 6,
    UnknownDigitalPassthrough = 7,
    Spdif = 8,
    DigitalAudioDisplayDevice = 9,
    Unknown = 10,
}

/// <summary>Priority tier of a device in a new priority list (spec §3.14): built-in first, virtual last.</summary>
public enum AudioDeviceTier
{
    BuiltIn = 0,
    Hardware = 1,
    Virtual = 2,
}

/// <summary>One active audio endpoint.</summary>
public sealed record AudioDevice
{
    /// <summary>The endpoint id string (stable for a given device and port). Used as the "UID" everywhere.</summary>
    public required string Id { get; init; }

    /// <summary>Friendly name, e.g. "Speakers (Realtek(R) Audio)".</summary>
    public required string Name { get; init; }

    public required AudioFlow Flow { get; init; }

    public AudioFormFactor FormFactor { get; init; } = AudioFormFactor.Unknown;

    public AudioDeviceTier Tier { get; init; } = AudioDeviceTier.Hardware;

    /// <summary>Headphones or a headset (form factor, Bluetooth audio or the name heuristic).</summary>
    public bool IsHeadphones { get; init; }

    public bool IsBluetooth { get; init; }
}

/// <summary>State of an endpoint's volume control (IAudioEndpointVolume).</summary>
public sealed record EndpointVolume
{
    /// <summary>Master level, 0…1.</summary>
    public float Scalar { get; init; }

    public bool Muted { get; init; }

    /// <summary>False for endpoints with a fixed level (some digital outputs).</summary>
    public bool CanSetVolume { get; init; } = true;

    public int ChannelCount { get; init; }
}

/// <summary>What the platform can do on this PC. The undocumented interfaces are probed at runtime.</summary>
public sealed record AudioCapabilities
{
    public static AudioCapabilities None { get; } = new();

    /// <summary>Changing the default device works (IPolicyConfig).</summary>
    public bool CanSetDefaultDevice { get; init; }

    /// <summary>Per-app output routing works (IAudioPolicyConfigFactory).</summary>
    public bool CanRouteApps { get; init; }

    /// <summary>Per-app sessions can be listed and adjusted.</summary>
    public bool HasSessions { get; init; }
}

/// <summary>
/// Everything about the devices at one moment: the active endpoints, the
/// defaults and each endpoint's volume. Immutable; the platform publishes a
/// new one after every (coalesced) change.
/// </summary>
public sealed record AudioSnapshot
{
    public static AudioSnapshot Empty { get; } = new();

    public IReadOnlyList<AudioDevice> Outputs { get; init; } = [];

    public IReadOnlyList<AudioDevice> Inputs { get; init; } = [];

    /// <summary>Default output (console role); null when there is none.</summary>
    public string? DefaultOutputId { get; init; }

    /// <summary>Default input (console role); null when there is none.</summary>
    public string? DefaultInputId { get; init; }

    public IReadOnlyDictionary<string, EndpointVolume> Volumes { get; init; } = new Dictionary<string, EndpointVolume>();

    /// <summary>Increments with every published snapshot.</summary>
    public long Generation { get; init; }

    /// <summary>The observation layer produced this (false for <see cref="Empty"/>).</summary>
    public bool IsLive { get; init; }

    public IReadOnlyList<AudioDevice> Devices(AudioFlow flow) => flow == AudioFlow.Render ? Outputs : Inputs;

    public string? DefaultId(AudioFlow flow) => flow == AudioFlow.Render ? DefaultOutputId : DefaultInputId;

    public AudioDevice? Find(string? id) =>
        id is null ? null : Outputs.FirstOrDefault(d => d.Id == id) ?? Inputs.FirstOrDefault(d => d.Id == id);

    public EndpointVolume? VolumeOf(string? id) => id is not null && Volumes.TryGetValue(id, out var v) ? v : null;
}

/// <summary>Who owns an audio session: the app the mixer shows as one row.</summary>
public sealed record AudioAppInfo
{
    /// <summary>
    /// Stable identity for saved preferences: the AppUserModelID of a packaged
    /// app, else the lowercase full executable path (or the process name when
    /// the path is unreadable). Null when nothing stable is known.
    /// </summary>
    public string? PersistenceId { get; init; }

    /// <summary>Row id: the persistence id, else <c>process:&lt;pid&gt;</c>.</summary>
    public required string GroupId { get; init; }

    public required string DisplayName { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>App icon (about 32 px, BGRA premultiplied), when one could be read.</summary>
    public PixelBuffer? Icon { get; init; }
}

/// <summary>One audio session (one app stream on one endpoint).</summary>
public sealed record AudioSessionInfo
{
    /// <summary>Unique per session instance (the session instance identifier).</summary>
    public required string Key { get; init; }

    /// <summary>The render endpoint the session plays to.</summary>
    public required string DeviceId { get; init; }

    public int ProcessId { get; init; }

    public bool IsSystemSounds { get; init; }

    /// <summary>The session's stream is running (AudioSessionStateActive): the "playing" dot.</summary>
    public bool IsActive { get; init; }

    /// <summary>Session volume, 0…1.</summary>
    public float Volume { get; init; } = 1f;

    public bool Muted { get; init; }

    /// <summary>The app's per-app output as Windows reports it (also routes set in Windows Settings); null = follows the default.</summary>
    public string? RoutedDeviceId { get; init; }

    public required AudioAppInfo App { get; init; }
}

/// <summary>A session's volume or mute changed from outside the app (Windows' mixer, the app itself).</summary>
public sealed class AudioSessionVolumeEventArgs(string sessionKey, float volume, bool muted) : EventArgs
{
    public string SessionKey { get; } = sessionKey;

    public float Volume { get; } = volume;

    public bool Muted { get; } = muted;
}
