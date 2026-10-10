// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Launcher;

/// <summary>An installed app (Start menu shortcut or packaged app).</summary>
public sealed record InstalledApp
{
    /// <summary>App identity: the AppUserModelID for packaged apps, else the lower-case target path.</summary>
    public required string Identity { get; init; }

    public required string Name { get; init; }

    /// <summary>What to launch: a .lnk path or <c>shell:AppsFolder\&lt;AUMID&gt;</c>.</summary>
    public required string LaunchTarget { get; init; }

    /// <summary>A file whose shell icon represents the app (.lnk or .exe).</summary>
    public string? IconPath { get; init; }

    /// <summary>The executable or shortcut on disk ("Show in File Explorer").</summary>
    public string? RevealPath { get; init; }

    /// <summary>The lower-case executable a shortcut starts (to recognise the app's windows); null for packaged apps.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Other names the app answers to (file description, product name, shortcut name).</summary>
    public IReadOnlyList<string> AlternateNames { get; init; } = [];

    public bool IsPackaged { get; init; }
}

/// <summary>A titled top-level window of a running app.</summary>
public sealed record OpenWindow
{
    public required nint Handle { get; init; }

    public required string Title { get; init; }

    public required string AppName { get; init; }

    public string? AppIdentity { get; init; }

    public int ProcessId { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>The window's AppUserModelID (packaged apps), to recognise running packaged apps.</summary>
    public string? AppUserModelId { get; init; }
}

public sealed record FileHit(string Path, string Name, string Folder);

/// <summary>The figures the "Answers about this PC" rows show.</summary>
public sealed record SystemAnswers
{
    public int? BatteryPercent { get; init; }

    public bool Charging { get; init; }

    public bool PluggedIn { get; init; }

    public long MemoryUsed { get; init; }

    public long MemoryTotal { get; init; }

    public long StorageFree { get; init; }

    public long StorageTotal { get; init; }
}

public enum PowerAction
{
    Sleep,
    Restart,
    ShutDown,
    LogOut,
}

/// <summary>A known folder row (Downloads, Documents, …).</summary>
public sealed record KnownFolder(string Key, string Path);

/// <summary>
/// The OS side of the Command Bar (spec 06 §7.6): app discovery and launch,
/// open windows, file search, answers, power and known folders. Every call
/// that can be slow is async and cancellable; nothing is indexed by the app.
/// </summary>
public interface ICommandBarPlatform
{
    /// <summary>Start-menu shortcuts plus packaged apps; scanned off the UI thread.</summary>
    Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken cancellationToken);

    bool Launch(InstalledApp app);

    /// <summary>Visible, unowned, titled windows of other apps (EnumWindows).</summary>
    IReadOnlyList<OpenWindow> GetWindows();

    bool Activate(OpenWindow window);

    /// <summary>Asks an app to quit (WM_CLOSE to its top-level windows; it may still show a save dialog).</summary>
    bool CloseApp(int processId);

    /// <summary>
    /// Files whose name contains every word, inside <paramref name="folders"/>
    /// (Windows Search where indexed, else a bounded walk). Hidden items and
    /// ignored names are never returned.
    /// </summary>
    Task<IReadOnlyList<FileHit>> SearchFilesAsync(IReadOnlyList<string> folders, IReadOnlyList<string> words, IReadOnlyCollection<string> ignores, int max, CancellationToken cancellationToken);

    /// <summary>The user's recent files (the Recent folder), matching every word.</summary>
    Task<IReadOnlyList<FileHit>> RecentFilesAsync(IReadOnlyList<string> words, int max, CancellationToken cancellationToken);

    SystemAnswers ReadAnswers();

    bool Power(PowerAction action);

    IReadOnlyList<KnownFolder> KnownFolders();

    /// <summary>
    /// The text selected in the focused control of the app in front (UI
    /// Automation TextPattern), at most <paramref name="maxLength"/> characters.
    /// Null when nothing is selected, the control is a password field, or the
    /// app does not expose its text. Call before the bar takes the focus.
    /// </summary>
    Task<string?> ReadSelectionAsync(int maxLength, CancellationToken cancellationToken);

    /// <summary>
    /// The shell icon of a file, folder, shortcut or <c>shell:AppsFolder\&lt;AUMID&gt;</c>
    /// app at about <paramref name="sizePixels"/> square (premultiplied BGRA), or null.
    /// </summary>
    Task<PixelBuffer?> LoadIconAsync(string path, int sizePixels, CancellationToken cancellationToken);

    /// <summary>The user profile folder (expands <c>~</c>).</summary>
    string HomeFolder { get; }
}
