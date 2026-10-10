// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Sound;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Platform.Fake.Sound;

/// <summary>
/// An in-memory audio system with sample devices and apps, so the mixer, the
/// switcher, priority and mic mute run and render on macOS/Linux. Everything
/// happens synchronously on the calling thread; tests drive it through the
/// public helpers (connect a device, change a session from "outside", ...).
/// </summary>
public sealed class FakeAudioPlatform : IAudioPlatform, IAudioEndpointAccess
{
    public const string SpeakersId = "{0.0.0.00000000}.{fake-speakers}";
    public const string HeadphonesId = "{0.0.0.00000000}.{fake-headphones}";
    public const string DisplayId = "{0.0.0.00000000}.{fake-display}";
    public const string CableOutId = "{0.0.0.00000000}.{fake-cable-in}";
    public const string MicrophoneId = "{0.0.1.00000000}.{fake-mic-array}";
    public const string HeadsetMicId = "{0.0.1.00000000}.{fake-headset-mic}";
    public const string CableInId = "{0.0.1.00000000}.{fake-cable-out}";

    private readonly object _gate = new();
    private readonly List<AudioDevice> _devices = [];
    private readonly Dictionary<string, DeviceState> _state = new(StringComparer.Ordinal);
    private readonly List<AudioSessionInfo> _sessions = [];
    private readonly Dictionary<int, string?> _routes = [];
    private string? _defaultOutput;
    private string? _defaultInput;
    private bool _observeDevices;
    private bool _observeSessions;
    private long _generation;

    public FakeAudioPlatform()
    {
        LoadSampleSetup();
    }

    public event EventHandler? SnapshotChanged;

    public event EventHandler? SessionsChanged;

    public event EventHandler<AudioSessionVolumeEventArgs>? SessionVolumeChanged;

    public AudioCapabilities Capabilities { get; set; } = new() { CanSetDefaultDevice = true, CanRouteApps = true, HasSessions = true };

    public AudioSnapshot Snapshot { get; private set; } = AudioSnapshot.Empty;

    public IReadOnlyList<AudioSessionInfo> Sessions { get; private set; } = [];

    /// <summary>Per-process routes set through <see cref="SetAppOutputAsync"/>.</summary>
    public IReadOnlyDictionary<int, string?> Routes
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, string?>(_routes);
            }
        }
    }

    public bool ObservesDevices => _observeDevices;

    public bool ObservesSessions => _observeSessions;

    // ── IAudioPlatform ──────────────────────────────────────────────────

    public void SetObservation(bool devices, bool sessions)
    {
        devices |= sessions;
        if (devices == _observeDevices && sessions == _observeSessions)
        {
            return;
        }

        _observeDevices = devices;
        _observeSessions = sessions;
        PublishDevices();
        PublishSessions();
    }

    public Task<T> InvokeAsync<T>(Func<IAudioEndpointAccess, T> work)
    {
        try
        {
            T result;
            lock (_gate)
            {
                result = work(this);
            }

            PublishDevices();
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    public void SetSessionVolume(IReadOnlyList<string> sessionKeys, float volume) =>
        UpdateSessions(sessionKeys, s => s with { Volume = Math.Clamp(volume, 0f, 1f) });

    public void SetSessionMute(IReadOnlyList<string> sessionKeys, bool muted) =>
        UpdateSessions(sessionKeys, s => s with { Muted = muted });

    public Task<bool> SetAppOutputAsync(IReadOnlyList<int> processIds, string? deviceId)
    {
        lock (_gate)
        {
            foreach (var pid in processIds)
            {
                _routes[pid] = deviceId;
            }
        }

        UpdateSessions(_sessions.Where(s => processIds.Contains(s.ProcessId)).Select(s => s.Key).ToList(), s => s with { RoutedDeviceId = deviceId });
        return Task.FromResult(Capabilities.CanRouteApps);
    }

    public Task<bool> ClearAppOutputsAsync()
    {
        lock (_gate)
        {
            _routes.Clear();
        }

        UpdateSessions(_sessions.Select(s => s.Key).ToList(), s => s with { RoutedDeviceId = null });
        return Task.FromResult(Capabilities.CanRouteApps);
    }

    // ── IAudioEndpointAccess (called under the lock) ────────────────────

    public IReadOnlyList<AudioDevice> ActiveDevices(AudioFlow flow) => _devices.Where(d => d.Flow == flow).ToList();

    public string? DefaultDeviceId(AudioFlow flow) => flow == AudioFlow.Render ? _defaultOutput : _defaultInput;

    public EndpointVolume? ReadVolume(string deviceId) =>
        _state.TryGetValue(deviceId, out var s) && _devices.Any(d => d.Id == deviceId)
            ? new EndpointVolume { Scalar = s.Volume, Muted = s.Muted, CanSetVolume = s.CanSetVolume, ChannelCount = s.Channels.Length }
            : null;

    public bool TrySetVolume(string deviceId, float scalar)
    {
        if (!_state.TryGetValue(deviceId, out var s) || !s.CanSetVolume)
        {
            return false;
        }

        s.Volume = Math.Clamp(scalar, 0f, 1f);
        for (var i = 0; i < s.Channels.Length; i++)
        {
            s.Channels[i] = s.Volume;
        }

        return true;
    }

    public bool TrySetMute(string deviceId, bool muted)
    {
        if (!_state.TryGetValue(deviceId, out var s) || !s.MuteWorks)
        {
            return false;
        }

        s.Muted = muted;
        return true;
    }

    public IReadOnlyList<float>? ReadChannelVolumes(string deviceId) =>
        _state.TryGetValue(deviceId, out var s) ? s.Channels.ToArray() : null;

    public bool TrySetChannelVolume(string deviceId, int channel, float scalar)
    {
        if (!_state.TryGetValue(deviceId, out var s) || channel < 0 || channel >= s.Channels.Length)
        {
            return false;
        }

        s.Channels[channel] = Math.Clamp(scalar, 0f, 1f);
        return true;
    }

    public bool TrySetDefaultDevice(string deviceId, AudioFlow flow)
    {
        if (!Capabilities.CanSetDefaultDevice || !_devices.Any(d => d.Id == deviceId && d.Flow == flow))
        {
            return false;
        }

        if (flow == AudioFlow.Render)
        {
            _defaultOutput = deviceId;
        }
        else
        {
            _defaultInput = deviceId;
        }

        return true;
    }

    public bool IsCapturing(string deviceId) => _state.TryGetValue(deviceId, out var s) && s.Capturing;

    // ── Test and demo helpers ───────────────────────────────────────────

    /// <summary>Connects a device (Windows may make it the default, as it often does for headsets).</summary>
    public void Connect(AudioDevice device, float volume = 0.5f, bool makeDefault = false)
    {
        lock (_gate)
        {
            _devices.RemoveAll(d => d.Id == device.Id);
            _devices.Add(device);
            _state[device.Id] = new DeviceState { Volume = volume, Channels = [volume, volume] };
            if (makeDefault)
            {
                TrySetDefaultDevice(device.Id, device.Flow);
            }
        }

        PublishDevices();
    }

    /// <summary>Disconnects a device; the default falls back to the first remaining one.</summary>
    public void Disconnect(string deviceId)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                return;
            }

            _devices.Remove(device);
            if (_defaultOutput == deviceId)
            {
                _defaultOutput = _devices.FirstOrDefault(d => d.Flow == AudioFlow.Render)?.Id;
            }

            if (_defaultInput == deviceId)
            {
                _defaultInput = _devices.FirstOrDefault(d => d.Flow == AudioFlow.Capture)?.Id;
            }
        }

        PublishDevices();
    }

    /// <summary>The default changed outside the app (Windows Settings).</summary>
    public void SetDefaultExternally(string deviceId, AudioFlow flow)
    {
        lock (_gate)
        {
            TrySetDefaultDevice(deviceId, flow);
        }

        PublishDevices();
    }

    /// <summary>A device whose mute switch ignores writes (exercises the level fallback).</summary>
    public void SetMuteBroken(string deviceId, bool broken)
    {
        lock (_gate)
        {
            if (_state.TryGetValue(deviceId, out var s))
            {
                s.MuteWorks = !broken;
            }
        }
    }

    public void SetEndpointVolumeExternally(string deviceId, float volume, bool? muted = null)
    {
        lock (_gate)
        {
            if (_state.TryGetValue(deviceId, out var s))
            {
                s.Volume = volume;
                s.Muted = muted ?? s.Muted;
            }
        }

        PublishDevices();
    }

    public void AddSession(AudioSessionInfo session)
    {
        lock (_gate)
        {
            _sessions.RemoveAll(s => s.Key == session.Key);
            _sessions.Add(session);
        }

        PublishSessions();
    }

    public void RemoveSession(string key)
    {
        lock (_gate)
        {
            _sessions.RemoveAll(s => s.Key == key);
        }

        PublishSessions();
    }

    /// <summary>Windows' own mixer (or the app) changed a session.</summary>
    public void ChangeSessionExternally(string key, float volume, bool muted)
    {
        UpdateSessions([key], s => s with { Volume = volume, Muted = muted });
        if (_observeSessions)
        {
            SessionVolumeChanged?.Invoke(this, new AudioSessionVolumeEventArgs(key, volume, muted));
        }
    }

    public AudioSessionInfo? Session(string key)
    {
        lock (_gate)
        {
            return _sessions.FirstOrDefault(s => s.Key == key);
        }
    }

    /// <summary>Removes every device and session (tests start from a blank system).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _devices.Clear();
            _state.Clear();
            _sessions.Clear();
            _routes.Clear();
            _defaultOutput = null;
            _defaultInput = null;
        }

        PublishDevices();
        PublishSessions();
    }

    /// <summary>Restores the sample PC: four outputs, three microphones, five apps.</summary>
    public void LoadSampleSetup()
    {
        lock (_gate)
        {
            _devices.Clear();
            _state.Clear();
            _sessions.Clear();
            _routes.Clear();
            AddDevice(new AudioDevice { Id = SpeakersId, Name = "Speakers (Realtek(R) Audio)", Flow = AudioFlow.Render, FormFactor = AudioFormFactor.Speakers, Tier = AudioDeviceTier.BuiltIn }, 0.64f);
            AddDevice(new AudioDevice { Id = HeadphonesId, Name = "Headphones (WH-1000XM4 Stereo)", Flow = AudioFlow.Render, FormFactor = AudioFormFactor.Headphones, Tier = AudioDeviceTier.Hardware, IsHeadphones = true, IsBluetooth = true }, 0.35f);
            AddDevice(new AudioDevice { Id = DisplayId, Name = "LG ULTRAGEAR (NVIDIA High Definition Audio)", Flow = AudioFlow.Render, FormFactor = AudioFormFactor.DigitalAudioDisplayDevice, Tier = AudioDeviceTier.Hardware }, 1f);
            AddDevice(new AudioDevice { Id = CableOutId, Name = "CABLE Input (VB-Audio Virtual Cable)", Flow = AudioFlow.Render, FormFactor = AudioFormFactor.Speakers, Tier = AudioDeviceTier.Virtual }, 1f);
            AddDevice(new AudioDevice { Id = MicrophoneId, Name = "Microphone Array (Realtek(R) Audio)", Flow = AudioFlow.Capture, FormFactor = AudioFormFactor.Microphone, Tier = AudioDeviceTier.BuiltIn }, 0.8f);
            AddDevice(new AudioDevice { Id = HeadsetMicId, Name = "Headset Microphone (WH-1000XM4 Hands-Free)", Flow = AudioFlow.Capture, FormFactor = AudioFormFactor.Headset, Tier = AudioDeviceTier.Hardware, IsHeadphones = true, IsBluetooth = true }, 0.7f);
            AddDevice(new AudioDevice { Id = CableInId, Name = "CABLE Output (VB-Audio Virtual Cable)", Flow = AudioFlow.Capture, FormFactor = AudioFormFactor.LineLevel, Tier = AudioDeviceTier.Virtual }, 1f);
            _defaultOutput = SpeakersId;
            _defaultInput = MicrophoneId;

            _sessions.Add(SampleSession("spotify", 4012, @"c:\users\demo\appdata\roaming\spotify\spotify.exe", "Spotify", new SKColor(0x1D, 0xB9, 0x54), active: true, volume: 0.72f));
            _sessions.Add(SampleSession("edge-1", 7720, @"c:\program files (x86)\microsoft\edge\application\msedge.exe", "Microsoft Edge", new SKColor(0x0C, 0x7C, 0xD5), active: true, volume: 1f));
            _sessions.Add(SampleSession("edge-2", 7744, @"c:\program files (x86)\microsoft\edge\application\msedge.exe", "Microsoft Edge", new SKColor(0x0C, 0x7C, 0xD5), active: false, volume: 1f));
            _sessions.Add(SampleSession("discord", 9120, @"c:\users\demo\appdata\local\discord\app-1.0.9\discord.exe", "Discord", new SKColor(0x58, 0x65, 0xF2), active: false, volume: 0.45f, muted: true));
            _sessions.Add(SampleSession("teams", 10244, null, "Microsoft Teams", new SKColor(0x50, 0x59, 0xC9), active: false, volume: 1f, persistenceId: "MSTeams_8wekyb3d8bbwe!MSTeams"));
            _sessions.Add(new AudioSessionInfo
            {
                Key = "fake-session-system",
                DeviceId = SpeakersId,
                IsSystemSounds = true,
                Volume = 0.8f,
                App = new AudioAppInfo { GroupId = "system:sounds", PersistenceId = "system:sounds", DisplayName = "System sounds" },
            });
        }

        PublishDevices();
        PublishSessions();
    }

    // ── Internals ───────────────────────────────────────────────────────

    private void AddDevice(AudioDevice device, float volume)
    {
        _devices.Add(device);
        _state[device.Id] = new DeviceState { Volume = volume, Channels = [volume, volume] };
    }

    private static AudioSessionInfo SampleSession(string key, int pid, string? path, string name, SKColor color, bool active, float volume, bool muted = false, string? persistenceId = null)
    {
        var id = persistenceId ?? path!;
        return new AudioSessionInfo
        {
            Key = "fake-session-" + key,
            DeviceId = SpeakersId,
            ProcessId = pid,
            IsActive = active,
            Volume = volume,
            Muted = muted,
            App = new AudioAppInfo { PersistenceId = id, GroupId = id, DisplayName = name, ExecutablePath = path, Icon = RenderIcon(name, color) },
        };
    }

    /// <summary>A rounded tile with the app's initial, standing in for the executable's icon.</summary>
    public static PixelBuffer RenderIcon(string name, SKColor color, int size = 32)
    {
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(size, size));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var fill = new SKPaint { IsAntialias = true, Color = color };
        canvas.DrawRoundRect(new SKRect(1, 1, size - 1, size - 1), size * 0.22f, size * 0.22f, fill);
        using var font = new SKFont(SKTypeface.Default, size * 0.55f) { Embolden = true };
        using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
        var letter = name[..1].ToUpperInvariant();
        var width = font.MeasureText(letter);
        canvas.DrawText(letter, (size - width) / 2, (size / 2f) + (font.Size * 0.36f), SKTextAlign.Left, font, text);
        using var image = surface.Snapshot();
        return SkiaConvert.ToPixelBuffer(image);
    }

    private void UpdateSessions(IReadOnlyList<string> keys, Func<AudioSessionInfo, AudioSessionInfo> change)
    {
        var any = false;
        lock (_gate)
        {
            for (var i = 0; i < _sessions.Count; i++)
            {
                if (keys.Contains(_sessions[i].Key))
                {
                    _sessions[i] = change(_sessions[i]);
                    any = true;
                }
            }
        }

        if (any)
        {
            PublishSessions();
        }
    }

    private void PublishDevices()
    {
        AudioSnapshot next;
        lock (_gate)
        {
            if (!_observeDevices)
            {
                next = AudioSnapshot.Empty;
            }
            else
            {
                next = new AudioSnapshot
                {
                    Outputs = _devices.Where(d => d.Flow == AudioFlow.Render).ToList(),
                    Inputs = _devices.Where(d => d.Flow == AudioFlow.Capture).ToList(),
                    DefaultOutputId = _defaultOutput,
                    DefaultInputId = _defaultInput,
                    Volumes = _devices.Where(d => _state.ContainsKey(d.Id)).ToDictionary(d => d.Id, d => ReadVolume(d.Id)!, StringComparer.Ordinal),
                    Generation = ++_generation,
                    IsLive = true,
                };
            }

            if (SameAs(Snapshot, next))
            {
                return;
            }

            Snapshot = next;
        }

        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishSessions()
    {
        lock (_gate)
        {
            Sessions = _observeSessions ? _sessions.ToList() : [];
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool SameAs(AudioSnapshot a, AudioSnapshot b) =>
        a.IsLive == b.IsLive
        && a.DefaultOutputId == b.DefaultOutputId
        && a.DefaultInputId == b.DefaultInputId
        && a.Outputs.SequenceEqual(b.Outputs)
        && a.Inputs.SequenceEqual(b.Inputs)
        && a.Volumes.Count == b.Volumes.Count
        && a.Volumes.All(kv => b.Volumes.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private sealed class DeviceState
    {
        public float Volume { get; set; }

        public bool Muted { get; set; }

        public bool CanSetVolume { get; set; } = true;

        public bool MuteWorks { get; set; } = true;

        public bool Capturing { get; set; }

        public float[] Channels { get; set; } = [];
    }
}
