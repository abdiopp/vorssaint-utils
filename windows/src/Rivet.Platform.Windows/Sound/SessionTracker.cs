// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Rivet.Core.Diagnostics;
using Rivet.Core.Sound;

namespace Rivet.Platform.Windows.Sound;

/// <summary>Receives session callbacks (on the audio service's threads) and forwards them to the audio thread.</summary>
internal interface ISessionEventTarget
{
    void SessionCreated(string deviceId);

    void SessionVolumeChanged(string key, float volume, bool muted, Guid context);

    void SessionStateChanged(string key, SessionState state);

    void SessionGone(string key);
}

/// <summary>A new session appeared on one endpoint.</summary>
[GeneratedComClass]
internal sealed partial class SessionNotificationSink(string deviceId, ISessionEventTarget target) : IAudioSessionNotification
{
    public int OnSessionCreated(nint newSession)
    {
        // The new session is re-read on the audio thread; never call back into Core Audio from here.
        target.SessionCreated(deviceId);
        return 0;
    }
}

/// <summary>One session's volume, state and lifetime.</summary>
[GeneratedComClass]
internal sealed partial class SessionEventsSink(string key, ISessionEventTarget target) : IAudioSessionEvents
{
    public int OnDisplayNameChanged(nint newDisplayName, nint eventContext) => 0;

    public int OnIconPathChanged(nint newIconPath, nint eventContext) => 0;

    public int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext)
    {
        var context = eventContext != 0 ? Marshal.PtrToStructure<Guid>(eventContext) : Guid.Empty;
        target.SessionVolumeChanged(key, newVolume, newMute != 0, context);
        return 0;
    }

    public int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumes, uint changedChannel, nint eventContext) => 0;

    public int OnGroupingParamChanged(nint newGroupingParam, nint eventContext) => 0;

    public int OnStateChanged(SessionState newState)
    {
        target.SessionStateChanged(key, newState);
        return 0;
    }

    public int OnSessionDisconnected(int disconnectReason)
    {
        target.SessionGone(key);
        return 0;
    }
}

/// <summary>
/// The audio sessions of every active output (spec §3.9.8): one
/// IAudioSessionManager2 per endpoint with a session-created callback, and
/// per-session event callbacks. Lives on the audio thread; every COM object it
/// holds is released when a session expires, an endpoint goes away or it is
/// disposed.
/// </summary>
internal sealed class SessionTracker : IDisposable
{
    private const uint DeviceStateActive = 0x1;

    private readonly Guid _eventContext;
    private readonly AudioAppResolver _resolver;
    private readonly ISessionEventTarget _target;
    private readonly Dictionary<string, EndpointSessions> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly Func<int, string?> _readRoute;

    /// <param name="enumerator">Borrowed; the platform owns and releases it.</param>
    /// <param name="readRoute">The app's persisted output for a process (null when it follows the default).</param>
    public SessionTracker(IMMDeviceEnumerator enumerator, Guid eventContext, AudioAppResolver resolver, ISessionEventTarget target, Func<int, string?> readRoute)
    {
        _enumerator = enumerator;
        _readRoute = readRoute;
        _eventContext = eventContext;
        _resolver = resolver;
        _target = target;
    }

    /// <summary>Follows the set of active outputs: new endpoints get a session manager, gone ones are released.</summary>
    public void SyncEndpoints(IReadOnlyCollection<string> renderDeviceIds)
    {
        foreach (var gone in _endpoints.Keys.Where(id => !renderDeviceIds.Contains(id)).ToList())
        {
            RemoveEndpoint(gone);
        }

        foreach (var id in renderDeviceIds)
        {
            if (!_endpoints.ContainsKey(id))
            {
                AddEndpoint(id);
            }
        }
    }

    public void Rescan(string deviceId)
    {
        if (_endpoints.TryGetValue(deviceId, out var endpoint))
        {
            Rescan(endpoint);
        }
    }

    public IReadOnlyList<AudioSessionInfo> Snapshot()
    {
        var result = new List<AudioSessionInfo>(_sessions.Count);
        foreach (var session in _sessions.Values)
        {
            if (!session.IsSystemSounds && session.ProcessId <= 0)
            {
                // A stream no single process owns: nothing to name, remember or route.
                continue;
            }

            var app = session.IsSystemSounds
                ? new AudioAppInfo { GroupId = MixerRow.SystemSoundsId, PersistenceId = MixerRow.SystemSoundsId, DisplayName = "System sounds" }
                : _resolver.Resolve(session.ProcessId, session.DisplayName, session.IconPath);
            result.Add(new AudioSessionInfo
            {
                Key = session.Key,
                DeviceId = session.DeviceId,
                ProcessId = session.ProcessId,
                IsSystemSounds = session.IsSystemSounds,
                IsActive = session.State == SessionState.Active,
                Volume = session.Volume,
                Muted = session.Muted,
                RoutedDeviceId = session.RoutedDeviceId,
                App = app,
            });
        }

        return result;
    }

    /// <summary>Records a change reported by a callback. Returns false for unknown sessions.</summary>
    public bool UpdateVolume(string key, float volume, bool muted)
    {
        if (!_sessions.TryGetValue(key, out var session))
        {
            return false;
        }

        session.Volume = volume;
        session.Muted = muted;
        return true;
    }

    public void UpdateState(string key, SessionState state)
    {
        if (!_sessions.TryGetValue(key, out var session))
        {
            return;
        }

        if (state == SessionState.Expired)
        {
            Untrack(session);
        }
        else
        {
            session.State = state;
        }
    }

    public void Remove(string key)
    {
        if (_sessions.TryGetValue(key, out var session))
        {
            Untrack(session);
        }
    }

    /// <summary>Records a per-app output the platform just set (null = cleared) for every session of these processes.</summary>
    public void SetRouted(IReadOnlyCollection<int>? processIds, string? deviceId)
    {
        foreach (var session in _sessions.Values)
        {
            if (processIds is null || processIds.Contains(session.ProcessId))
            {
                session.RoutedDeviceId = deviceId;
            }
        }
    }

    /// <summary>Writes a volume with the app's own event context, so the change is not reported back as external.</summary>
    public void SetVolume(string key, float volume)
    {
        if (_sessions.TryGetValue(key, out var session))
        {
            var level = Math.Clamp(volume, 0f, 1f);
            if (session.SimpleVolume?.SetMasterVolume(level, in _eventContext) >= 0)
            {
                session.Volume = level;
            }
        }
    }

    public void SetMute(string key, bool muted)
    {
        if (_sessions.TryGetValue(key, out var session) && session.SimpleVolume?.SetMute(muted ? 1 : 0, in _eventContext) >= 0)
        {
            session.Muted = muted;
        }
    }

    /// <summary>
    /// Whether an app records from a microphone: any active capture session of
    /// another process. Unknown (an error) counts as "yes", so a failed mute is
    /// reported rather than hidden.
    /// </summary>
    public static bool IsCapturing(IMMDeviceEnumerator enumerator, string captureDeviceId)
    {
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (enumerator.GetDevice(captureDeviceId, out var devicePointer) < 0 || (device = ComObjects.Wrap<IMMDevice>(devicePointer)) is null)
            {
                return true;
            }

            if (device.Activate(ComObjects.IidAudioSessionManager2, SoundNative.ClsctxAll, 0, out var managerPointer) < 0
                || (manager = ComObjects.Wrap<IAudioSessionManager2>(managerPointer)) is null
                || manager.GetSessionEnumerator(out var enumPointer) < 0
                || (sessions = ComObjects.Wrap<IAudioSessionEnumerator>(enumPointer)) is null
                || sessions.GetCount(out var count) < 0)
            {
                return true;
            }

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var pointer) < 0 || ComObjects.Wrap<IAudioSessionControl2>(pointer) is not { } control)
                {
                    continue;
                }

                try
                {
                    if (control.GetState(out var state) >= 0 && state == SessionState.Active
                        && control.GetProcessId(out var pid) >= 0 && pid != (uint)Environment.ProcessId)
                    {
                        return true;
                    }
                }
                finally
                {
                    ComObjects.Release(control);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug("sound", $"Could not tell whether a microphone is in use: {ex.Message}");
            return true;
        }
        finally
        {
            ComObjects.Release(sessions);
            ComObjects.Release(manager);
            ComObjects.Release(device);
        }
    }

    private void AddEndpoint(string deviceId)
    {
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        try
        {
            if (_enumerator.GetDevice(deviceId, out var devicePointer) < 0 || (device = ComObjects.Wrap<IMMDevice>(devicePointer)) is null
                || device.GetState(out var state) < 0 || (state & DeviceStateActive) == 0)
            {
                ComObjects.Release(device);
                return;
            }

            if (device.Activate(ComObjects.IidAudioSessionManager2, SoundNative.ClsctxAll, 0, out var managerPointer) < 0
                || (manager = ComObjects.Wrap<IAudioSessionManager2>(managerPointer)) is null)
            {
                ComObjects.Release(device);
                return;
            }

            var sink = new SessionNotificationSink(deviceId, _target);
            var sinkPointer = ComObjects.InterfaceFor(sink, ComObjects.IidAudioSessionNotification);
            if (sinkPointer != 0 && manager.RegisterSessionNotification(sinkPointer) < 0)
            {
                Marshal.Release(sinkPointer);
                sinkPointer = 0;
            }

            var endpoint = new EndpointSessions(deviceId, device, manager, sink, sinkPointer);
            _endpoints[deviceId] = endpoint;

            // Enumerating after registering also starts the session-created notifications.
            Rescan(endpoint);
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Could not watch the sessions of an output.", ex);
            ComObjects.Release(manager);
            ComObjects.Release(device);
        }
    }

    private void RemoveEndpoint(string deviceId)
    {
        if (!_endpoints.Remove(deviceId, out var endpoint))
        {
            return;
        }

        foreach (var session in _sessions.Values.Where(s => s.DeviceId == deviceId).ToList())
        {
            Untrack(session);
        }

        Release(endpoint);
    }

    private void Rescan(EndpointSessions endpoint)
    {
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (endpoint.Manager.GetSessionEnumerator(out var enumPointer) < 0 || (sessions = ComObjects.Wrap<IAudioSessionEnumerator>(enumPointer)) is null
                || sessions.GetCount(out var count) < 0)
            {
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var pointer) < 0 || ComObjects.Wrap<IAudioSessionControl2>(pointer) is not { } control)
                {
                    continue;
                }

                var key = control.GetSessionInstanceIdentifier(out var keyPointer) >= 0 ? SoundNative.TakeCoTaskString(keyPointer) : null;
                if (key is null || _sessions.ContainsKey(key) || control.GetState(out var state) < 0 || state == SessionState.Expired)
                {
                    if (key is not null)
                    {
                        seen.Add(key);
                    }

                    ComObjects.Release(control);
                    continue;
                }

                seen.Add(key);
                Track(endpoint.DeviceId, key, control, state);
            }

            foreach (var stale in _sessions.Values.Where(s => s.DeviceId == endpoint.DeviceId && !seen.Contains(s.Key)).ToList())
            {
                Untrack(stale);
            }

            _resolver.Prune(_sessions.Values.Select(s => s.ProcessId));
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Could not list the sessions of an output.", ex);
        }
        finally
        {
            ComObjects.Release(sessions);
        }
    }

    private void Track(string deviceId, string key, IAudioSessionControl2 control, SessionState state)
    {
        var session = new TrackedSession(key, deviceId, control)
        {
            State = state,
            SimpleVolume = control as ISimpleAudioVolume,
        };

        if (control.GetProcessId(out var pid) >= 0)
        {
            session.ProcessId = (int)pid;
        }

        session.IsSystemSounds = control.IsSystemSoundsSession() == SoundNative.SOk;
        session.RoutedDeviceId = session.IsSystemSounds || session.ProcessId <= 0 ? null : _readRoute(session.ProcessId);
        session.DisplayName = control.GetDisplayName(out var name) >= 0 ? SoundNative.TakeCoTaskString(name) : null;
        session.IconPath = control.GetIconPath(out var icon) >= 0 ? SoundNative.TakeCoTaskString(icon) : null;
        if (session.SimpleVolume is { } simple)
        {
            if (simple.GetMasterVolume(out var level) >= 0)
            {
                session.Volume = level;
            }

            if (simple.GetMute(out var mute) >= 0)
            {
                session.Muted = mute != 0;
            }
        }

        var sink = new SessionEventsSink(key, _target);
        var sinkPointer = ComObjects.InterfaceFor(sink, ComObjects.IidAudioSessionEvents);
        if (sinkPointer != 0 && control.RegisterAudioSessionNotification(sinkPointer) < 0)
        {
            Marshal.Release(sinkPointer);
            sinkPointer = 0;
        }

        session.Sink = sink;
        session.SinkPointer = sinkPointer;
        _sessions[key] = session;
    }

    private void Untrack(TrackedSession session)
    {
        _sessions.Remove(session.Key);
        if (session.SinkPointer != 0)
        {
            try
            {
                session.Control.UnregisterAudioSessionNotification(session.SinkPointer);
            }
            catch (Exception)
            {
                // The session died with its endpoint.
            }

            Marshal.Release(session.SinkPointer);
            session.SinkPointer = 0;
        }

        ComObjects.Release(session.Control);
    }

    private static void Release(EndpointSessions endpoint)
    {
        if (endpoint.SinkPointer != 0)
        {
            try
            {
                endpoint.Manager.UnregisterSessionNotification(endpoint.SinkPointer);
            }
            catch (Exception)
            {
            }

            Marshal.Release(endpoint.SinkPointer);
        }

        ComObjects.Release(endpoint.Manager);
        ComObjects.Release(endpoint.Device);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values.ToList())
        {
            Untrack(session);
        }

        foreach (var endpoint in _endpoints.Values)
        {
            Release(endpoint);
        }

        _endpoints.Clear();
    }

    private sealed record EndpointSessions(string DeviceId, IMMDevice Device, IAudioSessionManager2 Manager, SessionNotificationSink Sink, nint SinkPointer);

    private sealed class TrackedSession(string key, string deviceId, IAudioSessionControl2 control)
    {
        public string Key { get; } = key;

        public string DeviceId { get; } = deviceId;

        public IAudioSessionControl2 Control { get; } = control;

        public ISimpleAudioVolume? SimpleVolume { get; init; }

        public SessionState State { get; set; }

        public int ProcessId { get; set; }

        public bool IsSystemSounds { get; set; }

        public string? DisplayName { get; set; }

        public string? IconPath { get; set; }

        public float Volume { get; set; } = 1f;

        public bool Muted { get; set; }

        public string? RoutedDeviceId { get; set; }

        public SessionEventsSink? Sink { get; set; }

        public nint SinkPointer { get; set; }
    }
}
