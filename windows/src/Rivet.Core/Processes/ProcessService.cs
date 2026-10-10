// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Processes;

/// <summary>
/// The Kill Process list and its actions. Snapshots run off the UI thread,
/// at most one at a time, with a 3 s freshness cache; every kill re-checks
/// the target's creation time right before acting.
/// </summary>
public sealed class ProcessService
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PostKillRefreshDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(10);

    private readonly IProcessPlatform _platform;
    private readonly ISettingsStore _settings;
    private readonly CpuSampler _cpu = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<ProcessIdentity, Facts> _facts = [];
    private readonly Dictionary<string, ExecutableDescription?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ownImage;
    private IReadOnlyList<RawProcess> _snapshot = [];
    private IReadOnlyList<ProcessRow> _processes = [];
    private IReadOnlyList<ProcessRow> _rows = [];
    private IReadOnlySet<int> _windows = new HashSet<int>();
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public ProcessService(IProcessPlatform platform, ISettingsStore settings)
    {
        _platform = platform;
        _settings = settings;
        _ownImage = Path.GetFileName(Environment.ProcessPath ?? string.Empty);
        settings.Observe(() => Rebuild(), ProcessSettings.GroupRelated, ProcessSettings.SortBy, ProcessSettings.SortAscending);
    }

    /// <summary>Raised on the UI thread after the rows changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Rows as shown: grouped when "Group related processes" is on, sorted by the saved column.</summary>
    public IReadOnlyList<ProcessRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows;
            }
        }
    }

    /// <summary>Every process of the last snapshot, ungrouped (Command Bar, Port Manager, Uninstaller).</summary>
    public IReadOnlyList<ProcessRow> Processes
    {
        get
        {
            lock (_gate)
            {
                return _processes;
            }
        }
    }

    public int ProcessCount => Processes.Count;

    public bool IsRefreshing { get; private set; }

    public bool HasSnapshot => Processes.Count > 0;

    public ProcessSortColumn SortColumn => ProcessListRules.ParseSort(_settings.Get(ProcessSettings.SortBy));

    public bool SortAscending => _settings.Get(ProcessSettings.SortAscending);

    public void ClickColumn(ProcessSortColumn column)
    {
        var (next, ascending) = ProcessListRules.Click(SortColumn, SortAscending, column);
        _settings.Set(ProcessSettings.SortAscending, ascending);
        _settings.Set(ProcessSettings.SortBy, ProcessListRules.SortKey(next));
    }

    /// <summary>Takes a snapshot unless the last one is fresher than 3 s (or <paramref name="force"/>).</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastRefreshUtc < Freshness && HasSnapshot)
        {
            return;
        }

        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            IsRefreshing = true;
            RaiseChanged();
            await Task.Run(TakeSnapshot).ConfigureAwait(false);
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
            RaiseChanged();
        }
    }

    /// <summary>The live row for a PID, if it is still in the last snapshot.</summary>
    public ProcessRow? Find(int pid) => Processes.FirstOrDefault(p => p.Pid == pid);

    public Task<KillReport> KillAsync(ProcessRow row, KillMode mode, CancellationToken cancellationToken = default) =>
        Task.Run(() => KillCore(row, mode, cancellationToken), cancellationToken);

    /// <summary>Kills a process identified only by PID and identity (Port Manager rows).</summary>
    public Task<KillReport> KillAsync(ProcessIdentity identity, string name, bool force, CancellationToken cancellationToken = default)
    {
        var row = Find(identity.Pid) is { } live && live.Identity == identity
            ? live
            : new ProcessRow { Identity = identity, Name = name, ImageName = name, IsProtected = !identity.IsKnown };
        return KillAsync(row, force ? KillMode.ForceKill : KillMode.Kill, cancellationToken);
    }

    /// <summary>
    /// The arguments part of a Windows command line: argv[0] is quoted up to
    /// the next quote, or ends at the first space or tab (CommandLineToArgvW rules).
    /// </summary>
    public static string ArgumentsOf(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        int end;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            end = close < 0 ? text.Length : close + 1;
        }
        else
        {
            end = text.IndexOfAny([' ', '\t']);
            if (end < 0)
            {
                end = text.Length;
            }
        }

        return text[end..].TrimStart(' ', '\t');
    }

    private void TakeSnapshot()
    {
        IReadOnlyList<RawProcess> snapshot;
        try
        {
            snapshot = _platform.Snapshot();
        }
        catch (Exception ex)
        {
            Log.Warn("processes", "Process snapshot failed.", ex);
            return;
        }

        if (snapshot.Count == 0)
        {
            // A failed or empty snapshot keeps the previous list.
            return;
        }

        var nowTicks = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        var cpu = _cpu.Sample(snapshot, nowTicks, _platform.LogicalProcessorCount);
        IReadOnlySet<int> windows;
        try
        {
            windows = _platform.ProcessesWithWindows();
        }
        catch (Exception ex)
        {
            Log.Warn("processes", "Window enumeration failed.", ex);
            windows = new HashSet<int>();
        }

        var live = new HashSet<ProcessIdentity>(snapshot.Select(p => p.Identity));
        var rows = new List<ProcessRow>(snapshot.Count);
        foreach (var process in snapshot)
        {
            if (process.Pid <= 0)
            {
                continue;
            }

            var facts = FactsFor(process);
            rows.Add(new ProcessRow
            {
                Identity = process.Identity,
                ParentPid = process.ParentPid,
                Name = facts.DisplayName,
                ImageName = process.ImageName,
                Path = facts.Path,
                User = facts.User,
                CpuPercent = cpu.TryGetValue(process.Identity, out var share) ? share : null,
                MemoryBytes = process.WorkingSetBytes,
                Members = [process.Identity],
                IsProtected = facts.IsProtected,
                HasWindows = windows.Contains(process.Pid),
            });
        }

        lock (_gate)
        {
            // Forget facts about processes that ended.
            foreach (var stale in _facts.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _facts.Remove(stale);
            }

            _snapshot = snapshot;
            _windows = windows;
            _processes = rows;
            _lastRefreshUtc = DateTime.UtcNow;
        }

        Rebuild();
    }

    private Facts FactsFor(RawProcess process)
    {
        lock (_gate)
        {
            if (_facts.TryGetValue(process.Identity, out var cached))
            {
                return cached;
            }
        }

        var identity = process.Identity;
        var path = SafeCall(() => _platform.ImagePath(identity));
        var description = path is null ? null : DescriptionFor(path);
        var critical = identity.IsKnown && SafeCall(() => _platform.IsCritical(identity));
        var user = SafeCall(() => _platform.UserName(identity));
        var name = description?.FileDescription is { Length: > 0 } described ? described.Trim() : process.ImageName;
        var facts = new Facts(
            path,
            user,
            name,
            ProcessProtection.IsProtected(process.Pid, process.ImageName, _platform.CurrentProcessId, identity.IsKnown, critical, _ownImage.Length > 0 ? _ownImage : null));
        lock (_gate)
        {
            _facts[identity] = facts;
        }

        return facts;
    }

    private ExecutableDescription? DescriptionFor(string path)
    {
        lock (_gate)
        {
            if (_descriptions.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var description = SafeCall(() => _platform.Describe(path));
        lock (_gate)
        {
            _descriptions[path] = description;
        }

        return description;
    }

    private static T? SafeCall<T>(Func<T?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            Log.Debug("processes", $"Process query failed: {ex.Message}");
            return default;
        }
    }

    private void Rebuild()
    {
        IReadOnlyList<ProcessRow> processes;
        lock (_gate)
        {
            processes = _processes;
        }

        IEnumerable<ProcessRow> rows = processes;
        if (_settings.Get(ProcessSettings.GroupRelated) && processes.Count > 0)
        {
            rows = Group(processes);
        }

        var sorted = ProcessListRules.Sort(rows, SortColumn, SortAscending);
        lock (_gate)
        {
            _rows = sorted;
        }

        RaiseChanged();
    }

    private IEnumerable<ProcessRow> Group(IReadOnlyList<ProcessRow> processes)
    {
        IReadOnlyList<RawProcess> snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        var pathByPid = processes.ToDictionary(p => p.Pid, p => p.Path);
        var owners = ProcessTree.GroupOwners(snapshot, p => pathByPid.GetValueOrDefault(p.Pid));
        var byPid = processes.ToDictionary(p => p.Pid);
        foreach (var group in processes.GroupBy(p => owners.GetValueOrDefault(p.Pid, p.Pid)))
        {
            if (!byPid.TryGetValue(group.Key, out var owner))
            {
                // The owner left the snapshot: the macOS app drops such a group.
                continue;
            }

            var members = group.ToList();
            if (members.Count == 1)
            {
                yield return owner;
                continue;
            }

            var cpu = members.Any(m => m.CpuPercent.HasValue) ? members.Sum(m => m.CpuPercent ?? 0) : (double?)null;
            yield return owner with
            {
                CpuPercent = cpu,
                MemoryBytes = members.Sum(m => m.MemoryBytes),
                MemberCount = members.Count,
                Members = members.Select(m => m.Identity).ToList(),
                HasWindows = members.Any(m => m.HasWindows) && owner.HasWindows,
            };
        }
    }

    private KillReport KillCore(ProcessRow row, KillMode mode, CancellationToken cancellationToken)
    {
        var removed = new List<ProcessIdentity>();
        var denied = new List<ProcessIdentity>();
        var failed = 0;
        var relaunchFailed = false;

        void Account(ProcessIdentity identity, KillOutcome outcome)
        {
            switch (outcome)
            {
                case KillOutcome.Done:
                case KillOutcome.AlreadyGone:
                case KillOutcome.IdentityChanged:
                    // A changed identity means the original process is gone already.
                    removed.Add(identity);
                    break;
                case KillOutcome.AccessDenied:
                    denied.Add(identity);
                    break;
                default:
                    failed++;
                    break;
            }
        }

        if (row.IsProtected || !row.Identity.IsKnown)
        {
            return new KillReport { Mode = mode, TargetName = row.Name, Failed = 1 };
        }

        switch (mode)
        {
            case KillMode.Kill:
                Account(row.Identity, Polite(row.Identity));
                break;
            case KillMode.ForceKill:
                Account(row.Identity, _platform.Terminate(row.Identity));
                break;
            case KillMode.KillAll:
                foreach (var target in Processes.Where(p => !p.IsProtected && p.Identity.IsKnown && string.Equals(p.Name, row.Name, StringComparison.Ordinal)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Account(target.Identity, Polite(target.Identity));
                }

                break;
            case KillMode.KillTree:
                KillTree(row, Account, cancellationToken);
                break;
            case KillMode.Restart:
                relaunchFailed = !Restart(row, Account, cancellationToken);
                break;
        }

        RemoveRows(removed);
        ScheduleRefresh();
        return new KillReport
        {
            Mode = mode,
            TargetName = row.Name,
            Removed = removed.Count,
            NeedsAdministrator = denied,
            Failed = failed,
            RelaunchFailed = relaunchFailed,
        };
    }

    /// <summary>Windows: WM_CLOSE to its windows; processes without windows have no polite signal and are terminated.</summary>
    private KillOutcome Polite(ProcessIdentity identity)
    {
        bool hasWindows;
        lock (_gate)
        {
            hasWindows = _windows.Contains(identity.Pid);
        }

        if (hasWindows)
        {
            var closed = _platform.CloseWindows(identity);
            if (closed != KillOutcome.NoWindows)
            {
                return closed;
            }
        }

        return _platform.Terminate(identity);
    }

    private void KillTree(ProcessRow row, Action<ProcessIdentity, KillOutcome> account, CancellationToken cancellationToken)
    {
        IReadOnlyList<RawProcess> snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        var root = snapshot.FirstOrDefault(p => p.Identity == row.Identity);
        if (root is null)
        {
            account(row.Identity, KillOutcome.AlreadyGone);
            return;
        }

        var protectedPids = Processes.Where(p => p.IsProtected).Select(p => p.Pid).ToHashSet();
        foreach (var child in ProcessTree.DescendantsDeepestFirst(root, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protectedPids.Contains(child.Pid) || !child.Identity.IsKnown)
            {
                continue;
            }

            account(child.Identity, _platform.Terminate(child.Identity));
        }

        account(root.Identity, _platform.Terminate(root.Identity));
    }

    /// <summary>Close politely, wait up to 10 s for the exit, start the same executable with the same arguments.</summary>
    private bool Restart(ProcessRow row, Action<ProcessIdentity, KillOutcome> account, CancellationToken cancellationToken)
    {
        if (!row.CanRestart || row.Path is not { } path)
        {
            account(row.Identity, KillOutcome.Failed);
            return true;
        }

        var commandLine = _platform.CommandLine(row.Identity);
        var arguments = commandLine is null ? null : ArgumentsOf(commandLine);
        var closed = _platform.CloseWindows(row.Identity);
        if (closed is KillOutcome.AccessDenied or KillOutcome.Failed or KillOutcome.NoWindows)
        {
            account(row.Identity, closed == KillOutcome.NoWindows ? KillOutcome.Failed : closed);
            return true;
        }

        var exited = closed is KillOutcome.AlreadyGone or KillOutcome.IdentityChanged
                     || _platform.WaitForExitAsync(row.Identity, RestartWait, cancellationToken).GetAwaiter().GetResult();
        if (!exited)
        {
            // The app is still running (it may be asking to save): never start a second copy.
            account(row.Identity, KillOutcome.Failed);
            return true;
        }

        account(row.Identity, KillOutcome.Done);
        return _platform.Launch(path, string.IsNullOrWhiteSpace(arguments) ? null : arguments, Path.GetDirectoryName(path));
    }

    private void RemoveRows(IReadOnlyCollection<ProcessIdentity> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var set = removed.ToHashSet();
        lock (_gate)
        {
            _processes = _processes.Where(p => !set.Contains(p.Identity)).ToList();
            _rows = _rows.Where(r => !set.Contains(r.Identity)).ToList();
        }

        RaiseChanged();
    }

    private void ScheduleRefresh() =>
        _ = Task.Delay(PostKillRefreshDelay).ContinueWith(_ => RefreshAsync(force: true), TaskScheduler.Default);

    private void RaiseChanged() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private sealed record Facts(string? Path, string? User, string DisplayName, bool IsProtected);
}
