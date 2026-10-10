// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Cleaner;

namespace Rivet.Core.Maintenance.Uninstaller;

/// <summary>
/// Finds what an app left behind (spec §3.2.11 leftover roots and evidence).
/// Exact evidence — the install folder itself, the package family, an
/// executable name in crash reports, shortcuts and startup entries pointing
/// into the install folder — starts checked. Name matches in AppData,
/// ProgramData, %TEMP% and the registry are related: unchecked, and only when
/// no other installed app shares the matching name.
/// </summary>
public sealed class LeftoverScanner
{
    private const int MaxShortcutBytes = 64 * 1024;

    private static readonly HashSet<string> SkippedRegistryChildren = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Clients", "Policies", "RegisteredApplications", "Wow6432Node", "ODBC", "Microsoft",
    };

    private readonly ICleanerFileSystem _fs;
    private readonly ICleanerPlatform _cleanerPlatform;
    private readonly IUninstallerPlatform _platform;
    private readonly CleanerFolders _folders;
    private readonly CriticalPaths _critical;

    public LeftoverScanner(ICleanerFileSystem fs, ICleanerPlatform cleanerPlatform, IUninstallerPlatform platform, CleanerFolders folders)
    {
        _fs = fs;
        _cleanerPlatform = cleanerPlatform;
        _platform = platform;
        _folders = folders;
        _critical = CriticalPaths.For(folders);
    }

    public IReadOnlyList<LeftoverItem> Scan(AppFingerprint app, CancellationToken cancellationToken)
    {
        if (!app.Trusted)
        {
            return [];
        }

        var candidates = new List<LeftoverItem>();
        var tokens = app.ExclusiveTokens;

        // Exact: the install folder residue and the package data.
        if (app.InstallFolder is { } installFolder && _fs.Stat(installFolder) is { IsDirectory: true, IsReparsePoint: false } residue)
        {
            var admin = !SafePaths.IsInside(installFolder, Path.Combine(_folders.LocalAppData, "Programs"));
            AddFile(candidates, residue, LeftoverCategory.Application, exact: true, root: SafePaths.Parent(installFolder), admin, "location", cancellationToken);
        }

        if (app.App.PackageFamilyName is { } family)
        {
            var packages = Path.Combine(_folders.LocalAppData, "Packages");
            if (_fs.Stat(Path.Combine(packages, family)) is { IsDirectory: true, IsReparsePoint: false } package)
            {
                AddFile(candidates, package, LeftoverCategory.Package, exact: true, root: packages, admin: false, "pfn", cancellationToken);
            }
        }

        // Related: AppData, LocalLow and ProgramData (Vendor\Product up to two levels deep).
        MatchFolder(candidates, _folders.RoamingAppData, LeftoverCategory.Support, app, tokens, admin: false, cancellationToken);
        MatchFolder(candidates, _folders.LocalAppData, LeftoverCategory.Caches, app, tokens, admin: false, cancellationToken);
        MatchFolder(candidates, _folders.LocalLow, LeftoverCategory.Support, app, tokens, admin: false, cancellationToken);
        MatchFolder(candidates, _folders.ProgramData, LeftoverCategory.Support, app, tokens, admin: true, cancellationToken);

        // %TEMP%: exact names only (no prefixes), still unchecked.
        foreach (var entry in _fs.List(_folders.Temp))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsReparsePoint && tokens.Contains(AppTokens.Normalize(entry.Name)))
            {
                AddFile(candidates, entry, LeftoverCategory.Caches, exact: false, root: _folders.Temp, admin: false, AppTokens.Normalize(entry.Name), cancellationToken);
            }
        }

        // Exact by executable name: crash dumps and Windows Error Reporting folders.
        if (app.Executables.Count > 0)
        {
            var crashDumps = Path.Combine(_folders.LocalAppData, "CrashDumps");
            foreach (var dump in _fs.List(crashDumps))
            {
                if (!dump.IsDirectory && !dump.IsReparsePoint && app.Executables.Any(exe => dump.Name.StartsWith(exe + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    AddFile(candidates, dump, LeftoverCategory.Logs, exact: true, root: crashDumps, admin: false, "crash", cancellationToken);
                }
            }

            var wer = Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "WER");
            foreach (var queue in new[] { "ReportArchive", "ReportQueue" })
            {
                var folder = Path.Combine(wer, queue);
                foreach (var report in _fs.List(folder))
                {
                    if (report.IsDirectory && !report.IsReparsePoint && IsReportFor(report.Name, app.Executables))
                    {
                        AddFile(candidates, report, LeftoverCategory.Logs, exact: true, root: folder, admin: false, "wer", cancellationToken);
                    }
                }
            }
        }

        // Exact: shortcuts that point into the install folder.
        if (app.InstallFolder is { } target)
        {
            AddShortcuts(candidates, _folders.StartMenuPrograms, target, admin: false, cancellationToken);
            AddShortcuts(candidates, _folders.CommonStartMenuPrograms, target, admin: true, cancellationToken);
            AddShortcuts(candidates, _folders.Desktop, target, admin: false, cancellationToken, depth: 0);
            if (_folders.CommonDesktop is { } commonDesktop)
            {
                AddShortcuts(candidates, commonDesktop, target, admin: true, cancellationToken, depth: 0);
            }
        }

        AddRegistry(candidates, app, tokens);
        AddStartup(candidates, app);
        AddReviewOnly(candidates, app);
        return Order(Deduplicate(candidates));
    }

    /// <summary>
    /// Processes candidates by increasing path length: a related candidate with
    /// an exact one beneath it is dropped, the same path merges (exact wins),
    /// and anything nested under an accepted path is dropped.
    /// </summary>
    public static IReadOnlyList<LeftoverItem> Deduplicate(IEnumerable<LeftoverItem> candidates)
    {
        var all = candidates.ToList();
        var files = all.Where(c => c.Kind is LeftoverKind.File or LeftoverKind.Folder).OrderBy(c => c.Path.Length).ToList();
        var accepted = new List<LeftoverItem>();
        foreach (var candidate in files)
        {
            if (accepted.Any(a => SafePaths.IsInside(candidate.Path, a.Path)))
            {
                continue;
            }

            var same = accepted.FindIndex(a => SafePaths.Equal(a.Path, candidate.Path));
            if (same >= 0)
            {
                if (candidate.Exact && !accepted[same].Exact)
                {
                    accepted[same] = candidate;
                }

                continue;
            }

            if (!candidate.Exact && files.Any(other => other.Exact && SafePaths.IsInside(other.Path, candidate.Path)))
            {
                continue;
            }

            accepted.Add(candidate);
        }

        var others = all.Where(c => c.Kind is not (LeftoverKind.File or LeftoverKind.Folder))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Exact).First());
        return accepted.Concat(others).ToList();
    }

    /// <summary>By category, then size descending.</summary>
    public static IReadOnlyList<LeftoverItem> Order(IEnumerable<LeftoverItem> items) =>
        items.OrderBy(i => i.Category).ThenByDescending(i => i.Size).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>"AppCrash_code.exe_1f2e…" / "AppHang_…" / "Critical_…" for one of the executables.</summary>
    public static bool IsReportFor(string folderName, IReadOnlySet<string> executables)
    {
        foreach (var prefix in new[] { "AppCrash_", "AppHang_", "Critical_", "NonCritical_", "AppCrashEx_" })
        {
            if (folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = folderName[prefix.Length..];
                return executables.Any(exe => rest.StartsWith(exe + "_", StringComparison.OrdinalIgnoreCase));
            }
        }

        return false;
    }

    private void MatchFolder(List<LeftoverItem> candidates, string root, LeftoverCategory category, AppFingerprint app, IReadOnlySet<string> tokens, bool admin, CancellationToken cancellationToken)
    {
        foreach (var entry in _fs.List(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsReparsePoint || entry.IsCloudPlaceholder || _critical.Contains(entry.Path))
            {
                continue;
            }

            var normalized = AppTokens.Normalize(entry.Name);
            if (entry.IsDirectory && app.Publisher is { } publisher && normalized == publisher)
            {
                // Vendor folder: look for the product inside; the whole folder only when no other app shares the vendor.
                var before = candidates.Count;
                foreach (var child in _fs.List(entry.Path))
                {
                    if (!child.IsReparsePoint && AppTokens.Match(child.Name, tokens) is { } childToken)
                    {
                        AddFile(candidates, child, category, exact: false, root: entry.Path, admin, childToken, cancellationToken);
                    }
                }

                if (candidates.Count == before && !app.PublisherShared && tokens.Count > 0)
                {
                    AddFile(candidates, entry, category, exact: false, root: root, admin, "publisher", cancellationToken);
                }

                continue;
            }

            if (AppTokens.Match(entry.Name, tokens) is not { } token)
            {
                continue;
            }

            // A related folder with a more specific nested hit is replaced by that hit.
            if (entry.IsDirectory)
            {
                var nested = _fs.List(entry.Path).Where(c => c.IsDirectory && !c.IsReparsePoint && AppTokens.Match(c.Name, tokens) is not null && AppTokens.Normalize(c.Name) != normalized).ToList();
                if (nested.Count > 0 && nested.Count < 4 && !tokens.Contains(normalized))
                {
                    foreach (var child in nested)
                    {
                        AddFile(candidates, child, category, exact: false, root: entry.Path, admin, AppTokens.Match(child.Name, tokens), cancellationToken);
                    }

                    continue;
                }
            }

            AddFile(candidates, entry, category, exact: false, root: root, admin, token, cancellationToken);
        }
    }

    private void AddShortcuts(List<LeftoverItem> candidates, string folder, string installFolder, bool admin, CancellationToken cancellationToken, int depth = 3)
    {
        var hits = new List<FsEntry>();
        void Walk(string path, int remaining)
        {
            foreach (var entry in _fs.List(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsReparsePoint)
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (remaining > 0)
                    {
                        Walk(entry.Path, remaining - 1);
                    }
                }
                else if (entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && PointsInto(entry.Path, installFolder))
                {
                    hits.Add(entry);
                }
            }
        }

        Walk(folder, depth);

        // A Start menu subfolder holding only this app's shortcuts goes as a whole.
        foreach (var group in hits.GroupBy(h => SafePaths.Parent(h.Path) ?? folder, StringComparer.OrdinalIgnoreCase))
        {
            var parent = group.Key;
            var siblings = _fs.List(parent);
            if (!SafePaths.Equal(parent, folder) && siblings.Count == group.Count() && _fs.Stat(parent) is { } parentEntry)
            {
                AddFile(candidates, parentEntry, LeftoverCategory.Shortcuts, exact: true, root: SafePaths.Parent(parent), admin, "shortcut", cancellationToken);
                continue;
            }

            foreach (var hit in group)
            {
                AddFile(candidates, hit, LeftoverCategory.Shortcuts, exact: true, root: parent, admin, "shortcut", cancellationToken);
            }
        }
    }

    private bool PointsInto(string shortcut, string installFolder)
    {
        var bytes = _fs.ReadBytes(shortcut, MaxShortcutBytes);
        if (bytes is null || ShellLink.Read(bytes) is not { Path: { } path, IsUncertain: false })
        {
            return false;
        }

        var target = Environment.ExpandEnvironmentVariables(path);
        return SafePaths.IsAbsolute(target) && (SafePaths.IsInside(target, installFolder) || SafePaths.Equal(target, installFolder));
    }

    private void AddRegistry(List<LeftoverItem> candidates, AppFingerprint app, IReadOnlySet<string> tokens)
    {
        var roots = new[]
        {
            new RegistryKeyRef(RegistryKeyRef.CurrentUser, "Software"),
            new RegistryKeyRef(RegistryKeyRef.LocalMachine, "SOFTWARE"),
            new RegistryKeyRef(RegistryKeyRef.LocalMachine, "SOFTWARE", View32: true),
        };
        foreach (var root in roots)
        {
            foreach (var name in _platform.SubKeyNames(root))
            {
                if (SkippedRegistryChildren.Contains(name))
                {
                    continue;
                }

                var normalized = AppTokens.Normalize(name);
                var key = root.Child(name);
                if (app.Publisher is { } publisher && normalized == publisher)
                {
                    var found = false;
                    foreach (var child in _platform.SubKeyNames(key))
                    {
                        if (AppTokens.Match(child, tokens) is { } token)
                        {
                            AddKey(candidates, key.Child(child), LeftoverCategory.Registry, exact: false, token);
                            found = true;
                        }
                    }

                    if (!found && !app.PublisherShared && tokens.Count > 0)
                    {
                        AddKey(candidates, key, LeftoverCategory.Registry, exact: false, "publisher");
                    }
                }
                else if (AppTokens.Match(name, tokens) is { } token)
                {
                    AddKey(candidates, key, LeftoverCategory.Registry, exact: false, token);
                }
            }
        }

        // App Paths entries of the app's executables: exact.
        foreach (var exe in app.Executables)
        {
            foreach (var hive in new[] { RegistryKeyRef.CurrentUser, RegistryKeyRef.LocalMachine })
            {
                var key = new RegistryKeyRef(hive, hive == RegistryKeyRef.CurrentUser ? @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe : @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (_platform.KeyExists(key))
                {
                    AddKey(candidates, key, LeftoverCategory.Registry, exact: true, exe);
                }
            }
        }
    }

    private void AddKey(List<LeftoverItem> candidates, RegistryKeyRef key, LeftoverCategory category, bool exact, string? evidence) =>
        candidates.Add(new LeftoverItem
        {
            Id = "reg:" + key.FullPath,
            Kind = LeftoverKind.RegistryKey,
            Category = category,
            Path = key.DisplayPath,
            Name = key.Path.Split('\\')[^1],
            Size = 0,
            Exact = exact,
            RequiresAdministrator = key.IsMachine,
            Registry = key,
            Evidence = evidence,
        });

    private void AddStartup(List<LeftoverItem> candidates, AppFingerprint app)
    {
        foreach (var entry in _cleanerPlatform.StartupEntries(includeMachine: true))
        {
            var files = CleanerRules.ExecutablesOf(entry.Command, Environment.ExpandEnvironmentVariables, p => _fs.Stat(p) is not null);
            var matches = files.Any(f => (app.InstallFolder is { } folder && SafePaths.IsInside(f, folder)) || app.Executables.Contains(SafePaths.FileName(f).ToLowerInvariant()));
            if (!matches)
            {
                continue;
            }

            candidates.Add(new LeftoverItem
            {
                Id = "startup:" + entry.DisplayPath,
                Kind = LeftoverKind.StartupValue,
                Category = LeftoverCategory.Startup,
                Path = entry.DisplayPath,
                Name = entry.ValueName,
                Detail = entry.Command,
                Exact = true,
                RequiresAdministrator = entry.IsMachine,
                Startup = entry,
                Evidence = "run",
            });
        }
    }

    private void AddReviewOnly(List<LeftoverItem> candidates, AppFingerprint app)
    {
        bool Points(string command)
        {
            var files = CleanerRules.ExecutablesOf(command, Environment.ExpandEnvironmentVariables, p => _fs.Stat(p) is not null);
            return files.Any(f => (app.InstallFolder is { } folder && SafePaths.IsInside(f, folder)) || app.Executables.Contains(SafePaths.FileName(f).ToLowerInvariant()));
        }

        foreach (var task in _platform.ScheduledTasks())
        {
            if (task.Commands.Any(Points))
            {
                candidates.Add(new LeftoverItem
                {
                    Id = "task:" + task.Path,
                    Kind = LeftoverKind.ScheduledTask,
                    Category = LeftoverCategory.Review,
                    Path = task.Path,
                    Name = task.Path.Split('\\')[^1],
                    Detail = task.Commands.FirstOrDefault(),
                    Exact = false,
                    Evidence = "task",
                });
            }
        }

        foreach (var service in _platform.Services())
        {
            if (Points(service.ImagePath))
            {
                candidates.Add(new LeftoverItem
                {
                    Id = "service:" + service.Name,
                    Kind = LeftoverKind.Service,
                    Category = LeftoverCategory.Review,
                    Path = service.Name,
                    Name = service.DisplayName,
                    Detail = service.ImagePath,
                    Exact = false,
                    RequiresAdministrator = true,
                    Evidence = "service",
                });
            }
        }
    }

    private void AddFile(List<LeftoverItem> candidates, FsEntry entry, LeftoverCategory category, bool exact, string? root, bool admin, string? evidence, CancellationToken cancellationToken)
    {
        if (root is null || entry.IsReparsePoint || entry.IsCloudPlaceholder || _critical.Contains(entry.Path) || !SafePaths.ChainIsPlain(_fs, entry.Path, root))
        {
            return;
        }

        if (_folders.OwnLocal is { } own && (SafePaths.Equal(entry.Path, own) || SafePaths.IsInside(entry.Path, own) || SafePaths.IsInside(own, entry.Path)))
        {
            return;
        }

        if (_fs.Identity(entry.Path) is not { } identity)
        {
            return;
        }

        candidates.Add(new LeftoverItem
        {
            Id = entry.Path,
            Kind = entry.IsDirectory ? LeftoverKind.Folder : LeftoverKind.File,
            Category = category,
            Path = entry.Path,
            Name = entry.Name,
            Size = _fs.Measure(entry.Path, cancellationToken).Bytes,
            Exact = exact,
            Identity = identity,
            Root = root,
            RequiresAdministrator = admin || SafePaths.IsInside(entry.Path, _folders.ProgramData) || SafePaths.IsInside(entry.Path, _folders.ProgramFiles)
                                    || (_folders.ProgramFilesX86 is { } x86 && SafePaths.IsInside(entry.Path, x86)),
            Evidence = evidence,
        });
    }
}
