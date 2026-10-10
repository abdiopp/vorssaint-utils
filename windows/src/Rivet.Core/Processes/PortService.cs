// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Processes;

/// <summary>
/// Listening ports of every process (Windows lists all users and services,
/// unlike the unprivileged macOS <c>lsof</c>), refreshed on demand only.
/// A row may be killed only when its process kept the same creation time
/// across the table read, so a reused PID is never targeted.
/// </summary>
public sealed class PortService
{
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IPortTablePlatform _ports;
    private readonly IProcessPlatform _processes;
    private readonly ISettingsStore _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ProcessIdentity, (string? Path, string? User, bool Critical)> _facts = [];
    private IReadOnlyList<PortRow> _rows = [];

    public PortService(IPortTablePlatform ports, IProcessPlatform processes, ISettingsStore settings)
    {
        _ports = ports;
        _processes = processes;
        _settings = settings;
    }

    /// <summary>Raised on the UI thread after a refresh.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<PortRow> Rows => Volatile.Read(ref _rows);

    /// <summary>The last refresh failed: the previous list is shown, marked stale by the message.</summary>
    public bool LastRefreshFailed { get; private set; }

    public bool HasLoaded { get; private set; }

    public bool IsRefreshing { get; private set; }

    /// <summary>Refreshes once; a refresh requested while one runs is ignored.</summary>
    public async Task RefreshAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            Raise();
            var includeUdp = _settings.Get(ProcessSettings.PortManagerShowUdp);
            var rows = await Task.Run(() => Read(includeUdp)).WaitAsync(ReadTimeout).ConfigureAwait(false);
            Volatile.Write(ref _rows, rows);
            LastRefreshFailed = false;
            HasLoaded = true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("ports", "Could not read listening ports.", ex);
            LastRefreshFailed = true;
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
            Raise();
        }
    }

    private IReadOnlyList<PortRow> Read(bool includeUdp)
    {
        var before = CreationTimes();
        var entries = _ports.ReadListeners(includeUdp);
        var snapshot = _processes.Snapshot();
        var after = snapshot.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var ownImage = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        var rows = new List<PortRow>(entries.Count);
        foreach (var entry in entries)
        {
            after.TryGetValue(entry.Pid, out var process);
            var creation = process?.CreationTime ?? 0;
            var stable = creation > 0 && before.TryGetValue(entry.Pid, out var earlier) && earlier == creation;
            var identity = new ProcessIdentity(entry.Pid, creation);
            var facts = FactsFor(identity);
            var image = process?.ImageName ?? (entry.Pid == 4 ? "System" : "?");
            rows.Add(new PortRow
            {
                Protocol = entry.Protocol,
                Address = entry.Address,
                Port = entry.Port,
                Pid = entry.Pid,
                ProcessName = image,
                ProcessPath = facts.Path,
                User = facts.User,
                CreationTime = creation,
                IsStable = stable,
                IsProtected = ProcessProtection.IsProtected(entry.Pid, image, _processes.CurrentProcessId, identity.IsKnown, facts.Critical, ownImage.Length > 0 ? ownImage : null),
            });
        }

        return PortRules.Normalize(rows);
    }

    private Dictionary<int, long> CreationTimes()
    {
        var map = new Dictionary<int, long>();
        foreach (var process in _processes.Snapshot())
        {
            map.TryAdd(process.Pid, process.CreationTime);
        }

        return map;
    }

    private (string? Path, string? User, bool Critical) FactsFor(ProcessIdentity identity)
    {
        lock (_facts)
        {
            if (_facts.TryGetValue(identity, out var cached))
            {
                return cached;
            }
        }

        (string? Path, string? User, bool Critical) facts = (null, null, false);
        if (identity.IsKnown)
        {
            try
            {
                facts = (_processes.ImagePath(identity), _processes.UserName(identity), _processes.IsCritical(identity));
            }
            catch (Exception ex)
            {
                Log.Debug("ports", $"Process facts unavailable for {identity.Pid}: {ex.Message}");
            }
        }

        lock (_facts)
        {
            if (_facts.Count > 4096)
            {
                _facts.Clear();
            }

            _facts[identity] = facts;
        }

        return facts;
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
}
