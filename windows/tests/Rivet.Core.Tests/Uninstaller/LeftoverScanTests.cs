// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Platform;
using Rivet.Core.Settings;
using Rivet.Core.Tests.Cleaner;
using Xunit;

namespace Rivet.Core.Tests.Uninstaller;

/// <summary>
/// An app "installed" in a temporary profile: the scan must find its residue
/// and leftovers, leave other apps' data alone, and removal must back up
/// registry keys before deleting them. The recycler only records requests.
/// </summary>
public sealed class LeftoverScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rivet-uninstaller-" + Guid.NewGuid().ToString("N"));
    private readonly CleanerFolders _folders;
    private readonly FakeUninstallerPlatform _platform = new();
    private readonly FakeCleanerPlatform _cleanerPlatform = new();
    private readonly ManagedFileSystem _fs = new();
    private readonly DateTime _old = DateTime.UtcNow.AddDays(-30);

    public LeftoverScanTests()
    {
        var profile = Path.Combine(_root, "Users", "alex");
        _folders = CleanerRulesTests.Folders(profile, Path.Combine(_root, "Windows"), Path.Combine(_root, "ProgramData")) with
        {
            ProgramFiles = Path.Combine(_root, "Program Files"),
            ProgramFilesX86 = Path.Combine(_root, "Program Files (x86)"),
        };
    }

    public void Dispose()
    {
        if (_root.StartsWith(Path.GetTempPath(), StringComparison.Ordinal) && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Scan_finds_exact_and_related_leftovers_and_respects_exclusivity()
    {
        var (app, others) = InstallContoso();
        var fingerprint = AppFingerprint.Build(app, [app, .. others], _folders, _fs);
        Assert.True(fingerprint.Trusted);
        Assert.Contains("studio.exe", fingerprint.Executables);
        Assert.DoesNotContain("unins000.exe", fingerprint.Executables);
        Assert.True(fingerprint.PublisherShared);

        var items = new LeftoverScanner(_fs, _cleanerPlatform, _platform, _folders).Scan(fingerprint, CancellationToken.None);
        string Rel(LeftoverItem i) => Path.GetRelativePath(_root, i.Path);

        var residue = Assert.Single(items, i => i.Category == LeftoverCategory.Application);
        Assert.True(residue.Exact);
        Assert.True(residue.RequiresAdministrator);

        // Vendor folder shared with Contoso Paint: only the product folder.
        var support = Assert.Single(items, i => i.Category == LeftoverCategory.Support && Rel(i).Contains("Roaming", StringComparison.Ordinal));
        Assert.EndsWith(Path.Combine("Contoso", "Studio"), support.Path, StringComparison.Ordinal);
        Assert.False(support.Exact);
        Assert.DoesNotContain(items, i => i.Path.EndsWith(Path.Combine("Contoso", "Paint"), StringComparison.Ordinal));

        // A folder named after the executable, in Local.
        Assert.Single(items, i => i.Category == LeftoverCategory.Caches && i.Name == "Studio");

        // Crash dump and error report by executable name: exact.
        Assert.Single(items, i => i.Category == LeftoverCategory.Logs && i.Name.StartsWith("studio.exe.", StringComparison.Ordinal) && i.Exact);
        Assert.Single(items, i => i.Category == LeftoverCategory.Logs && i.Name.StartsWith("AppCrash_studio.exe_", StringComparison.Ordinal) && i.Exact);
        Assert.DoesNotContain(items, i => i.Name.StartsWith("AppCrash_paint.exe_", StringComparison.Ordinal));

        // Start menu folder holding only the app's shortcuts goes as a whole.
        var shortcuts = Assert.Single(items, i => i.Category == LeftoverCategory.Shortcuts);
        Assert.Equal("Contoso Studio", shortcuts.Name);
        Assert.True(shortcuts.Exact);

        // Registry: HKCU\Software\Contoso\Studio (related), App Paths (exact), Run value (exact).
        Assert.Single(items, i => i.Kind == LeftoverKind.RegistryKey && i.Path == @"HKCU\Software\Contoso\Studio" && !i.Exact);
        Assert.DoesNotContain(items, i => i.Path == @"HKCU\Software\Contoso\Paint");
        Assert.Single(items, i => i.Kind == LeftoverKind.RegistryKey && i.Path.EndsWith(@"App Paths\studio.exe", StringComparison.Ordinal) && i.Exact);
        var run = Assert.Single(items, i => i.Kind == LeftoverKind.StartupValue);
        Assert.Equal("ContosoStudio", run.Name);

        // Scheduled task and service: review only.
        Assert.Single(items, i => i.Kind == LeftoverKind.ScheduledTask && i.IsReviewOnly);
        Assert.Single(items, i => i.Kind == LeftoverKind.Service && i.IsReviewOnly);

        // Nothing from the other app or unrelated folders.
        Assert.DoesNotContain(items, i => i.Path.Contains("Paint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Path.Contains("Unrelated", StringComparison.Ordinal));
    }

    [Fact]
    public void Untrusted_apps_get_no_leftovers()
    {
        var app = new InstalledApp { Key = "x", Kind = InstalledAppKind.Win32, DisplayName = "Portable Thing", UninstallString = "x.exe", InstallLocation = Path.Combine(_root, "Downloads", "Thing") };
        MakeFile(Path.Combine(_folders.RoamingAppData, "Portable Thing"), "settings.json");
        var fingerprint = AppFingerprint.Build(app, [app], _folders, _fs);
        Assert.False(fingerprint.Trusted);
        Assert.Empty(new LeftoverScanner(_fs, _cleanerPlatform, _platform, _folders).Scan(fingerprint, CancellationToken.None));
    }

    [Fact]
    public void Dedupe_prefers_exact_finds_and_drops_nested_ones()
    {
        LeftoverItem Item(string path, bool exact) => new() { Id = path, Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support, Path = path, Name = Path.GetFileName(path), Exact = exact };
        var a = Path.Combine(_root, "A");
        var items = LeftoverScanner.Deduplicate(
        [
            Item(a, exact: false),
            Item(Path.Combine(a, "B"), exact: true),
            Item(Path.Combine(_root, "C"), exact: false),
            Item(Path.Combine(_root, "C", "D"), exact: false),
            Item(Path.Combine(_root, "C"), exact: true),
        ]);
        Assert.Equal([Path.Combine(_root, "C"), Path.Combine(a, "B")], items.Select(i => i.Path).OrderBy(p => p.Length).ToList());
        Assert.True(items.Single(i => i.Path == Path.Combine(_root, "C")).Exact);
    }

    [Fact]
    public async Task Service_runs_the_uninstaller_verifies_and_removes_with_backups()
    {
        var (app, others) = InstallContoso();
        var provider = new FakeApps([app, .. others]);
        var recycler = new RecordingRecycler();
        var backups = Path.Combine(_root, "backups");
        var service = new UninstallerService(provider, _platform, _cleanerPlatform, _fs, recycler, new FixedFolders(_folders), new NoProcesses(), new ProcessService(new NoProcesses(), SettingsStore.InMemory()), SettingsStore.InMemory(), () => Directory.CreateDirectory(backups).FullName);

        await service.LoadAppsAsync();
        Assert.Equal(2, service.Apps.Count);
        await service.SelectAsync(service.Apps.Single(a => a.DisplayName == "Contoso Studio"));
        Assert.NotNull(service.Fingerprint);

        // The uninstaller "removes" the entry but leaves the files behind.
        _platform.StillInstalled = false;
        await service.UninstallAsync();
        Assert.Equal(UninstallerPhase.Results, service.Phase);
        Assert.Equal(1, _platform.Runs);

        // Exact finds start checked; related ones and review-only ones do not.
        Assert.All(service.Leftovers.Where(service.IsIncluded), i => Assert.True(i.Exact));
        Assert.All(service.Leftovers.Where(i => i.IsReviewOnly), i => Assert.False(service.IsIncluded(i)));
        var related = service.Leftovers.Single(i => i.Path == @"HKCU\Software\Contoso\Studio");
        service.SetIncluded(related, true);

        await service.RemoveSelectedAsync();
        Assert.Equal(UninstallerPhase.Done, service.Phase);
        Assert.Contains(@"HKEY_CURRENT_USER\Software\Contoso\Studio", _platform.Deleted);
        var backup = Assert.Single(Directory.GetFiles(backups, "*.reg"), f => f.Contains("Contoso_Studio", StringComparison.Ordinal));
        var text = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(backup)[2..]);
        Assert.Contains("[HKEY_CURRENT_USER\\Software\\Contoso\\Studio]", text, StringComparison.Ordinal);
        Assert.Contains("\"Theme\"=\"dark\"", text, StringComparison.Ordinal);
        Assert.NotEmpty(recycler.Recycled);
        Assert.Equal("ContosoStudio", Assert.Single(_cleanerPlatform.RemovedStartup));
        Assert.NotNull(service.LastResult!.BackupFolder);
    }

    [Fact]
    public async Task An_app_still_installed_after_its_uninstaller_gets_no_leftovers()
    {
        var (app, others) = InstallContoso();
        var service = new UninstallerService(new FakeApps([app, .. others]), _platform, _cleanerPlatform, _fs, new RecordingRecycler(), new FixedFolders(_folders), new NoProcesses(), new ProcessService(new NoProcesses(), SettingsStore.InMemory()), SettingsStore.InMemory(), () => _root);
        await service.LoadAppsAsync();
        await service.SelectAsync(service.Apps.Single(a => a.DisplayName == "Contoso Studio"));
        _platform.StillInstalled = true;
        await service.UninstallAsync();
        Assert.Equal(UninstallerPhase.StillInstalled, service.Phase);
        Assert.Empty(service.Leftovers);
    }

    private (InstalledApp App, InstalledApp[] Others) InstallContoso()
    {
        var install = Path.Combine(_root, "Program Files", "Contoso", "Studio");
        MakeFile(install, "studio.exe");
        MakeFile(install, "unins000.exe");
        MakeFile(Path.Combine(_root, "Program Files", "Contoso", "Paint"), "paint.exe");
        MakeFile(Path.Combine(_folders.RoamingAppData, "Contoso", "Studio"), "settings.json");
        MakeFile(Path.Combine(_folders.RoamingAppData, "Contoso", "Paint"), "settings.json");
        MakeFile(Path.Combine(_folders.LocalAppData, "Studio"), "cache.bin");
        MakeFile(Path.Combine(_folders.LocalAppData, "Unrelated"), "x.bin");
        MakeFile(Path.Combine(_folders.LocalAppData, "CrashDumps"), "studio.exe.4321.dmp");
        MakeFile(Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_studio.exe_123abc_cab_1"), "Report.wer");
        MakeFile(Path.Combine(_folders.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive", "AppCrash_paint.exe_9_cab_2"), "Report.wer");
        var menu = Path.Combine(_folders.StartMenuPrograms, "Contoso Studio");
        Directory.CreateDirectory(menu);
        File.WriteAllBytes(Path.Combine(menu, "Contoso Studio.lnk"), CleanerRulesTests.Shortcut(Path.Combine(install, "studio.exe"), unicode: true));
        File.WriteAllBytes(Path.Combine(menu, "Uninstall Contoso Studio.lnk"), CleanerRulesTests.Shortcut(Path.Combine(install, "unins000.exe"), unicode: true));

        _platform.Keys[@"HKEY_CURRENT_USER\Software"] = ["Contoso", "Microsoft", "Classes"];
        _platform.Keys[@"HKEY_CURRENT_USER\Software\Contoso"] = ["Studio", "Paint"];
        _platform.Keys[@"HKEY_CURRENT_USER\Software\Contoso\Studio"] = [];
        _platform.Keys[@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\studio.exe"] = [];
        _platform.Tasks.Add(new ScheduledTaskInfo(@"\Contoso\Studio Update", ["\"" + Path.Combine(install, "studio.exe") + "\" --update"]));
        _platform.ServicesList.Add(new ServiceInfo("ContosoSvc", "Contoso Studio Service", "\"" + Path.Combine(install, "svc", "contososvc.exe") + "\""));
        _cleanerPlatform.Startup.Add(new StartupEntry { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "ContosoStudio", Command = "\"" + Path.Combine(install, "studio.exe") + "\" --tray" });

        var app = new InstalledApp
        {
            Key = "arp:Machine64:ContosoStudio", Kind = InstalledAppKind.Win32, DisplayName = "Contoso Studio", Publisher = "Contoso Ltd.",
            InstallLocation = install, UninstallString = "\"" + Path.Combine(install, "unins000.exe") + "\"", Scope = RegistryScope.Machine64,
        };
        var paint = new InstalledApp
        {
            Key = "arp:Machine64:ContosoPaint", Kind = InstalledAppKind.Win32, DisplayName = "Contoso Paint", Publisher = "Contoso Ltd.",
            InstallLocation = Path.Combine(_root, "Program Files", "Contoso", "Paint"), UninstallString = "x.exe", Scope = RegistryScope.Machine64,
        };
        return (app, [paint]);
    }

    private static void MakeFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), new byte[64]);
    }

    private sealed class FixedFolders(CleanerFolders folders) : IKnownFolders
    {
        public CleanerFolders Folders { get; } = folders;
    }

    private sealed class FakeApps(IReadOnlyList<InstalledApp> apps) : IInstalledAppsProvider
    {
        public IReadOnlyList<InstalledApp> Enumerate() => apps;

        public PixelBuffer? LoadIcon(InstalledApp app, int size = 32) => null;
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

    private sealed class FakeUninstallerPlatform : IUninstallerPlatform
    {
        public Dictionary<string, List<string>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ScheduledTaskInfo> Tasks { get; } = [];

        public List<ServiceInfo> ServicesList { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool StillInstalled { get; set; }

        public int Runs { get; private set; }

        public bool IsElevated => false;

        public Task<UninstallRunResult> RunAsync(InstalledApp app, bool quiet, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(new UninstallRunResult(UninstallRunStatus.Finished, 0));
        }

        public bool IsStillInstalled(InstalledApp app) => StillInstalled;

        public IReadOnlyList<string> SubKeyNames(RegistryKeyRef key) => Keys.TryGetValue(key.FullPath, out var names) ? names : [];

        public bool KeyExists(RegistryKeyRef key) => Keys.ContainsKey(key.FullPath) && !Deleted.Contains(key.FullPath);

        public RegKeySnapshot? Snapshot(RegistryKeyRef key) =>
            KeyExists(key) ? new RegKeySnapshot(key.FullPath, [new RegValue("Theme", 1, "dark")], []) : null;

        public bool DeleteKey(RegistryKeyRef key)
        {
            Deleted.Add(key.FullPath);
            return true;
        }

        public IReadOnlyList<ScheduledTaskInfo> ScheduledTasks() => Tasks;

        public IReadOnlyList<ServiceInfo> Services() => ServicesList;
    }

    private sealed class FakeCleanerPlatform : ICleanerPlatform
    {
        public List<StartupEntry> Startup { get; } = [];

        public List<string> RemovedStartup { get; } = [];

        public bool IsElevated => false;

        public RecycleBinInfo? QueryRecycleBin() => null;

        public bool EmptyRecycleBin() => false;

        public IReadOnlySet<string> RunningProcessNames() => new HashSet<string>();

        public IReadOnlyList<string> RunningProcessPaths() => [];

        public bool? IsPackageFamilyInstalled(string familyName) => true;

        public IReadOnlyList<string> InstalledProgramLocations() => [];

        public IReadOnlyList<StartupEntry> StartupEntries(bool includeMachine) => Startup;

        public bool RemoveStartupEntry(StartupEntry entry, string backupFolder)
        {
            RemovedStartup.Add(entry.ValueName);
            return true;
        }

        public DriveKind DriveKindOf(string path) => DriveKind.Fixed;
    }

    private sealed class NoProcesses : IProcessPlatform
    {
        public int CurrentProcessId => 1;

        public int LogicalProcessorCount => 4;

        public IReadOnlyList<RawProcess> Snapshot() => [];

        public string? ImagePath(ProcessIdentity identity) => null;

        public ExecutableDescription? Describe(string path) => null;

        public string? UserName(ProcessIdentity identity) => null;

        public bool IsCritical(ProcessIdentity identity) => false;

        public IReadOnlySet<int> ProcessesWithWindows() => new HashSet<int>();

        public KillOutcome CloseWindows(ProcessIdentity identity) => KillOutcome.Done;

        public KillOutcome Terminate(ProcessIdentity identity) => KillOutcome.Done;

        public Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);

        public string? CommandLine(ProcessIdentity identity) => null;

        public bool Launch(string path, string? arguments, string? workingDirectory) => true;

    }
}
