// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.App;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>State shared by the categories of one scan.</summary>
public sealed class CleanerScanContext
{
    public required bool Attended { get; init; }

    public required int ScreenshotAgeDays { get; init; }

    public required DateTime NowUtc { get; init; }

    public required bool Elevated { get; init; }

    public IReadOnlySet<string> RunningNames { get; init; } = new HashSet<string>();

    public IReadOnlyList<string> RunningPaths { get; init; } = [];

    /// <summary>Paths claimed by the leftovers category: one path, one row, one decision.</summary>
    public HashSet<string> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apps whose caches were skipped because they are running ("Google Chrome").</summary>
    public SortedSet<string> SkippedRunning { get; } = new(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Some locations need administrator rights and were not scanned.</summary>
    public bool AdministratorSkipped { get; set; }
}

/// <summary>
/// Scans the Windows categories (spec §3.1.9). Items only ever come from the
/// fixed locations below; reparse points are never followed, cloud
/// placeholders never offered, critical folders never listed, and each item
/// records its identity for the removal-time guard.
/// </summary>
public sealed class CleanerScanner
{
    /// <summary>Fixed order; screenshots last and only in attended scans.</summary>
    public static readonly IReadOnlyList<CleanerCategory> Order =
    [
        CleanerCategory.Leftovers, CleanerCategory.LoginItems, CleanerCategory.Caches, CleanerCategory.Logs,
        CleanerCategory.Developer, CleanerCategory.Trash, CleanerCategory.DeviceBackups, CleanerCategory.Screenshots,
    ];

    private const int MaxShortcutBytes = 64 * 1024;

    private readonly ICleanerFileSystem _fs;
    private readonly ICleanerPlatform _platform;
    private readonly CleanerFolders _folders;
    private readonly CriticalPaths _critical;

    public CleanerScanner(ICleanerFileSystem fs, ICleanerPlatform platform, CleanerFolders folders)
    {
        _fs = fs;
        _platform = platform;
        _folders = folders;
        _critical = CriticalPaths.For(folders);
    }

    public CriticalPaths Critical => _critical;

    public IReadOnlyList<CleanerItem> Scan(CleanerCategory category, CleanerScanContext context, CancellationToken cancellationToken)
    {
        var items = category switch
        {
            CleanerCategory.Leftovers => Leftovers(context, cancellationToken),
            CleanerCategory.LoginItems => LoginItems(context),
            CleanerCategory.Caches => Caches(context, cancellationToken),
            CleanerCategory.Logs => Logs(context, cancellationToken),
            CleanerCategory.Developer => Developer(context, cancellationToken),
            CleanerCategory.Trash => Trash(),
            CleanerCategory.DeviceBackups => DeviceBackups(cancellationToken),
            CleanerCategory.Screenshots => Screenshots(context, cancellationToken),
            _ => [],
        };

        if (category == CleanerCategory.Leftovers)
        {
            foreach (var item in items)
            {
                context.Claimed.Add(SafePaths.Normalize(item.Path));
            }
        }
        else if (category is CleanerCategory.Caches or CleanerCategory.Logs)
        {
            items = items.Where(i => !context.Claimed.Contains(SafePaths.Normalize(i.Path))).ToList();
        }

        return items.OrderByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ── Leftovers ────────────────────────────────────────────────────

    private List<CleanerItem> Leftovers(CleanerScanContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanerItem>();

        // (1) Package data of MSIX apps that are no longer installed for this user: exact evidence.
        var packages = Path.Combine(_folders.LocalAppData, "Packages");
        foreach (var entry in _fs.List(packages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsDirectory || entry.IsReparsePoint || !CleanerRules.IsPackageFamilyName(entry.Name))
            {
                continue;
            }

            // Unknown counts as installed: never guess against a living app.
            if (_platform.IsPackageFamilyInstalled(entry.Name) != false)
            {
                continue;
            }

            Add(items, entry, CleanerCategory.Leftovers, entry.Name, entry.Name, recommended: false, root: packages, cancellationToken, evidence: "pfn:" + entry.Name);
        }

        // (2) Per-user install folders no Add/Remove Programs entry points at and with no program left.
        var programs = Path.Combine(_folders.LocalAppData, "Programs");
        var locations = _platform.InstalledProgramLocations();
        foreach (var entry in _fs.List(programs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsDirectory || entry.IsReparsePoint || entry.IsCloudPlaceholder || IsOwn(entry.Path))
            {
                continue;
            }

            var measure = _fs.Measure(entry.Path, cancellationToken);
            if (!CleanerRules.IsOrphanedProgramsFolder(entry.Path, locations, ContainsExecutable(entry.Path, 3), measure.NewestWriteUtc, context.NowUtc))
            {
                continue;
            }

            Add(items, entry, CleanerCategory.Leftovers, entry.Name, null, recommended: false, root: programs, cancellationToken, measure, evidence: "programs");
        }

        // (3) Start menu shortcuts whose program is gone from a fixed drive.
        AddBrokenShortcuts(items, _folders.StartMenuPrograms, admin: false, cancellationToken);
        if (context.Elevated)
        {
            AddBrokenShortcuts(items, _folders.CommonStartMenuPrograms, admin: true, cancellationToken);
        }

        return items;
    }

    private void AddBrokenShortcuts(List<CleanerItem> items, string folder, bool admin, CancellationToken cancellationToken)
    {
        foreach (var file in Walk(folder, maxDepth: 4, cancellationToken))
        {
            if (!file.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || file.IsReparsePoint)
            {
                continue;
            }

            if (BrokenShortcutTarget(file.Path) is not { } target)
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file.Name);
            Add(items, file, CleanerCategory.Leftovers, name, target, recommended: false, root: SafePaths.Parent(file.Path), cancellationToken, admin: admin, evidence: "shortcut");
        }
    }

    /// <summary>The target of a shortcut whose program is proven missing, or null.</summary>
    public string? BrokenShortcutTarget(string shortcut)
    {
        var bytes = _fs.ReadBytes(shortcut, MaxShortcutBytes);
        if (bytes is null || ShellLink.Read(bytes) is not { } link || link.IsAdvertised || link.IsNetwork || link.IsUncertain || link.Path is null)
        {
            return null;
        }

        var target = Environment.ExpandEnvironmentVariables(link.Path);
        if (!SafePaths.IsAbsolute(target) || target.Contains('%'))
        {
            return null;
        }

        return CleanerRules.IsOrphanedCommand([target], p => _fs.Stat(p) is not null, _platform.DriveKindOf, _folders.Windows) ? target : null;
    }

    // ── Orphaned startup items ───────────────────────────────────────

    private List<CleanerItem> LoginItems(CleanerScanContext context)
    {
        var items = new List<CleanerItem>();
        foreach (var entry in _platform.StartupEntries(context.Elevated))
        {
            if (!IsOrphanedStartup(entry))
            {
                continue;
            }

            items.Add(new CleanerItem
            {
                Id = "startup:" + entry.DisplayPath,
                Category = CleanerCategory.LoginItems,
                Kind = CleanerItemKind.StartupValue,
                Path = entry.DisplayPath,
                Name = entry.ValueName,
                Detail = entry.Command,
                Recommended = true,
                RequiresAdministrator = entry.IsMachine,
                Startup = entry,
            });
        }

        if (!context.Elevated)
        {
            context.AdministratorSkipped = true;
        }

        AddBrokenStartupFolder(items, _folders.Startup, admin: false);
        if (context.Elevated)
        {
            AddBrokenStartupFolder(items, _folders.CommonStartup, admin: true);
        }

        return items;
    }

    /// <summary>Every file the command runs is missing from a fixed drive (spec §3.1.9 orphan rule).</summary>
    public bool IsOrphanedStartup(StartupEntry entry)
    {
        var executables = CleanerRules.ExecutablesOf(entry.Command, Environment.ExpandEnvironmentVariables, p => _fs.Stat(p) is not null);
        return CleanerRules.IsOrphanedCommand(executables, p => _fs.Stat(p) is not null, _platform.DriveKindOf, _folders.Windows);
    }

    private void AddBrokenStartupFolder(List<CleanerItem> items, string folder, bool admin)
    {
        foreach (var file in _fs.List(folder))
        {
            if (file.IsDirectory || file.IsReparsePoint || !file.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (BrokenShortcutTarget(file.Path) is not { } target)
            {
                continue;
            }

            items.Add(new CleanerItem
            {
                Id = file.Path,
                Category = CleanerCategory.LoginItems,
                Kind = CleanerItemKind.File,
                Path = file.Path,
                Name = Path.GetFileNameWithoutExtension(file.Name),
                Detail = target,
                Size = Math.Max(file.Length, 1),
                Recommended = true,
                RequiresAdministrator = admin,
                Identity = _fs.Identity(file.Path),
                Root = folder,
                Evidence = "shortcut",
            });
        }
    }

    // ── Caches ───────────────────────────────────────────────────────

    private List<CleanerItem> Caches(CleanerScanContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanerItem>();

        // User temp: top-level entries untouched for 24 hours.
        foreach (var entry in _fs.List(_folders.Temp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint || entry.IsCloudPlaceholder || IsOwn(entry.Path)
                || entry.Name.StartsWith(AppIdentity.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var measure = _fs.Measure(entry.Path, cancellationToken);
            if (!CleanerRules.IsStaleTemp(measure.NewestWriteUtc, entry.CreationUtc, context.NowUtc) || (!entry.IsDirectory && _fs.IsInUse(entry.Path)))
            {
                continue;
            }

            Add(items, entry, CleanerCategory.Caches, entry.Name, null, recommended: true, root: _folders.Temp, cancellationToken, measure);
        }

        // Browser caches, skipped while the browser runs.
        foreach (var browser in CleanerRules.Browsers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var userData = SafePaths.Join(_folders.LocalAppData, browser.UserDataPath);
            if (_fs.Stat(userData) is not { IsDirectory: true, IsReparsePoint: false })
            {
                continue;
            }

            if (context.RunningNames.Contains(browser.ProcessName))
            {
                context.SkippedRunning.Add(browser.Name);
                continue;
            }

            if (browser.IsChromium)
            {
                AddChromium(items, browser, userData, cancellationToken);
            }
            else
            {
                foreach (var profile in _fs.List(userData).Where(p => p.IsDirectory && !p.IsReparsePoint))
                {
                    foreach (var cache in CleanerRules.FirefoxProfileCaches)
                    {
                        AddFolder(items, Path.Combine(profile.Path, cache), CleanerCategory.Caches, $"{browser.Name} · {profile.Name}", true, profile.Path, cancellationToken);
                    }
                }
            }
        }

        // Internet cache, minus folders holding copies of documents apps may have open.
        var inetCache = Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "INetCache");
        foreach (var entry in _fs.List(inetCache))
        {
            if (entry.IsReparsePoint || CleanerRules.InetCacheExclusions.Contains(entry.Name))
            {
                continue;
            }

            if (!entry.IsDirectory && _fs.IsInUse(entry.Path))
            {
                continue;
            }

            Add(items, entry, CleanerCategory.Caches, entry.Name, null, recommended: true, root: inetCache, cancellationToken);
        }

        // Fixed cache folders (GPU shader caches, package caches, offline media).
        foreach (var location in CleanerRules.UserCaches.Where(l => l.Category == CleanerCategory.Caches))
        {
            AddLocation(items, location, context, cancellationToken);
        }

        // Explorer's thumbnail and icon caches: Explorer keeps most of them open (those are skipped).
        var explorer = Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "Explorer");
        foreach (var file in _fs.List(explorer))
        {
            if (file.IsDirectory || file.IsReparsePoint
                || !(file.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) || file.Name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase))
                || !file.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                || _fs.IsInUse(file.Path))
            {
                continue;
            }

            Add(items, file, CleanerCategory.Caches, file.Name, null, recommended: false, root: explorer, cancellationToken);
        }

        // Windows temp folder: administrator only, unchecked.
        var windowsTemp = Path.Combine(_folders.Windows, "Temp");
        if (context.Elevated)
        {
            foreach (var entry in _fs.List(windowsTemp))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsReparsePoint || entry.IsCloudPlaceholder)
                {
                    continue;
                }

                var measure = _fs.Measure(entry.Path, cancellationToken);
                if (!CleanerRules.IsStaleTemp(measure.NewestWriteUtc, entry.CreationUtc, context.NowUtc) || (!entry.IsDirectory && _fs.IsInUse(entry.Path)))
                {
                    continue;
                }

                Add(items, entry, CleanerCategory.Caches, entry.Name, null, recommended: false, root: windowsTemp, cancellationToken, measure, admin: true);
            }
        }
        else
        {
            context.AdministratorSkipped = true;
        }

        return items;
    }

    private void AddChromium(List<CleanerItem> items, BrowserDefinition browser, string userData, CancellationToken cancellationToken)
    {
        foreach (var shared in CleanerRules.ChromiumSharedCaches)
        {
            AddFolder(items, Path.Combine(userData, shared), CleanerCategory.Caches, browser.Name, true, userData, cancellationToken);
        }

        foreach (var profile in _fs.List(userData))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!profile.IsDirectory || profile.IsReparsePoint || _fs.Stat(Path.Combine(profile.Path, "Preferences")) is null)
            {
                continue;
            }

            var detail = $"{browser.Name} · {profile.Name}";
            foreach (var cache in CleanerRules.ChromiumProfileCaches)
            {
                AddFolder(items, Path.Combine(profile.Path, cache), CleanerCategory.Caches, detail, true, profile.Path, cancellationToken);
            }

            var serviceWorker = SafePaths.Join(profile.Path, CleanerRules.ChromiumServiceWorkerCache);
            AddFolder(items, serviceWorker, CleanerCategory.Caches, detail, false, SafePaths.Parent(serviceWorker), cancellationToken);
        }
    }

    private void AddLocation(List<CleanerItem> items, CacheLocation location, CleanerScanContext context, CancellationToken cancellationToken)
    {
        var path = location.Path(_folders);
        if (_fs.Stat(path) is not { IsDirectory: true, IsReparsePoint: false })
        {
            return;
        }

        if (location.SkipWhileRunning is { } process && context.RunningNames.Contains(process))
        {
            context.SkippedRunning.Add(location.Description);
            return;
        }

        AddFolder(items, path, location.Category, null, location.Recommended, SafePaths.Parent(path), cancellationToken);
    }

    // ── Crash dumps and error reports ────────────────────────────────

    private List<CleanerItem> Logs(CleanerScanContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanerItem>();
        var crashDumps = Path.Combine(_folders.LocalAppData, "CrashDumps");
        AddChildren(items, crashDumps, CleanerCategory.Logs, e => !e.IsDirectory && e.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase), true, false, cancellationToken);
        var userWer = Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "WER");
        AddChildren(items, Path.Combine(userWer, "ReportArchive"), CleanerCategory.Logs, e => e.IsDirectory, true, false, cancellationToken);
        AddChildren(items, Path.Combine(userWer, "ReportQueue"), CleanerCategory.Logs, e => e.IsDirectory, true, false, cancellationToken);

        if (!context.Elevated)
        {
            context.AdministratorSkipped = true;
            return items;
        }

        var machineWer = Path.Combine(_folders.ProgramData, "Microsoft", "Windows", "WER");
        foreach (var folder in new[] { "ReportArchive", "ReportQueue", "Temp" })
        {
            AddChildren(items, Path.Combine(machineWer, folder), CleanerCategory.Logs, _ => true, true, true, cancellationToken);
        }

        AddChildren(items, Path.Combine(_folders.Windows, "Minidump"), CleanerCategory.Logs, e => !e.IsDirectory && e.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase), true, true, cancellationToken);
        var memoryDump = Path.Combine(_folders.Windows, "MEMORY.DMP");
        if (_fs.Stat(memoryDump) is { IsDirectory: false, IsReparsePoint: false } dump && !_fs.IsInUse(memoryDump))
        {
            Add(items, dump, CleanerCategory.Logs, dump.Name, null, recommended: true, root: _folders.Windows, cancellationToken, admin: true);
        }

        AddChildren(items, Path.Combine(_folders.Windows, "Logs", "CBS"), CleanerCategory.Logs,
            e => !e.IsDirectory && e.Name.StartsWith("CbsPersist_", StringComparison.OrdinalIgnoreCase)
                 && (e.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)),
            true, true, cancellationToken);
        return items;
    }

    // ── Developer caches ─────────────────────────────────────────────

    private List<CleanerItem> Developer(CleanerScanContext context, CancellationToken cancellationToken)
    {
        var items = new List<CleanerItem>();
        var visualStudio = Path.Combine(_folders.LocalAppData, "Microsoft", "VisualStudio");
        if (context.RunningNames.Contains("devenv.exe"))
        {
            if (_fs.Stat(visualStudio) is not null)
            {
                context.SkippedRunning.Add("Visual Studio");
            }
        }
        else
        {
            foreach (var version in _fs.List(visualStudio).Where(v => v.IsDirectory && !v.IsReparsePoint))
            {
                AddFolder(items, Path.Combine(version.Path, "ComponentModelCache"), CleanerCategory.Developer, "Visual Studio " + version.Name, true, version.Path, cancellationToken);
            }
        }

        var jetBrains = Path.Combine(_folders.LocalAppData, "JetBrains");
        if (context.RunningPaths.Any(p => p.Contains(@"\jetbrains\", StringComparison.OrdinalIgnoreCase)))
        {
            if (_fs.Stat(jetBrains) is not null)
            {
                context.SkippedRunning.Add("JetBrains");
            }
        }
        else
        {
            foreach (var product in _fs.List(jetBrains).Where(v => v.IsDirectory && !v.IsReparsePoint))
            {
                AddFolder(items, Path.Combine(product.Path, "caches"), CleanerCategory.Developer, product.Name, true, product.Path, cancellationToken);
            }
        }

        foreach (var location in CleanerRules.UserCaches.Where(l => l.Category == CleanerCategory.Developer))
        {
            AddLocation(items, location, context, cancellationToken);
        }

        return items;
    }

    // ── Recycle Bin ──────────────────────────────────────────────────

    private List<CleanerItem> Trash()
    {
        var info = _platform.QueryRecycleBin();
        if (info is null || info.Items <= 0 || info.Bytes <= 0)
        {
            return [];
        }

        return
        [
            new CleanerItem
            {
                Id = "recycleBin",
                Category = CleanerCategory.Trash,
                Kind = CleanerItemKind.RecycleBin,
                Path = "shell:RecycleBinFolder",
                Name = "Recycle Bin",
                Detail = info.Items.ToString(CultureInfo.CurrentCulture),
                Size = info.Bytes,
                Recommended = false,
                Permanent = true,
            },
        ];
    }

    // ── iPhone backups ───────────────────────────────────────────────

    private List<CleanerItem> DeviceBackups(CancellationToken cancellationToken)
    {
        var items = new List<CleanerItem>();
        var roots = new[]
        {
            Path.Combine(_folders.RoamingAppData, "Apple Computer", "MobileSync", "Backup"),
            Path.Combine(_folders.UserProfile, "Apple", "MobileSync", "Backup"),
        };
        foreach (var root in roots)
        {
            foreach (var backup in _fs.List(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!backup.IsDirectory || backup.IsReparsePoint || backup.IsHidden)
                {
                    continue;
                }

                var (device, date) = CleanerRules.ReadBackupInfo(_fs.ReadText(Path.Combine(backup.Path, "Info.plist"), 4 * 1024 * 1024));
                var detail = device is not null
                    ? date is { } d ? $"{device}, {d.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}" : device
                    : backup.Name;
                Add(items, backup, CleanerCategory.DeviceBackups, device ?? backup.Name, detail, recommended: false, root: root, cancellationToken);
            }
        }

        return items;
    }

    // ── Forgotten screenshots ────────────────────────────────────────

    private List<CleanerItem> Screenshots(CleanerScanContext context, CancellationToken cancellationToken)
    {
        // An unattended scan never reads the person's own folders.
        if (!context.Attended || context.ScreenshotAgeDays <= 0)
        {
            return [];
        }

        var items = new List<CleanerItem>();
        var folder = _folders.Screenshots;
        foreach (var file in _fs.List(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.IsDirectory || file.IsHidden || file.IsReparsePoint || file.IsCloudPlaceholder || file.Length <= 0)
            {
                continue;
            }

            if (!CleanerRules.IsDefaultScreenshotName(file.Name, file.CreationUtc.ToLocalTime()) || !CleanerRules.IsForgotten(file, context.ScreenshotAgeDays, context.NowUtc))
            {
                continue;
            }

            Add(items, file, CleanerCategory.Screenshots, file.Name, file.CreationUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), recommended: false, root: folder, cancellationToken);
        }

        return items;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void AddChildren(List<CleanerItem> items, string folder, CleanerCategory category, Func<FsEntry, bool> filter, bool recommended, bool admin, CancellationToken cancellationToken)
    {
        foreach (var entry in _fs.List(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint || entry.IsCloudPlaceholder || !filter(entry) || (!entry.IsDirectory && _fs.IsInUse(entry.Path)))
            {
                continue;
            }

            Add(items, entry, category, entry.Name, null, recommended, folder, cancellationToken, admin: admin);
        }
    }

    private void AddFolder(List<CleanerItem> items, string path, CleanerCategory category, string? detail, bool recommended, string? root, CancellationToken cancellationToken)
    {
        if (_fs.Stat(path) is { IsDirectory: true, IsReparsePoint: false } entry)
        {
            Add(items, entry, category, entry.Name, detail, recommended, root, cancellationToken);
        }
    }

    private void Add(
        List<CleanerItem> items,
        FsEntry entry,
        CleanerCategory category,
        string name,
        string? detail,
        bool recommended,
        string? root,
        CancellationToken cancellationToken,
        TreeMeasure? measure = null,
        bool admin = false,
        string? evidence = null)
    {
        if (entry.IsReparsePoint || entry.IsCloudPlaceholder || _critical.Contains(entry.Path) || IsOwn(entry.Path))
        {
            return;
        }

        var size = (measure ?? _fs.Measure(entry.Path, cancellationToken)).Bytes;
        if (size <= 0)
        {
            return;
        }

        var identity = _fs.Identity(entry.Path);
        if (identity is null)
        {
            return;
        }

        items.Add(new CleanerItem
        {
            Id = entry.Path,
            Category = category,
            Kind = entry.IsDirectory ? CleanerItemKind.Folder : CleanerItemKind.File,
            Path = entry.Path,
            Name = name,
            Detail = detail,
            Size = size,
            Recommended = recommended,
            RequiresAdministrator = admin,
            Identity = identity,
            Root = root,
            Evidence = evidence,
        });
    }

    private bool IsOwn(string path) =>
        (_folders.OwnLocal is { } local && (SafePaths.Equal(path, local) || SafePaths.IsInside(path, local) || SafePaths.IsInside(local, path)))
        || (_folders.OwnRoaming is { } roaming && (SafePaths.Equal(path, roaming) || SafePaths.IsInside(path, roaming) || SafePaths.IsInside(roaming, path)));

    private bool ContainsExecutable(string folder, int depth)
    {
        foreach (var entry in _fs.List(folder))
        {
            if (entry.IsReparsePoint)
            {
                continue;
            }

            if (!entry.IsDirectory && entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entry.IsDirectory && depth > 0 && ContainsExecutable(entry.Path, depth - 1))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<FsEntry> Walk(string folder, int maxDepth, CancellationToken cancellationToken)
    {
        foreach (var entry in _fs.List(folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                if (maxDepth > 0)
                {
                    foreach (var child in Walk(entry.Path, maxDepth - 1, cancellationToken))
                    {
                        yield return child;
                    }
                }
            }
            else
            {
                yield return entry;
            }
        }
    }
}
