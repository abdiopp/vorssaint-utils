// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

/// <summary>
/// The per-app volume mixer (spec §3.9): rows from the audio sessions,
/// remembered volumes re-applied when an app's session appears, Windows' own
/// per-app mute, per-app output routing, pins, custom order and hidden apps.
/// Runs only while the mixer is installed; state lives on the UI thread.
/// </summary>
public sealed class MixerService : IDisposable
{
    /// <summary>A brand-new session that changes its own volume this soon is set back to the saved value.</summary>
    private static readonly TimeSpan NewSessionGrace = TimeSpan.FromSeconds(2);

    private readonly IAudioPlatform _platform;
    private readonly AudioDeviceService _devices;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly IDisposable _settingsSubscription;
    private readonly Dictionary<string, double> _sessionVolumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionRoutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastAudible = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _firstSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string?> _routedProcesses = [];
    private IReadOnlyList<AudioSessionInfo> _sessions = [];
    private IReadOnlyList<MixerRow> _allRows = [];
    private IReadOnlyList<MixerRow> _rows = [];
    private bool _active;
    private bool _disposed;

    public MixerService(IAudioPlatform platform, AudioDeviceService devices, ISettingsStore settings, TimeProvider? time = null)
    {
        _platform = platform;
        _devices = devices;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _platform.SessionsChanged += OnSessionsChanged;
        _platform.SessionVolumeChanged += OnSessionVolumeChanged;
        _devices.Changed += OnDevicesChanged;
        _devices.UniversalSwitched += OnUniversalSwitched;
        _settingsSubscription = _settings.Observe(
            () => UiThread.Run(Rebuild),
            SoundSettings.AppVolumes, SoundSettings.AppOutputDevices, SoundSettings.AppArrangement,
            SoundSettings.HiddenApps, SoundSettings.HideInactiveApps, SoundSettings.ShowSystemSounds);
    }

    /// <summary>Raised on the UI thread after the rows changed.</summary>
    public event EventHandler? Changed;

    public bool IsActive => _active;

    /// <summary>The rows the user sees, in order.</summary>
    public IReadOnlyList<MixerRow> Rows => _rows;

    /// <summary>Every known row, hidden ones included.</summary>
    public IReadOnlyList<MixerRow> AllRows => _allRows;

    /// <summary>Per-app output works on this PC (the undocumented routing interface answered).</summary>
    public bool CanRouteApps => _platform.Capabilities.CanRouteApps;

    /// <summary>The "Apps in the list" chooser entries.</summary>
    public IReadOnlyList<MixerListEntry> ListEntries =>
        MixerRowBuilder.ListEntries(_allRows, _settings.Get(SoundSettings.HiddenApps), _settings.Get(SoundSettings.ShowSystemSounds), SystemSoundsName, Culture);

    public int HiddenCount => _settings.Get(SoundSettings.HiddenApps).Count + (_settings.Get(SoundSettings.ShowSystemSounds) ? 0 : 1);

    public MixerRow? Find(string rowId) => _allRows.FirstOrDefault(r => r.RowId == rowId);

    private static string SystemSoundsName => L.Get("Strings.mixerSoundEffectsOutputTitle");

    private static CultureInfo Culture => Localizer.Current.Culture;

    /// <summary>Starts or stops session discovery with the feature.</summary>
    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        if (!active)
        {
            _sessions = [];
            _firstSeen.Clear();
            _routedProcesses.Clear();
            Rebuild();
            return;
        }

        Refresh(_platform.Sessions);
    }

    // ── User actions ────────────────────────────────────────────────────

    /// <summary>Sets an app's volume (0…1): shown at once, remembered, written to every session of the app.</summary>
    public void SetVolume(string rowId, double volume)
    {
        if (Find(rowId) is not { } row)
        {
            return;
        }

        volume = VolumeMath.ClampAppVolume(volume);
        if (!VolumeMath.IsSilent(volume))
        {
            _lastAudible[rowId] = volume;
        }

        Remember(row, volume);
        if (row.SessionKeys.Count > 0)
        {
            _platform.SetSessionVolume(row.SessionKeys, (float)volume);
        }

        Rebuild();
    }

    /// <summary>
    /// The speaker button. Windows keeps a mute switch per app, so muting keeps
    /// the slider where it is; unmuting a row whose slider sits at 0 brings
    /// back its last audible level (100 % when none).
    /// </summary>
    public void ToggleMute(string rowId)
    {
        if (Find(rowId) is not { } row)
        {
            return;
        }

        if (row.Muted)
        {
            _platform.SetSessionMute(row.SessionKeys, false);
            if (VolumeMath.IsSilent(row.Volume))
            {
                SetVolume(rowId, _lastAudible.GetValueOrDefault(rowId, 1.0));
            }
        }
        else if (VolumeMath.IsSilent(row.Volume))
        {
            SetVolume(rowId, _lastAudible.GetValueOrDefault(rowId, 1.0));
        }
        else if (row.SessionKeys.Count > 0)
        {
            _platform.SetSessionMute(row.SessionKeys, true);
        }
    }

    /// <summary>Back to 100 % and unmuted.</summary>
    public void ResetVolume(string rowId)
    {
        if (Find(rowId) is { Muted: true } row)
        {
            _platform.SetSessionMute(row.SessionKeys, false);
        }

        SetVolume(rowId, 1.0);
    }

    /// <summary>
    /// Routes an app to an output (null = Default). The preference is kept when
    /// the device disconnects (audio follows the default until it returns).
    /// Many apps only move after reopening their stream.
    /// </summary>
    public async Task<bool> SetOutputAsync(string rowId, string? deviceId)
    {
        if (Find(rowId) is not { } row || row.IsSystemSounds)
        {
            return false;
        }

        if (row.PersistenceId is { } id)
        {
            var routes = _settings.Get(SoundSettings.AppOutputDevices).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (deviceId is null)
            {
                routes.Remove(id);
            }
            else
            {
                routes[id] = deviceId;
            }

            _settings.Set(SoundSettings.AppOutputDevices, routes);
        }
        else if (deviceId is null)
        {
            _sessionRoutes.Remove(rowId);
        }
        else
        {
            _sessionRoutes[rowId] = deviceId;
        }

        Rebuild();
        if (!CanRouteApps || row.ProcessIds.Count == 0)
        {
            return CanRouteApps;
        }

        foreach (var pid in row.ProcessIds)
        {
            _routedProcesses[pid] = deviceId;
        }

        try
        {
            return await _platform.SetAppOutputAsync(row.ProcessIds, deviceId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("sound", "Per-app output failed.", ex);
            return false;
        }
    }

    public void Pin(string rowId) => UpdateArrangement(rowId, (a, id) => a.Pin(id));

    public void Unpin(string rowId) => UpdateArrangement(rowId, (a, id) => a.Unpin(id));

    /// <summary>Moves a row before or after another row of the same pin group (Ctrl+drag, or the menu).</summary>
    public void Move(string rowId, string targetRowId, bool after)
    {
        if (Find(rowId) is not { PersistenceId: { } moved } row || Find(targetRowId) is not { PersistenceId: { } target } targetRow
            || row.IsPinned != targetRow.IsPinned)
        {
            return;
        }

        var group = _rows.Where(r => r.IsPinned == row.IsPinned && r.PersistenceId is not null).Select(r => r.PersistenceId!).ToList();
        var arrangement = MixerArrangement.Parse(_settings.Get(SoundSettings.AppArrangement)).Move(group, moved, target, after);
        _settings.Set(SoundSettings.AppArrangement, arrangement.ToJson());
    }

    /// <summary>Swaps with the visible neighbour of the same group (accessibility Move Up/Down).</summary>
    public void MoveBy(string rowId, int direction)
    {
        if (Neighbour(rowId, direction) is { } neighbour)
        {
            Move(rowId, neighbour.RowId, after: direction > 0);
        }
    }

    public bool CanMoveBy(string rowId, int direction) => Neighbour(rowId, direction) is not null;

    /// <summary>Takes an app off the list; it then plays untouched by the mixer.</summary>
    public void Hide(string rowId)
    {
        if (Find(rowId) is not { } row)
        {
            return;
        }

        if (row.IsSystemSounds)
        {
            _settings.Set(SoundSettings.ShowSystemSounds, false);
        }
        else if (row.CanHide)
        {
            var hidden = _settings.Get(SoundSettings.HiddenApps).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            hidden[row.PersistenceId!] = row.DisplayName;
            _settings.Set(SoundSettings.HiddenApps, hidden);
        }
    }

    /// <summary>The "Apps in the list" checkboxes.</summary>
    public void SetShown(string id, bool shown)
    {
        if (id == MixerRow.SystemSoundsId)
        {
            _settings.Set(SoundSettings.ShowSystemSounds, shown);
            return;
        }

        var hidden = _settings.Get(SoundSettings.HiddenApps).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        if (shown)
        {
            hidden.Remove(id);
        }
        else
        {
            var name = _allRows.FirstOrDefault(r => r.PersistenceId == id)?.DisplayName ?? id;
            hidden[id] = name;
        }

        _settings.Set(SoundSettings.HiddenApps, hidden);
    }

    // ── Platform events ─────────────────────────────────────────────────

    private void OnSessionsChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (_active && !_disposed)
        {
            Refresh(_platform.Sessions);
        }
    });

    private void OnSessionVolumeChanged(object? sender, AudioSessionVolumeEventArgs e) => UiThread.Post(() =>
    {
        if (_active && !_disposed)
        {
            AdoptExternalChange(e);
        }
    });

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_active)
        {
            Rebuild();
        }
    }

    private void OnUniversalSwitched(object? sender, EventArgs e)
    {
        _sessionRoutes.Clear();
        _routedProcesses.Clear();
        Rebuild();
    }

    /// <summary>
    /// New sessions get the app's saved volume and saved output; volumes apply
    /// as soon as the app produces sound, with no panel open. Hidden apps are
    /// never touched.
    /// </summary>
    private void Refresh(IReadOnlyList<AudioSessionInfo> sessions)
    {
        var known = _sessions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        _sessions = sessions;
        var now = _time.GetTimestamp();
        var hidden = _settings.Get(SoundSettings.HiddenApps);
        var showSystem = _settings.Get(SoundSettings.ShowSystemSounds);
        var savedVolumes = _settings.Get(SoundSettings.AppVolumes);
        var savedRoutes = _settings.Get(SoundSettings.AppOutputDevices);

        foreach (var session in sessions)
        {
            if (known.Contains(session.Key) || session.ProcessId == Environment.ProcessId && !session.IsSystemSounds)
            {
                continue;
            }

            _firstSeen[session.Key] = now;
            var persistenceId = session.IsSystemSounds ? MixerRow.SystemSoundsId : session.App.PersistenceId;
            var rowId = session.IsSystemSounds ? MixerRow.SystemSoundsId : session.App.GroupId;
            var isHidden = session.IsSystemSounds ? !showSystem : persistenceId is not null && hidden.ContainsKey(persistenceId);
            if (isHidden)
            {
                continue;
            }

            double? saved = persistenceId is not null
                ? savedVolumes.TryGetValue(persistenceId, out var v) ? v : null
                : _sessionVolumes.TryGetValue(rowId, out var s) ? s : null;
            if (saved is { } volume && Math.Abs(session.Volume - volume) >= VolumeMath.UnityTolerance)
            {
                _platform.SetSessionVolume([session.Key], (float)volume);
            }

            var route = session.IsSystemSounds ? null
                : persistenceId is not null ? savedRoutes.GetValueOrDefault(persistenceId) : _sessionRoutes.GetValueOrDefault(rowId);
            if (route is not null && CanRouteApps && session.ProcessId > 0
                && (!_routedProcesses.TryGetValue(session.ProcessId, out var applied) || applied != route))
            {
                _routedProcesses[session.ProcessId] = route;
                _ = _platform.SetAppOutputAsync([session.ProcessId], route);
            }
        }

        var live = sessions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _firstSeen.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _firstSeen.Remove(key);
        }

        Rebuild();
    }

    /// <summary>
    /// Someone else (Windows' mixer, the app's own slider) changed a session:
    /// the row follows and remembers it, and the app's other sessions follow
    /// too. A session that is only a moment old is set back to the saved value
    /// instead (apps that restore their own level on start would otherwise
    /// overwrite the user's choice). The comparison is against the remembered
    /// value, not the row: the sessions publish may already show the change.
    /// </summary>
    private void AdoptExternalChange(AudioSessionVolumeEventArgs e)
    {
        var session = _platform.Sessions.FirstOrDefault(s => s.Key == e.SessionKey);
        if (session is null)
        {
            return;
        }

        var rowId = session.IsSystemSounds ? MixerRow.SystemSoundsId : session.App.GroupId;
        if (Find(rowId) is not { IsHidden: false } row)
        {
            Refresh(_platform.Sessions);
            return;
        }

        var others = _platform.Sessions.Where(s => s.Key != e.SessionKey && row.SessionKeys.Contains(s.Key)).ToList();
        double? saved = row.PersistenceId is { } pid
            ? _settings.Get(SoundSettings.AppVolumes).TryGetValue(pid, out var v) ? v : null
            : _sessionVolumes.TryGetValue(rowId, out var s) ? s : null;
        var remembered = saved ?? 1.0;
        var volumeChanged = Math.Abs(e.Volume - remembered) >= VolumeMath.UnityTolerance
                            || others.Any(o => Math.Abs(o.Volume - e.Volume) >= VolumeMath.UnityTolerance);
        if (volumeChanged)
        {
            var young = _firstSeen.TryGetValue(e.SessionKey, out var seen) && _time.GetElapsedTime(seen) < NewSessionGrace;
            if (young && saved is { } keep)
            {
                _platform.SetSessionVolume([e.SessionKey], (float)keep);
            }
            else
            {
                var volume = VolumeMath.ClampAppVolume(e.Volume);
                if (!VolumeMath.IsSilent(volume))
                {
                    _lastAudible[rowId] = volume;
                }

                Remember(row, volume);
                var differing = others.Where(o => Math.Abs(o.Volume - volume) >= VolumeMath.UnityTolerance).Select(o => o.Key).ToList();
                if (differing.Count > 0)
                {
                    _platform.SetSessionVolume(differing, (float)volume);
                }
            }
        }

        var unmatchedMute = others.Where(o => o.Muted != e.Muted).Select(o => o.Key).ToList();
        if (unmatchedMute.Count > 0)
        {
            _platform.SetSessionMute(unmatchedMute, e.Muted);
        }

        Refresh(_platform.Sessions);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void Remember(MixerRow row, double volume)
    {
        if (row.PersistenceId is { } id)
        {
            var volumes = _settings.Get(SoundSettings.AppVolumes).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (VolumeMath.IsUnity(volume))
            {
                volumes.Remove(id);
            }
            else
            {
                volumes[id] = volume;
            }

            _settings.Set(SoundSettings.AppVolumes, volumes);
        }
        else if (VolumeMath.IsUnity(volume))
        {
            _sessionVolumes.Remove(row.RowId);
        }
        else
        {
            _sessionVolumes[row.RowId] = volume;
        }
    }

    private MixerRow? Neighbour(string rowId, int direction)
    {
        var index = _rows.ToList().FindIndex(r => r.RowId == rowId);
        if (index < 0 || _rows[index].PersistenceId is null)
        {
            return null;
        }

        var row = _rows[index];
        for (var i = index + Math.Sign(direction); i >= 0 && i < _rows.Count; i += Math.Sign(direction))
        {
            if (_rows[i].IsPinned != row.IsPinned)
            {
                return null;
            }

            if (_rows[i].PersistenceId is not null)
            {
                return _rows[i];
            }
        }

        return null;
    }

    private void UpdateArrangement(string rowId, Func<MixerArrangement, string, MixerArrangement> change)
    {
        if (Find(rowId) is not { PersistenceId: { } id })
        {
            return;
        }

        var arrangement = change(MixerArrangement.Parse(_settings.Get(SoundSettings.AppArrangement)), id);
        _settings.Set(SoundSettings.AppArrangement, arrangement.ToJson());
    }

    private void Rebuild()
    {
        if (_disposed)
        {
            return;
        }

        if (!_active)
        {
            _allRows = [];
            _rows = [];
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var arrangement = MixerArrangement.Parse(_settings.Get(SoundSettings.AppArrangement));
            _allRows = MixerRowBuilder.Build(new MixerRowInputs
            {
                Sessions = _sessions,
                SavedVolumes = _settings.Get(SoundSettings.AppVolumes),
                SessionVolumes = _sessionVolumes,
                SavedRoutes = _settings.Get(SoundSettings.AppOutputDevices),
                SessionRoutes = _sessionRoutes,
                HiddenApps = _settings.Get(SoundSettings.HiddenApps),
                ShowSystemSounds = _settings.Get(SoundSettings.ShowSystemSounds),
                Arrangement = arrangement,
                OwnProcessId = Environment.ProcessId,
                SystemSoundsName = SystemSoundsName,
                Culture = Culture,
            });
            _rows = MixerRowBuilder.Visible(_allRows, arrangement, _settings.Get(SoundSettings.HideInactiveApps));
        }
        catch (Exception ex)
        {
            Log.Error("sound", "Could not build the mixer rows.", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _platform.SessionsChanged -= OnSessionsChanged;
        _platform.SessionVolumeChanged -= OnSessionVolumeChanged;
        _devices.Changed -= OnDevicesChanged;
        _devices.UniversalSwitched -= OnUniversalSwitched;
        _settingsSubscription.Dispose();
    }
}
