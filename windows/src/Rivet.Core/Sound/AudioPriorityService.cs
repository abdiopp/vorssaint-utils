// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Sound;

/// <summary>One row of a priority list, for the UI.</summary>
public sealed record PriorityEntry(string Id, string Name, int Rank, bool IsAvailable, bool IsCurrent);

/// <summary>
/// Audio device priority (spec §3.14): two ordered lists (outputs,
/// microphones). When the set of connected devices changes, the highest
/// connected device in the list becomes the default. A change of the default
/// alone never counts, so a manual choice stays until hardware changes.
/// </summary>
public sealed class AudioPriorityService : IDisposable
{
    public static readonly TimeSpan EnforceDebounce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PlacementDelay = TimeSpan.FromSeconds(2);

    private readonly AudioDeviceService _devices;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Half _output;
    private readonly Half _input;
    private readonly IDisposable _subscription;
    private bool _active;

    public AudioPriorityService(AudioDeviceService devices, ISettingsStore settings, TimeProvider? time = null)
    {
        _devices = devices;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _output = new Half(AudioFlow.Render, SoundSettings.PriorityOutputIds, FeatureKeys.AudioPriorityOutputEnabled, settings);
        _input = new Half(AudioFlow.Capture, SoundSettings.PriorityInputIds, FeatureKeys.AudioPriorityInputEnabled, settings);
        _devices.Changed += OnDevicesChanged;
        _subscription = settings.Observe(() => UiThread.Run(OnSettingsChanged),
            FeatureKeys.AudioPriorityOutputEnabled, FeatureKeys.AudioPriorityInputEnabled,
            SoundSettings.PriorityOutputIds, SoundSettings.PriorityInputIds, SoundSettings.PriorityDeviceNames);
    }

    /// <summary>Raised on the UI thread when a list, a name or the devices changed.</summary>
    public event EventHandler? Changed;

    public bool IsActive => _active;

    public bool IsEnabled(AudioFlow flow) => _settings.Get(HalfFor(flow).Enabled);

    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        foreach (var half in new[] { _output, _input })
        {
            half.Reset();
        }

        _devices.PriorityOwnsInput = active && IsEnabled(AudioFlow.Capture);
        if (active)
        {
            OnDevicesChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>The list for the UI: rank, last-known name when disconnected, current badge.</summary>
    public IReadOnlyList<PriorityEntry> Entries(AudioFlow flow)
    {
        var snapshot = _devices.Snapshot;
        var connected = snapshot.Devices(flow).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var names = _settings.Get(SoundSettings.PriorityDeviceNames);
        var current = snapshot.DefaultId(flow);
        return _settings.Get(HalfFor(flow).List)
            .Select((id, index) => new PriorityEntry(
                id,
                connected.TryGetValue(id, out var device) ? device.Name : names.GetValueOrDefault(id, id),
                index + 1,
                connected.ContainsKey(id),
                id == current))
            .ToList();
    }

    /// <summary>Drag or Move Up/Down in a list: an edit, which triggers enforcement.</summary>
    public void Move(AudioFlow flow, int from, int to)
    {
        var half = HalfFor(flow);
        var list = _settings.Get(half.List);
        var moved = PriorityLists.Move(list, from, to);
        if (!ReferenceEquals(moved, list))
        {
            _settings.Set(half.List, moved);
            ScheduleEnforce(half);
        }
    }

    private Half HalfFor(AudioFlow flow) => flow == AudioFlow.Render ? _output : _input;

    private void OnSettingsChanged()
    {
        _devices.PriorityOwnsInput = _active && IsEnabled(AudioFlow.Capture);
        foreach (var half in new[] { _output, _input })
        {
            var enabled = _settings.Get(half.Enabled);
            if (enabled && !half.WasEnabled)
            {
                ScheduleEnforce(half);
            }

            half.WasEnabled = enabled;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (!_active || !_devices.IsLive)
        {
            return;
        }

        Observe(_output);
        Observe(_input);
        UpdateNames();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The first non-empty snapshot is the baseline (launch is not every device
    /// connecting); afterwards a change in the set of connected devices
    /// schedules enforcement, and devices the list does not know yet are
    /// placed after the OS settled.
    /// </summary>
    private void Observe(Half half)
    {
        var snapshot = _devices.Snapshot;
        var devices = snapshot.Devices(half.Flow);
        if (devices.Count == 0)
        {
            return;
        }

        var list = _settings.Get(half.List);
        if (list.Count == 0)
        {
            list = PriorityLists.Initial(devices, snapshot.DefaultId(half.Flow));
            _settings.Set(half.List, list);
        }

        var available = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (half.Settled is null)
        {
            half.Settled = available;
            SchedulePlacement(half, available.Where(id => !list.Contains(id)));
            return;
        }

        if (!half.Settled.SetEquals(available))
        {
            var added = available.Except(half.Settled).ToList();
            half.Settled = available;
            SchedulePlacement(half, added.Where(id => !list.Contains(id)));
            ScheduleEnforce(half);
        }
    }

    private void SchedulePlacement(Half half, IEnumerable<string> ids)
    {
        var any = false;
        foreach (var id in ids)
        {
            any |= half.PendingPlacement.Add(id);
        }

        if (!any)
        {
            return;
        }

        half.PlacementTimer?.Dispose();
        half.PlacementTimer = _time.CreateTimer(_ => UiThread.Post(() => Place(half)), null, PlacementDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A new device waited 2 s; now it gets its place (first when it is the current device).</summary>
    private void Place(Half half)
    {
        if (!_active || !_devices.IsLive)
        {
            return;
        }

        var snapshot = _devices.Snapshot;
        var devices = snapshot.Devices(half.Flow).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var available = devices.Keys.ToHashSet(StringComparer.Ordinal);
        var current = snapshot.DefaultId(half.Flow);
        var list = _settings.Get(half.List);
        foreach (var id in half.PendingPlacement.ToList())
        {
            half.PendingPlacement.Remove(id);
            if (devices.TryGetValue(id, out var device))
            {
                list = PriorityLists.Place(list, id, device.Tier, id == current, other => devices.TryGetValue(other, out var d) ? d.Tier : null, available);
            }
        }

        _settings.Set(half.List, list);
        UpdateNames();
    }

    private void ScheduleEnforce(Half half)
    {
        if (!_active)
        {
            return;
        }

        if (half.EnforceTimer is { } timer)
        {
            timer.Change(EnforceDebounce, Timeout.InfiniteTimeSpan);
        }
        else
        {
            half.EnforceTimer = _time.CreateTimer(_ => UiThread.Post(() => Enforce(half)), null, EnforceDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Target = first connected device in list order; switch only if it differs from the current one.</summary>
    private void Enforce(Half half)
    {
        if (!_active || !_devices.IsLive || !_settings.Get(half.Enabled))
        {
            return;
        }

        var snapshot = _devices.Snapshot;
        var available = snapshot.Devices(half.Flow).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var target = PriorityLists.SwitchTarget(_settings.Get(half.List), available, snapshot.DefaultId(half.Flow));
        if (target is null)
        {
            return;
        }

        Log.Info("sound", $"Audio priority: switching the {(half.Flow == AudioFlow.Render ? "output" : "microphone")} to a higher-priority device.");
        _ = _devices.SetDefaultDeviceAsync(target, half.Flow);
    }

    /// <summary>Last-known names are refreshed for connected devices and pruned to ids still in a list.</summary>
    private void UpdateNames()
    {
        var snapshot = _devices.Snapshot;
        var names = _settings.Get(SoundSettings.PriorityDeviceNames);
        var listed = _settings.Get(_output.List).Concat(_settings.Get(_input.List));
        var updated = PriorityLists.UpdateNames(names, listed, snapshot.Outputs.Concat(snapshot.Inputs));
        if (updated.Count != names.Count || updated.Any(kv => names.GetValueOrDefault(kv.Key) != kv.Value))
        {
            _settings.Set(SoundSettings.PriorityDeviceNames, updated);
        }
    }

    public void Dispose()
    {
        _devices.Changed -= OnDevicesChanged;
        _subscription.Dispose();
        _output.Reset();
        _input.Reset();
    }

    private sealed class Half(AudioFlow flow, Setting<IReadOnlyList<string>> list, Setting<bool> enabled, ISettingsStore settings)
    {
        public AudioFlow Flow { get; } = flow;

        public Setting<IReadOnlyList<string>> List { get; } = list;

        public Setting<bool> Enabled { get; } = enabled;

        public bool WasEnabled { get; set; } = settings.Get(enabled);

        /// <summary>The settled set of connected devices; null until the baseline snapshot.</summary>
        public HashSet<string>? Settled { get; set; }

        public HashSet<string> PendingPlacement { get; } = new(StringComparer.Ordinal);

        public ITimer? EnforceTimer { get; set; }

        public ITimer? PlacementTimer { get; set; }

        public void Reset()
        {
            Settled = null;
            PendingPlacement.Clear();
            EnforceTimer?.Dispose();
            EnforceTimer = null;
            PlacementTimer?.Dispose();
            PlacementTimer = null;
        }
    }
}
