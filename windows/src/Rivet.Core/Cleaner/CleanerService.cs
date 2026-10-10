// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Cleaner;

public enum CleanerPhase
{
    Idle,
    Scanning,
    Results,
    Cleaning,
    Done,
}

/// <summary>
/// The Cleaner (one shared instance for the panel and Settings):
/// Idle → Scanning → Results → Cleaning → Done. Everything is reviewed
/// first; files go to the Recycle Bin; the Recycle Bin row is the only
/// permanent removal and is labelled as such.
/// </summary>
public sealed class CleanerService
{
    private readonly ICleanerFileSystem _fs;
    private readonly ICleanerPlatform _platform;
    private readonly IRecycler _recycler;
    private readonly IKnownFolders _knownFolders;
    private readonly ISettingsStore _settings;
    private readonly Func<string> _backupFolder;
    private readonly object _gate = new();
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);
    private CancellationTokenSource? _scanCancellation;
    private int _token;
    private IReadOnlyList<CleanerItem> _items = [];

    public CleanerService(ICleanerFileSystem fs, ICleanerPlatform platform, IRecycler recycler, IKnownFolders knownFolders, ISettingsStore settings, Func<string> backupFolder)
    {
        _fs = fs;
        _platform = platform;
        _recycler = recycler;
        _knownFolders = knownFolders;
        _settings = settings;
        _backupFolder = backupFolder;
    }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event EventHandler? Changed;

    public CleanerPhase Phase { get; private set; } = CleanerPhase.Idle;

    /// <summary>The category being scanned (for "Scanning… Caches").</summary>
    public CleanerCategory? ScanningCategory { get; private set; }

    public IReadOnlyList<CleanerItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    public CleanResult? LastResult { get; private set; }

    /// <summary>Apps whose caches were left out because they were running.</summary>
    public IReadOnlyList<string> SkippedRunning { get; private set; } = [];

    /// <summary>Some locations need administrator rights and were not scanned.</summary>
    public bool AdministratorSkipped { get; private set; }

    public bool IsElevated => _platform.IsElevated;

    public long TotalBytes => Items.Sum(i => i.Size);

    public int SelectedCount => Items.Count(IsIncluded);

    public long SelectedBytes => Items.Where(IsIncluded).Sum(i => i.Size);

    public bool IsIncluded(CleanerItem item)
    {
        lock (_gate)
        {
            return _included.Contains(item.Id);
        }
    }

    public void SetIncluded(CleanerItem item, bool include)
    {
        if (Phase != CleanerPhase.Results)
        {
            return;
        }

        lock (_gate)
        {
            if (include)
            {
                _included.Add(item.Id);
            }
            else
            {
                _included.Remove(item.Id);
            }
        }

        Raise();
    }

    public void SetGroupIncluded(CleanerGroup group, bool include)
    {
        if (Phase != CleanerPhase.Results)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var item in _items.Where(i => i.Group == group))
            {
                if (include)
                {
                    _included.Add(item.Id);
                }
                else
                {
                    _included.Remove(item.Id);
                }
            }
        }

        Raise();
    }

    /// <summary>Every item of the group is included (the group checkbox).</summary>
    public bool? GroupState(CleanerGroup group)
    {
        var members = Items.Where(i => i.Group == group).ToList();
        if (members.Count == 0)
        {
            return false;
        }

        var included = members.Count(IsIncluded);
        return included == members.Count ? true : included == 0 ? false : null;
    }

    /// <summary>
    /// Scans every category in order off the UI thread. <paramref name="attended"/>
    /// is true for a scan a person started; the scheduled pass never reads
    /// the person's own folders (screenshots).
    /// </summary>
    public async Task ScanAsync(bool attended)
    {
        CancellationTokenSource cancellation;
        int token;
        lock (_gate)
        {
            if (Phase is CleanerPhase.Scanning or CleanerPhase.Cleaning)
            {
                return;
            }

            _scanCancellation?.Dispose();
            _scanCancellation = cancellation = new CancellationTokenSource();
            token = ++_token;
            _items = [];
            _included.Clear();
            Phase = CleanerPhase.Scanning;
            LastResult = null;
            SkippedRunning = [];
            AdministratorSkipped = false;
        }

        Raise();
        try
        {
            var result = await Task.Run(() => RunScan(attended, token, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (token != _token || cancellation.IsCancellationRequested)
                {
                    return;
                }

                _items = result.Items;
                foreach (var item in result.Items.Where(i => i.Recommended))
                {
                    _included.Add(item.Id);
                }

                SkippedRunning = result.Context.SkippedRunning.ToList();
                AdministratorSkipped = result.Context.AdministratorSkipped;
                ScanningCategory = null;
                Phase = CleanerPhase.Results;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled scans deliver nothing; Cancel() already reset the phase.
        }
        catch (Exception ex)
        {
            Log.Error("cleaner", "Scan failed.", ex);
            lock (_gate)
            {
                if (token == _token)
                {
                    Phase = CleanerPhase.Idle;
                    ScanningCategory = null;
                }
            }
        }

        Raise();
    }

    /// <summary>Back to Idle from any state except Cleaning (a running clean finishes).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (Phase == CleanerPhase.Cleaning)
            {
                return;
            }

            _token++;
            _scanCancellation?.Cancel();
            _items = [];
            _included.Clear();
            ScanningCategory = null;
            Phase = CleanerPhase.Idle;
        }

        Raise();
    }

    /// <summary>
    /// Cleans the included items. <paramref name="escalate"/> is true for a
    /// manual clean (someone can answer a UAC prompt), false for the schedule.
    /// The Recycle Bin is emptied first, so the clean never wipes what it just
    /// made recoverable.
    /// </summary>
    public async Task<CleanResult> CleanSelectedAsync(bool escalate)
    {
        List<CleanerItem> selected;
        lock (_gate)
        {
            if (Phase != CleanerPhase.Results)
            {
                return new CleanResult();
            }

            selected = _items.Where(i => _included.Contains(i.Id)).ToList();
            Phase = CleanerPhase.Cleaning;
        }

        Raise();
        CleanResult result;
        try
        {
            result = await Task.Run(() => Clean(selected, escalate)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("cleaner", "Clean failed.", ex);
            result = new CleanResult { Failed = selected.Count, FailedNames = selected.Select(i => i.Name).Take(4).ToList() };
        }

        lock (_gate)
        {
            LastResult = result;
            _items = [];
            _included.Clear();
            Phase = CleanerPhase.Done;
        }

        Raise();
        return result;
    }

    /// <summary>
    /// The last line of defence, checked right before each removal: not a
    /// critical folder, same identity as at scan time, no reparse point on
    /// the way, still a direct child of its category folder, deep enough, and
    /// the leftover evidence still holds.
    /// </summary>
    public bool MayRemove(CleanerItem item)
    {
        if (item.Kind is CleanerItemKind.RecycleBin or CleanerItemKind.StartupValue)
        {
            return true;
        }

        var folders = _knownFolders.Folders;
        var critical = CriticalPaths.For(folders);
        if (critical.Contains(item.Path) || SafePaths.ComponentCount(item.Path) < 4)
        {
            return false;
        }

        var entry = _fs.Stat(item.Path);
        if (entry is null || entry.IsReparsePoint || entry.IsCloudPlaceholder)
        {
            return false;
        }

        if (item.Identity is not { } identity || _fs.Identity(item.Path) != identity)
        {
            return false;
        }

        if (item.Root is { } root)
        {
            if (!SafePaths.IsDirectChild(item.Path, root) || !SafePaths.ChainIsPlain(_fs, item.Path, root))
            {
                return false;
            }
        }

        return item.Evidence switch
        {
            { } pfn when pfn.StartsWith("pfn:", StringComparison.Ordinal) => _platform.IsPackageFamilyInstalled(pfn[4..]) == false,
            "programs" => CleanerRules.IsOrphanedProgramsFolder(item.Path, _platform.InstalledProgramLocations(), ContainsExecutable(item.Path), _fs.Measure(item.Path, CancellationToken.None).NewestWriteUtc, DateTime.UtcNow),
            "shortcut" => new CleanerScanner(_fs, _platform, folders).BrokenShortcutTarget(item.Path) is not null,
            _ => item.Category != CleanerCategory.Screenshots || CleanerRules.IsDefaultScreenshotName(entry.Name, entry.CreationUtc.ToLocalTime()),
        };
    }

    private (IReadOnlyList<CleanerItem> Items, CleanerScanContext Context) RunScan(bool attended, int token, CancellationToken cancellationToken)
    {
        var scanner = new CleanerScanner(_fs, _platform, _knownFolders.Folders);
        var context = new CleanerScanContext
        {
            Attended = attended,
            ScreenshotAgeDays = _settings.Get(CleanerSettings.ScreenshotAgeDays),
            NowUtc = DateTime.UtcNow,
            Elevated = _platform.IsElevated,
            RunningNames = _platform.RunningProcessNames(),
            RunningPaths = _platform.RunningProcessPaths(),
        };
        var items = new List<CleanerItem>();
        foreach (var category in CleanerScanner.Order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (token != _token)
                {
                    throw new OperationCanceledException();
                }

                ScanningCategory = category;
            }

            Raise();
            try
            {
                items.AddRange(scanner.Scan(category, context, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("cleaner", $"Category {category} failed.", ex);
            }
        }

        return (items, context);
    }

    private CleanResult Clean(IReadOnlyList<CleanerItem> selected, bool escalate)
    {
        long freed = 0;
        var removed = 0;
        var failed = 0;
        var inUse = 0;
        var permanent = 0;
        var failedNames = new List<string>();
        string? backupFolder = null;

        void Fail(CleanerItem item)
        {
            failed++;
            failedNames.Add(item.Name);
        }

        // 1. Recycle Bin first (permanent, labelled as such).
        foreach (var bin in selected.Where(i => i.Kind == CleanerItemKind.RecycleBin))
        {
            if (_platform.EmptyRecycleBin())
            {
                freed += bin.Size;
                removed++;
            }
            else
            {
                Fail(bin);
            }
        }

        // 2. Startup values: a .reg backup first, then the value.
        foreach (var value in selected.Where(i => i.Kind == CleanerItemKind.StartupValue && i.Startup is not null))
        {
            backupFolder ??= _backupFolder();
            if (value.RequiresAdministrator && !_platform.IsElevated)
            {
                Fail(value);
                continue;
            }

            // Still orphaned now (the program may have come back since the scan)?
            if (!new CleanerScanner(_fs, _platform, _knownFolders.Folders).IsOrphanedStartup(value.Startup!))
            {
                Fail(value);
                continue;
            }

            if (_platform.RemoveStartupEntry(value.Startup!, backupFolder))
            {
                removed++;
            }
            else
            {
                Fail(value);
            }
        }

        // 3. Files and folders: guard, then one verified recycle batch.
        var files = selected.Where(i => i.Kind is CleanerItemKind.File or CleanerItemKind.Folder).ToList();
        var allowed = new List<CleanerItem>();
        foreach (var item in files)
        {
            if (item.RequiresAdministrator && !_platform.IsElevated)
            {
                Fail(item);
            }
            else if (MayRemove(item))
            {
                allowed.Add(item);
            }
            else if (_fs.Stat(item.Path) is null)
            {
                // Confirmed absent (removed by its app meanwhile): nothing to do, nothing freed.
                removed++;
            }
            else
            {
                Fail(item);
            }
        }

        if (allowed.Count > 0)
        {
            var byPath = allowed.ToDictionary(i => SafePaths.Normalize(i.Path), StringComparer.OrdinalIgnoreCase);
            var outcomes = _recycler.Recycle(allowed.Select(i => i.Path).ToList(), allowElevationPrompt: false);
            foreach (var outcome in outcomes)
            {
                if (!byPath.TryGetValue(SafePaths.Normalize(outcome.Path), out var item))
                {
                    continue;
                }

                switch (outcome.Status)
                {
                    case RecycleStatus.Recycled:
                        freed += item.Size;
                        removed++;
                        break;
                    case RecycleStatus.DeletedPermanently:
                        freed += item.Size;
                        removed++;
                        permanent++;
                        break;
                    case RecycleStatus.InUse:
                        inUse++;
                        break;
                    case RecycleStatus.Missing:
                        removed++;
                        break;
                    default:
                        Fail(item);
                        break;
                }
            }
        }

        Log.Info("cleaner", $"Cleaned {removed} items, {freed} bytes; {failed} failed, {inUse} in use, {permanent} not recyclable (escalate {escalate}).");
        return new CleanResult
        {
            FreedBytes = freed,
            Removed = removed,
            Failed = failed,
            InUse = inUse,
            DeletedPermanently = permanent,
            FailedNames = failedNames,
            BackupFolder = backupFolder is not null && Directory.Exists(backupFolder) ? backupFolder : null,
        };
    }

    private bool ContainsExecutable(string folder)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((folder, 0));
        while (stack.Count > 0)
        {
            var (path, depth) = stack.Pop();
            foreach (var entry in _fs.List(path))
            {
                if (entry.IsReparsePoint)
                {
                    continue;
                }

                if (!entry.IsDirectory && entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (entry.IsDirectory && depth < 3)
                {
                    stack.Push((entry.Path, depth + 1));
                }
            }
        }

        return false;
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
}
