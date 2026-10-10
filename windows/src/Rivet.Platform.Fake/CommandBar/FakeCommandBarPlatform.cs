// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Clipboard;
using Rivet.Core.Launcher;
using Rivet.Core.Modules;

namespace Rivet.Platform.Fake.Launcher;

/// <summary>
/// Sample apps and windows for the development build; file search walks the
/// configured folders for real (bounded) so the feature can be tried on any OS.
/// </summary>
public sealed class FakeCommandBarPlatform : ICommandBarPlatform
{
    public List<string> Launched { get; } = [];

    public List<PowerAction> PowerRequests { get; } = [];

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InstalledApp>>(
        [
            App("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", packaged: true),
            App("Microsoft Edge", "c:\\program files (x86)\\microsoft\\edge\\application\\msedge.exe", alternate: ["Edge", "Browser"]),
            App("Notepad", "c:\\windows\\system32\\notepad.exe"),
            App("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App", packaged: true),
            App("Visual Studio Code", "c:\\users\\me\\appdata\\local\\programs\\microsoft vs code\\code.exe", alternate: ["Code"]),
            App("Windows Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", packaged: true, alternate: ["Terminal"]),
            App("File Explorer", "c:\\windows\\explorer.exe", alternate: ["Explorer"]),
            App("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", packaged: true),
        ]);

    private static InstalledApp App(string name, string identity, bool packaged = false, IReadOnlyList<string>? alternate = null) => new()
    {
        Identity = identity,
        Name = name,
        LaunchTarget = packaged ? "shell:AppsFolder\\" + identity : identity,
        IsPackaged = packaged,
        AlternateNames = alternate ?? [],
        RevealPath = packaged ? null : identity,
        ExecutablePath = packaged ? null : identity,
    };

    public bool Launch(InstalledApp app)
    {
        Launched.Add(app.LaunchTarget);
        Rivet.Core.Diagnostics.Log.Info("commandBar", $"[fake] launch {app.Name}");
        return true;
    }

    public IReadOnlyList<OpenWindow> GetWindows() =>
    [
        new() { Handle = 0x101, Title = "Quarterly report.docx - Word", AppName = "Word", AppIdentity = "c:\\program files\\microsoft office\\root\\office16\\winword.exe", ProcessId = 501 },
        new() { Handle = 0x102, Title = "Inbox - Outlook", AppName = "Outlook", AppIdentity = "c:\\program files\\microsoft office\\root\\office16\\outlook.exe", ProcessId = 502 },
        new() { Handle = 0x103, Title = "notes.txt - Notepad", AppName = "Notepad", AppIdentity = "c:\\windows\\system32\\notepad.exe", ProcessId = 4242 },
    ];

    public bool Activate(OpenWindow window)
    {
        Rivet.Core.Diagnostics.Log.Info("commandBar", $"[fake] activate window {window.Handle}");
        return true;
    }

    public bool CloseApp(int processId)
    {
        Rivet.Core.Diagnostics.Log.Info("commandBar", $"[fake] close process {processId}");
        return true;
    }

    public Task<IReadOnlyList<FileHit>> SearchFilesAsync(IReadOnlyList<string> folders, IReadOnlyList<string> words, IReadOnlyCollection<string> ignores, int max, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<FileHit>>(() =>
        {
            var hits = new List<FileHit>();
            var folded = words.Select(TextFold.ForCommand).ToList();
            foreach (var folder in folders.Where(Directory.Exists))
            {
                var stack = new Stack<(string Path, int Depth)>();
                stack.Push((folder, 0));
                while (stack.Count > 0 && hits.Count < max)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (current, depth) = stack.Pop();
                    IEnumerable<string> entries;
                    try
                    {
                        entries = Directory.EnumerateFileSystemEntries(current).ToList();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        var name = Path.GetFileName(entry);
                        if (CommandBarPreferences.IsIgnored(name, ignores))
                        {
                            continue;
                        }

                        if (Directory.Exists(entry))
                        {
                            if (depth < 6)
                            {
                                stack.Push((entry, depth + 1));
                            }
                        }

                        var foldedName = TextFold.ForCommand(name);
                        if (folded.All(w => foldedName.Contains(w, StringComparison.Ordinal)))
                        {
                            hits.Add(new FileHit(entry, name, Path.GetDirectoryName(entry) ?? folder));
                        }
                    }
                }
            }

            return hits.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(h => h.Path, StringComparer.Ordinal).Take(max).ToList();
        }, cancellationToken);

    public Task<IReadOnlyList<FileHit>> RecentFilesAsync(IReadOnlyList<string> words, int max, CancellationToken cancellationToken)
    {
        var samples = new[] { "Budget 2026.xlsx", "Quarterly report.docx", "Holiday photos.zip", "notes.txt" };
        var folded = words.Select(TextFold.ForCommand).ToList();
        IReadOnlyList<FileHit> hits = samples
            .Where(n => folded.All(w => TextFold.ForCommand(n).Contains(w, StringComparison.Ordinal)))
            .Select(n => new FileHit(Path.Combine(HomeFolder, "Documents", n), n, Path.Combine(HomeFolder, "Documents")))
            .Take(max)
            .ToList();
        return Task.FromResult(hits);
    }

    public SystemAnswers ReadAnswers() => new()
    {
        BatteryPercent = 87,
        Charging = false,
        PluggedIn = false,
        MemoryUsed = 9_800_000_000,
        MemoryTotal = 16_000_000_000,
        StorageFree = 312_000_000_000,
        StorageTotal = 1_000_000_000_000,
    };

    public bool Power(PowerAction action)
    {
        PowerRequests.Add(action);
        Rivet.Core.Diagnostics.Log.Info("commandBar", $"[fake] power {action}");
        return true;
    }

    public IReadOnlyList<KnownFolder> KnownFolders()
    {
        var home = HomeFolder;
        return
        [
            new("downloads", Path.Combine(home, "Downloads")),
            new("documents", Path.Combine(home, "Documents")),
            new("desktop", Path.Combine(home, "Desktop")),
            new("videos", Path.Combine(home, "Videos")),
            new("pictures", Path.Combine(home, "Pictures")),
            new("music", Path.Combine(home, "Music")),
            new("home", home),
        ];
    }

    public string HomeFolder => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>What <see cref="ReadSelectionAsync"/> reports (tests and the development build).</summary>
    public string? Selection { get; set; }

    public Task<string?> ReadSelectionAsync(int maxLength, CancellationToken cancellationToken) =>
        Task.FromResult(Selection is { } text && text.Length > maxLength ? text[..maxLength] : Selection);

    /// <summary>No shell icons off Windows: rows show their glyph.</summary>
    public Task<Rivet.Core.Platform.PixelBuffer?> LoadIconAsync(string path, int sizePixels, CancellationToken cancellationToken) =>
        Task.FromResult<Rivet.Core.Platform.PixelBuffer?>(null);
}

public sealed class FakeCommandBarRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<FakeCommandBarPlatform>();
        services.AddSingleton<ICommandBarPlatform>(sp => sp.GetRequiredService<FakeCommandBarPlatform>());
    }
}
