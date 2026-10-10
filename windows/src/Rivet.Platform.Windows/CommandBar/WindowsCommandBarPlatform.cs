// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Clipboard;
using Rivet.Core.Diagnostics;
using Rivet.Core.Launcher;
using Rivet.Core.Modules;
using Rivet.Core.Platform;
using Rivet.Platform.Windows.Clipboard;
using Windows.Management.Deployment;
using Windows.Storage.Search;
using StorageFolder = Windows.Storage.StorageFolder;
using FileAttributes = System.IO.FileAttributes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Com;
using Windows.Win32.System.Power;
using Windows.Win32.System.Shutdown;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rivet.Platform.Windows.Launcher;

/// <summary>
/// The Command Bar's Windows side (spec 06 §7.6). Apps: Start-menu shortcuts
/// (both Start menu folders) plus packaged apps from
/// Windows.Management.Deployment, launched through <c>shell:AppsFolder</c>.
/// Files: Windows.Storage queries, which use the Windows Search index where
/// the folder is indexed and enumerate otherwise (chosen over the OLE DB
/// Search.CollatorDSO provider because it needs no extra package and handles
/// non-indexed folders); a bounded walk is the last resort.
/// </summary>
public sealed class WindowsCommandBarPlatform : ICommandBarPlatform
{
    private static readonly string[] SkippedShortcutWords = ["uninstall", "readme", "release notes", "help", "documentation", "license", "website"];

    public string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ── Apps ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken)
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        await Task.Run(() =>
        {
            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     })
            {
                var programs = Path.Combine(folder, "Programs");
                if (!Directory.Exists(programs))
                {
                    continue;
                }

                IEnumerable<string> shortcuts;
                try
                {
                    shortcuts = Directory.EnumerateFiles(programs, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var shortcut in shortcuts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(shortcut);
                    var lower = name.ToLowerInvariant();
                    if (SkippedShortcutWords.Any(w => lower.Contains(w, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    apps.TryAdd(name, new InstalledApp
                    {
                        Identity = shortcut.ToLowerInvariant(),
                        Name = name,
                        LaunchTarget = shortcut,
                        IconPath = shortcut,
                        RevealPath = shortcut,
                        ExecutablePath = ShellInterop.ShortcutTarget(shortcut),
                    });
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var app in await PackagedAppsAsync(cancellationToken).ConfigureAwait(false))
            {
                apps.TryAdd(app.Name, app);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("commandBar", "Packaged apps could not be listed.", ex);
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<List<InstalledApp>> PackagedAppsAsync(CancellationToken cancellationToken)
    {
        var result = new List<InstalledApp>();
        var manager = new global::Windows.Management.Deployment.PackageManager();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle)
                {
                    continue;
                }

                foreach (var entry in await package.GetAppListEntriesAsync())
                {
                    var name = entry.DisplayInfo.DisplayName;
                    var aumid = entry.AppUserModelId;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new InstalledApp
                    {
                        Identity = aumid,
                        Name = name,
                        LaunchTarget = "shell:AppsFolder\\" + aumid,
                        IconPath = "shell:AppsFolder\\" + aumid,
                        IsPackaged = true,
                        AlternateNames = package.DisplayName is { Length: > 0 } displayName && displayName != name ? [displayName] : [],
                    });
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
            {
                // Some system packages refuse to describe themselves; skip them.
            }
        }

        return result;
    }

    public bool Launch(InstalledApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.LaunchTarget) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn("commandBar", $"Could not open {app.Name}.", ex);
            return false;
        }
    }

    // ── Windows ────────────────────────────────────────────────────────

    public unsafe IReadOnlyList<OpenWindow> GetWindows()
    {
        var windows = new List<(HWND Hwnd, uint Pid)>();
        WNDENUMPROC callback = (hwnd, _) =>
        {
            if (WindowsForegroundService.IsAppWindow(hwnd))
            {
                uint pid;
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                if (pid != 0 && pid != Environment.ProcessId)
                {
                    windows.Add((hwnd, pid));
                }
            }

            return true;
        };
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);

        var identities = new Dictionary<uint, ProcessIdentity?>();
        var result = new List<OpenWindow>(windows.Count);
        var buffer = new char[512];
        foreach (var (hwnd, pid) in windows)
        {
            int length;
            fixed (char* p = buffer)
            {
                length = PInvoke.GetWindowText(hwnd, new PWSTR(p), buffer.Length);
            }

            if (length <= 0)
            {
                continue;
            }

            if (!identities.TryGetValue(pid, out var identity))
            {
                identities[pid] = identity = ProcessIdentity.Of((int)pid);
            }

            var title = new string(buffer, 0, length);
            var appName = identity?.Name ?? string.Empty;
            if (string.Equals(title, appName, StringComparison.OrdinalIgnoreCase))
            {
                continue; // a title that only repeats the app name says nothing
            }

            result.Add(new OpenWindow
            {
                Handle = (nint)hwnd.Value,
                Title = title,
                AppName = appName,
                AppIdentity = identity?.Identity,
                ProcessId = (int)pid,
                ExecutablePath = identity?.Path,
                AppUserModelId = ShellInterop.WindowAppId(hwnd, pid),
            });
        }

        return result;
    }

    public unsafe bool Activate(OpenWindow window)
    {
        var hwnd = new HWND((void*)window.Handle);
        if (PInvoke.IsIconic(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        if (PInvoke.SetForegroundWindow(hwnd))
        {
            return true;
        }

        var foreground = PInvoke.GetForegroundWindow();
        var foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
        var thisThread = PInvoke.GetCurrentThreadId();
        if (foregroundThread == thisThread || !PInvoke.AttachThreadInput(thisThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            PInvoke.BringWindowToTop(hwnd);
            return PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            PInvoke.AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    public unsafe bool CloseApp(int processId)
    {
        var targets = new List<HWND>();
        WNDENUMPROC callback = (hwnd, _) =>
        {
            uint pid;
            PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            if (pid == processId && PInvoke.IsWindowVisible(hwnd))
            {
                targets.Add(hwnd);
            }

            return true;
        };
        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        foreach (var hwnd in targets)
        {
            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
        }

        return targets.Count > 0;
    }

    // ── Files ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FileHit>> SearchFilesAsync(IReadOnlyList<string> folders, IReadOnlyList<string> words, IReadOnlyCollection<string> ignores, int max, CancellationToken cancellationToken)
    {
        var folded = words.Select(TextFold.ForCommand).Where(w => w.Length > 0).ToList();
        if (folded.Count == 0)
        {
            return [];
        }

        var hits = new List<FileHit>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                hits.AddRange(await IndexedSearchAsync(folder, words, folded, ignores, max, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or IOException)
            {
                Log.Debug("commandBar", $"Windows Search query failed ({ex.GetType().Name}); walking the folder instead.");
                hits.AddRange(Walk(folder, folded, ignores, max, cancellationToken));
            }

            if (hits.Count >= max)
            {
                break;
            }
        }

        return hits
            .DistinctBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static async Task<List<FileHit>> IndexedSearchAsync(string folder, IReadOnlyList<string> words, IReadOnlyList<string> folded, IReadOnlyCollection<string> ignores, int max, CancellationToken cancellationToken)
    {
        var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);
        var options = new QueryOptions(CommonFileQuery.DefaultQuery, ["*"])
        {
            FolderDepth = FolderDepth.Deep,
            IndexerOption = IndexerOption.UseIndexerWhenAvailable,
            // AQS: every word must occur in the file name (~= is "contains"); quotes are stripped from user words.
            UserSearchFilter = string.Join(' ', words.Select(w => $"System.FileName:~=\"{w.Replace("\"", string.Empty, StringComparison.Ordinal)}\"")),
        };
        var query = storageFolder.CreateFileQueryWithOptions(options);
        var files = await query.GetFilesAsync(0, (uint)Math.Min(1000, max * 5));
        var hits = new List<FileHit>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.Path;
            if (string.IsNullOrEmpty(path) || !Offerable(path, folder, ignores))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            var foldedName = TextFold.ForCommand(name);
            if (folded.All(w => foldedName.Contains(w, StringComparison.Ordinal)))
            {
                hits.Add(new FileHit(path, name, Path.GetDirectoryName(path) ?? folder));
            }
        }

        return hits;
    }

    /// <summary>Not hidden, not system, not inside a hidden or ignored folder.</summary>
    private static bool Offerable(string path, string root, IReadOnlyCollection<string> ignores)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, path);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (CommandBarPreferences.IsIgnored(part, ignores))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<FileHit> Walk(string folder, IReadOnlyList<string> folded, IReadOnlyCollection<string> ignores, int max, CancellationToken cancellationToken)
    {
        var hits = new List<FileHit>();
        var visited = 0;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((folder, 0));
        while (stack.Count > 0 && hits.Count < max && visited < 50_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (current, depth) = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                visited++;
                var name = Path.GetFileName(entry);
                if (CommandBarPreferences.IsIgnored(name, ignores))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (depth < 8)
                    {
                        stack.Push((entry, depth + 1));
                    }

                    continue;
                }

                var foldedName = TextFold.ForCommand(name);
                if (folded.All(w => foldedName.Contains(w, StringComparison.Ordinal)))
                {
                    hits.Add(new FileHit(entry, name, Path.GetDirectoryName(entry) ?? folder));
                }
            }
        }

        return hits;
    }

    public Task<IReadOnlyList<FileHit>> RecentFilesAsync(IReadOnlyList<string> words, int max, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FileHit>>(() =>
        {
            var folded = words.Select(TextFold.ForCommand).Where(w => w.Length > 0).ToList();
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (folded.Count == 0 || !Directory.Exists(recent))
            {
                return [];
            }

            return new DirectoryInfo(recent).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(300)
                .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f.Name)))
                .Where(x => folded.All(w => TextFold.ForCommand(x.Name).Contains(w, StringComparison.Ordinal)))
                .Take(max)
                .Select(x => new FileHit(x.File.FullName, x.Name, recent))
                .ToList();
        }, cancellationToken);

    // ── Answers, power, folders ────────────────────────────────────────

    public SystemAnswers ReadAnswers()
    {
        var answers = new SystemAnswers();
        if (PInvoke.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent <= 100)
        {
            answers = answers with
            {
                BatteryPercent = power.BatteryLifePercent,
                Charging = (power.BatteryFlag & 8) != 0,
                PluggedIn = power.ACLineStatus == 1,
            };
        }

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (PInvoke.GlobalMemoryStatusEx(ref memory))
        {
            answers = answers with { MemoryTotal = (long)memory.ullTotalPhys, MemoryUsed = (long)(memory.ullTotalPhys - memory.ullAvailPhys) };
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        if (PInvoke.GetDiskFreeSpaceEx(systemRoot, out var available, out var total, out _))
        {
            answers = answers with { StorageFree = (long)available, StorageTotal = (long)total };
        }

        return answers;
    }

    public bool Power(PowerAction action)
    {
        switch (action)
        {
            case PowerAction.Sleep:
                return PInvoke.SetSuspendState(false, false, false);
            case PowerAction.LogOut:
                return PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_LOGOFF, SHUTDOWN_REASON.SHTDN_REASON_FLAG_PLANNED);
            default:
                EnableShutdownPrivilege();
                var flags = action == PowerAction.Restart ? EXIT_WINDOWS_FLAGS.EWX_REBOOT : EXIT_WINDOWS_FLAGS.EWX_POWEROFF;
                return PInvoke.ExitWindowsEx(flags, SHUTDOWN_REASON.SHTDN_REASON_FLAG_PLANNED);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SinglePrivilege
    {
        public uint Count;
        public LUID Luid;
        public uint Attributes;
    }

    private static unsafe void EnableShutdownPrivilege()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
        {
            return;
        }

        try
        {
            if (!PInvoke.LookupPrivilegeValue(null, PInvoke.SE_SHUTDOWN_NAME, out var luid))
            {
                return;
            }

            var privilege = new SinglePrivilege { Count = 1, Luid = luid, Attributes = 2 }; // SE_PRIVILEGE_ENABLED
            using var safe = new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)token.Value, ownsHandle: false);
            PInvoke.AdjustTokenPrivileges(safe, false, (TOKEN_PRIVILEGES*)&privilege, default);
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }

    public IReadOnlyList<KnownFolder> KnownFolders() =>
    [
        new("downloads", KnownFolderPath(PInvoke.FOLDERID_Downloads) ?? Path.Combine(HomeFolder, "Downloads")),
        new("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        new("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        new("videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        new("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        new("music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
        new("home", HomeFolder),
    ];

    // ── Icons ──────────────────────────────────────────────────────────

    public Task<PixelBuffer?> LoadIconAsync(string path, int sizePixels, CancellationToken cancellationToken) =>
        ShellInterop.IconAsync(path, sizePixels, cancellationToken);

    // ── Selection ──────────────────────────────────────────────────────

    public Task<string?> ReadSelectionAsync(int maxLength, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ReadSelection(maxLength));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Info("commandBar", $"Reading the selection failed ({ex.GetType().Name}).");
                completion.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "Rivet selection read",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        cancellationToken.Register(() => completion.TrySetResult(null));
        return completion.Task;
    }

    /// <summary>UIA: focused element → TextPattern → first selected range (never password fields or this app).</summary>
    private static string? ReadSelection(int maxLength)
    {
        PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        try
        {
            var automation = (IUIAutomation)new CUIAutomation();
            var element = automation.GetFocusedElement();
            if (element is null || element.CurrentIsPassword || element.CurrentProcessId == Environment.ProcessId)
            {
                return null;
            }

            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is not IUIAutomationTextPattern pattern)
            {
                return null;
            }

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return null;
            }

            var bstr = ranges.GetElement(0).GetText(maxLength);
            string? text;
            try
            {
                text = bstr.ToString();
            }
            finally
            {
                PInvoke.SysFreeString(bstr);
            }

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    private static unsafe string? KnownFolderPath(Guid id)
    {
        PWSTR path;
        if (PInvoke.SHGetKnownFolderPath(&id, 0, HANDLE.Null, &path).Failed)
        {
            return null;
        }

        try
        {
            return path.ToString();
        }
        finally
        {
            PInvoke.CoTaskMemFree(path.Value);
        }
    }
}

public sealed class WindowsCommandBarRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services) =>
        services.AddSingleton<ICommandBarPlatform, WindowsCommandBarPlatform>();
}
