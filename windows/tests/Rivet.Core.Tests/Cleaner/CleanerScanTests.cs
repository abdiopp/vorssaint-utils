// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Xunit;

namespace Rivet.Core.Tests.Cleaner;

/// <summary>
/// Scans a fake profile built in a temporary folder. The recycler only
/// records what it was asked to recycle; nothing outside the temporary
/// folder is ever read or touched.
/// </summary>
public sealed class CleanerScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rivet-cleaner-" + Guid.NewGuid().ToString("N"));
    private readonly CleanerFolders _folders;
    private readonly FakePlatform _platform = new();
    private readonly RecordingRecycler _recycler = new();
    private readonly SettingsStore _settings = SettingsStore.InMemory();
    private readonly DateTime _old = DateTime.UtcNow.AddDays(-10);

    public CleanerScanTests()
    {
        var profile = Path.Combine(_root, "Users", "alex");
        _folders = CleanerRulesTests.Folders(profile, Path.Combine(_root, "Windows"), Path.Combine(_root, "ProgramData")) with
        {
            ProgramFiles = Path.Combine(_root, "Program Files"),
            ProgramFilesX86 = Path.Combine(_root, "Program Files (x86)"),
            OwnLocal = Path.Combine(profile, "AppData", "Local", "Rivet"),
        };
    }

    public void Dispose()
    {
        // Only the test's own temporary tree.
        if (_root.StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Scan_finds_each_category_with_the_right_preselection()
    {
        // Temp: one stale folder, one fresh file, our own temp file.
        var staleTemp = MakeFile(_folders.Temp, "old-installer", "setup.log", 5000, _old);
        MakeFile(_folders.Temp, null, "fresh.tmp", 100, DateTime.UtcNow);
        MakeFile(_folders.Temp, null, "Rivet-update.tmp", 100, _old);

        // Chrome profile caches (Chrome not running) and Edge (running → skipped).
        var chrome = Path.Combine(_folders.LocalAppData, "Google", "Chrome", "User Data");
        MakeFile(Path.Combine(chrome, "Default"), null, "Preferences", 10, _old);
        MakeFile(Path.Combine(chrome, "Default", "Cache"), "Cache_Data", "data_0", 4096, _old);
        MakeFile(Path.Combine(chrome, "Default", "Service Worker", "CacheStorage"), "abc", "index", 2048, _old);
        var edge = Path.Combine(_folders.LocalAppData, "Microsoft", "Edge", "User Data");
        MakeFile(Path.Combine(edge, "Default"), null, "Preferences", 10, _old);
        MakeFile(Path.Combine(edge, "Default", "Cache"), null, "data_1", 4096, _old);
        _platform.Running.Add("msedge.exe");

        // Crash dumps, a stale package folder, Recycle Bin content.
        MakeFile(Path.Combine(_folders.LocalAppData, "CrashDumps"), null, "app.exe.1234.dmp", 9000, _old);
        MakeFile(Path.Combine(_folders.LocalAppData, "Packages", "Contoso.Gone_8wekyb3d8bbwe"), "LocalState", "db", 3000, _old);
        MakeFile(Path.Combine(_folders.LocalAppData, "Packages", "Contoso.Alive_8wekyb3d8bbwe"), "LocalState", "db", 3000, _old);
        _platform.RemovedFamilies.Add("Contoso.Gone_8wekyb3d8bbwe");
        _platform.Bin = new RecycleBinInfo(3, 123456);

        // Orphaned startup value, plus one whose program exists.
        var present = MakeFile(Path.Combine(_root, "Program Files", "Here"), null, "here.exe", 10, _old);
        _platform.Startup.Add(new StartupEntry { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Gone", Command = "\"C:\\Program Files\\Gone\\gone.exe\" /tray" });
        _platform.Startup.Add(new StartupEntry { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Here", Command = "\"" + present + "\" /tray" });

        // A broken Start menu shortcut.
        WriteBytes(_folders.StartMenuPrograms, "Gone App.lnk", CleanerRulesTests.Shortcut("C:\\Program Files\\Gone\\gone.exe", unicode: true));

        var service = NewService();
        await service.ScanAsync(attended: true);
        Assert.Equal(CleanerPhase.Results, service.Phase);
        var items = service.Items;

        var temp = Assert.Single(items, i => i.Category == CleanerCategory.Caches && SafePaths.IsDirectChild(i.Path, _folders.Temp));
        Assert.True(temp.Recommended);
        Assert.Equal(Path.GetDirectoryName(staleTemp), temp.Path);

        var chromeCache = Assert.Single(items, i => i.Detail == "Google Chrome · Default" && i.Name == "Cache");
        Assert.True(service.IsIncluded(chromeCache));
        var serviceWorker = Assert.Single(items, i => i.Name == "CacheStorage");
        Assert.Equal(CleanerGroup.OtherCaches, serviceWorker.Group);
        Assert.False(service.IsIncluded(serviceWorker));
        Assert.DoesNotContain(items, i => i.Detail?.StartsWith("Microsoft Edge", StringComparison.Ordinal) == true);
        Assert.Contains("Microsoft Edge", service.SkippedRunning);

        Assert.Single(items, i => i.Category == CleanerCategory.Logs);
        var leftover = Assert.Single(items, i => i.Category == CleanerCategory.Leftovers && i.Evidence?.StartsWith("pfn:", StringComparison.Ordinal) == true);
        Assert.Equal("Contoso.Gone_8wekyb3d8bbwe", leftover.Name);
        Assert.False(service.IsIncluded(leftover));

        var shortcut = Assert.Single(items, i => i.Evidence == "shortcut");
        Assert.Equal("Gone App", shortcut.Name);

        var startup = Assert.Single(items, i => i.Kind == CleanerItemKind.StartupValue);
        Assert.Equal("Gone", startup.Name);
        Assert.True(service.IsIncluded(startup));

        var bin = Assert.Single(items, i => i.Kind == CleanerItemKind.RecycleBin);
        Assert.True(bin.Permanent);
        Assert.False(service.IsIncluded(bin));

        // Administrator-only locations were left out and the scan says so.
        Assert.True(service.AdministratorSkipped);
    }

    [Fact]
    public async Task Links_are_never_followed_or_offered()
    {
        var outside = Path.Combine(_root, "Elsewhere");
        MakeFile(outside, "precious", "thesis.docx", 70000, _old);
        Directory.CreateDirectory(_folders.Temp);
        Directory.CreateSymbolicLink(Path.Combine(_folders.Temp, "link-to-elsewhere"), Path.Combine(outside, "precious"));
        MakeFile(_folders.Temp, "real-old", "x.log", 100, _old);

        var service = NewService();
        await service.ScanAsync(attended: true);
        Assert.DoesNotContain(service.Items, i => i.Path.Contains("link-to-elsewhere", StringComparison.Ordinal));
        var real = Assert.Single(service.Items, i => i.Name == "real-old");
        Assert.Equal(100, real.Size);
    }

    [Fact]
    public async Task Clean_empties_the_bin_first_recycles_files_and_backs_up_startup_values()
    {
        MakeFile(_folders.Temp, "old-installer", "setup.log", 5000, _old);
        MakeFile(Path.Combine(_folders.LocalAppData, "CrashDumps"), null, "a.dmp", 9000, _old);
        _platform.Bin = new RecycleBinInfo(1, 777);
        _platform.Startup.Add(new StartupEntry { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Gone", Command = "C:\\Gone\\gone.exe" });

        var service = NewService();
        await service.ScanAsync(attended: true);
        service.SetIncluded(service.Items.Single(i => i.Kind == CleanerItemKind.RecycleBin), true);
        var result = await service.CleanSelectedAsync(escalate: true);

        Assert.Equal(CleanerPhase.Done, service.Phase);
        Assert.Equal(["emptyBin", "removeStartup:Gone"], _platform.Calls);
        Assert.Equal(2, _recycler.Recycled.Count);
        Assert.Equal(777 + 5000 + 9000, result.FreedBytes);
        Assert.Equal(0, result.Failed);
        Assert.NotNull(result.BackupFolder);
        // The recycler is a recorder: the files are still on disk.
        Assert.True(File.Exists(Path.Combine(_folders.Temp, "old-installer", "setup.log")));
    }

    [Fact]
    public async Task A_path_swapped_after_the_scan_is_refused()
    {
        var file = MakeFile(Path.Combine(_folders.LocalAppData, "CrashDumps"), null, "a.dmp", 9000, _old);
        var service = NewService();
        await service.ScanAsync(attended: true);
        var dump = Assert.Single(service.Items, i => i.Category == CleanerCategory.Logs);
        Assert.True(service.MayRemove(dump));

        // Replace the file with a different one at the same path.
        File.Delete(file);
        File.WriteAllBytes(file, new byte[12345]);
        File.SetCreationTimeUtc(file, DateTime.UtcNow);
        Assert.False(service.MayRemove(dump));

        var result = await service.CleanSelectedAsync(escalate: true);
        Assert.Empty(_recycler.Recycled);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Guard_refuses_critical_and_moved_items()
    {
        var service = NewService();
        var critical = new CleanerItem { Id = "x", Category = CleanerCategory.Caches, Kind = CleanerItemKind.Folder, Path = _folders.Temp, Name = "Temp", Size = 1, Identity = new FileIdentity(1, 1, 1) };
        Assert.False(service.MayRemove(critical));

        var nested = MakeFile(Path.Combine(_folders.Temp, "a", "b"), null, "c.log", 10, _old);
        var fs = new ManagedFileSystem();
        var deep = new CleanerItem
        {
            Id = "y", Category = CleanerCategory.Caches, Kind = CleanerItemKind.File, Path = nested, Name = "c.log", Size = 10,
            Identity = fs.Identity(nested), Root = _folders.Temp,
        };
        // Not a direct child of its category folder any more.
        Assert.False(service.MayRemove(deep));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Cancelling_a_scan_delivers_nothing()
    {
        MakeFile(_folders.Temp, "old-installer", "setup.log", 5000, _old);
        _platform.SlowCategory = true;
        var service = NewService();
        var scan = service.ScanAsync(attended: true);
        await Task.Delay(20);
        service.Reset();
        await scan;
        Assert.Equal(CleanerPhase.Idle, service.Phase);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task Scheduled_runs_clean_only_the_safe_part_and_notify()
    {
        MakeFile(_folders.Temp, "old-installer", "setup.log", 5000, _old);
        MakeFile(Path.Combine(_folders.LocalAppData, "Packages", "Contoso.Gone_8wekyb3d8bbwe"), "LocalState", "db", 3000, _old);
        _platform.RemovedFamilies.Add("Contoso.Gone_8wekyb3d8bbwe");
        _platform.Bin = new RecycleBinInfo(1, 999);

        var service = NewService();
        await service.ScanAsync(attended: false);
        Assert.All(service.Items.Where(service.IsIncluded), i => Assert.True(CleanerGroups.IsSafe(i.Group)));
        var result = await service.CleanSelectedAsync(escalate: false);
        Assert.Single(_recycler.Recycled);
        Assert.Empty(_platform.Calls);
        Assert.Equal(5000, result.FreedBytes);

        Assert.Equal("Nothing to clean. Your PC is tidy.", CleanerScheduler.NotificationBody(0, 0));
        Assert.StartsWith("4.88 KB freed", CleanerScheduler.NotificationBody(5000, 0), StringComparison.Ordinal);
        Assert.EndsWith("Recycle Bin.", CleanerScheduler.NotificationBody(5000, 1), StringComparison.Ordinal);
    }

    private CleanerService NewService() =>
        new(new ManagedFileSystem(), _platform, _recycler, new FixedFolders(_folders), _settings, () => Directory.CreateDirectory(Path.Combine(_root, "backups")).FullName);

    private string MakeFile(string folder, string? sub, string name, int size, DateTime timeUtc)
    {
        var directory = sub is null ? folder : Path.Combine(folder, sub);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, timeUtc);
        File.SetCreationTimeUtc(path, timeUtc);
        Directory.SetLastWriteTimeUtc(directory, timeUtc);
        if (sub is not null)
        {
            Directory.SetCreationTimeUtc(directory, timeUtc);
        }

        return path;
    }

    private static void WriteBytes(string folder, string name, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
    }

    private sealed class FixedFolders(CleanerFolders folders) : IKnownFolders
    {
        public CleanerFolders Folders { get; } = folders;
    }

    private sealed class RecordingRecycler : IRecycler
    {
        public List<string> Recycled { get; } = [];

        public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, bool allowElevationPrompt)
        {
            Recycled.AddRange(paths);
            return paths.Select(p => new RecycleOutcome(p, RecycleStatus.Recycled)).ToList();
        }
    }

    private sealed class FakePlatform : ICleanerPlatform
    {
        public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> RemovedFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<StartupEntry> Startup { get; } = [];

        public List<string> Calls { get; } = [];

        public RecycleBinInfo? Bin { get; set; }

        public bool SlowCategory { get; set; }

        public bool IsElevated => false;

        public RecycleBinInfo? QueryRecycleBin() => Bin;

        public bool EmptyRecycleBin()
        {
            Calls.Add("emptyBin");
            return true;
        }

        public IReadOnlySet<string> RunningProcessNames() => Running;

        public IReadOnlyList<string> RunningProcessPaths() => [];

        public bool? IsPackageFamilyInstalled(string familyName) => !RemovedFamilies.Contains(familyName);

        public IReadOnlyList<string> InstalledProgramLocations()
        {
            if (SlowCategory)
            {
                Thread.Sleep(300);
            }

            return [];
        }

        public IReadOnlyList<StartupEntry> StartupEntries(bool includeMachine) => Startup;

        public bool RemoveStartupEntry(StartupEntry entry, string backupFolder)
        {
            Calls.Add("removeStartup:" + entry.ValueName);
            return Directory.Exists(backupFolder);
        }

        public DriveKind DriveKindOf(string path) => DriveKind.Fixed;
    }
}
