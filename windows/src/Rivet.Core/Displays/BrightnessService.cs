// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Localization;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Displays;

/// <summary>A brightness change the OSD should show.</summary>
public sealed record BrightnessFeedback(DisplayStatus Display, double Level);

/// <summary>
/// Display brightness (spec 03 §3.19, Windows routes): laptop panels through
/// WMI, external monitors through DDC/CI, and a dark overlay for everything
/// else. All hardware work runs on one serial worker; slider drags fold into
/// one write of the newest value per display, DDC commands to a monitor are
/// at least 50 ms apart, and a key step after a pause first reads a readable
/// monitor (it has its own buttons). Stopping removes every overlay; the
/// hardware levels stay as the user set them.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private readonly object _gate = new();
    private readonly ISettingsStore _settings;
    private readonly IDisplayCatalog _catalog;
    private readonly ISystemBrightness _system;
    private readonly IDdcChannel _ddc;
    private readonly ISoftwareDimmer _dimmer;
    private readonly Func<string?>? _pointerDisplay;
    private readonly Func<IBrightnessWorker> _workerFactory;
    private readonly List<Entry> _entries = [];

    // Session memory, by connection: the overlay level of a display that went away
    // (re-applied with a floor when it returns) and the last level written to a
    // monitor that cannot be read.
    private readonly Dictionary<string, double> _vanishedPictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _blindLevels = new(StringComparer.OrdinalIgnoreCase);

    private IBrightnessWorker? _worker;
    private IReadOnlyList<DisplayStatus> _displays = [];
    private IDisposable? _settleTimer;
    private IDisposable? _wakeTimer;
    private bool _running;
    private bool _ready;
    private long _version;
    private long _appliedVersion;
    private double _lastRebuildAt = double.NegativeInfinity;

    public BrightnessService(
        ISettingsStore settings,
        IDisplayCatalog catalog,
        ISystemBrightness system,
        IDdcChannel ddc,
        ISoftwareDimmer dimmer,
        Func<string?>? pointerDisplay = null,
        Func<IBrightnessWorker>? workerFactory = null)
    {
        _settings = settings;
        _catalog = catalog;
        _system = system;
        _ddc = ddc;
        _dimmer = dimmer;
        _pointerDisplay = pointerDisplay;
        _workerFactory = workerFactory ?? (static () => new SerialBrightnessWorker());
    }

    /// <summary>Raised on the UI thread whenever <see cref="Displays"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread for changes the brightness OSD should show.</summary>
    public event EventHandler<BrightnessFeedback>? Adjusted;

    /// <summary>The displays in panel order (built-in first, then left to right). Read on the UI thread.</summary>
    public IReadOnlyList<DisplayStatus> Displays => _displays;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The first display scan finished (until then the UI shows a placeholder, not "No display found").</summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _ready;
            }
        }
    }

    /// <summary>Starts or stops the feature (installed and "Control displays" on).</summary>
    public void Sync(bool enabled)
    {
        IBrightnessWorker? stopping = null;
        lock (_gate)
        {
            if (enabled == _running)
            {
                return;
            }

            _running = enabled;
            if (enabled)
            {
                _worker = _workerFactory();
                _ready = false;
            }
            else
            {
                stopping = _worker;
                _worker = null;
                _settleTimer?.Dispose();
                _wakeTimer?.Dispose();
                _settleTimer = _wakeTimer = null;
                _entries.Clear();
                _vanishedPictures.Clear();
                _ready = false;
            }
        }

        if (enabled)
        {
            _catalog.ConfigurationChanged += OnConfigurationChanged;
            _catalog.Resumed += OnResumed;
            Post(() => Rebuild(full: true));
            Log.Info("brightness", "Display control started.");
            return;
        }

        _catalog.ConfigurationChanged -= OnConfigurationChanged;
        _catalog.Resumed -= OnResumed;
        RemoveOverlays();
        if (stopping is not null)
        {
            stopping.Post(() => Safe(_ddc.Close, "close DDC handles"));
            stopping.Dispose();
        }

        Publish();
        Log.Info("brightness", "Display control stopped; overlays removed.");
    }

    /// <summary>Re-reads levels (panel or Settings appeared). Cheap when nothing changed.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (!_running || (_worker is { } worker && worker.Now - _lastRebuildAt < 2))
            {
                return;
            }
        }

        Post(() => Rebuild(full: false));
    }

    /// <summary>Sets a display's level (slider, shortcut). The published value changes at once.</summary>
    public void SetLevel(string displayId, double level, bool showOsd = false)
    {
        bool queue;
        lock (_gate)
        {
            var entry = Find(displayId);
            if (!_running || entry is null || entry.Route == BrightnessRoute.None)
            {
                return;
            }

            queue = SetLevelLocked(entry, level);
        }

        AfterSet(displayId, queue, showOsd);
    }

    /// <summary>
    /// One shortcut press on <paramref name="displayId"/> (or the shortcut
    /// target): steps from the remembered level inside the trust window, or
    /// reads a readable display first and adds the presses made meanwhile.
    /// </summary>
    public void Step(int direction, string? displayId = null)
    {
        var step = BrightnessMath.StepSize(_settings.Get(DisplaySettings.KeyStep));
        var showOsd = _settings.Get(DisplaySettings.OsdEnabled);
        string id;
        bool queue;
        lock (_gate)
        {
            var entry = displayId is null ? ShortcutTargetLocked() : Find(displayId);
            if (!_running || entry is null || entry.Route == BrightnessRoute.None || _worker is null)
            {
                return;
            }

            id = entry.Device.Id;
            var readable = entry.Route == BrightnessRoute.System || (entry.Route == BrightnessRoute.Ddc && entry.Ddc == DdcState.Live);
            if (readable && _worker.Now - entry.KnownAt > BrightnessMath.TrustWindowSeconds)
            {
                entry.PendingSteps += Math.Sign(direction);
                entry.StepShowsOsd |= showOsd;
                if (!entry.ReadQueued)
                {
                    entry.ReadQueued = true;
                    _worker.Post(() => ReadThenStep(id, step));
                }

                return;
            }

            queue = SetLevelLocked(entry, BrightnessMath.Step(entry.Level, direction, step));
        }

        AfterSet(id, queue, showOsd);
    }

    /// <summary>The display the shortcuts act on: under the pointer when following it, else the main one.</summary>
    public string? ShortcutTarget()
    {
        lock (_gate)
        {
            return ShortcutTargetLocked()?.Device.Id;
        }
    }

    /// <summary>"Dim the picture" for a monitor whose DDC channel cannot be trusted.</summary>
    public void SetForcedSoftware(string displayId, bool on)
    {
        string path;
        lock (_gate)
        {
            if (!_running || Find(displayId) is not { } entry)
            {
                return;
            }

            path = entry.Device.PathKey;
        }

        var forced = _settings.Get(DisplaySettings.ForcedSoftwarePaths);
        _settings.Set(DisplaySettings.ForcedSoftwarePaths, on ? PathList.Add(forced, path) : PathList.Remove(forced, path));
        if (!on)
        {
            // Restore the picture, forget the write-only verdict and probe again.
            _settings.Set(DisplaySettings.WriteOnlyPaths, PathList.Remove(_settings.Get(DisplaySettings.WriteOnlyPaths), path));
        }

        Post(() =>
        {
            if (!on)
            {
                lock (_gate)
                {
                    if (Find(displayId) is { } entry)
                    {
                        ApplyPictureLocked(entry, 1);
                    }
                }
            }

            Rebuild(full: true);
        });
    }

    /// <summary>"Extra dimming": the lowest quarter of the slider dims the picture below the monitor's minimum.</summary>
    public void SetExtendedDimming(string displayId, bool on)
    {
        string path;
        lock (_gate)
        {
            if (!_running || Find(displayId) is not { } entry)
            {
                return;
            }

            path = entry.Device.PathKey;
        }

        var paths = _settings.Get(DisplaySettings.ExtendedDimmingPaths);
        _settings.Set(DisplaySettings.ExtendedDimmingPaths, on ? PathList.Add(paths, path) : PathList.Remove(paths, path));
        Post(() =>
        {
            DisplayDevice device;
            lock (_gate)
            {
                if (Find(displayId) is not { } entry)
                {
                    return;
                }

                entry.Extended = on;
                if (!on && entry.Picture < 1)
                {
                    ApplyPictureLocked(entry, 1);
                }

                device = entry.Device;
                if (entry.Ddc != DdcState.Live)
                {
                    return;
                }
            }

            var reading = SafeRead(device);
            lock (_gate)
            {
                if (Find(displayId) is { } entry && reading is { } r)
                {
                    entry.DdcMax = BrightnessMath.DdcMaximum(r.Maximum);
                    entry.Hardware = BrightnessMath.FromDdc(r.Current, r.Maximum);
                    entry.Level = on ? BrightnessMath.JoinExtended(entry.Hardware, entry.Picture) : entry.Hardware;
                    entry.KnownAt = Now;
                }
            }

            Publish();
        });
    }

    public void Dispose() => Sync(false);

    // ── Work on the serial worker ─────────────────────────────────────────

    private void Rebuild(bool full)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }
        }

        IReadOnlyList<DisplayDevice> devices;
        try
        {
            devices = _catalog.Enumerate();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Could not list the displays.", ex);
            devices = [];
        }

        Dictionary<string, Entry> previous;
        lock (_gate)
        {
            previous = _entries.ToDictionary(e => e.Device.Id, StringComparer.OrdinalIgnoreCase);
        }

        // A light refresh keeps the probes of an unchanged set of displays.
        var unchanged = previous.Count == devices.Count
            && devices.All(d => previous.TryGetValue(d.Id, out var p) && string.Equals(p.Device.PathKey, d.PathKey, StringComparison.OrdinalIgnoreCase));
        if (!unchanged)
        {
            full = true;
        }

        if (full)
        {
            Safe(() => _ddc.Open(devices), "open DDC handles");
        }

        var forced = _settings.Get(DisplaySettings.ForcedSoftwarePaths);
        var extended = _settings.Get(DisplaySettings.ExtendedDimmingPaths);
        var writeOnly = _settings.Get(DisplaySettings.WriteOnlyPaths);
        var built = new List<Entry>();
        foreach (var device in devices)
        {
            previous.TryGetValue(device.Id, out var old);
            if (old is not null && !string.Equals(old.Device.PathKey, device.PathKey, StringComparison.OrdinalIgnoreCase))
            {
                old = null; // Another monitor now uses this display number.
            }

            var entry = new Entry
            {
                Device = device,
                Forced = PathList.Contains(forced, device.PathKey),
                Extended = PathList.Contains(extended, device.PathKey),
                Picture = old?.Picture ?? 1,
                LastCommandAt = old?.LastCommandAt ?? double.NegativeInfinity,
            };
            if (!full && old is not null)
            {
                Reuse(entry, old);
            }
            else
            {
                Probe(entry, old, writeOnly);
            }

            built.Add(entry);
        }

        built.Sort(static (a, b) =>
        {
            var c = b.Device.IsInternal.CompareTo(a.Device.IsInternal);
            if (c != 0) return c;
            c = a.Device.Bounds.X.CompareTo(b.Device.Bounds.X);
            return c != 0 ? c : a.Device.Bounds.Y.CompareTo(b.Device.Bounds.Y);
        });

        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            var ids = built.Select(e => e.Device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _entries.Where(e => !ids.Contains(e.Device.Id)))
            {
                if (gone.Picture < 1)
                {
                    _vanishedPictures[gone.Device.PathKey] = gone.Route == BrightnessRoute.Software ? gone.Level : gone.Picture;
                    var id = gone.Device.Id;
                    UiThread.Post(() => SafeUi(() => _dimmer.Remove(id)));
                }
            }

            _entries.Clear();
            _entries.AddRange(built);
            foreach (var entry in _entries)
            {
                // Re-apply overlays after every rebuild (the bounds may have moved); drop stale ones.
                var picture = entry.Route switch
                {
                    BrightnessRoute.Software => entry.Level,
                    BrightnessRoute.Ddc when entry.Extended && entry.Ddc == DdcState.Live => entry.Picture,
                    _ => 1,
                };
                ApplyPictureLocked(entry, picture, reapply: true);
            }

            _lastRebuildAt = Now;
            _ready = true;
        }

        Publish();
    }

    /// <summary>Picks the route of a display (full rebuilds).</summary>
    private void Probe(Entry entry, Entry? old, List<string> writeOnly)
    {
        var device = entry.Device;
        var hasChannel = !device.IsInternal && Safe(() => _ddc.HasChannel(device), "check the DDC channel");
        if (entry.Forced)
        {
            entry.Route = BrightnessRoute.Software;
            entry.Ddc = hasChannel ? (PathList.Contains(writeOnly, device.PathKey) ? DdcState.WriteOnly : DdcState.Unknown) : DdcState.None;
            entry.Level = old?.Route == BrightnessRoute.Software ? old.Level : ReturningPicture(device) ?? 1;
            return;
        }

        if (device.IsInternal && SafeSystemRead(device) is { } percent)
        {
            entry.Route = BrightnessRoute.System;
            entry.Level = BrightnessMath.FromPercent(percent);
            entry.KnownAt = Now;
            return;
        }

        if (hasChannel)
        {
            entry.Route = BrightnessRoute.Ddc;
            Pace(entry);
            var reading = SafeRead(device);
            entry.LastCommandAt = Now;
            if (reading is { } r)
            {
                entry.Ddc = DdcState.Live;
                entry.DdcMax = BrightnessMath.DdcMaximum(r.Maximum);
                entry.Hardware = BrightnessMath.FromDdc(r.Current, r.Maximum);
                if (entry.Extended)
                {
                    entry.Picture = ReturningPicture(device) ?? entry.Picture;
                    entry.Level = BrightnessMath.JoinExtended(entry.Hardware, entry.Picture);
                }
                else
                {
                    entry.Picture = 1;
                    entry.Level = entry.Hardware;
                }

                entry.KnownAt = Now;
                return;
            }

            entry.Ddc = PathList.Contains(writeOnly, device.PathKey) ? DdcState.WriteOnly : DdcState.Unknown;
            entry.Picture = 1;
            entry.Level = old?.Route == BrightnessRoute.Ddc ? old.Level : _blindLevels.GetValueOrDefault(BlindKey(device), BrightnessMath.UnknownLevel);
            return;
        }

        entry.Route = BrightnessRoute.Software;
        entry.Ddc = device.IsInternal ? DdcState.None : DdcState.Dead;
        entry.Level = old?.Route == BrightnessRoute.Software ? old.Level : ReturningPicture(device) ?? 1;
    }

    /// <summary>Keeps the probe of an unchanged display and re-reads its level when it can be read.</summary>
    private void Reuse(Entry entry, Entry old)
    {
        entry.Route = old.Route;
        entry.Ddc = old.Ddc;
        entry.DdcMax = old.DdcMax;
        entry.Level = old.Level;
        entry.Hardware = old.Hardware;
        entry.KnownAt = old.KnownAt;
        entry.Error = old.Error;
        if (entry.Route == BrightnessRoute.System && SafeSystemRead(entry.Device) is { } percent)
        {
            entry.Level = BrightnessMath.FromPercent(percent);
            entry.KnownAt = Now;
        }
        else if (entry.Route == BrightnessRoute.Ddc && entry.Ddc == DdcState.Live)
        {
            Pace(entry);
            var reading = SafeRead(entry.Device);
            entry.LastCommandAt = Now;
            if (reading is { } r)
            {
                entry.DdcMax = BrightnessMath.DdcMaximum(r.Maximum);
                entry.Hardware = BrightnessMath.FromDdc(r.Current, r.Maximum);
                entry.Level = entry.Extended ? BrightnessMath.JoinExtended(entry.Hardware, entry.Picture) : entry.Hardware;
                entry.KnownAt = Now;
            }
        }
    }

    private void FlushWrite(string displayId)
    {
        Entry entry;
        double level;
        BrightnessRoute route;
        lock (_gate)
        {
            if (!_running || Find(displayId) is not { } found)
            {
                return;
            }

            entry = found;
            entry.WriteQueued = false;
            if (entry.Pending is not { } pending)
            {
                return;
            }

            level = pending;
            entry.Pending = null;
            route = entry.Route;
            if (route == BrightnessRoute.Software)
            {
                ApplyPictureLocked(entry, level);
                return;
            }
        }

        if (route == BrightnessRoute.System)
        {
            var ok = SafeSystemWrite(entry.Device, BrightnessMath.ToPercent(level));
            SetError(entry, ok ? null : L.Get("win.displays.writeFailed"));
        }
        else if (route == BrightnessRoute.Ddc)
        {
            WriteDdc(entry, level);
        }
    }

    private void WriteDdc(Entry entry, double level)
    {
        bool extended;
        double hardwareNow;
        lock (_gate)
        {
            extended = entry.Extended && entry.Ddc == DdcState.Live;
            hardwareNow = entry.Hardware;
        }

        if (extended)
        {
            var (hardware, picture) = BrightnessMath.SplitExtended(level);
            if (picture < 1)
            {
                // The monitor goes to its minimum first (once; drags in this range skip DDC), then the picture dims.
                if (hardwareNow != 0 && !DdcWrite(entry, 0))
                {
                    SetError(entry, L.Get("win.displays.writeFailed"));
                    return;
                }

                lock (_gate)
                {
                    entry.Hardware = 0;
                    ApplyPictureLocked(entry, picture);
                }

                SetError(entry, null);
                return;
            }

            // The picture is restored before any brighter hardware write.
            lock (_gate)
            {
                ApplyPictureLocked(entry, 1);
            }

            var wrote = DdcWrite(entry, hardware);
            if (wrote)
            {
                lock (_gate)
                {
                    entry.Hardware = hardware;
                }
            }

            SetError(entry, wrote ? null : L.Get("win.displays.writeFailed"));
            return;
        }

        if (DdcWrite(entry, level))
        {
            string? learnedPath = null;
            lock (_gate)
            {
                entry.Hardware = level;
                if (entry.Ddc == DdcState.Unknown)
                {
                    // Writes land but reads never answer: remember the connection so it is not probed again.
                    entry.Ddc = DdcState.WriteOnly;
                    learnedPath = entry.Device.PathKey;
                }

                if (entry.Ddc == DdcState.WriteOnly)
                {
                    _blindLevels[BlindKey(entry.Device)] = level;
                }

                entry.Error = null;
            }

            if (learnedPath is not null)
            {
                UiThread.Post(() => _settings.Set(DisplaySettings.WriteOnlyPaths, PathList.Add(_settings.Get(DisplaySettings.WriteOnlyPaths), learnedPath)));
            }

            Publish();
            return;
        }

        DdcState state;
        lock (_gate)
        {
            state = entry.Ddc;
            if (state == DdcState.Unknown)
            {
                // Writes are rejected: the connection cannot carry DDC (HDMI adapters, TVs). Dim the picture instead.
                entry.Ddc = DdcState.Dead;
                entry.Route = BrightnessRoute.Software;
                entry.Error = null;
                ApplyPictureLocked(entry, entry.Pending ?? level);
            }
        }

        switch (state)
        {
            case DdcState.Unknown:
                Log.Info("brightness", $"DDC writes rejected on {entry.Device.Id}; dimming the picture instead.");
                Publish();
                break;
            case DdcState.WriteOnly:
                // A failed write on a write-only connection forgets the verdict and rebuilds.
                var path = entry.Device.PathKey;
                UiThread.Post(() => _settings.Set(DisplaySettings.WriteOnlyPaths, PathList.Remove(_settings.Get(DisplaySettings.WriteOnlyPaths), path)));
                SetError(entry, L.Get("win.displays.writeFailed"));
                Post(() => Rebuild(full: true));
                break;
            default:
                SetError(entry, L.Get("win.displays.writeFailed"));
                break;
        }
    }

    private bool DdcWrite(Entry entry, double level)
    {
        Pace(entry);
        int value;
        lock (_gate)
        {
            value = BrightnessMath.ToDdc(level, entry.DdcMax);
        }

        var ok = Safe(() => _ddc.Write(entry.Device, value), "write DDC luminance");
        lock (_gate)
        {
            entry.LastCommandAt = Now;
        }

        return ok;
    }

    /// <summary>At least 50 ms between whole commands to one monitor (faster streams make some drop the signal).</summary>
    private void Pace(Entry entry)
    {
        var worker = _worker;
        if (worker is null)
        {
            return;
        }

        double last;
        lock (_gate)
        {
            last = entry.LastCommandAt;
        }

        var wait = last + BrightnessMath.DdcPacingSeconds - worker.Now;
        if (wait > 0)
        {
            worker.Pause(TimeSpan.FromSeconds(wait));
        }
    }

    private void ReadThenStep(string displayId, double step)
    {
        DisplayDevice device;
        BrightnessRoute route;
        Entry paced;
        lock (_gate)
        {
            if (!_running || Find(displayId) is not { } entry)
            {
                return;
            }

            device = entry.Device;
            route = entry.Route;
            paced = entry;
        }

        if (route == BrightnessRoute.Ddc)
        {
            Pace(paced);
        }

        double? hardware = null;
        if (route == BrightnessRoute.System)
        {
            hardware = SafeSystemRead(device) is { } percent ? BrightnessMath.FromPercent(percent) : null;
        }
        else if (SafeRead(device) is { } reading)
        {
            hardware = BrightnessMath.FromDdc(reading.Current, reading.Maximum);
        }

        bool queue;
        bool osd;
        lock (_gate)
        {
            if (!_running || Find(displayId) is not { } entry)
            {
                return;
            }

            if (route == BrightnessRoute.Ddc)
            {
                entry.LastCommandAt = Now;
            }

            if (hardware is { } level)
            {
                entry.Hardware = level;
                entry.Level = route == BrightnessRoute.Ddc && entry.Extended ? BrightnessMath.JoinExtended(level, entry.Picture) : level;
                entry.KnownAt = Now;
            }

            var steps = entry.PendingSteps;
            osd = entry.StepShowsOsd;
            entry.PendingSteps = 0;
            entry.StepShowsOsd = false;
            entry.ReadQueued = false;
            queue = SetLevelLocked(entry, BrightnessMath.Steps(entry.Level, steps, step));
        }

        AfterSet(displayId, queue, osd);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private double Now => _worker?.Now ?? 0;

    private Entry? Find(string displayId) => _entries.FirstOrDefault(e => string.Equals(e.Device.Id, displayId, StringComparison.OrdinalIgnoreCase));

    private Entry? ShortcutTargetLocked()
    {
        if (_settings.Get(DisplaySettings.FollowPointer) && _pointerDisplay?.Invoke() is { } id && Find(id) is { } under)
        {
            return under;
        }

        return _entries.FirstOrDefault(e => e.Device.IsPrimary) ?? _entries.FirstOrDefault();
    }

    /// <summary>Updates the published level and marks a write; true when a write job must be queued.</summary>
    private bool SetLevelLocked(Entry entry, double level)
    {
        level = Math.Clamp(level, 0, 1);
        entry.Level = level;
        entry.KnownAt = Now;
        entry.Pending = level;
        if (entry.WriteQueued)
        {
            return false;
        }

        entry.WriteQueued = true;
        return true;
    }

    private void AfterSet(string displayId, bool queue, bool showOsd)
    {
        if (queue)
        {
            Post(() => FlushWrite(displayId));
        }

        var snapshot = Publish();
        if (showOsd && snapshot.FirstOrDefault(d => string.Equals(d.Id, displayId, StringComparison.OrdinalIgnoreCase)) is { } status)
        {
            UiThread.Run(() => Adjusted?.Invoke(this, new BrightnessFeedback(status, status.Level)));
        }
    }

    /// <summary>
    /// Records the overlay factor and posts the overlay change to the UI
    /// thread (FIFO, so changes land in order). <paramref name="reapply"/>
    /// re-sends a dim overlay even when its factor did not change.
    /// </summary>
    private void ApplyPictureLocked(Entry entry, double factor, bool reapply = false)
    {
        var target = BrightnessMath.IsUndimmed(factor) ? 1 : Math.Clamp(factor, 0, 1);
        var was = entry.Picture;
        entry.Picture = target;
        var device = entry.Device;
        if (target >= 1)
        {
            if (was < 1)
            {
                UiThread.Post(() => SafeUi(() => _dimmer.Remove(device.Id)));
            }

            return;
        }

        if (reapply || Math.Abs(was - target) > 1e-9)
        {
            UiThread.Post(() => SafeUi(() => _dimmer.Apply(device, target)));
        }
    }

    private void RemoveOverlays() => UiThread.Run(() => SafeUi(_dimmer.RemoveAll));

    private double? ReturningPicture(DisplayDevice device)
    {
        lock (_gate)
        {
            if (_vanishedPictures.Remove(device.PathKey, out var picture))
            {
                return Math.Max(picture, BrightnessMath.ReconnectFloor);
            }
        }

        return null;
    }

    private static string BlindKey(DisplayDevice device) => device.Fingerprint + "@" + device.PathKey;

    private void SetError(Entry entry, string? error)
    {
        bool changed;
        lock (_gate)
        {
            changed = entry.Error != error;
            entry.Error = error;
        }

        if (changed)
        {
            Publish();
        }
    }

    private IReadOnlyList<DisplayStatus> Publish()
    {
        List<DisplayStatus> list;
        long version;
        lock (_gate)
        {
            version = ++_version;
            list = _running ? _entries.Select(ToStatus).ToList() : [];
        }

        UiThread.Run(() =>
        {
            if (version <= _appliedVersion)
            {
                return;
            }

            _appliedVersion = version;
            _displays = list;
            Changed?.Invoke(this, EventArgs.Empty);
        });
        return list;
    }

    private static DisplayStatus ToStatus(Entry e) => new()
    {
        Id = e.Device.Id,
        Name = e.Device.Name,
        PathKey = e.Device.PathKey,
        IsInternal = e.Device.IsInternal,
        IsPrimary = e.Device.IsPrimary,
        Bounds = e.Device.Bounds,
        Route = e.Route,
        Ddc = e.Ddc,
        Level = e.Level,
        ForcedSoftware = e.Forced,
        ExtendedDimming = e.Extended,
        Error = e.Error,
    };

    private void Post(Action work)
    {
        IBrightnessWorker? worker;
        lock (_gate)
        {
            worker = _running ? _worker : null;
        }

        worker?.Post(work);
    }

    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            // One fixed settle window from the first event: change storms coalesce.
            if (!_running || _settleTimer is not null || _worker is null)
            {
                return;
            }

            _settleTimer = _worker.Schedule(TimeSpan.FromSeconds(BrightnessMath.SettleSeconds), () =>
            {
                lock (_gate)
                {
                    _settleTimer = null;
                }

                Rebuild(full: true);
            });
        }
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_running || _worker is null)
            {
                return;
            }

            _wakeTimer?.Dispose();
            _wakeTimer = _worker.Schedule(TimeSpan.FromSeconds(BrightnessMath.WakeSettleSeconds), () =>
            {
                lock (_gate)
                {
                    _wakeTimer = null;
                }

                Rebuild(full: true);
            });
        }
    }

    private DdcReading? SafeRead(DisplayDevice device)
    {
        try
        {
            return _ddc.Read(device);
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", $"DDC read failed on {device.Id}.", ex);
            return null;
        }
    }

    private int? SafeSystemRead(DisplayDevice device)
    {
        try
        {
            return _system.Read(device) is { } percent ? Math.Clamp(percent, 0, 100) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", $"Panel brightness read failed on {device.Id}.", ex);
            return null;
        }
    }

    private bool SafeSystemWrite(DisplayDevice device, int percent)
    {
        try
        {
            return _system.Write(device, percent);
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", $"Panel brightness write failed on {device.Id}.", ex);
            return false;
        }
    }

    private static void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", $"Could not {what}.", ex);
        }
    }

    private static bool Safe(Func<bool> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", $"Could not {what}.", ex);
            return false;
        }
    }

    private static void SafeUi(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("brightness", "Overlay update failed.", ex);
        }
    }

    private sealed class Entry
    {
        public required DisplayDevice Device { get; init; }

        public BrightnessRoute Route { get; set; }

        public DdcState Ddc { get; set; }

        public int DdcMax { get; set; } = 100;

        /// <summary>The published slider level.</summary>
        public double Level { get; set; }

        /// <summary>When <see cref="Level"/> last matched the hardware (trust window).</summary>
        public double KnownAt { get; set; } = double.NegativeInfinity;

        /// <summary>The newest requested level not written yet.</summary>
        public double? Pending { get; set; }

        public bool WriteQueued { get; set; }

        public double LastCommandAt { get; set; } = double.NegativeInfinity;

        /// <summary>The overlay factor applied now (1 = none).</summary>
        public double Picture { get; set; } = 1;

        /// <summary>The monitor's own level (DDC), when known.</summary>
        public double Hardware { get; set; } = -1;

        public bool Forced { get; set; }

        public bool Extended { get; set; }

        public string? Error { get; set; }

        public int PendingSteps { get; set; }

        public bool StepShowsOsd { get; set; }

        public bool ReadQueued { get; set; }
    }
}
