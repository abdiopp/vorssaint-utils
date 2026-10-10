// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Maintenance.Cleaner;
using Rivet.Core.Maintenance.Processes;
using Rivet.Core.Maintenance.Uninstaller;
using Rivet.Core.Modules;
using Rivet.Platform.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using WinPackageManager = global::Windows.Management.Deployment.PackageManager;

namespace Rivet.Platform.Windows.Cleaner;

/// <summary>Known folders from the shell (Downloads and Screenshots can be moved by the person).</summary>
public sealed class WindowsKnownFolders(AppPaths paths) : IKnownFolders
{
    private CleanerFolders? _folders;

    public CleanerFolders Folders => _folders ??= Build();

    private CleanerFolders Build()
    {
        string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        var profile = Special(Environment.SpecialFolder.UserProfile);
        var pictures = Special(Environment.SpecialFolder.MyPictures);
        return new CleanerFolders
        {
            Temp = Path.GetTempPath().TrimEnd('\\'),
            LocalAppData = Special(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = Special(Environment.SpecialFolder.ApplicationData),
            LocalLow = Known(PInvoke.FOLDERID_LocalAppDataLow) ?? Path.Combine(profile, "AppData", "LocalLow"),
            UserProfile = profile,
            ProgramData = Special(Environment.SpecialFolder.CommonApplicationData),
            Windows = Special(Environment.SpecialFolder.Windows),
            ProgramFiles = Special(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = NullIfEmpty(Special(Environment.SpecialFolder.ProgramFilesX86)),
            StartMenuPrograms = Known(PInvoke.FOLDERID_Programs) ?? Special(Environment.SpecialFolder.Programs),
            CommonStartMenuPrograms = Known(PInvoke.FOLDERID_CommonPrograms) ?? Special(Environment.SpecialFolder.CommonPrograms),
            Startup = Known(PInvoke.FOLDERID_Startup) ?? Special(Environment.SpecialFolder.Startup),
            CommonStartup = Known(PInvoke.FOLDERID_CommonStartup) ?? Special(Environment.SpecialFolder.CommonStartup),
            Desktop = Special(Environment.SpecialFolder.DesktopDirectory),
            CommonDesktop = Known(PInvoke.FOLDERID_PublicDesktop) ?? NullIfEmpty(Special(Environment.SpecialFolder.CommonDesktopDirectory)),
            Documents = Special(Environment.SpecialFolder.MyDocuments),
            Downloads = Known(PInvoke.FOLDERID_Downloads) ?? Path.Combine(profile, "Downloads"),
            Pictures = pictures,
            Music = NullIfEmpty(Special(Environment.SpecialFolder.MyMusic)),
            Videos = NullIfEmpty(Special(Environment.SpecialFolder.MyVideos)),
            Screenshots = Known(PInvoke.FOLDERID_Screenshots) ?? Path.Combine(pictures, "Screenshots"),
            OneDrive = NullIfEmpty(Environment.GetEnvironmentVariable("OneDrive")),
            OwnLocal = paths.LocalRoot,
            OwnRoaming = paths.RoamingRoot,
        };
    }

    private static unsafe string? Known(Guid id)
    {
        if (!PInvoke.SHGetKnownFolderPath(in id, KNOWN_FOLDER_FLAG.KF_FLAG_DONT_VERIFY, null, out var path).Succeeded)
        {
            return null;
        }

        try
        {
            return path.Value == null ? null : NullIfEmpty(new string(path.Value));
        }
        finally
        {
            PInvoke.CoTaskMemFree(path.Value);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Windows facilities of the Cleaner.</summary>
public sealed class WindowsCleanerPlatform(IProcessPlatform processes, IInstalledAppsProvider installedApps) : ICleanerPlatform
{
    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    private static readonly (RegistryHive Hive, RegistryView View, string Path)[] RunKeys =
    [
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
    ];

    public bool IsElevated => Environment.IsPrivilegedProcess;

    public unsafe RecycleBinInfo? QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)sizeof(SHQUERYRBINFO) };
        return PInvoke.SHQueryRecycleBin(null, ref info).Succeeded ? new RecycleBinInfo(info.i64NumItems, info.i64Size) : null;
    }

    public bool EmptyRecycleBin()
    {
        var result = PInvoke.SHEmptyRecycleBin(HWND.Null, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
        // An already empty bin reports a failure; what counts is that it is empty now.
        return result.Succeeded || QueryRecycleBin() is { Items: 0 };
    }

    public IReadOnlySet<string> RunningProcessNames() =>
        processes.Snapshot().Select(p => p.ImageName.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> RunningProcessPaths() =>
        processes.Snapshot()
            .Where(p => p.Identity.IsKnown)
            .Select(p => processes.ImagePath(p.Identity))
            .OfType<string>()
            .Select(p => p.ToLowerInvariant())
            .Distinct()
            .ToList();

    public bool? IsPackageFamilyInstalled(string familyName)
    {
        try
        {
            return new WinPackageManager().FindPackagesForUser(string.Empty, familyName).Any();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn("cleaner", $"Could not check package family {familyName}.", ex);
            return null;
        }
    }

    public IReadOnlyList<string> InstalledProgramLocations() =>
        installedApps.Enumerate().Select(a => a.InstallLocation).OfType<string>().ToList();

    public IReadOnlyList<StartupEntry> StartupEntries(bool includeMachine)
    {
        var entries = new List<StartupEntry>();
        foreach (var (hive, view, path) in RunKeys)
        {
            if (hive == RegistryHive.LocalMachine && !includeMachine)
            {
                continue;
            }

            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetValueNames())
                {
                    var kind = key.GetValueKind(name);
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
                        || key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command
                        || string.IsNullOrWhiteSpace(command) || name.Length == 0)
                    {
                        continue;
                    }

                    entries.Add(new StartupEntry
                    {
                        Hive = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE",
                        KeyPath = view == RegistryView.Registry32 ? path.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\", StringComparison.Ordinal) : path,
                        ValueName = name,
                        Command = command,
                        Is32BitView = view == RegistryView.Registry32,
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Debug("cleaner", $"Cannot read {path}: {ex.Message}");
            }
        }

        // The 32-bit view of HKCU is the same key: drop repeats.
        return entries.GroupBy(e => e.DisplayPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public bool RemoveStartupEntry(StartupEntry entry, string backupFolder)
    {
        try
        {
            var hive = entry.IsMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var view = entry.IsMachine ? (entry.Is32BitView ? RegistryView.Registry32 : RegistryView.Registry64) : RegistryView.Default;
            var path = entry.Is32BitView ? entry.KeyPath.Replace(@"SOFTWARE\WOW6432Node\", @"SOFTWARE\", StringComparison.OrdinalIgnoreCase) : entry.KeyPath;
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: true);
            if (key?.GetValue(entry.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string current
                || !string.Equals(current, entry.Command, StringComparison.Ordinal))
            {
                // Gone or changed since the scan: nothing is deleted.
                return false;
            }

            var kind = key.GetValueKind(entry.ValueName) == RegistryValueKind.ExpandString ? RegValueKind.ExpandString : RegValueKind.String;
            var text = RegFile.ForValues($@"{entry.Hive}\{entry.KeyPath}", [new RegValue(entry.ValueName, (int)kind, current)]);
            Directory.CreateDirectory(backupFolder);
            var file = Path.Combine(backupFolder, $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)} {Sanitize(entry.ValueName)}.reg");
            File.WriteAllBytes(file, RegFile.Encode(text));
            key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("cleaner", $"Could not remove the startup entry {entry.DisplayPath}.", ex);
            return false;
        }
    }

    public DriveKind DriveKindOf(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return DriveKind.Network;
            }

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return DriveKind.Unknown;
            }

            var drive = new DriveInfo(root);
            return drive.DriveType switch
            {
                DriveType.Fixed => DriveKind.Fixed,
                DriveType.Removable => DriveKind.Removable,
                DriveType.Network => DriveKind.Network,
                DriveType.CDRom => DriveKind.Optical,
                DriveType.NoRootDirectory => DriveKind.Unavailable,
                _ => DriveKind.Unknown,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return DriveKind.Unavailable;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 80 ? clean[..80] : clean;
    }
}

/// <summary>Reads the Zone.Identifier stream Windows attaches to downloaded files.</summary>
public sealed class WindowsWebMarkReader : IWebMarkReader
{
    public string? ReadZoneIdentifier(string path)
    {
        try
        {
            var stream = path + ":Zone.Identifier";
            var info = new FileInfo(stream);
            if (!info.Exists || info.Length > 16 * 1024)
            {
                return null;
            }

            return File.ReadAllText(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Resume from sleep and clock changes, from the app's hidden host window.</summary>
public sealed class WindowsClockEvents : ISystemClockEvents, IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmTimeChange = 0x001E;
    private const nuint PbtApmResumeSuspend = 0x7;
    private const nuint PbtApmResumeAutomatic = 0x12;

    private readonly IServiceProvider _services;
    private NativeWindowHost? _host;

    public WindowsClockEvents(IServiceProvider services)
    {
        _services = services;
    }

    public event EventHandler? Resumed
    {
        add
        {
            Attach();
            ResumedCore += value;
        }

        remove => ResumedCore -= value;
    }

    public event EventHandler? TimeChanged
    {
        add
        {
            Attach();
            TimeChangedCore += value;
        }

        remove => TimeChangedCore -= value;
    }

    private event EventHandler? ResumedCore;

    private event EventHandler? TimeChangedCore;

    public void Dispose() => _host?.RemoveHandler(OnMessage);

    private void Attach()
    {
        if (_host is not null)
        {
            return;
        }

        try
        {
            _host = _services.GetRequiredService<NativeWindowHost>();
            _host.AddHandler(OnMessage);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            // Without the host window, schedules still re-check every few minutes.
            Log.Warn("cleaner", "Wake and clock notifications are unavailable.", ex);
        }
    }

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == WmPowerBroadcast && wParam is PbtApmResumeAutomatic or PbtApmResumeSuspend)
        {
            ResumedCore?.Invoke(this, EventArgs.Empty);
        }
        else if (message == WmTimeChange)
        {
            TimeChangedCore?.Invoke(this, EventArgs.Empty);
        }

        return null;
    }
}

public sealed class CleanerRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<WindowsFileSystem>();
        services.AddSingleton<ICleanerFileSystem>(sp => sp.GetRequiredService<WindowsFileSystem>());
        services.AddSingleton<IRecycler, WindowsRecycler>();
        services.AddSingleton<IKnownFolders, WindowsKnownFolders>();
        services.AddSingleton<ICleanerPlatform, WindowsCleanerPlatform>();
        services.AddSingleton<IWebMarkReader, WindowsWebMarkReader>();
        services.AddSingleton<ISystemClockEvents, WindowsClockEvents>();
    }
}
