// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Cleaner;

/// <summary>The sample PC: a user "Alex" with the usual clutter of a Windows profile.</summary>
public static class SampleProfile
{
    public const string Profile = @"C:\Users\Alex";
    public const string Local = Profile + @"\AppData\Local";
    public const string Roaming = Profile + @"\AppData\Roaming";

    public static CleanerFolders Folders { get; } = new()
    {
        UserProfile = Profile,
        Temp = Local + @"\Temp",
        LocalAppData = Local,
        RoamingAppData = Roaming,
        LocalLow = Profile + @"\AppData\LocalLow",
        ProgramData = @"C:\ProgramData",
        Windows = @"C:\Windows",
        ProgramFiles = @"C:\Program Files",
        ProgramFilesX86 = @"C:\Program Files (x86)",
        StartMenuPrograms = Roaming + @"\Microsoft\Windows\Start Menu\Programs",
        CommonStartMenuPrograms = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
        Startup = Roaming + @"\Microsoft\Windows\Start Menu\Programs\Startup",
        CommonStartup = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup",
        Desktop = Profile + @"\Desktop",
        CommonDesktop = @"C:\Users\Public\Desktop",
        Documents = Profile + @"\Documents",
        Downloads = Profile + @"\Downloads",
        Pictures = Profile + @"\Pictures",
        Music = Profile + @"\Music",
        Videos = Profile + @"\Videos",
        Screenshots = Profile + @"\Pictures\Screenshots",
        OneDrive = Profile + @"\OneDrive",
        OwnLocal = Local + @"\Rivet",
        OwnRoaming = Roaming + @"\Rivet",
    };

    public static void Populate(InMemoryFileSystem fs)
    {
        var temp = Folders.Temp;
        fs.FileMb(temp + @"\{4F1E2D3C-setup}\setup.msi", 48.2, 9);
        fs.FileMb(temp + @"\chrome_installer.log", 0.12, 4);
        fs.FileMb(temp + @"\WinGet\cache\Git-2.46.0-64-bit.exe", 61.4, 12);
        fs.FileMb(temp + @"\~DF3A1.tmp", 0.5, 0.1);
        fs.FileMb(temp + @"\Rivet-update.json", 0.01, 3);

        var chrome = Local + @"\Google\Chrome\User Data";
        fs.File(chrome + @"\Default\Preferences", 40_000, 1);
        fs.FileMb(chrome + @"\Default\Cache\Cache_Data\data_1", 384, 1);
        fs.FileMb(chrome + @"\Default\Code Cache\js\index", 96.5, 1);
        fs.FileMb(chrome + @"\Default\GPUCache\data_0", 12.4, 1);
        fs.FileMb(chrome + @"\Default\Service Worker\CacheStorage\8f2a\index", 163, 1);
        fs.File(chrome + @"\Profile 1\Preferences", 20_000, 3);
        fs.FileMb(chrome + @"\Profile 1\Cache\Cache_Data\data_1", 58.1, 3);
        fs.FileMb(chrome + @"\ShaderCache\data_0", 6.2, 2);

        var edge = Local + @"\Microsoft\Edge\User Data";
        fs.File(edge + @"\Default\Preferences", 30_000, 1);
        fs.FileMb(edge + @"\Default\Cache\Cache_Data\data_1", 212, 1);

        fs.FileMb(Local + @"\Mozilla\Firefox\Profiles\x8k2m1.default-release\cache2\entries\A1B2", 141, 2);
        fs.FileMb(Local + @"\Microsoft\Windows\INetCache\IE\container.dat", 14.6, 20);
        fs.FileMb(Local + @"\Microsoft\Windows\INetCache\Content.Outlook\ABC123\Quarterly report.xlsx", 1.1, 2);
        fs.FileMb(Local + @"\D3DSCache\shader.idx", 64.3, 5);
        fs.FileMb(Local + @"\NVIDIA\DXCache\dx.bin", 221, 3);
        fs.FileMb(Local + @"\npm-cache\_cacache\content-v2\sha512\blob", 1240, 15);
        fs.FileMb(Local + @"\pip\Cache\http\blob", 342, 40);
        fs.FileMb(Local + @"\Spotify\Storage\offline.bnk", 2150, 6);
        fs.FileMb(Local + @"\Microsoft\Windows\Explorer\thumbcache_1280.db", 45.2, 2);
        fs.FileMb(Local + @"\Microsoft\Windows\Explorer\thumbcache_idx.db", 1.1, 0.5, inUse: true);
        fs.FileMb(Local + @"\Microsoft\Windows\Explorer\iconcache_32.db", 3.4, 2);

        fs.FileMb(Local + @"\CrashDumps\Code.exe.12345.dmp", 221, 8);
        fs.FileMb(Local + @"\Microsoft\Windows\WER\ReportArchive\AppCrash_Spotify.exe_4c2d91f0_cab_1a2b\Report.wer", 2.3, 25);

        fs.FileMb(Local + @"\Packages\Contoso.OldGame_8wekyb3d8bbwe\LocalState\saves.db", 820, 60);
        fs.FileMb(Local + @"\Packages\Microsoft.WindowsCalculator_8wekyb3d8bbwe\Settings\settings.dat", 0.1, 3);
        fs.FileMb(Local + @"\Programs\OldTool\resources\app.asar", 96, 90);

        fs.FileMb(Local + @"\Microsoft\VisualStudio\17.0_9a1b2c3d\ComponentModelCache\Microsoft.VisualStudio.Default.cache", 182, 4);
        fs.FileMb(Local + @"\JetBrains\Rider2024.2\caches\content.dat", 1610, 2);
        fs.FileMb(Profile + @"\.gradle\caches\modules-2\files-2.1\blob", 2430, 30);
        fs.FileMb(Profile + @"\.nuget\packages\newtonsoft.json\13.0.3\lib.dll", 3120, 30);

        var backup = Profile + @"\Apple\MobileSync\Backup\00008110-001A2B3C0E";
        fs.File(backup + @"\Info.plist", 0, 120, content: System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>Device Name</key><string>Alex's iPhone</string><key>Last Backup Date</key><date>2025-11-02T18:04:11Z</date></dict></plist>"));
        fs.FileMb(backup + @"\Manifest.db", 18_400, 120);

        fs.FileMb(Folders.Screenshots + @"\Screenshot (12).png", 1.4, 75);
        fs.FileMb(Folders.Screenshots + @"\Screenshot (13).png", 2.1, 70);
        fs.FileMb(Folders.Screenshots + @"\Screenshot (14).png", 0.8, 2);
        fs.FileMb(Folders.Screenshots + @"\Invoice March.png", 0.6, 80);

        fs.File(Folders.StartMenuPrograms + @"\Old Editor.lnk", 0, 200, content: InMemoryFileSystem.Shortcut(@"C:\Program Files\Old Editor\editor.exe"));
        fs.File(Local + @"\Microsoft\OneDrive\OneDrive.exe", 52_000_000, 30);

        // Downloads (WhatsApp review).
        fs.FileMb(Folders.Downloads + @"\WhatsApp Image 2026-09-01 at 10.15.32_ab12cd34.jpg", 0.4, 38);
        fs.FileMb(Folders.Downloads + @"\WhatsApp Video 2026-09-12 at 18.02.11.mp4", 24.6, 27);
        fs.FileMb(Folders.Downloads + @"\Lease agreement.pdf", 1.8, 21);
        fs.FileMb(Folders.Downloads + @"\Holiday photos.zip", 312, 12);
        fs.FileMb(Folders.Downloads + @"\setup-x64.exe", 88, 3);

        // An app the sample Uninstaller can remove, with its leftovers.
        fs.FileMb(@"C:\Program Files\Contoso\Studio\studio.exe", 84, 200);
        fs.FileMb(@"C:\Program Files\Contoso\Studio\unins000.exe", 3, 200);
        fs.FileMb(@"C:\Program Files\Contoso\Studio\plugins\render.dll", 140, 200);
        fs.FileMb(@"C:\Program Files\Contoso\Paint\paint.exe", 40, 300);
        fs.FileMb(Roaming + @"\Contoso\Studio\settings.json", 0.02, 3);
        fs.FileMb(Roaming + @"\Contoso\Studio\Projects\recent.db", 8.4, 3);
        fs.FileMb(Roaming + @"\Contoso\Paint\settings.json", 0.01, 3);
        fs.FileMb(Local + @"\Studio\GPUCache\data", 320, 3);
        fs.FileMb(Local + @"\CrashDumps\studio.exe.4321.dmp", 64, 40);
        fs.File(Folders.StartMenuPrograms + @"\Contoso Studio\Contoso Studio.lnk", 0, 200, content: InMemoryFileSystem.Shortcut(@"C:\Program Files\Contoso\Studio\studio.exe"));
        fs.File(Folders.StartMenuPrograms + @"\Contoso Studio\Uninstall Contoso Studio.lnk", 0, 200, content: InMemoryFileSystem.Shortcut(@"C:\Program Files\Contoso\Studio\unins000.exe"));
    }
}

public sealed class FakeKnownFolders : IKnownFolders
{
    public CleanerFolders Folders => SampleProfile.Folders;
}

/// <summary>"Recycles" by dropping items from the in-memory tree; nothing real is touched.</summary>
public sealed class FakeRecycler(InMemoryFileSystem fs) : IRecycler
{
    public IReadOnlyList<RecycleOutcome> Recycle(IReadOnlyList<string> paths, bool allowElevationPrompt)
    {
        var outcomes = new List<RecycleOutcome>();
        foreach (var path in paths)
        {
            if (fs.Stat(path) is null)
            {
                outcomes.Add(new RecycleOutcome(path, RecycleStatus.Missing));
            }
            else if (fs.IsInUse(path))
            {
                outcomes.Add(new RecycleOutcome(path, RecycleStatus.InUse));
            }
            else
            {
                fs.Remove(path);
                outcomes.Add(new RecycleOutcome(path, RecycleStatus.Recycled));
                Log.Info("cleaner", $"[fake] recycled {path}");
            }
        }

        return outcomes;
    }
}

public sealed class FakeCleanerPlatform(InMemoryFileSystem fs, IInstalledAppsProvider apps) : ICleanerPlatform
{
    private readonly object _gate = new();
    private readonly List<StartupEntry> _startup =
    [
        new() { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "OldUpdater", Command = "\"C:\\Program Files\\OldUpdater\\updater.exe\" /silent" },
        new() { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "OneDrive", Command = "\"C:\\Users\\Alex\\AppData\\Local\\Microsoft\\OneDrive\\OneDrive.exe\" /background" },
        new() { Hive = "HKEY_CURRENT_USER", KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "PortableSync", Command = "\"E:\\Tools\\sync.exe\"" },
    ];

    private RecycleBinInfo _bin = new(37, 1_934_000_000);

    public bool IsElevated => false;

    public RecycleBinInfo? QueryRecycleBin()
    {
        lock (_gate)
        {
            return _bin;
        }
    }

    public bool EmptyRecycleBin()
    {
        lock (_gate)
        {
            _bin = new RecycleBinInfo(0, 0);
        }

        Log.Info("cleaner", "[fake] emptied the Recycle Bin");
        return true;
    }

    public IReadOnlySet<string> RunningProcessNames() => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "msedge.exe", "explorer.exe", "teams.exe" };

    public IReadOnlyList<string> RunningProcessPaths() => [@"c:\windows\explorer.exe"];

    public bool? IsPackageFamilyInstalled(string familyName) => !familyName.StartsWith("Contoso.OldGame", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> InstalledProgramLocations() => apps.Enumerate().Select(a => a.InstallLocation).OfType<string>().ToList();

    public IReadOnlyList<StartupEntry> StartupEntries(bool includeMachine)
    {
        lock (_gate)
        {
            return _startup.ToList();
        }
    }

    public bool RemoveStartupEntry(StartupEntry entry, string backupFolder)
    {
        lock (_gate)
        {
            Log.Info("cleaner", $"[fake] removed startup entry {entry.DisplayPath}");
            return _startup.Remove(entry);
        }
    }

    public DriveKind DriveKindOf(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? DriveKind.Network
        : path.StartsWith("C:", StringComparison.OrdinalIgnoreCase) ? DriveKind.Fixed
        : path.StartsWith("E:", StringComparison.OrdinalIgnoreCase) ? DriveKind.Removable
        : DriveKind.Unavailable;

    internal InMemoryFileSystem FileSystem => fs;
}

/// <summary>Mark of the Web for two of the sample downloads.</summary>
public sealed class FakeWebMarkReader : IWebMarkReader
{
    public string? ReadZoneIdentifier(string path)
    {
        var name = SafePaths.FileName(path);
        return name switch
        {
            "Lease agreement.pdf" => "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://web.whatsapp.com/\r\nHostUrl=https://mmg.whatsapp.net/v/t62.7119-24/lease.enc\r\n",
            "Holiday photos.zip" => "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://photos.example.com/download/holiday.zip\r\n",
            "WhatsApp Video 2026-09-12 at 18.02.11.mp4" => "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://mmg.whatsapp.net/v/t62.7161-24/video.enc\r\n",
            _ => null,
        };
    }
}

public sealed class FakeCleanerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<InMemoryFileSystem>();
        services.AddSingleton<ICleanerFileSystem>(sp => sp.GetRequiredService<InMemoryFileSystem>());
        services.AddSingleton<IRecycler, FakeRecycler>();
        services.AddSingleton<IKnownFolders, FakeKnownFolders>();
        services.AddSingleton<ICleanerPlatform, FakeCleanerPlatform>();
        services.AddSingleton<IWebMarkReader, FakeWebMarkReader>();
    }
}
