// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using NAudio.CoreAudioApi;
using Rivet.Core.Diagnostics;
using Rivet.Core.Sound;

namespace Rivet.Platform.Windows.Sound;

/// <summary>
/// The Windows audio system (spec §3.9.8–§3.14 Windows mappings).
///
/// Devices, properties, endpoint volume and device notifications come from
/// NAudio's Core Audio wrappers; sessions, the default-device switch and
/// per-app routing use direct COM (see <see cref="SessionTracker"/>,
/// <see cref="PolicyConfigClient"/>, <see cref="AudioPolicyRouting"/>).
///
/// Threading: every COM object is created and used on one MTA
/// <see cref="AudioThread"/>; it is created there so NAudio's endpoint
/// volume (which captures the creating thread's SynchronizationContext)
/// calls back directly. Callbacks arrive on the audio service's threads and
/// only post work. Notification bursts fold into one refresh (0.2 s).
/// </summary>
public sealed class WindowsAudioPlatform : IAudioPlatform, ISessionEventTarget, IDisposable
{
    /// <summary>Event context of every write this app makes, so its own changes are recognized.</summary>
    internal static readonly Guid EventContext = new("9b7f3c2e-41d6-4c8a-a5e1-6f0d2b9c7e14");

    private static readonly PropertyKey EnumeratorNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly TimeSpan DeviceCoalesce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SessionCoalesce = TimeSpan.FromMilliseconds(60);
    private static readonly ERole[] AllRoles = [ERole.Console, ERole.Multimedia, ERole.Communications];

    private readonly AudioThread _thread = new("Audio");
    private readonly object _gate = new();
    private readonly Timer _deviceTimer;
    private readonly Timer _sessionTimer;
    private readonly Dictionary<string, float> _pendingVolumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pendingMutes = new(StringComparer.Ordinal);
    private readonly EndpointAccess _access;
    private bool _writesQueued;
    private bool _deviceRefreshQueued;
    private bool _volumesOnly = true;
    private long _lastDeviceRefresh;
    private bool _sessionPublishQueued;
    private volatile AudioSnapshot _snapshot = AudioSnapshot.Empty;
    private volatile IReadOnlyList<AudioSessionInfo> _sessions = [];
    private volatile AudioCapabilities _capabilities = AudioCapabilities.None;

    // Audio-thread state.
    private readonly Dictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private MMDeviceEnumerator? _enumerator;
    private IMMDeviceEnumerator? _coreEnumerator;
    private MMDeviceNotificationClient? _notifications;
    private SessionTracker? _tracker;
    private AudioAppResolver? _resolver;
    private PolicyConfigClient? _policy;
    private AudioPolicyRouting? _routing;
    private bool _probed;
    private bool _observeDevices;
    private bool _observeSessions;
    private long _generation;

    public WindowsAudioPlatform()
    {
        _access = new EndpointAccess(this);
        _deviceTimer = new Timer(_ => _thread.Post(RefreshDevices), null, Timeout.Infinite, Timeout.Infinite);
        _sessionTimer = new Timer(_ => _thread.Post(PublishSessions), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? SnapshotChanged;

    public event EventHandler? SessionsChanged;

    public event EventHandler<AudioSessionVolumeEventArgs>? SessionVolumeChanged;

    public AudioCapabilities Capabilities => _capabilities;

    public AudioSnapshot Snapshot => _snapshot;

    public IReadOnlyList<AudioSessionInfo> Sessions => _sessions;

    // ── IAudioPlatform ──────────────────────────────────────────────────

    public void SetObservation(bool devices, bool sessions)
    {
        devices |= sessions;
        _thread.Post(() => ApplyObservation(devices, sessions));
    }

    public Task<T> InvokeAsync<T>(Func<IAudioEndpointAccess, T> work) => _thread.InvokeAsync(() =>
    {
        if (!EnsureEnumerator())
        {
            throw new InvalidOperationException("The Windows audio system is not available.");
        }

        return work(_access);
    });

    public void SetSessionVolume(IReadOnlyList<string> sessionKeys, float volume) =>
        QueueSessionWrites(() =>
        {
            foreach (var key in sessionKeys)
            {
                _pendingVolumes[key] = volume;
            }
        });

    public void SetSessionMute(IReadOnlyList<string> sessionKeys, bool muted) =>
        QueueSessionWrites(() =>
        {
            foreach (var key in sessionKeys)
            {
                _pendingMutes[key] = muted;
            }
        });

    public Task<bool> SetAppOutputAsync(IReadOnlyList<int> processIds, string? deviceId) => _thread.InvokeAsync(() =>
    {
        if (!EnsureEnumerator() || _routing is null)
        {
            return false;
        }

        var ok = true;
        var pids = processIds.Where(p => p > 0).Distinct().ToList();
        foreach (var pid in pids)
        {
            // Console and multimedia are the roles apps open their streams with.
            foreach (var role in new[] { ERole.Console, ERole.Multimedia })
            {
                var hr = _routing.SetPersistedDefaultEndpoint((uint)pid, EDataFlow.Render, role, deviceId);
                if (hr < 0)
                {
                    ok = false;
                    Log.Warn("sound", $"Per-app output was refused (0x{hr:X8}).");
                }
            }
        }

        if (ok && _tracker is { } tracker)
        {
            tracker.SetRouted(pids, deviceId);
            RequestSessionPublish();
        }

        return ok;
    });

    public Task<bool> ClearAppOutputsAsync() => _thread.InvokeAsync(() =>
    {
        if (!EnsureEnumerator() || _routing is null)
        {
            return false;
        }

        var hr = _routing.ClearAllPersistedApplicationDefaultEndpoints();
        if (hr < 0)
        {
            Log.Warn("sound", $"Clearing per-app outputs was refused (0x{hr:X8}).");
        }
        else if (_tracker is { } tracker)
        {
            tracker.SetRouted(null, null);
            RequestSessionPublish();
        }

        return hr >= 0;
    });

    /// <summary>What Windows has persisted for the app of <paramref name="processId"/> (audio thread).</summary>
    private string? ReadPersistedRoute(int processId)
    {
        if (_routing is null)
        {
            return null;
        }

        try
        {
            return _routing.GetPersistedDefaultEndpoint((uint)processId, EDataFlow.Render, ERole.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Debug("sound", $"Could not read an app's output: {ex.Message}");
            return null;
        }
    }

    // ── Session callbacks (audio service threads) ───────────────────────

    void ISessionEventTarget.SessionCreated(string deviceId) => _thread.Post(() =>
    {
        if (_tracker is { } tracker)
        {
            tracker.Rescan(deviceId);
            RequestSessionPublish();
        }
    });

    void ISessionEventTarget.SessionVolumeChanged(string key, float volume, bool muted, Guid context) => _thread.Post(() =>
    {
        if (_tracker is null || !_tracker.UpdateVolume(key, volume, muted))
        {
            return;
        }

        if (context == EventContext)
        {
            RequestSessionPublish();
            return;
        }

        PublishSessions();
        SessionVolumeChanged?.Invoke(this, new AudioSessionVolumeEventArgs(key, volume, muted));
    });

    void ISessionEventTarget.SessionStateChanged(string key, SessionState state) => _thread.Post(() =>
    {
        if (_tracker is { } tracker)
        {
            tracker.UpdateState(key, state);
            RequestSessionPublish();
        }
    });

    void ISessionEventTarget.SessionGone(string key) => _thread.Post(() =>
    {
        if (_tracker is { } tracker)
        {
            tracker.Remove(key);
            RequestSessionPublish();
        }
    });

    // ── Observation (audio thread) ──────────────────────────────────────

    private bool EnsureEnumerator()
    {
        if (_enumerator is not null)
        {
            return true;
        }

        try
        {
            _enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            Log.Error("sound", "The Windows audio system is not available.", ex);
            return false;
        }

        if (!_probed)
        {
            _probed = true;
            _policy = PolicyConfigClient.TryCreate();
            _routing = AudioPolicyRouting.TryCreate(Environment.OSVersion.Version.Build);
            _capabilities = new AudioCapabilities
            {
                CanSetDefaultDevice = _policy is not null,
                CanRouteApps = _routing is not null,
                HasSessions = true,
            };
            Log.Info("sound", $"Audio: default switch {(_policy is null ? "unavailable" : "available")}, per-app output {(_routing is null ? "unavailable" : "available")}.");
        }

        return true;
    }

    private IMMDeviceEnumerator? CoreEnumerator()
    {
        if (_coreEnumerator is null
            && SoundNative.CoCreateInstance(ComObjects.ClsidMMDeviceEnumerator, 0, SoundNative.ClsctxAll, ComObjects.IidMMDeviceEnumerator, out var pointer) >= 0)
        {
            _coreEnumerator = ComObjects.Wrap<IMMDeviceEnumerator>(pointer);
        }

        return _coreEnumerator;
    }

    private void ApplyObservation(bool devices, bool sessions)
    {
        if (!EnsureEnumerator())
        {
            return;
        }

        if (devices && !_observeDevices)
        {
            _observeDevices = true;
            try
            {
                _notifications = _enumerator!.CreateNotificationClient(useSynchronizationContext: false);
                _notifications.DeviceAdded += OnDeviceListChanged;
                _notifications.DeviceRemoved += OnDeviceListChanged;
                _notifications.DeviceStateChanged += OnDeviceListChanged;
                _notifications.DefaultDeviceChanged += OnDeviceListChanged;
                _notifications.PropertyValueChanged += OnPropertyValueChanged;
            }
            catch (Exception ex)
            {
                Log.Warn("sound", "Could not listen for audio device changes.", ex);
            }

            lock (_gate)
            {
                _volumesOnly = false;
            }

            RefreshDevices();
        }
        else if (!devices && _observeDevices)
        {
            _observeDevices = false;
            StopNotifications();
            DisposeEndpoints();
            _snapshot = AudioSnapshot.Empty;
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        }

        if (sessions && _observeDevices && !_observeSessions)
        {
            if (CoreEnumerator() is { } coreEnumerator)
            {
                _observeSessions = true;
                _resolver ??= new AudioAppResolver(_thread, RequestSessionPublish);
                _tracker = new SessionTracker(coreEnumerator, EventContext, _resolver, this, ReadPersistedRoute);
                _tracker.SyncEndpoints(_snapshot.Outputs.Select(d => d.Id).ToList());
                PublishSessions();
            }
            else
            {
                Log.Warn("sound", "Could not create the session enumerator; per-app volume is unavailable.");
            }
        }
        else if (!sessions && _observeSessions)
        {
            _observeSessions = false;
            _tracker?.Dispose();
            _tracker = null;
            _sessions = [];
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDeviceListChanged(object? sender, EventArgs e) => RequestDeviceRefresh(full: true);

    private void OnPropertyValueChanged(object? sender, DevicePropertyChangedEventArgs e)
    {
        // Names and form factors change rarely; everything else (levels, formats) is not shown.
        var key = e.PropertyKey;
        if (Same(key, PropertyKeys.PKEY_Device_FriendlyName) || Same(key, PropertyKeys.PKEY_AudioEndpoint_FormFactor) || Same(key, PropertyKeys.PKEY_Device_DeviceDesc))
        {
            var id = e.DeviceId;
            _thread.Post(() =>
            {
                if (_endpoints.TryGetValue(id, out var endpoint))
                {
                    endpoint.Stale = true;
                }
            });
            RequestDeviceRefresh(full: true);
        }
    }

    private static bool Same(PropertyKey a, PropertyKey b) => a.formatId == b.formatId && a.propertyId == b.propertyId;

    /// <summary>
    /// An isolated notification refreshes at once; notifications within 0.2 s
    /// of the last refresh fold into one trailing refresh. Volume-only changes
    /// re-read just the levels.
    /// </summary>
    private void RequestDeviceRefresh(bool full)
    {
        lock (_gate)
        {
            if (full)
            {
                _volumesOnly = false;
            }

            if (_deviceRefreshQueued)
            {
                return;
            }

            _deviceRefreshQueued = true;
            var since = Stopwatch.GetElapsedTime(_lastDeviceRefresh);
            if (since >= DeviceCoalesce)
            {
                _thread.Post(RefreshDevices);
            }
            else
            {
                _deviceTimer.Change(DeviceCoalesce - since, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RefreshDevices()
    {
        bool volumesOnly;
        lock (_gate)
        {
            _deviceRefreshQueued = false;
            volumesOnly = _volumesOnly && _snapshot.IsLive;
            _volumesOnly = true;
            _lastDeviceRefresh = Stopwatch.GetTimestamp();
        }

        if (!_observeDevices || _enumerator is null)
        {
            return;
        }

        try
        {
            var previous = _snapshot;
            IReadOnlyList<AudioDevice> outputs, inputs;
            string? defaultOutput, defaultInput;
            if (volumesOnly)
            {
                (outputs, inputs, defaultOutput, defaultInput) = (previous.Outputs, previous.Inputs, previous.DefaultOutputId, previous.DefaultInputId);
            }
            else
            {
                outputs = ReadActive(DataFlow.Render);
                inputs = ReadActive(DataFlow.Capture);
                var live = outputs.Concat(inputs).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var gone in _endpoints.Keys.Where(id => !live.Contains(id)).ToList())
                {
                    Evict(gone);
                }

                defaultOutput = DefaultId(DataFlow.Render);
                defaultInput = DefaultId(DataFlow.Capture);
            }

            var volumes = new Dictionary<string, EndpointVolume>(StringComparer.Ordinal);
            foreach (var device in outputs.Concat(inputs))
            {
                if (ReadVolumeState(device.Id) is { } volume)
                {
                    volumes[device.Id] = volume;
                }
            }

            var next = new AudioSnapshot
            {
                Outputs = outputs,
                Inputs = inputs,
                DefaultOutputId = defaultOutput,
                DefaultInputId = defaultInput,
                Volumes = volumes,
                Generation = ++_generation,
                IsLive = true,
            };

            if (!volumesOnly)
            {
                _tracker?.SyncEndpoints(outputs.Select(d => d.Id).ToList());
                RequestSessionPublish();
            }

            if (!SameContent(previous, next))
            {
                _snapshot = next;
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Could not read the audio devices.", ex);
        }
    }

    private static bool SameContent(AudioSnapshot a, AudioSnapshot b) =>
        a.IsLive == b.IsLive
        && a.DefaultOutputId == b.DefaultOutputId
        && a.DefaultInputId == b.DefaultInputId
        && a.Outputs.SequenceEqual(b.Outputs)
        && a.Inputs.SequenceEqual(b.Inputs)
        && a.Volumes.Count == b.Volumes.Count
        && a.Volumes.All(kv => b.Volumes.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // ── Sessions (audio thread) ─────────────────────────────────────────

    private void QueueSessionWrites(Action record)
    {
        lock (_gate)
        {
            record();
            if (_writesQueued)
            {
                return;
            }

            _writesQueued = true;
        }

        _thread.Post(FlushSessionWrites);
    }

    /// <summary>Only the newest value per session is written (a slider drag sends many).</summary>
    private void FlushSessionWrites()
    {
        KeyValuePair<string, float>[] volumes;
        KeyValuePair<string, bool>[] mutes;
        lock (_gate)
        {
            volumes = _pendingVolumes.ToArray();
            mutes = _pendingMutes.ToArray();
            _pendingVolumes.Clear();
            _pendingMutes.Clear();
            _writesQueued = false;
        }

        if (_tracker is not { } tracker)
        {
            return;
        }

        foreach (var (key, volume) in volumes)
        {
            tracker.SetVolume(key, volume);
        }

        foreach (var (key, muted) in mutes)
        {
            tracker.SetMute(key, muted);
        }

        PublishSessions();
    }

    private void RequestSessionPublish()
    {
        lock (_gate)
        {
            if (_sessionPublishQueued)
            {
                return;
            }

            _sessionPublishQueued = true;
        }

        _sessionTimer.Change(SessionCoalesce, Timeout.InfiniteTimeSpan);
    }

    private void PublishSessions()
    {
        lock (_gate)
        {
            _sessionPublishQueued = false;
        }

        _sessions = _tracker?.Snapshot() ?? [];
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Endpoints (audio thread) ────────────────────────────────────────

    private List<AudioDevice> ReadActive(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        using var collection = _enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active);
        foreach (var device in collection)
        {
            string id;
            try
            {
                id = device.ID;
            }
            catch (Exception)
            {
                device.Dispose();
                continue;
            }

            if (_endpoints.TryGetValue(id, out var cached) && !cached.Stale)
            {
                device.Dispose();
                result.Add(cached.Info);
                continue;
            }

            Evict(id);
            var info = ReadInfo(device, id, flow == DataFlow.Render ? AudioFlow.Render : AudioFlow.Capture);
            _endpoints[id] = new Endpoint(device, info);
            result.Add(info);
        }

        return result;
    }

    private string? DefaultId(DataFlow flow)
    {
        try
        {
            if (_enumerator!.TryGetDefaultAudioEndpoint(flow, Role.Console, out var device) && device is not null)
            {
                using (device)
                {
                    return device.ID;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("sound", $"No default {flow} device: {ex.Message}");
        }

        return null;
    }

    private Endpoint? GetEndpoint(string id)
    {
        if (_endpoints.TryGetValue(id, out var endpoint))
        {
            return endpoint;
        }

        MMDevice? device = null;
        try
        {
            device = _enumerator!.GetDevice(id);
            if (device.State != DeviceState.Active)
            {
                device.Dispose();
                return null;
            }

            var flow = device.DataFlow == DataFlow.Capture ? AudioFlow.Capture : AudioFlow.Render;
            endpoint = new Endpoint(device, ReadInfo(device, id, flow));
            _endpoints[id] = endpoint;
            return endpoint;
        }
        catch (Exception)
        {
            device?.Dispose();
            return null;
        }
    }

    private void Evict(string id)
    {
        if (_endpoints.Remove(id, out var endpoint))
        {
            endpoint.Dispose();
        }
    }

    private void DisposeEndpoints()
    {
        foreach (var endpoint in _endpoints.Values)
        {
            endpoint.Dispose();
        }

        _endpoints.Clear();
    }

    private void StopNotifications()
    {
        if (_notifications is { } notifications)
        {
            _notifications = null;
            notifications.DeviceAdded -= OnDeviceListChanged;
            notifications.DeviceRemoved -= OnDeviceListChanged;
            notifications.DeviceStateChanged -= OnDeviceListChanged;
            notifications.DefaultDeviceChanged -= OnDeviceListChanged;
            notifications.PropertyValueChanged -= OnPropertyValueChanged;
            try
            {
                notifications.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("sound", $"Device notifications did not unregister cleanly: {ex.Message}");
            }
        }
    }

    /// <summary>Endpoint volume, created on this thread with the app's event context on every write.</summary>
    private AudioEndpointVolume? VolumeControl(Endpoint endpoint)
    {
        if (endpoint.Volume is { } existing)
        {
            return existing;
        }

        if (endpoint.VolumeFailed)
        {
            return null;
        }

        try
        {
            var volume = endpoint.Device.AudioEndpointVolume;
            volume.NotificationGuid = EventContext;
            for (var i = 0; i < volume.Channels.Count; i++)
            {
                volume.Channels[i].NotificationGuid = EventContext;
            }

            endpoint.Changed = () => RequestDeviceRefresh(full: false);
            volume.OnVolumeNotification += endpoint.OnNotification;
            endpoint.Volume = volume;
            return volume;
        }
        catch (Exception ex)
        {
            endpoint.VolumeFailed = true;
            Log.Debug("sound", $"No volume control on {endpoint.Info.Name}: {ex.Message}");
            return null;
        }
    }

    private EndpointVolume? ReadVolumeState(string id)
    {
        if (GetEndpoint(id) is not { } endpoint || VolumeControl(endpoint) is not { } volume)
        {
            return null;
        }

        try
        {
            var range = volume.VolumeRange;
            return new EndpointVolume
            {
                Scalar = Math.Clamp(volume.MasterVolumeLevelScalar, 0f, 1f),
                Muted = volume.Mute,
                CanSetVolume = range.MaxDecibels - range.MinDecibels > 0.01f,
                ChannelCount = volume.Channels.Count,
            };
        }
        catch (Exception)
        {
            Evict(id);
            return null;
        }
    }

    private static AudioDevice ReadInfo(MMDevice device, string id, AudioFlow flow)
    {
        var name = id;
        try
        {
            name = device.FriendlyName;
        }
        catch (Exception)
        {
        }

        PropertyStore? properties = null;
        try
        {
            properties = device.Properties;
        }
        catch (Exception)
        {
        }

        var formFactor = Property(properties, PropertyKeys.PKEY_AudioEndpoint_FormFactor) is uint value
            ? DeviceClassifier.ParseFormFactor(value)
            : AudioFormFactor.Unknown;
        var enumerator = Property(properties, EnumeratorNameKey) as string
                         ?? DeviceClassifier.EnumeratorFromInstancePath(Property(properties, PropertyKeys.PKEY_Device_ControllerDeviceId) as string)
                         ?? DeviceClassifier.EnumeratorFromInstancePath(Property(properties, PropertyKeys.PKEY_Device_InterfaceKey) as string);
        var description = Property(properties, PropertyKeys.PKEY_Device_DeviceDesc) as string;
        var bluetooth = DeviceClassifier.IsBluetoothEnumerator(enumerator);
        return new AudioDevice
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            Flow = flow,
            FormFactor = formFactor,
            Tier = DeviceClassifier.TierFor(enumerator, name),
            IsBluetooth = bluetooth,
            IsHeadphones = DeviceClassifier.IsHeadphones(formFactor, bluetooth, name, id, description),
        };
    }

    private static object? Property(PropertyStore? store, PropertyKey key)
    {
        if (store is null)
        {
            return null;
        }

        try
        {
            return store.TryGetValue<object>(key, out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _deviceTimer.Dispose();
        _sessionTimer.Dispose();
        _thread.Shutdown(() =>
        {
            _tracker?.Dispose();
            _tracker = null;
            StopNotifications();
            DisposeEndpoints();
            _enumerator?.Dispose();
            _enumerator = null;
            ComObjects.Release(_coreEnumerator);
            _coreEnumerator = null;
            _policy?.Dispose();
            _policy = null;
            _routing?.Dispose();
            _routing = null;
        });
    }

    /// <summary>One active endpoint and the objects kept for it.</summary>
    private sealed class Endpoint(MMDevice device, AudioDevice info) : IDisposable
    {
        public MMDevice Device { get; } = device;

        public AudioDevice Info { get; } = info;

        public AudioEndpointVolume? Volume { get; set; }

        public bool VolumeFailed { get; set; }

        /// <summary>Name or form factor changed: re-read on the next refresh.</summary>
        public bool Stale { get; set; }

        public Action? Changed { get; set; }

        public void OnNotification(AudioVolumeNotificationData data) => Changed?.Invoke();

        public void Dispose()
        {
            if (Volume is { } volume)
            {
                volume.OnVolumeNotification -= OnNotification;
            }

            Changed = null;
            try
            {
                // Also disposes the endpoint volume, which unregisters its callback.
                Device.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Synchronous endpoint operations for work running on the audio thread.</summary>
    private sealed class EndpointAccess(WindowsAudioPlatform platform) : IAudioEndpointAccess
    {
        public IReadOnlyList<AudioDevice> ActiveDevices(AudioFlow flow)
        {
            try
            {
                return platform.ReadActive(flow == AudioFlow.Render ? DataFlow.Render : DataFlow.Capture);
            }
            catch (Exception ex)
            {
                Log.Warn("sound", "Could not list the audio devices.", ex);
                return [];
            }
        }

        public string? DefaultDeviceId(AudioFlow flow) => platform.DefaultId(flow == AudioFlow.Render ? DataFlow.Render : DataFlow.Capture);

        public EndpointVolume? ReadVolume(string deviceId) => platform.ReadVolumeState(deviceId);

        public bool TrySetVolume(string deviceId, float scalar) => Write(deviceId, v => v.MasterVolumeLevelScalar = Math.Clamp(scalar, 0f, 1f));

        public bool TrySetMute(string deviceId, bool muted) => Write(deviceId, v => v.Mute = muted);

        public IReadOnlyList<float>? ReadChannelVolumes(string deviceId)
        {
            if (platform.GetEndpoint(deviceId) is not { } endpoint || platform.VolumeControl(endpoint) is not { } volume)
            {
                return null;
            }

            try
            {
                var channels = volume.Channels;
                var result = new float[channels.Count];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = channels[i].VolumeLevelScalar;
                }

                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool TrySetChannelVolume(string deviceId, int channel, float scalar) => Write(deviceId, v =>
        {
            if (channel < 0 || channel >= v.Channels.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(channel));
            }

            v.Channels[channel].VolumeLevelScalar = Math.Clamp(scalar, 0f, 1f);
        });

        /// <summary>Every role (console, multimedia, communications), confirmed by reading the default back.</summary>
        public bool TrySetDefaultDevice(string deviceId, AudioFlow flow)
        {
            if (platform._policy is not { } policy)
            {
                return false;
            }

            var dataFlow = flow == AudioFlow.Render ? DataFlow.Render : DataFlow.Capture;
            var accepted = true;
            foreach (var role in AllRoles)
            {
                var hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                {
                    accepted = false;
                    Log.Warn("sound", $"Windows refused the default device for {role} (0x{hr:X8}).");
                }
            }

            // The policy service normally applies the change at once; give it a
            // moment before calling a switch that Windows accepted a failure.
            var confirmed = platform.DefaultId(dataFlow) == deviceId;
            for (var attempt = 0; !confirmed && accepted && attempt < 10; attempt++)
            {
                Thread.Sleep(20);
                confirmed = platform.DefaultId(dataFlow) == deviceId;
            }

            if (!confirmed && accepted)
            {
                Log.Warn("sound", "Windows accepted the default device but still reports the previous one.");
            }

            if (confirmed && platform._observeDevices)
            {
                lock (platform._gate)
                {
                    platform._volumesOnly = false;
                }

                platform.RefreshDevices();
            }

            return confirmed;
        }

        public bool IsCapturing(string deviceId) =>
            platform.CoreEnumerator() is not { } enumerator || SessionTracker.IsCapturing(enumerator, deviceId);

        private bool Write(string deviceId, Action<AudioEndpointVolume> write)
        {
            if (platform.GetEndpoint(deviceId) is not { } endpoint || platform.VolumeControl(endpoint) is not { } volume)
            {
                return false;
            }

            try
            {
                write(volume);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("sound", $"Endpoint write failed: {ex.Message}");
                return false;
            }
        }
    }
}
