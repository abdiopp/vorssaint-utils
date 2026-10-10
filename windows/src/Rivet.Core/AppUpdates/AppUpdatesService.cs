// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Features;
using Rivet.Core.Localization;
using Rivet.Core.Maintenance.PackageManager;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.AppUpdates;

/// <summary>
/// App updates on Windows: winget (which also correlates apps it did not
/// install), Microsoft Store apps (through winget's msstore source, installed
/// by the Store), and Electron apps' own feeds. One check at a time; a request
/// during a check is remembered and runs once more afterwards.
/// </summary>
public sealed class AppUpdatesService : IFeatureController, IDisposable
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ScheduleStartupDelay = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan ScheduleTolerance = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTimerStep = TimeSpan.FromMinutes(5);

    private readonly WingetClient _winget;
    private readonly PackageOperationLane _lane;
    private readonly ISettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly IInstalledAppsProvider _apps;
    private readonly OnlineUpdateSource _online;
    private readonly IShellService _shell;
    private readonly object _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<AppUpdateRow> _merged = [];
    private IReadOnlyList<AppUpdateRow> _rows = [];
    private HashSet<string> _selection = new(StringComparer.Ordinal);
    private Timer? _timer;
    private bool _available;
    private bool _pending;
    private bool _pendingAutomatic = true;
    private int _sourceGeneration;
    private bool _handOffPending;

    public AppUpdatesService(
        WingetClient winget,
        PackageOperationLane lane,
        ISettingsStore settings,
        INotificationService notifications,
        IInstalledAppsProvider apps,
        OnlineUpdateSource online,
        IShellService shell)
    {
        _winget = winget;
        _lane = lane;
        _settings = settings;
        _notifications = notifications;
        _apps = apps;
        _online = online;
        _shell = shell;
        _lane.Changed += (_, _) => Raise();
        _lane.Finished += OnLaneFinished;
        _subscriptions.Add(settings.Observe(() =>
        {
            Interlocked.Increment(ref _sourceGeneration);
            Raise();
        }, AppUpdatesSettings.IncludePackageManager, AppUpdatesSettings.IncludeStore, AppUpdatesSettings.IncludeOnline));
        _subscriptions.Add(settings.Observe(() => Arm(), AppUpdatesSettings.CheckFrequency));
        _subscriptions.Add(settings.Observe(() =>
        {
            // A settings restore during a check discards that check and runs once more.
            Interlocked.Increment(ref _sourceGeneration);
            ReapplyRules();
        }, AppUpdatesSettings.Rules));
    }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event EventHandler? Changed;

    public PackageOperationLane Lane => _lane;

    public IReadOnlyList<AppUpdateRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows;
            }
        }
    }

    public IReadOnlySet<string> Selection
    {
        get
        {
            lock (_gate)
            {
                return _selection.ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    public bool IsChecking { get; private set; }

    /// <summary>A check finished in this process (empty states only show after one).</summary>
    public bool CheckedThisSession { get; private set; }

    /// <summary>winget is missing while its source is on.</summary>
    public bool PackageManagerMissing { get; private set; }

    /// <summary>winget is present but its source agreements were not accepted in the app yet.</summary>
    public bool NeedsAgreement { get; private set; }

    public bool OnlineIncomplete { get; private set; }

    public IReadOnlyList<string> UncheckedNames { get; private set; } = [];

    public string? LastError { get; private set; }

    public IReadOnlyList<UpdateRule> Rules => UpdateRules.Decode(_settings.Get(AppUpdatesSettings.Rules));

    public DateTime? LastCheckUtc
    {
        get
        {
            var seconds = _settings.Get(AppUpdatesSettings.LastCheck);
            return seconds > 0 ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
        }
    }

    public bool IsBusy => IsChecking || _lane.IsRunning;

    public int SelectedCount => Rows.Count(r => r.IsSelectable && Selection.Contains(r.Id));

    public void Sync(bool available)
    {
        _available = available;
        Arm();
        if (!available)
        {
            lock (_gate)
            {
                _merged = [];
                _rows = [];
                _selection.Clear();
            }

            CheckedThisSession = false;
            Raise();
        }
    }

    /// <summary>Every surface calls this when it appears: checks when stale, notChecked, or after a hand-off.</summary>
    public void CheckIfNeeded()
    {
        var last = LastCheckUtc;
        if (_handOffPending || !CheckedThisSession || last is null || DateTime.UtcNow - last.Value >= StaleAfter)
        {
            _handOffPending = false;
            _ = CheckAsync(automatic: false);
        }
    }

    public async Task CheckAsync(bool automatic = false)
    {
        lock (_gate)
        {
            if (IsChecking)
            {
                _pending = true;
                _pendingAutomatic &= automatic;
                return;
            }

            IsChecking = true;
            LastError = null;
        }

        Raise();
        var runAutomatic = automatic;
        while (true)
        {
            var generation = Volatile.Read(ref _sourceGeneration);
            CheckOutcome? outcome = null;
            try
            {
                outcome = await Task.Run(() => RunCheckAsync()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("appUpdates", "Update check failed.", ex);
                LastError = ex.Message;
            }

            var discard = generation != Volatile.Read(ref _sourceGeneration) || !_available;
            if (outcome is not null && !discard)
            {
                Finish(outcome, runAutomatic);
            }

            lock (_gate)
            {
                if (!_pending && !discard)
                {
                    IsChecking = false;
                    break;
                }

                if (!_available)
                {
                    IsChecking = false;
                    _pending = false;
                    break;
                }

                runAutomatic = _pending ? _pendingAutomatic : runAutomatic;
                _pending = false;
                _pendingAutomatic = true;
            }
        }

        Arm();
        Raise();
    }

    public void SetSelected(string rowId, bool selected)
    {
        lock (_gate)
        {
            if (selected)
            {
                _selection.Add(rowId);
            }
            else
            {
                _selection.Remove(rowId);
            }
        }

        Raise();
    }

    public void SelectAll()
    {
        lock (_gate)
        {
            _selection = _rows.Where(r => r.IsSelectable).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        }

        Raise();
    }

    public void ClearSelection()
    {
        lock (_gate)
        {
            _selection.Clear();
        }

        Raise();
    }

    /// <summary>
    /// winget rows of the selection in list order, one after another through
    /// the shared lane; Store rows hand off to the Store (one product page, or
    /// the downloads page for several).
    /// </summary>
    public async Task UpdateSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelectable && Selection.Contains(r.Id)).ToList();
        var store = selected.Where(r => r.Kind == AppUpdateKind.Store).ToList();
        if (store.Count == 1)
        {
            OpenStorePage(store[0].PackageId);
        }
        else if (store.Count > 1)
        {
            OpenStorePage(null);
        }

        var packages = selected.Where(r => r.Kind == AppUpdateKind.PackageManager && r.PackageId is not null)
            .Select(r => new PackageOperationRequest(PackageOperationKind.Upgrade, r.PackageId, r.Name, r.Source))
            .ToList();
        if (packages.Count > 0)
        {
            await _lane.RunBatchAsync(packages).ConfigureAwait(false);
        }
    }

    public async Task UpdateOneAsync(AppUpdateRow row)
    {
        switch (row.Kind)
        {
            case AppUpdateKind.PackageManager when row.PackageId is not null:
                await _lane.RunAsync(new PackageOperationRequest(PackageOperationKind.Upgrade, row.PackageId, row.Name, row.Source)).ConfigureAwait(false);
                break;
            case AppUpdateKind.Store:
                OpenStorePage(row.PackageId);
                break;
            case AppUpdateKind.Online:
                OpenApp(row);
                break;
        }
    }

    public void OpenApp(AppUpdateRow row)
    {
        if (row.AppPath is { } path)
        {
            _handOffPending = true;
            _shell.OpenFile(path);
        }
    }

    public void OpenStorePage(string? productId)
    {
        _handOffPending = true;
        _shell.OpenUrl(productId is { Length: > 0 } id && id.All(char.IsAsciiLetterOrDigit)
            ? "ms-windows-store://pdp/?productid=" + id
            : "ms-windows-store://downloadsandupdates");
    }

    /// <summary>Skips exactly this release (a newer one shows again).</summary>
    public void SkipVersion(AppUpdateRow row)
    {
        if (IsChecking || VersionComparer.IsUncomparable(row.LatestVersion))
        {
            return;
        }

        SaveRules(UpdateRules.WithRule(Rules, new UpdateRule { Key = row.RuleKey, Name = row.Name, Version = row.LatestVersion }));
    }

    /// <summary>Stops checking this app until the rule is removed.</summary>
    public void ExcludeApp(AppUpdateRow row)
    {
        if (IsChecking)
        {
            return;
        }

        SaveRules(UpdateRules.WithRule(Rules, new UpdateRule { Key = row.RuleKey, Name = row.Name }));
    }

    /// <summary>
    /// Removing a skip restores the cached results without a check; removing an
    /// exclusion marks the session as not checked (the hint asks for Check now).
    /// </summary>
    public void RemoveRule(UpdateRule rule)
    {
        if (IsBusy)
        {
            return;
        }

        SaveRules(UpdateRules.Without(Rules, rule.Key));
        if (rule.IsExclusion)
        {
            CheckedThisSession = false;
            Raise();
        }
    }

    /// <summary>The next background check, or null when the schedule is off.</summary>
    public DateTime? NextCheckUtc()
    {
        var interval = Interval(_settings.Get(AppUpdatesSettings.CheckFrequency));
        if (interval is null)
        {
            return null;
        }

        return NextCheck(LastCheckUtc, interval.Value, DateTime.UtcNow);
    }

    /// <summary>lastCheck + interval; never checked or already due → now + 180 s (startup never waits on winget).</summary>
    public static DateTime NextCheck(DateTime? lastCheckUtc, TimeSpan interval, DateTime nowUtc)
    {
        if (lastCheckUtc is { } last && last + interval > nowUtc)
        {
            return last + interval;
        }

        return nowUtc + ScheduleStartupDelay;
    }

    public static TimeSpan? Interval(string frequency) => frequency switch
    {
        "daily" => TimeSpan.FromHours(24),
        "weekly" => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>The notification text for <paramref name="count"/> findings.</summary>
    public static string NotificationBody(int count) =>
        count == 1 ? L.Get("appUpdates.notificationBodyOne") : L.Format("appUpdates.notificationBodyFormat", count.ToString(CultureInfo.CurrentCulture));

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private async Task<CheckOutcome> RunCheckAsync()
    {
        var rules = Rules;
        var includePackages = _settings.Get(AppUpdatesSettings.IncludePackageManager);
        var includeStore = _settings.Get(AppUpdatesSettings.IncludeStore);
        var includeOnline = _settings.Get(AppUpdatesSettings.IncludeOnline);
        var rows = new List<AppUpdateRow>();
        var wingetNames = new List<string>();
        var missing = false;
        var needsAgreement = false;
        string? error = null;

        if (includePackages || includeStore)
        {
            var availability = await _winget.DetectAsync().ConfigureAwait(false);
            if (availability != WingetAvailability.Available)
            {
                missing = includePackages;
            }
            else if (!_winget.AgreementsAccepted)
            {
                needsAgreement = true;
            }
            else
            {
                var result = await _winget.ListUpgradesAsync(CancellationToken.None).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    foreach (var package in result.Value!)
                    {
                        wingetNames.Add(package.Name);
                        if (ToRow(package, includePackages, includeStore, rules) is { } row)
                        {
                            rows.Add(row);
                        }
                    }
                }
                else
                {
                    error = result.Error;
                }
            }
        }

        IReadOnlyList<string> notChecked = [];
        if (includeOnline)
        {
            try
            {
                var apps = _apps.Enumerate();
                var covered = wingetNames.Select(n => n.TrimEnd(WingetPackage.Ellipsis)).ToList();
                var online = await _online.CheckAsync(
                    apps,
                    app => UpdateRules.IsExcluded(rules, OnlineUpdateSource.RuleKeyFor(app)) || IsOwnApp(app.DisplayName, null)
                           || covered.Any(n => n.Length > 0 && app.DisplayName.StartsWith(n, StringComparison.OrdinalIgnoreCase)),
                    CancellationToken.None).ConfigureAwait(false);
                rows.AddRange(online.Rows);
                notChecked = online.UncheckedNames;
            }
            catch (Exception ex)
            {
                Log.Warn("appUpdates", "Online update check failed.", ex);
            }
        }

        return new CheckOutcome(AppUpdateList.Merge(rows), missing, needsAgreement, notChecked, error);
    }

    private static AppUpdateRow? ToRow(WingetPackage package, bool includePackages, bool includeStore, IReadOnlyList<UpdateRule> rules)
    {
        if (package.RequiresExplicitUpgrade || package.IsLocal || !package.HasUpdate || IsOwnApp(package.Name, package.Id))
        {
            // Pinned and explicit-only packages are not offered, as winget itself does.
            return null;
        }

        if (UpdateRules.IsExcluded(rules, package.Id))
        {
            return null;
        }

        if (package.IsStore)
        {
            return includeStore
                ? new AppUpdateRow
                {
                    Id = AppUpdateRow.StoreRowId(package.Id),
                    Kind = AppUpdateKind.Store,
                    Name = package.Name,
                    InstalledVersion = package.Version,
                    LatestVersion = package.Available!,
                    RuleKey = package.Id,
                    PackageId = package.Id,
                    Source = package.Source,
                    IdUnresolved = package.IdTruncated,
                }
                : null;
        }

        return includePackages
            ? new AppUpdateRow
            {
                Id = AppUpdateRow.PackageRowId(package.Id),
                Kind = AppUpdateKind.PackageManager,
                Name = package.Name,
                InstalledVersion = package.Version,
                LatestVersion = package.Available!,
                RuleKey = package.Id,
                PackageId = package.Id,
                Source = package.Source,
                IdUnresolved = package.IdTruncated,
            }
            : null;
    }

    /// <summary>This app updates itself; its own package never shows up here.</summary>
    private static bool IsOwnApp(string name, string? id) =>
        string.Equals(name.TrimEnd(WingetPackage.Ellipsis), AppIdentity.DisplayName, StringComparison.OrdinalIgnoreCase)
        || (id is not null && (string.Equals(id, AppIdentity.Id, StringComparison.OrdinalIgnoreCase)
                               || id.EndsWith("." + AppIdentity.Id, StringComparison.OrdinalIgnoreCase)));

    private void Finish(CheckOutcome outcome, bool automatic)
    {
        var rules = Rules;
        var visible = UpdateRules.Apply(rules, outcome.Rows);
        lock (_gate)
        {
            _selection = AppUpdateList.Reconcile(_selection, _rows, visible);
            _merged = outcome.Rows;
            _rows = visible;
        }

        PackageManagerMissing = outcome.PackageManagerMissing;
        NeedsAgreement = outcome.NeedsAgreement;
        UncheckedNames = outcome.UncheckedNames;
        OnlineIncomplete = outcome.UncheckedNames.Count > 0;
        LastError = outcome.Error;
        CheckedThisSession = true;
        _settings.Set(AppUpdatesSettings.LastCheck, (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
        _settings.Set(AppUpdatesSettings.LastCount, visible.Count);
        if (automatic)
        {
            Announce(visible);
        }
    }

    /// <summary>
    /// Automatic passes notify only about findings never announced before;
    /// the announced list is pruned to current findings, so a later update of
    /// the same app is announced again.
    /// </summary>
    private void Announce(IReadOnlyList<AppUpdateRow> visible)
    {
        var current = visible.Select(r => r.Id + "@" + VersionComparer.Core(r.LatestVersion)).ToList();
        var announced = _settings.Get(AppUpdatesSettings.NotifiedIds).ToHashSet(StringComparer.Ordinal);
        var fresh = current.Where(id => !announced.Contains(id)).ToList();
        _settings.Set(AppUpdatesSettings.NotifiedIds, current);
        if (fresh.Count == 0 || !_settings.Get(AppUpdatesSettings.Notify) || Interval(_settings.Get(AppUpdatesSettings.CheckFrequency)) is null)
        {
            return;
        }

        _notifications.Show(new NotificationRequest
        {
            Title = L.Get("appUpdates.pageTitle"),
            Body = NotificationBody(visible.Count),
            ClickActionId = AppUpdatesActions.Open,
            Tag = "appUpdates",
        });
    }

    private void ReapplyRules()
    {
        var rules = Rules;
        lock (_gate)
        {
            var visible = UpdateRules.Apply(rules, _merged);
            _selection.IntersectWith(visible.Select(r => r.Id));
            _rows = visible;
        }

        Raise();
    }

    private void SaveRules(IReadOnlyList<UpdateRule> rules) =>
        _settings.Set(AppUpdatesSettings.Rules, UpdateRules.Encode(rules));

    private void OnLaneFinished(object? sender, PackageOperationState state)
    {
        // The list re-checks itself after an upgrade it started (winget may have updated other rows too).
        if (state.Kind is PackageOperationKind.Upgrade or PackageOperationKind.UpgradeAll && state.Result != OperationResult.Cancelled && _available)
        {
            _ = CheckAsync(automatic: false);
        }
    }

    private void Arm()
    {
        _timer?.Dispose();
        _timer = null;
        if (!_available || NextCheckUtc() is not { } next)
        {
            return;
        }

        // Short steps keep the schedule right across sleep and clock changes.
        var due = next - DateTime.UtcNow;
        var step = due < TimeSpan.Zero ? TimeSpan.Zero : (due > MaxTimerStep ? MaxTimerStep : due);
        _timer = new Timer(_ => OnTimer(), null, step, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        if (NextCheckUtc() is { } next && next - DateTime.UtcNow <= ScheduleTolerance && _available)
        {
            UiThread.Post(() => _ = CheckAsync(automatic: true));
            return;
        }

        Arm();
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private sealed record CheckOutcome(
        IReadOnlyList<AppUpdateRow> Rows,
        bool PackageManagerMissing,
        bool NeedsAgreement,
        IReadOnlyList<string> UncheckedNames,
        string? Error);
}

/// <summary>Action ids of the App updates module (kept here so Core can name them in notifications).</summary>
public static class AppUpdatesActions
{
    public const string Open = FeatureIds.AppUpdates + ".open";

    public const string Check = FeatureIds.AppUpdates + ".check";
}
