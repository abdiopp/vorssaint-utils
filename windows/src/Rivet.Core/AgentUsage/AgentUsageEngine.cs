// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Agents;

/// <summary>What the engine reads and how it alerts, re-evaluated on every pass.</summary>
public sealed record AgentUsageOptions
{
    public required IReadOnlySet<AgentProvider> Providers { get; init; }

    public double LimitThreshold { get; init; } = 80;

    public double DailyBudget { get; init; }

    /// <summary>The archive is only resumed by the same build.</summary>
    public string Build { get; init; } = AppIdentity.VersionString;
}

/// <summary>
/// Reads the agents' local logs incrementally on a background worker (spec 07
/// §3.8.2–§3.8.4): discovery, resume from the archive, a file watcher, a 2 s
/// poll of recent logs (agents keep logs open, so writes are not always
/// reported), a 30 s housekeeping tick, and a debounced snapshot for the UI.
/// All state is guarded by one lock; events are raised outside it.
/// </summary>
public sealed class AgentUsageEngine : IDisposable
{
    public const int MaxSessionRecordBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly IAgentUsagePlatform _platform;
    private readonly AgentUsagePaths _paths;
    private readonly Func<AgentUsageOptions> _options;
    private readonly string? _archivePath;
    private readonly AgentPriceManager? _prices;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _zone;
    private readonly AgentUsageStore _store = new();
    private readonly LimitAlerts _alerts = new();
    private readonly Dictionary<string, LogCursor> _cursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OpenCodeReader> _openCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _openCodeSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenClaudeSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _dirty = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<object> _pendingEvents = [];
    private volatile CancellationTokenSource? _cts;
    private Timer? _pollTimer;
    private Timer? _tickTimer;
    private Timer? _publishTimer;
    private volatile bool _loaded;
    private volatile bool _rescan;
    private DateTimeOffset _lastDiscovery;
    private DateTimeOffset _lastSave;
    private DateTime _claudeConfigTime;
    private string? _claudeOrganization;
    private DateTime _claudeHistoryTime;
    private List<ClaudeUsageSample> _claudeHistory = [];
    private DateTime? _budgetNotifiedDay;
    private TimeSpan? _offlineSince;
    private DateTimeOffset _offlineSinceWall;
    private bool _stateChanged;

    public AgentUsageEngine(
        IAgentUsagePlatform platform,
        AgentUsagePaths paths,
        Func<AgentUsageOptions> options,
        string? archivePath,
        AgentPriceManager? prices = null,
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? zone = null)
    {
        _platform = platform;
        _paths = paths;
        _options = options;
        _archivePath = archivePath;
        _prices = prices;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _zone = zone ?? TimeZoneInfo.Local;
        if (_prices is not null)
        {
            _prices.Changed += OnPricesChanged;
        }
    }

    /// <summary>A new snapshot (raised on a worker thread).</summary>
    public event EventHandler<AgentUsageSnapshot>? Published;

    /// <summary>A completed task finished recently (raised on a worker thread; filtering by length is up to the listener).</summary>
    public event EventHandler<AgentFinished>? TaskFinished;

    public event EventHandler<AgentAlert>? Alerted;

    public bool IsLoaded => _loaded;

    /// <summary>The store, for tests.</summary>
    internal AgentUsageStore Store => _store;

    /// <summary>Starts the background read, the timers and the file watchers.</summary>
    public void Start()
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }

        var token = cts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                Initialize(token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                StartWatchers();
                lock (_gate)
                {
                    if (_cts is null || token.IsCancellationRequested)
                    {
                        return;
                    }

                    _pollTimer = new Timer(_ => Safe(Poll), null, AgentUsageConstants.PollInterval, AgentUsageConstants.PollInterval);
                    _tickTimer = new Timer(_ => Safe(MainTick), null, AgentUsageConstants.MainTick, AgentUsageConstants.MainTick);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("agents", "Reading agent usage failed.", ex);
            }
        }, token);
    }

    /// <summary>Stops everything. The archive is saved, or deleted when <paramref name="keepArchive"/> is false.</summary>
    public void Stop(bool keepArchive)
    {
        // Cancel first: a long initial read holds the lock and stops at its next chunk.
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        lock (_gate)
        {
            _pollTimer?.Dispose();
            _tickTimer?.Dispose();
            _publishTimer?.Dispose();
            _pollTimer = _tickTimer = _publishTimer = null;
        }

        StopWatchers();
        lock (_gate)
        {
            if (keepArchive && _loaded)
            {
                SaveArchiveLocked();
            }
            else if (!keepArchive && _archivePath is not null)
            {
                AgentUsageArchive.Delete(_archivePath);
            }

            _store.Clear();
            _cursors.Clear();
            _openCode.Clear();
            _openCodeSeen.Clear();
            _alerts.Clear();
            _loaded = false;
        }

        cts?.Dispose();
    }

    public void Dispose()
    {
        if (_prices is not null)
        {
            _prices.Changed -= OnPricesChanged;
        }

        Stop(keepArchive: true);
    }

    /// <summary>The initial read: discovery, resume, every log, then transitions are enabled.</summary>
    public void Initialize(CancellationToken cancel = default)
    {
        lock (_gate)
        {
            _store.Clear();
            _cursors.Clear();
            _openCode.Clear();
            _openCodeSeen.Clear();
            _alerts.Clear();
            _seenClaudeSessions.Clear();
            _loaded = false;
            _store.Prices = _prices?.Current ?? PriceList.Bundled;
            var options = _options();
            var now = _clock();
            var files = _paths.Discover(options.Providers, now);
            ResumeFromArchive(files, options, now);
            foreach (var file in files)
            {
                cancel.ThrowIfCancellationRequested();
                ReadFile(file, now, cancel);
            }

            _store.Tick(now);
            ReadClaudeConfig(force: true);
            ReadClaudeAppLimits(force: true, now);
            CheckClaudeProcesses(atLaunch: true);

            // From here on, transitions are real: history never notifies.
            _store.TransitionsEnabled = true;
            _store.DrainFinished();
            foreach (var limits in _store.Limits.Values)
            {
                _alerts.Check(limits, options.LimitThreshold, now);
            }

            if (options.DailyBudget > 0 && TodayCost(now) >= options.DailyBudget)
            {
                _budgetNotifiedDay = AgentUsageSummary.LocalDay(now, _zone);
            }

            _lastDiscovery = now;
            _lastSave = now;
            _loaded = true;
            SaveArchiveLocked();
        }

        PublishNow();
    }

    /// <summary>The 2 s poll: recent logs, watcher hints, live-turn housekeeping.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                return;
            }

            var now = _clock();
            var options = _options();
            var changed = false;
            if (_rescan || !_dirty.IsEmpty)
            {
                var discover = _rescan;
                _rescan = false;
                while (_dirty.TryDequeue(out var path))
                {
                    var known = path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
                    if (_cursors.ContainsKey(known))
                    {
                        changed |= ReadKnown(known, now);
                    }
                    else
                    {
                        discover = true;
                    }
                }

                if (discover)
                {
                    changed |= DiscoverNew(options, now);
                }
            }

            var turnFiles = _store.Turns.Keys.Concat(_store.Waiting.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var cursor in _cursors.Values.ToList())
            {
                var recent = now - cursor.Modified <= AgentUsageConstants.PollWindow;
                if (recent || turnFiles.Contains(cursor.Path) || turnFiles.Contains(AgentUsagePaths.ClaudeParentLog(cursor.Path)))
                {
                    changed |= ReadKnown(cursor.Path, now);
                }
            }

            changed |= PollOpenCode(now);
            changed |= CheckOffline(now);
            if (_store.Turns.Values.Concat(_store.Waiting.Values).Any(t => t.Provider == AgentProvider.Claude))
            {
                changed |= CheckClaudeProcesses(atLaunch: false) > 0;
            }

            if (changed)
            {
                CheckLimits(options, now);
                CollectFinished();
                _stateChanged = true;
            }
        }

        RaisePending();
        if (_stateChanged)
        {
            RequestPublish();
        }
    }

    /// <summary>The 30 s tick: housekeeping, older logs, rediscovery, Claude limits, archive, publish.</summary>
    public void MainTick()
    {
        DateTimeOffset now;
        lock (_gate)
        {
            if (!_loaded)
            {
                return;
            }

            now = _clock();
            var options = _options();
            if (_prices is not null && !ReferenceEquals(_prices.Current, _store.Prices))
            {
                _store.Prices = _prices.Current;
                _store.RepriceAll();
            }

            _store.Tick(now);
            foreach (var cursor in _cursors.Values.Where(c => now - c.Modified <= TimeSpan.FromHours(24)).ToList())
            {
                ReadKnown(cursor.Path, now);
            }

            if (now - _lastDiscovery >= AgentUsageConstants.RootRecheck)
            {
                DiscoverNew(options, now);
                ReadClaudeConfig(force: false);
                _lastDiscovery = now;
            }

            ReadClaudeAppLimits(force: false, now);
            CheckLimits(options, now);
            if (_store.TransitionsEnabled)
            {
                foreach (var alert in _alerts.Tick(_store.Limits.Values, now))
                {
                    _pendingEvents.Add(alert);
                }
            }

            CollectFinished();
            if (now - _lastSave >= AgentUsageConstants.SaveInterval)
            {
                SaveArchiveLocked();
                _lastSave = now;
            }
        }

        RaisePending();
        PublishNow();
        if (_prices is not null)
        {
            _ = _prices.UpdateIfDueAsync(now);
        }
    }

    public void SaveArchive()
    {
        lock (_gate)
        {
            SaveArchiveLocked();
        }
    }

    /// <summary>Builds the snapshot now (also raises <see cref="Published"/> via <see cref="PublishNow"/>).</summary>
    public AgentUsageSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                return AgentUsageSnapshot.Loading;
            }

            var now = _clock();
            var options = _options();
            var enabled = options.Providers.ToHashSet();
            var records = _store.Records.Values.Where(r => enabled.Contains(r.Provider)).ToList();
            var providers = new List<AgentProviderStatus>();
            foreach (var provider in AgentProviders.All)
            {
                _store.Limits.TryGetValue(provider, out var limits);
                var providerRecords = records.Where(r => r.Provider == provider);
                DateTimeOffset? last = null;
                foreach (var record in providerRecords)
                {
                    if (last is null || record.Date > last)
                    {
                        last = record.Date;
                    }
                }

                providers.Add(new AgentProviderStatus
                {
                    Provider = provider,
                    Enabled = enabled.Contains(provider),
                    Found = _paths.IsFound(provider),
                    Seen = enabled.Contains(provider) && _store.IsSeen(provider),
                    Plan = provider switch
                    {
                        AgentProvider.Claude => _store.ClaudePlan,
                        AgentProvider.Codex => _store.CodexPlan,
                        _ => null,
                    },
                    Windows = limits?.Windows.Select(w => w.Current(now)).OrderBy(w => w.Kind).ThenBy(w => w.Scope).ToList() ?? [],
                    LimitsSource = limits?.Source,
                    LimitsObservedAt = limits?.ObservedAt,
                    LastActivity = last,
                    Working = _store.Turns.Values.Any(t => t.Provider == provider),
                    Estimate = provider == AgentProvider.Claude && limits is null ? ClaudeLimits.Estimate(records, now) : null,
                    Locations = _paths.Roots(provider),
                });
            }

            var days = AgentUsageSummary.Days(records, now, _zone);
            var claudeLimits = _store.Limits.TryGetValue(AgentProvider.Claude, out var claude) && claude.Source == LimitSource.ClaudeApp
                ? (now - claude.ObservedAt < ClaudeLimits.Freshness ? ClaudeLimitsStatus.Fresh : ClaudeLimitsStatus.Stale)
                : ClaudeAppInstalled() ? ClaudeLimitsStatus.Stale : ClaudeLimitsStatus.None;
            return new AgentUsageSnapshot
            {
                Loaded = true,
                At = now,
                Providers = providers,
                Live = _store.Turns.Values.Where(t => enabled.Contains(t.Provider)).OrderBy(t => t.Started)
                    .Select(t => new LiveTurn(t.Provider, t.Project, t.Model, t.Started, t.OutputTokens, t.Cost)).ToList(),
                Today = AgentUsageSummary.Period(records, AgentPeriod.Today, now, _zone),
                Week = AgentUsageSummary.Period(records, AgentPeriod.Week, now, _zone),
                Month = AgentUsageSummary.Period(records, AgentPeriod.Month, now, _zone),
                Hours = AgentUsageSummary.Hours(records, now, _zone),
                Days = days,
                Activity = AgentUsageSummary.Activity(days),
                PricesUpdated = _store.Prices?.Updated,
                ClaudeLimits = claudeLimits,
                ClaudeAppInstalled = ClaudeAppInstalled(),
            };
        }
    }

    /// <summary>Publishes a snapshot now and checks the daily budget.</summary>
    public void PublishNow()
    {
        var snapshot = BuildSnapshot();
        if (!snapshot.Loaded)
        {
            return;
        }

        lock (_gate)
        {
            var options = _options();
            var today = AgentUsageSummary.LocalDay(snapshot.At, _zone);
            var cost = snapshot.Today?.Totals.Cost ?? 0;
            if (options.DailyBudget > 0 && cost >= options.DailyBudget && _budgetNotifiedDay != today)
            {
                _budgetNotifiedDay = today;
                _pendingEvents.Add(new BudgetAlert(cost));
            }

            _stateChanged = false;
        }

        RaisePending();
        Published?.Invoke(this, snapshot);
    }

    private void RequestPublish()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            _publishTimer ??= new Timer(_ => Safe(PublishNow), null, Timeout.Infinite, Timeout.Infinite);
            _publishTimer.Change(AgentUsageConstants.PublishDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void ResumeFromArchive(List<AgentLogFile> files, AgentUsageOptions options, DateTimeOffset now)
    {
        if (_archivePath is null || AgentUsageArchive.Load(_archivePath) is not { } archive)
        {
            return;
        }

        if (archive.Build != options.Build || AgentUsageArchive.ProvidersKey(options.Providers) != string.Join(',', archive.Providers.Order(StringComparer.Ordinal)))
        {
            return;
        }

        var discovered = files.Where(f => f.Provider != AgentProvider.OpenCode).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cursor in archive.Cursors)
        {
            if (!discovered.ContainsKey(cursor.Path) || cursor.Provider == AgentProvider.OpenCode)
            {
                continue;
            }

            var identity = _platform.FileIdentity(cursor.Path);
            if (identity != cursor.Identity || !FingerprintMatches(cursor))
            {
                continue;
            }

            cursor.Parser = null;
            _cursors[cursor.Path] = cursor;
            valid.Add(cursor.Path);
        }

        // Files the archive knew without a valid cursor are gone: every record touching one is
        // dropped, and the other files of those records are read again from the start, so the
        // result equals reading every log from scratch.
        var rereadFromStart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archived in archive.Records)
        {
            if (archived.ToRecord() is not { } record || record.Provider == AgentProvider.OpenCode)
            {
                continue;
            }

            if (record.Sources.Count == 0 || record.Sources.Any(s => !valid.Contains(s)))
            {
                rereadFromStart.UnionWith(record.Sources.Where(valid.Contains));
                continue;
            }

            _store.Records[record.Key] = record;
        }

        foreach (var path in rereadFromStart)
        {
            _cursors.Remove(path);
        }

        foreach (var limits in archive.Limits)
        {
            if (AgentProviders.FromId(limits.Provider) is { } provider)
            {
                _store.SetLimits(new ProviderLimits(provider, limits.Windows, limits.ObservedAt, limits.Source));
            }
        }

        _store.CodexPlan = archive.CodexPlan;
        _store.CodexPlanObserved = archive.CodexPlanObserved;
        foreach (var archivedTurn in archive.Turns)
        {
            if (archivedTurn.ToTurn() is { } turn && valid.Contains(TurnFile(turn.Key)))
            {
                _store.Turns[turn.Key] = turn;
            }
        }

        foreach (var archivedTurn in archive.Waiting)
        {
            if (archivedTurn.ToTurn() is { } turn && valid.Contains(TurnFile(turn.Key)))
            {
                _store.Waiting[turn.Key] = turn;
            }
        }

        _store.RepriceAll();
        _store.Tick(now);
    }

    private static string TurnFile(string turnKey) => turnKey;

    private bool FingerprintMatches(LogCursor cursor)
    {
        try
        {
            using var stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length >= cursor.Offset && LogCursorReader.Fingerprint(stream, cursor.Offset) == cursor.Fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SaveArchiveLocked()
    {
        if (_archivePath is null || !_loaded)
        {
            return;
        }

        var options = _options();
        var data = new AgentArchiveData
        {
            Build = options.Build,
            Providers = [.. options.Providers.Select(p => p.Id()).Order(StringComparer.Ordinal)],
            Records = [.. _store.Records.Values.Where(r => r.Provider != AgentProvider.OpenCode).Select(ArchivedRecord.From)],
            Limits = [.. _store.Limits.Values.Select(l => new ArchivedLimits(l.Provider.Id(), [.. l.Windows], l.ObservedAt, l.Source))],
            CodexPlan = _store.CodexPlan,
            CodexPlanObserved = _store.CodexPlanObserved,
            Turns = [.. _store.Turns.Values.Where(t => t.Provider != AgentProvider.OpenCode).Select(ArchivedTurn.From)],
            Waiting = [.. _store.Waiting.Values.Where(t => t.Provider != AgentProvider.OpenCode).Select(ArchivedTurn.From)],
            Cursors = [.. _cursors.Values],
        };
        AgentUsageArchive.Save(_archivePath, data);
    }

    private bool ReadFile(AgentLogFile file, DateTimeOffset now, CancellationToken cancel = default)
    {
        if (file.Provider == AgentProvider.OpenCode)
        {
            if (!_openCode.TryGetValue(file.Path, out var reader))
            {
                _openCode[file.Path] = reader = new OpenCodeReader(file.Path);
            }

            _openCodeSeen[file.Path] = file.ModifiedUtc;
            var entries = new List<AgentEntry>();
            reader.Read(entries, _platform.FileIdentity, now, cancel);
            _store.Apply(AgentProvider.OpenCode, file.Path, file.Path, entries, now);
            return entries.Count > 0;
        }

        if (!_cursors.TryGetValue(file.Path, out var cursor))
        {
            _cursors[file.Path] = cursor = new LogCursor { Path = file.Path, Provider = file.Provider };
        }

        return ReadCursor(cursor, now, cancel);
    }

    private bool ReadKnown(string path, DateTimeOffset now) =>
        _cursors.TryGetValue(path, out var cursor) && ReadCursor(cursor, now, default);

    private bool ReadCursor(LogCursor cursor, DateTimeOffset now, CancellationToken cancel)
    {
        var entries = new List<AgentEntry>();
        var outcome = LogCursorReader.Read(cursor, _platform.FileIdentity, entries, cancel);
        if (outcome == LogReadOutcome.Missing)
        {
            // A deleted log: its turn ends silently; what only it held goes at the next start.
            _cursors.Remove(cursor.Path);
            return _store.EndSilently(t => string.Equals(t.Key, cursor.Path, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        if (entries.Count == 0)
        {
            return outcome == LogReadOutcome.Restarted;
        }

        var turnKey = cursor.Provider == AgentProvider.Claude && AgentUsagePaths.IsClaudeSubagent(cursor.Path)
            ? AgentUsagePaths.ClaudeParentLog(cursor.Path)
            : cursor.Path;
        _store.Apply(cursor.Provider, cursor.Path, turnKey, entries, now);
        return true;
    }

    private bool DiscoverNew(AgentUsageOptions options, DateTimeOffset now)
    {
        var changed = false;
        foreach (var file in _paths.Discover(options.Providers, now))
        {
            if (file.Provider == AgentProvider.OpenCode ? !_openCode.ContainsKey(file.Path) : !_cursors.ContainsKey(file.Path))
            {
                changed |= ReadFile(file, now);
            }
        }

        return changed;
    }

    private bool PollOpenCode(DateTimeOffset now)
    {
        var changed = false;
        foreach (var path in _openCode.Keys.ToList())
        {
            DateTime modified;
            try
            {
                modified = File.GetLastWriteTimeUtc(path);
                if (File.Exists(path + "-wal"))
                {
                    var wal = File.GetLastWriteTimeUtc(path + "-wal");
                    if (wal > modified) modified = wal;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (_openCodeSeen.TryGetValue(path, out var seen) && modified <= seen)
            {
                continue;
            }

            changed |= ReadFile(new AgentLogFile(AgentProvider.OpenCode, path, modified), now);
        }

        return changed;
    }

    /// <summary>Network gone for 20 s of awake time: Claude turns end, unless the model replied since the drop or a Bash command runs.</summary>
    private bool CheckOffline(DateTimeOffset now)
    {
        if (_platform.IsNetworkAvailable)
        {
            _offlineSince = null;
            return false;
        }

        if (_offlineSince is null)
        {
            _offlineSince = _platform.AwakeTime;
            _offlineSinceWall = now;
            return false;
        }

        if (_platform.AwakeTime - _offlineSince.Value < AgentUsageConstants.OfflineGrace)
        {
            return false;
        }

        var dropped = _offlineSinceWall;
        return EndTurnsSilently(t => t.Provider == AgentProvider.Claude && t.RunningCommands.Count == 0 && (t.LastReply is null || t.LastReply < dropped)) > 0;
    }

    /// <summary>
    /// Claude Code's session registry (&lt;config&gt;/sessions/&lt;pid&gt;.json): a turn
    /// whose process died, or whose record vanished after being seen, ends
    /// silently; at launch so does one without a record. Records for another
    /// pidDomain (another OS) are skipped; unreadable records end nothing.
    /// </summary>
    private int CheckClaudeProcesses(bool atLaunch)
    {
        var directories = _paths.ClaudeSessionDirectories.Where(Directory.Exists).ToList();
        if (directories.Count == 0)
        {
            return 0;
        }

        var pids = new Dictionary<string, (int Pid, DateTimeOffset Written)>(StringComparer.OrdinalIgnoreCase);
        var unreadable = false;
        foreach (var directory in directories)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.json").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxSessionRecordBytes)
                    {
                        unreadable = true;
                        continue;
                    }

                    using var document = JsonDocument.Parse(File.ReadAllBytes(file));
                    var root = document.RootElement;
                    var domain = root.Get("pidDomain").String();
                    if (domain is not null && !string.Equals(domain, _platform.PidDomain, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (root.Get("sessionId").String() is { Length: > 0 } session && root.Get("pid").Number() is { } pid)
                    {
                        pids[session] = ((int)pid, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    unreadable = true;
                }
            }
        }

        var ended = 0;
        foreach (var turn in _store.Turns.Values.Concat(_store.Waiting.Values).Where(t => t.Provider == AgentProvider.Claude).ToList())
        {
            var session = Path.GetFileNameWithoutExtension(turn.Key);
            bool end;
            if (pids.TryGetValue(session, out var record))
            {
                _seenClaudeSessions.Add(session);
                end = _platform.ProcessState(record.Pid, record.Written) == AgentProcessState.Dead;
            }
            else
            {
                end = !unreadable && (_seenClaudeSessions.Contains(session) || atLaunch);
            }

            if (end)
            {
                ended += EndTurnsSilently(t => ReferenceEquals(t, turn));
            }
        }

        return ended;
    }

    private void ReadClaudeConfig(bool force)
    {
        var file = _paths.ClaudeConfigFile;
        try
        {
            if (!File.Exists(file))
            {
                return;
            }

            var modified = File.GetLastWriteTimeUtc(file);
            if (!force && modified == _claudeConfigTime)
            {
                return;
            }

            _claudeConfigTime = modified;
            using var document = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 256 });
            var account = document.RootElement.Get("oauthAccount");
            _claudeOrganization = account.Get("organizationUuid").String();
            var tier = account.Get("organizationRateLimitTier").String() ?? account.Get("userRateLimitTier").String();
            _store.ClaudePlan = AgentPricing.ClaudePlan(_store.Prices, tier, account.Get("organizationType").String());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Info("agents", "Claude Code's account file could not be read.");
        }
    }

    private void ReadClaudeAppLimits(bool force, DateTimeOffset now)
    {
        var file = _paths.ClaudeAppHistoryCandidates.FirstOrDefault(File.Exists);
        if (file is not null)
        {
            try
            {
                var modified = File.GetLastWriteTimeUtc(file);
                if (force || modified != _claudeHistoryTime)
                {
                    _claudeHistoryTime = modified;
                    var info = new FileInfo(file);
                    _claudeHistory = info.Length <= ClaudeLimits.MaxHistoryBytes
                        ? ClaudeLimits.ParseHistory(File.ReadAllBytes(file)) ?? []
                        : [];
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _claudeHistory = [];
            }
        }

        // Recomputed against "now" on every tick.
        var limits = _claudeHistory.Count == 0 ? null : ClaudeLimits.FromHistory(_claudeHistory, _claudeOrganization, now, FirstClaudeRequestBetween);
        if (limits is not null)
        {
            _store.SetLimits(limits);
        }
        else if (_store.Limits.TryGetValue(AgentProvider.Claude, out var old) && old.Source == LimitSource.ClaudeApp)
        {
            _store.Limits.Remove(AgentProvider.Claude);
        }
    }

    private DateTimeOffset? FirstClaudeRequestBetween(DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset? first = null;
        foreach (var record in _store.Records.Values)
        {
            if (record.Provider == AgentProvider.Claude && record.Date > from && record.Date <= to && (first is null || record.Date < first))
            {
                first = record.Date;
            }
        }

        return first;
    }

    private void CheckLimits(AgentUsageOptions options, DateTimeOffset now)
    {
        if (!_store.TransitionsEnabled)
        {
            return;
        }

        foreach (var limits in _store.Limits.Values.ToList())
        {
            if (!options.Providers.Contains(limits.Provider))
            {
                continue;
            }

            foreach (var alert in _alerts.Check(limits, options.LimitThreshold, now))
            {
                _pendingEvents.Add(alert);
            }
        }
    }

    private void CollectFinished()
    {
        var options = _options();
        foreach (var finished in _store.DrainFinished())
        {
            if (options.Providers.Contains(finished.Provider))
            {
                _pendingEvents.Add(finished);
            }
        }
    }

    private double TodayCost(DateTimeOffset now)
    {
        var today = AgentUsageSummary.LocalDay(now, _zone);
        return _store.Records.Values.Where(r => r.Date <= now && AgentUsageSummary.LocalDay(r.Date, _zone) == today).Sum(r => r.Cost ?? 0);
    }

    private bool ClaudeAppInstalled() =>
        _paths.ClaudeAppFolders.Any(Directory.Exists) || _paths.ClaudeAppHistoryCandidates.Any(File.Exists);

    private void RaisePending()
    {
        object[] events;
        lock (_gate)
        {
            if (_pendingEvents.Count == 0)
            {
                return;
            }

            events = [.. _pendingEvents];
            _pendingEvents.Clear();
        }

        foreach (var e in events)
        {
            switch (e)
            {
                case AgentFinished finished:
                    TaskFinished?.Invoke(this, finished);
                    break;
                case AgentAlert alert:
                    Alerted?.Invoke(this, alert);
                    break;
            }
        }
    }

    private void StartWatchers()
    {
        var options = _options();
        var roots = new List<(string Path, string Filter)>();
        foreach (var provider in options.Providers)
        {
            switch (provider)
            {
                case AgentProvider.OpenCode:
                    foreach (var db in _paths.OpenCodeDatabases.Where(File.Exists).Take(1))
                    {
                        roots.Add((Path.GetDirectoryName(db)!, "opencode.db*"));
                    }

                    break;
                default:
                    roots.AddRange(_paths.Roots(provider).Where(Directory.Exists).Select(r => (r, "*.jsonl")));
                    break;
            }
        }

        lock (_gate)
        {
            foreach (var (path, filter) in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(path, filter)
                    {
                        IncludeSubdirectories = filter == "*.jsonl",
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Changed += OnFileEvent;
                    watcher.Created += OnFileEvent;
                    watcher.Renamed += OnFileEvent;
                    watcher.Error += (_, _) => _rescan = true;
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
                {
                    Log.Info("agents", $"Cannot watch {path}: {ex.Message}");
                }
            }
        }
    }

    private void StopWatchers()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _dirty.Enqueue(e.FullPath);
        lock (_gate)
        {
            // Changes are read a second later (the watcher's latency), on the poll path.
            _pollTimer?.Change(AgentUsageConstants.PublishDebounce, AgentUsageConstants.PollInterval);
        }
    }

    private void OnPricesChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_loaded || _prices is null)
            {
                return;
            }

            _store.Prices = _prices.Current;
            _store.RepriceAll();
        }

        PublishNow();
    }

    /// <summary>Ends matching turns silently and tells their parsers, so the next prompt opens a new turn.</summary>
    private int EndTurnsSilently(Func<AgentTurn, bool> predicate)
    {
        var keys = _store.Turns.Values.Concat(_store.Waiting.Values).Where(predicate).Select(t => t.Key).ToList();
        var ended = _store.EndSilently(predicate);
        foreach (var key in keys)
        {
            if (_cursors.TryGetValue(key, out var cursor))
            {
                var parser = cursor.EnsureParser();
                parser.ForgetTurn();
                cursor.ParserState = parser.SaveState();
            }
        }

        return ended;
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("agents", "Agent usage update failed.", ex);
        }
    }
}
