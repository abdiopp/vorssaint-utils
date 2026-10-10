// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.App;

/// <summary>
/// Where the app keeps its files. On Windows: settings and user content in
/// <c>%APPDATA%\&lt;Id&gt;</c>, caches, logs and recordings in
/// <c>%LOCALAPPDATA%\&lt;Id&gt;</c>. Tests and the macOS development build
/// redirect everything with the <c>&lt;ID&gt;_DATA_DIR</c> environment variable.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string roamingRoot, string localRoot)
    {
        RoamingRoot = roamingRoot;
        LocalRoot = localRoot;
    }

    /// <summary>Settings and user content that may follow the user (snippets, scratchpad, presets).</summary>
    public string RoamingRoot { get; }

    /// <summary>Machine-local data: caches, logs, clipboard images, recording takes.</summary>
    public string LocalRoot { get; }

    public string SettingsFile => Path.Combine(RoamingRoot, "settings.json");

    public string Logs => Path.Combine(LocalRoot, "logs");

    public string Cache => Path.Combine(LocalRoot, "cache");

    public string Temp => Path.Combine(LocalRoot, "temp");

    /// <summary>A per-feature folder under the roaming root, created on demand.</summary>
    public string RoamingFolder(string name) => EnsureDirectory(Path.Combine(RoamingRoot, name));

    /// <summary>A per-feature folder under the local root, created on demand.</summary>
    public string LocalFolder(string name) => EnsureDirectory(Path.Combine(LocalRoot, name));

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static string DataDirectoryOverrideVariable => $"{AppIdentity.Id.ToUpperInvariant()}_DATA_DIR";

    /// <summary>The standard locations for the current user.</summary>
    public static AppPaths ForCurrentUser()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return new AppPaths(Path.Combine(overrideRoot, "roaming"), Path.Combine(overrideRoot, "local"));
        }

        var id = AppIdentity.Id;
        if (OperatingSystem.IsWindows())
        {
            return new AppPaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), id),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), id));
        }

        // Development runs on macOS/Linux keep their own folder so they never
        // mix with anything real.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".local", "share", $"{id}-dev");
        return new AppPaths(Path.Combine(root, "roaming"), Path.Combine(root, "local"));
    }

    /// <summary>A throwaway location, used by tests.</summary>
    public static AppPaths ForTemporaryDirectory(string root) =>
        new(Path.Combine(root, "roaming"), Path.Combine(root, "local"));
}
