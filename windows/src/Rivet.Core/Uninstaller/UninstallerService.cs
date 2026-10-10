// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Settings;
using Rivet.Core.Util;

namespace Rivet.Core.Maintenance.Uninstaller;

public enum UninstallerPhase
{
    /// <summary>The app list.</summary>
    List,

    /// <summary>One app selected: details, running copies, the uninstall button.</summary>
    Selected,

    /// <summary>The app's own uninstaller runs (or the package is being removed).</summary>
    Uninstalling,

    /// <summary>The uninstaller ended but the app is still installed: no leftovers are offered.</summary>
    StillInstalled,

    Scanning,
    Results,
    Removing,
    Done,
}

/// <summary>What removing the leftovers did.</summary>
public sealed record LeftoverResult
{
    public long FreedBytes { get; init; }

    public int Removed { get; init; }

    public IReadOnlyList<string> FailedNames { get; init; } = [];

    public int InUse { get; init; }

    public int DeletedPermanently { get; init; }

    /// <summary>Folder with the .reg backups of removed registry entries.</summary>
    public string? BackupFolder { get; init; }

    public bool Succeeded => FailedNames.Count == 0;
}

/// <summary>Uninstaller preferences.</summary>
public static class UninstallerSettings
{
    public static readonly Setting<bool> CommandBarEnabled = new("uninstallerCommandBarEnabled", false);

    /// <summary>Windows only: prefer QuietUninstallString / msiexec /qb.</summary>
    public static readonly Setting<bool> QuietMode = new("uninstallerQuietMode", false);

    public static readonly Setting<string> SortOrder =
        new("uninstallerSortOrder", "name", Sanitize.OneOfStrings("name", "name", "size", "date", "publisher"));
}

/// <summary>
/// The Uninstaller on Windows (spec §3.2.11): the app's own uninstaller runs
/// first (it is not reversible), then what it left behind is scanned and
/// reviewed; files go to the Recycle Bin and registry keys are saved to a
/// .reg backup before they are deleted. One shared instance for every surface.
/// </summary>
public sealed class UninstallerService
{
    private readonly IInstalledAppsProvider _apps;
    private readonly IUninstallerPlatform _platform;
    private readonly ICleanerPlatform _cleanerPlatform;
    private readonly ICleanerFileSystem _fs;
    private readonly IRecycler _recycler;
    private readonly IKnownFolders _folders;
    private readonly IProcessPlatform _processes;
    private readonly ProcessService _processService;
    private readonly ISettingsStore _settings;
    private readonly Func<string> _backupFolder;
    private readonly HashSet<string> _included = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _wait;
    private int _selection;

    public UninstallerService(
        IInstalledAppsProvider apps,
        IUninstallerPlatform platform,
        ICleanerPlatform cleanerPlatform,
        ICleanerFileSystem fs,
        IRecycler recycler,
        IKnownFolders folders,
        IProcessPlatform processes,
        ProcessService processService,
        ISettingsStore settings,
        Func<string> backupFolder)
    {
        _apps = apps;
        _platform = platform;
        _cleanerPlatform = cleanerPlatform;
        _fs = fs;
        _recycler = recycler;
        _folders = folders;
        _processes = processes;
        _processService = processService;
        _settings = settings;
        _backupFolder = backupFolder;
    }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event EventHandler? Changed;

    public UninstallerPhase Phase { get; private set; } = UninstallerPhase.List;

    public IReadOnlyList<InstalledApp> Apps { get; private set; } = [];

    public bool LoadingApps { get; private set; }

    public bool AppsLoaded { get; private set; }

    public InstalledApp? Selected { get; private set; }

    public AppFingerprint? Fingerprint { get; private set; }

    /// <summary>Running processes started from the app's install folder (close them before uninstalling).</summary>
    public IReadOnlyList<ProcessRow> RunningCopies { get; private set; } = [];

    public double? Progress { get; private set; }

    public string? Message { get; private set; }

    public IReadOnlyList<LeftoverItem> Leftovers { get; private set; } = [];

    public LeftoverResult? LastResult { get; private set; }

    public bool IsElevated => _platform.IsElevated;

    public bool Quiet
    {
        get => _settings.Get(UninstallerSettings.QuietMode);
        set => _settings.Set(UninstallerSettings.QuietMode, value);
    }

    public string SortOrder
    {
        get => _settings.Get(UninstallerSettings.SortOrder);
        set
        {
            _settings.Set(UninstallerSettings.SortOrder, value);
            Raise();
        }
    }

    public bool IsBusy => Phase is UninstallerPhase.Uninstalling or UninstallerPhase.Scanning or UninstallerPhase.Removing;

    public int SelectedCount => Leftovers.Count(IsIncluded);

    public long SelectedBytes => Leftovers.Where(IsIncluded).Sum(i => i.Size);

    public long TotalBytes => Leftovers.Sum(i => i.Size);

    public bool IsIncluded(LeftoverItem item)
    {
        lock (_gate)
        {
            return !item.IsReviewOnly && _included.Contains(item.Id);
        }
    }

    public void SetIncluded(LeftoverItem item, bool include)
    {
        if (Phase != UninstallerPhase.Results || item.IsReviewOnly)
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

    /// <summary>Apps through the search text and the sort order.</summary>
    public IReadOnlyList<InstalledApp> Filtered(string? search)
    {
        IEnumerable<InstalledApp> apps = Apps;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            apps = apps.Where(a => a.DisplayName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                   || (a.Publisher?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false));
        }

        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        return SortOrder switch
        {
            "size" => apps.OrderByDescending(a => a.SizeBytes ?? -1).ThenBy(a => a.DisplayName, comparer).ToList(),
            "date" => apps.OrderByDescending(a => a.InstallDate ?? DateOnly.MinValue).ThenBy(a => a.DisplayName, comparer).ToList(),
            "publisher" => apps.OrderBy(a => a.Publisher ?? "￿", comparer).ThenBy(a => a.DisplayName, comparer).ToList(),
            _ => apps.OrderBy(a => a.DisplayName, comparer).ToList(),
        };
    }

    public async Task LoadAppsAsync(bool force = false)
    {
        if (LoadingApps || (AppsLoaded && !force))
        {
            return;
        }

        LoadingApps = true;
        Raise();
        try
        {
            Apps = await Task.Run(() => ArpRules.Deduplicate(_apps.Enumerate().Where(a => a.CanUninstall))).ConfigureAwait(false);
            AppsLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("uninstaller", "Could not list installed apps.", ex);
        }
        finally
        {
            LoadingApps = false;
            Raise();
        }
    }

    /// <summary>Selects an app: builds its identity (before the uninstaller removes its files) and finds running copies.</summary>
    public async Task SelectAsync(InstalledApp app)
    {
        if (IsBusy)
        {
            return;
        }

        var selection = Interlocked.Increment(ref _selection);
        Selected = app;
        Fingerprint = null;
        RunningCopies = [];
        Message = null;
        Progress = null;
        Phase = UninstallerPhase.Selected;
        Raise();
        var (fingerprint, running) = await Task.Run(() =>
        {
            var folders = _folders.Folders;
            var print = AppFingerprint.Build(app, Apps, folders, _fs);
            return (print, FindRunning(print));
        }).ConfigureAwait(false);
        if (selection != Volatile.Read(ref _selection))
        {
            return;
        }

        Fingerprint = fingerprint;
        RunningCopies = running;
        Raise();
    }

    /// <summary>Closes (or force-quits) the app's running processes.</summary>
    public async Task CloseRunningAsync(bool force)
    {
        foreach (var row in RunningCopies.Where(r => !r.IsProtected))
        {
            await _processService.KillAsync(row, force ? KillMode.ForceKill : KillMode.Kill).ConfigureAwait(false);
        }

        await Task.Delay(force ? 300 : 1500).ConfigureAwait(false);
        if (Fingerprint is { } fingerprint)
        {
            RunningCopies = await Task.Run(() => FindRunning(fingerprint)).ConfigureAwait(false);
        }

        Raise();
    }

    /// <summary>Runs the uninstaller and waits; then verifies and scans for leftovers.</summary>
    public async Task UninstallAsync()
    {
        if (Selected is not { } app || Phase is not (UninstallerPhase.Selected or UninstallerPhase.StillInstalled))
        {
            return;
        }

        var fingerprint = Fingerprint ?? await Task.Run(() => AppFingerprint.Build(app, Apps, _folders.Folders, _fs)).ConfigureAwait(false);
        Fingerprint = fingerprint;
        _wait?.Dispose();
        var wait = _wait = new CancellationTokenSource();
        Phase = UninstallerPhase.Uninstalling;
        Message = null;
        Progress = null;
        Raise();

        var progress = new Progress<double>(p =>
        {
            Progress = p;
            Raise();
        });
        UninstallRunResult result;
        try
        {
            result = await _platform.RunAsync(app, Quiet, progress, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new UninstallRunResult(UninstallRunStatus.Cancelled);
        }

        if (result.Status == UninstallRunStatus.FailedToStart || result.Status == UninstallRunStatus.Failed)
        {
            Message = result.Message;
            Phase = UninstallerPhase.Selected;
            Raise();
            return;
        }

        // Finished, or the person said the uninstaller is done: check what Windows says.
        await VerifyAndScanAsync(app, fingerprint).ConfigureAwait(false);
    }

    /// <summary>"I've finished": stop waiting for the uninstaller and check now.</summary>
    public void StopWaiting() => _wait?.Cancel();

    public async Task RemoveSelectedAsync()
    {
        if (Phase != UninstallerPhase.Results)
        {
            return;
        }

        List<LeftoverItem> chosen;
        lock (_gate)
        {
            chosen = Leftovers.Where(i => !i.IsReviewOnly && _included.Contains(i.Id)).ToList();
        }

        Phase = UninstallerPhase.Removing;
        Raise();
        LeftoverResult result;
        try
        {
            result = await Task.Run(() => Remove(chosen)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("uninstaller", "Removing leftovers failed.", ex);
            result = new LeftoverResult { FailedNames = chosen.Select(c => c.Name).ToList() };
        }

        LastResult = result;
        Phase = UninstallerPhase.Done;
        Raise();
        _ = LoadAppsAsync(force: true);
    }

    /// <summary>Skips the leftovers review (nothing removed).</summary>
    public void FinishWithoutRemoving()
    {
        LastResult = new LeftoverResult();
        Phase = UninstallerPhase.Done;
        Raise();
        _ = LoadAppsAsync(force: true);
    }

    /// <summary>Back to the list (not while something runs).</summary>
    public void Reset()
    {
        if (IsBusy)
        {
            return;
        }

        Interlocked.Increment(ref _selection);
        Selected = null;
        Fingerprint = null;
        RunningCopies = [];
        Leftovers = [];
        lock (_gate)
        {
            _included.Clear();
        }

        Message = null;
        Phase = UninstallerPhase.List;
        Raise();
    }

    /// <summary>
    /// Removal-time guard for files: still under its scan root with no
    /// reparse point on the way, same identity, not a critical folder.
    /// </summary>
    public bool MayRemove(LeftoverItem item)
    {
        if (item.Kind is not (LeftoverKind.File or LeftoverKind.Folder))
        {
            return item.Kind is LeftoverKind.RegistryKey or LeftoverKind.StartupValue;
        }

        if (item.Root is not { } root || CriticalPaths.For(_folders.Folders).Contains(item.Path))
        {
            return false;
        }

        var entry = _fs.Stat(item.Path);
        return entry is not null && !entry.IsReparsePoint && item.Identity is { } identity && _fs.Identity(item.Path) == identity
               && (SafePaths.IsInside(item.Path, root)) && SafePaths.ChainIsPlain(_fs, item.Path, root);
    }

    private async Task VerifyAndScanAsync(InstalledApp app, AppFingerprint fingerprint)
    {
        var stillInstalled = await Task.Run(() => _platform.IsStillInstalled(app)).ConfigureAwait(false);
        if (stillInstalled)
        {
            Phase = UninstallerPhase.StillInstalled;
            Raise();
            return;
        }

        Phase = UninstallerPhase.Scanning;
        Raise();
        IReadOnlyList<LeftoverItem> leftovers;
        try
        {
            leftovers = await Task.Run(() => new LeftoverScanner(_fs, _cleanerPlatform, _platform, _folders.Folders).Scan(fingerprint, CancellationToken.None)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("uninstaller", "Leftover scan failed.", ex);
            leftovers = [];
        }

        lock (_gate)
        {
            _included.Clear();
            foreach (var item in leftovers.Where(l => l.Exact && !l.IsReviewOnly && (!l.RequiresAdministrator || l.Kind is LeftoverKind.File or LeftoverKind.Folder || _platform.IsElevated)))
            {
                _included.Add(item.Id);
            }
        }

        Leftovers = leftovers;
        Phase = UninstallerPhase.Results;
        Raise();
    }

    private IReadOnlyList<ProcessRow> FindRunning(AppFingerprint fingerprint)
    {
        if (fingerprint.InstallFolder is not { } folder)
        {
            return [];
        }

        var rows = new List<ProcessRow>();
        foreach (var process in _processes.Snapshot())
        {
            if (!process.Identity.IsKnown)
            {
                continue;
            }

            var path = _processes.ImagePath(process.Identity);
            if (path is not null && SafePaths.IsInside(path, folder))
            {
                rows.Add(new ProcessRow
                {
                    Identity = process.Identity,
                    ParentPid = process.ParentPid,
                    Name = process.ImageName,
                    ImageName = process.ImageName,
                    Path = path,
                    MemoryBytes = process.WorkingSetBytes,
                    HasWindows = _processes.ProcessesWithWindows().Contains(process.Pid),
                });
            }
        }

        return rows;
    }

    private LeftoverResult Remove(IReadOnlyList<LeftoverItem> chosen)
    {
        long freed = 0;
        var removed = 0;
        var inUse = 0;
        var permanent = 0;
        var failed = new List<string>();
        string? backupFolder = null;

        // Ownership again at removal time: a related name another app now uses is no longer evidence.
        var installed = _apps.Enumerate();
        var otherTokens = Selected is { } app
            ? AppFingerprint.Build(app, installed, _folders.Folders, _fs).OtherTokens
            : new HashSet<string>();

        foreach (var item in chosen.Where(i => i.Kind == LeftoverKind.RegistryKey && i.Registry is not null))
        {
            if (!item.Exact && item.Evidence is { } token && otherTokens.Contains(token))
            {
                failed.Add(item.Name);
                continue;
            }

            if (item.RequiresAdministrator && !_platform.IsElevated)
            {
                failed.Add(item.Name);
                continue;
            }

            var snapshot = _platform.Snapshot(item.Registry!);
            if (snapshot is null)
            {
                // Gone already counts as removed only when the key is confirmed absent.
                if (!_platform.KeyExists(item.Registry!))
                {
                    removed++;
                }
                else
                {
                    failed.Add(item.Name);
                }

                continue;
            }

            backupFolder ??= _backupFolder();
            var file = Path.Combine(backupFolder, SafeFileName(item.Registry!.DisplayPath) + ".reg");
            try
            {
                File.WriteAllBytes(file, RegFile.Encode(RegFile.ForKeys([snapshot])));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("uninstaller", $"Could not write the backup of {item.Registry.DisplayPath}; the key is kept.", ex);
                failed.Add(item.Name);
                continue;
            }

            if (_platform.DeleteKey(item.Registry))
            {
                removed++;
            }
            else
            {
                failed.Add(item.Name);
            }
        }

        foreach (var item in chosen.Where(i => i.Kind == LeftoverKind.StartupValue && i.Startup is not null))
        {
            backupFolder ??= _backupFolder();
            if ((item.RequiresAdministrator && !_platform.IsElevated) || !_cleanerPlatform.RemoveStartupEntry(item.Startup!, backupFolder))
            {
                failed.Add(item.Name);
            }
            else
            {
                removed++;
            }
        }

        var files = new List<LeftoverItem>();
        foreach (var item in chosen.Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Folder))
        {
            if (!item.Exact && item.Evidence is { } token && otherTokens.Contains(token))
            {
                failed.Add(item.Name);
            }
            else if (MayRemove(item))
            {
                files.Add(item);
            }
            else if (_fs.Stat(item.Path) is null)
            {
                removed++;
            }
            else
            {
                failed.Add(item.Name);
            }
        }

        if (files.Count > 0)
        {
            var byPath = files.ToDictionary(f => SafePaths.Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
            var needsAdmin = files.Any(f => f.RequiresAdministrator) && !_platform.IsElevated;
            foreach (var outcome in _recycler.Recycle(files.Select(f => f.Path).ToList(), allowElevationPrompt: needsAdmin))
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
                        failed.Add(item.Name);
                        break;
                    case RecycleStatus.Missing when _fs.Stat(item.Path) is null:
                        removed++;
                        break;
                    default:
                        failed.Add(item.Name);
                        break;
                }
            }
        }

        return new LeftoverResult
        {
            FreedBytes = freed,
            Removed = removed,
            FailedNames = failed,
            InUse = inUse,
            DeletedPermanently = permanent,
            BackupFolder = backupFolder is not null && Directory.Exists(backupFolder) && Directory.EnumerateFileSystemEntries(backupFolder).Any() ? backupFolder : null,
        };
    }

    private static string SafeFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).ToHashSet();
        var clean = new string(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 120 ? clean[..120] : clean;
    }

    private void Raise() => UiThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
}
