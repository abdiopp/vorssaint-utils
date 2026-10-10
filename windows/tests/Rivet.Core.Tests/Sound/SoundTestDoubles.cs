// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Sound;

namespace Rivet.Core.Tests.Sound;

/// <summary>A scriptable audio system: everything synchronous, every write recorded.</summary>
internal sealed class TestAudioPlatform : IAudioPlatform, IAudioEndpointAccess
{
    private readonly List<AudioDevice> _devices = [];
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private readonly List<AudioSessionInfo> _sessions = [];
    private string? _defaultOutput;
    private string? _defaultInput;
    private bool _observeDevices;
    private bool _observeSessions;
    private long _generation;

    public event EventHandler? SnapshotChanged;

    public event EventHandler? SessionsChanged;

    public event EventHandler<AudioSessionVolumeEventArgs>? SessionVolumeChanged;

    public AudioCapabilities Capabilities { get; set; } = new() { CanSetDefaultDevice = true, CanRouteApps = true, HasSessions = true };

    public AudioSnapshot Snapshot { get; private set; } = AudioSnapshot.Empty;

    public IReadOnlyList<AudioSessionInfo> Sessions { get; private set; } = [];

    public Dictionary<int, string?> Routes { get; } = [];

    public int ClearRoutesCount { get; private set; }

    public List<(string Key, float Volume)> SessionVolumeWrites { get; } = [];

    public List<(string Key, bool Muted)> SessionMuteWrites { get; } = [];

    public List<(string Id, AudioFlow Flow)> DefaultWrites { get; } = [];

    public List<(string Id, float Volume)> EndpointVolumeWrites { get; } = [];

    /// <summary>When false, every default-device change is refused.</summary>
    public bool AcceptDefaultChanges { get; set; } = true;

    public bool ObservesDevices => _observeDevices;

    public bool ObservesSessions => _observeSessions;

    public void SetObservation(bool devices, bool sessions)
    {
        devices |= sessions;
        _observeDevices = devices;
        _observeSessions = sessions;
        Publish();
        PublishSessions();
    }

    public Task<T> InvokeAsync<T>(Func<IAudioEndpointAccess, T> work)
    {
        var result = work(this);
        Publish();
        return Task.FromResult(result);
    }

    public void SetSessionVolume(IReadOnlyList<string> sessionKeys, float volume)
    {
        foreach (var key in sessionKeys)
        {
            SessionVolumeWrites.Add((key, volume));
        }

        Update(sessionKeys, s => s with { Volume = volume });
    }

    public void SetSessionMute(IReadOnlyList<string> sessionKeys, bool muted)
    {
        foreach (var key in sessionKeys)
        {
            SessionMuteWrites.Add((key, muted));
        }

        Update(sessionKeys, s => s with { Muted = muted });
    }

    public Task<bool> SetAppOutputAsync(IReadOnlyList<int> processIds, string? deviceId)
    {
        foreach (var pid in processIds)
        {
            Routes[pid] = deviceId;
        }

        Update(_sessions.Where(s => processIds.Contains(s.ProcessId)).Select(s => s.Key).ToList(), s => s with { RoutedDeviceId = deviceId });
        return Task.FromResult(Capabilities.CanRouteApps);
    }

    public Task<bool> ClearAppOutputsAsync()
    {
        ClearRoutesCount++;
        Routes.Clear();
        Update(_sessions.Select(s => s.Key).ToList(), s => s with { RoutedDeviceId = null });
        return Task.FromResult(true);
    }

    /// <summary>A route Windows already had for this app (e.g. chosen in Windows Settings).</summary>
    public void SetWindowsRoute(string key, string? deviceId) => Update([key], s => s with { RoutedDeviceId = deviceId });

    // ── IAudioEndpointAccess ────────────────────────────────────────────

    public IReadOnlyList<AudioDevice> ActiveDevices(AudioFlow flow) => _devices.Where(d => d.Flow == flow).ToList();

    public string? DefaultDeviceId(AudioFlow flow) => flow == AudioFlow.Render ? _defaultOutput : _defaultInput;

    public EndpointVolume? ReadVolume(string deviceId) =>
        _endpoints.TryGetValue(deviceId, out var e) && !e.Unreadable && _devices.Any(d => d.Id == deviceId)
            ? new EndpointVolume { Scalar = e.Volume, Muted = e.Muted, CanSetVolume = e.CanSetVolume, ChannelCount = e.Channels.Length }
            : null;

    public bool TrySetVolume(string deviceId, float scalar)
    {
        if (!_endpoints.TryGetValue(deviceId, out var e) || !e.CanSetVolume || e.VolumeIgnored)
        {
            return false;
        }

        EndpointVolumeWrites.Add((deviceId, scalar));
        e.Volume = scalar;
        return true;
    }

    public bool TrySetMute(string deviceId, bool muted)
    {
        if (!_endpoints.TryGetValue(deviceId, out var e))
        {
            return false;
        }

        if (e.MuteBroken)
        {
            return true; // Accepted and ignored, as some drivers do.
        }

        e.Muted = muted;
        return true;
    }

    public IReadOnlyList<float>? ReadChannelVolumes(string deviceId) =>
        _endpoints.TryGetValue(deviceId, out var e) ? e.Channels.ToArray() : null;

    public bool TrySetChannelVolume(string deviceId, int channel, float scalar)
    {
        if (!_endpoints.TryGetValue(deviceId, out var e) || channel >= e.Channels.Length)
        {
            return false;
        }

        e.Channels[channel] = scalar;
        return true;
    }

    public bool TrySetDefaultDevice(string deviceId, AudioFlow flow)
    {
        if (!AcceptDefaultChanges || !_devices.Any(d => d.Id == deviceId && d.Flow == flow))
        {
            return false;
        }

        DefaultWrites.Add((deviceId, flow));
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

    public bool IsCapturing(string deviceId) => _endpoints.TryGetValue(deviceId, out var e) && e.Capturing;

    // ── Scripting ───────────────────────────────────────────────────────

    public static AudioDevice Output(string id, string name, AudioDeviceTier tier = AudioDeviceTier.Hardware, bool headphones = false) =>
        new() { Id = id, Name = name, Flow = AudioFlow.Render, Tier = tier, IsHeadphones = headphones, FormFactor = headphones ? AudioFormFactor.Headphones : AudioFormFactor.Speakers };

    public static AudioDevice Input(string id, string name, AudioDeviceTier tier = AudioDeviceTier.Hardware) =>
        new() { Id = id, Name = name, Flow = AudioFlow.Capture, Tier = tier, FormFactor = AudioFormFactor.Microphone };

    public Endpoint Add(AudioDevice device, float volume = 0.5f, bool makeDefault = false, bool publish = true)
    {
        _devices.RemoveAll(d => d.Id == device.Id);
        _devices.Add(device);
        var endpoint = new Endpoint { Volume = volume, Channels = [volume, volume] };
        _endpoints[device.Id] = endpoint;
        if (makeDefault || (device.Flow == AudioFlow.Render ? _defaultOutput : _defaultInput) is null)
        {
            if (device.Flow == AudioFlow.Render)
            {
                _defaultOutput = device.Id;
            }
            else
            {
                _defaultInput = device.Id;
            }
        }

        if (publish)
        {
            Publish();
        }

        return endpoint;
    }

    /// <summary>Disconnects; Windows picks <paramref name="fallback"/> (or the first remaining) as the new default.</summary>
    public void Remove(string id, string? fallback = null)
    {
        var device = _devices.FirstOrDefault(d => d.Id == id);
        if (device is null)
        {
            return;
        }

        _devices.Remove(device);
        if (_defaultOutput == id)
        {
            _defaultOutput = fallback ?? _devices.FirstOrDefault(d => d.Flow == AudioFlow.Render)?.Id;
        }

        if (_defaultInput == id)
        {
            _defaultInput = fallback ?? _devices.FirstOrDefault(d => d.Flow == AudioFlow.Capture)?.Id;
        }

        Publish();
    }

    public Endpoint EndpointOf(string id) => _endpoints[id];

    public void SetDefault(string id, AudioFlow flow)
    {
        if (flow == AudioFlow.Render)
        {
            _defaultOutput = id;
        }
        else
        {
            _defaultInput = id;
        }

        Publish();
    }

    public void AddSession(string key, int pid, string? persistenceId, string name, float volume = 1f, bool active = true, bool muted = false, bool system = false, string device = "out")
    {
        _sessions.RemoveAll(s => s.Key == key);
        _sessions.Add(new AudioSessionInfo
        {
            Key = key,
            DeviceId = device,
            ProcessId = pid,
            IsSystemSounds = system,
            IsActive = active,
            Volume = volume,
            Muted = muted,
            App = system
                ? new AudioAppInfo { GroupId = MixerRow.SystemSoundsId, PersistenceId = MixerRow.SystemSoundsId, DisplayName = "System sounds" }
                : new AudioAppInfo { PersistenceId = persistenceId, GroupId = persistenceId ?? $"process:{pid}", DisplayName = name },
        });
        PublishSessions();
    }

    public void RemoveSession(string key)
    {
        _sessions.RemoveAll(s => s.Key == key);
        PublishSessions();
    }

    /// <summary>Windows' own mixer (or the app) changed a session.</summary>
    public void ChangeExternally(string key, float volume, bool muted = false)
    {
        Update([key], s => s with { Volume = volume, Muted = muted });
        SessionVolumeChanged?.Invoke(this, new AudioSessionVolumeEventArgs(key, volume, muted));
    }

    public AudioSessionInfo SessionOf(string key) => _sessions.Single(s => s.Key == key);

    public void Publish()
    {
        Snapshot = !_observeDevices
            ? AudioSnapshot.Empty
            : new AudioSnapshot
            {
                Outputs = _devices.Where(d => d.Flow == AudioFlow.Render).ToList(),
                Inputs = _devices.Where(d => d.Flow == AudioFlow.Capture).ToList(),
                DefaultOutputId = _defaultOutput,
                DefaultInputId = _defaultInput,
                Volumes = _devices.Where(d => ReadVolume(d.Id) is not null).ToDictionary(d => d.Id, d => ReadVolume(d.Id)!),
                Generation = ++_generation,
                IsLive = true,
            };
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishSessions()
    {
        Sessions = _observeSessions ? _sessions.ToList() : [];
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Update(IReadOnlyList<string> keys, Func<AudioSessionInfo, AudioSessionInfo> change)
    {
        for (var i = 0; i < _sessions.Count; i++)
        {
            if (keys.Contains(_sessions[i].Key))
            {
                _sessions[i] = change(_sessions[i]);
            }
        }

        PublishSessions();
    }

    public sealed class Endpoint
    {
        public float Volume { get; set; }

        public bool Muted { get; set; }

        public bool CanSetVolume { get; set; } = true;

        /// <summary>Writes report success but change nothing.</summary>
        public bool VolumeIgnored { get; set; }

        /// <summary>The mute switch accepts writes and ignores them.</summary>
        public bool MuteBroken { get; set; }

        public bool Unreadable { get; set; }

        public bool Capturing { get; set; }

        public float[] Channels { get; set; } = [];
    }
}

/// <summary>Time that only moves when a test says so; due timers fire in order.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    private DateTimeOffset _now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _ticks + by.Ticks;
        while (true)
        {
            var next = _timers.Where(t => t.DueAt is { } due && due <= target).OrderBy(t => t.DueAt).FirstOrDefault();
            if (next is null)
            {
                break;
            }

            _ticks = next.DueAt!.Value;
            next.Fire();
        }

        _now += by;
        _ticks = target;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public long? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : owner._ticks + _period.Ticks;
            callback(state);
        }

        public void Dispose()
        {
            DueAt = null;
            owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keyboard hook stand-in: tests push key events through <see cref="Raise"/>.</summary>
internal sealed class TestInputHooks : IInputHooks
{
    private readonly List<KeyboardHookHandler> _keyboard = [];

    public int KeyboardSubscribers => _keyboard.Count;

    public IDisposable SubscribeKeyboard(KeyboardHookHandler handler, int priority = 0)
    {
        _keyboard.Add(handler);
        return new Unsubscribe(() => _keyboard.Remove(handler));
    }

    public IDisposable SubscribeMouse(MouseHookHandler handler, int priority = 0) => new Unsubscribe(() => { });

    /// <summary>Returns true when a subscriber swallowed the key.</summary>
    public bool Raise(int virtualKey, KeyAction action, Shortcuts.KeyModifiers modifiers = Shortcuts.KeyModifiers.None)
    {
        var e = new KeyboardHookEvent { VirtualKey = virtualKey, Action = action, Modifiers = modifiers };
        foreach (var handler in _keyboard.ToArray())
        {
            if (handler(ref e))
            {
                return true;
            }
        }

        return false;
    }

    public void SendKeys(IReadOnlyList<(int VirtualKey, KeyAction Action)> strokes)
    {
    }

    public void SendText(string text)
    {
    }

    public void SendWheel(int delta, bool horizontal)
    {
    }

    public void SendMouse(MouseHookKind kind, int xButton = 0)
    {
    }

    public bool IsKeyDown(int virtualKey) => false;

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
