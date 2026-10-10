// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Rivet.Core.Maintenance.Cleaner;

/// <summary>A browser whose HTTP caches the Cleaner can clear.</summary>
public sealed record BrowserDefinition(string Name, string ProcessName, string UserDataPath, bool IsChromium);

/// <summary>
/// A fixed cache folder (path relative to a known folder). The description is
/// for code readers; the list shows the folder name and its parent path.
/// </summary>
public sealed record CacheLocation(string Description, Func<CleanerFolders, string> Path, bool Recommended, string? SkipWhileRunning = null, CleanerCategory Category = CleanerCategory.Caches);

/// <summary>
/// The Windows category rules (spec §3.1.9): which locations, which entries,
/// what starts checked. Pure functions over paths and names so they are
/// unit tested without Windows.
/// </summary>
public static partial class CleanerRules
{
    /// <summary>Temporary files younger than this are left alone (they may still be in use).</summary>
    public static readonly TimeSpan TempMinimumAge = TimeSpan.FromHours(24);

    /// <summary>Per-user install folders touched in the last week are never called leftovers (an install may be running).</summary>
    public static readonly TimeSpan ProgramsFolderMinimumAge = TimeSpan.FromDays(7);

    public static readonly IReadOnlyList<BrowserDefinition> Browsers =
    [
        new("Microsoft Edge", "msedge.exe", @"Microsoft\Edge\User Data", true),
        new("Google Chrome", "chrome.exe", @"Google\Chrome\User Data", true),
        new("Brave", "brave.exe", @"BraveSoftware\Brave-Browser\User Data", true),
        new("Vivaldi", "vivaldi.exe", @"Vivaldi\User Data", true),
        new("Firefox", "firefox.exe", @"Mozilla\Firefox\Profiles", false),
    ];

    /// <summary>Per-profile Chromium cache folders that rebuild on their own (checked).</summary>
    public static readonly IReadOnlyList<string> ChromiumProfileCaches = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    /// <summary>Browser-wide Chromium shader caches (checked).</summary>
    public static readonly IReadOnlyList<string> ChromiumSharedCaches = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache"];

    /// <summary>Offline web-app data: safe, but sites re-download it (unchecked).</summary>
    public const string ChromiumServiceWorkerCache = @"Service Worker\CacheStorage";

    public static readonly IReadOnlyList<string> FirefoxProfileCaches = ["cache2", "startupCache"];

    /// <summary>Single cache folders of the user. Checked ones rebuild silently; unchecked ones cost a re-download.</summary>
    public static readonly IReadOnlyList<CacheLocation> UserCaches =
    [
        new("DirectX shader cache", f => Path.Combine(f.LocalAppData, "D3DSCache"), true),
        new("NVIDIA DirectX cache", f => Path.Combine(f.LocalAppData, "NVIDIA", "DXCache"), true),
        new("NVIDIA OpenGL cache", f => Path.Combine(f.LocalAppData, "NVIDIA", "GLCache"), true),
        new("NVIDIA shader cache", f => Path.Combine(f.LocalAppData, "NVIDIA Corporation", "NV_Cache"), true),
        new("AMD DirectX cache", f => Path.Combine(f.LocalAppData, "AMD", "DxCache"), true),
        new("AMD DXC cache", f => Path.Combine(f.LocalAppData, "AMD", "DxcCache"), true),
        new("AMD OpenGL cache", f => Path.Combine(f.LocalAppData, "AMD", "GLCache"), true),
        new("AMD Vulkan cache", f => Path.Combine(f.LocalAppData, "AMD", "VkCache"), true),
        new("Intel shader cache", f => Path.Combine(f.LocalLow, "Intel", "ShaderCache"), true),
        new("npm", f => Path.Combine(f.LocalAppData, "npm-cache"), true, "node.exe"),
        new("pip", f => Path.Combine(f.LocalAppData, "pip", "Cache"), true),
        new("Yarn", f => Path.Combine(f.LocalAppData, "Yarn", "Cache"), true, "node.exe"),
        new("NuGet HTTP cache", f => Path.Combine(f.LocalAppData, "NuGet", "v3-cache"), true),
        new("Spotify offline storage", f => Path.Combine(f.LocalAppData, "Spotify", "Storage"), false, "spotify.exe"),
        new("Gradle", f => Path.Combine(f.UserProfile, ".gradle", "caches"), false, "java.exe", CleanerCategory.Developer),
        new("NuGet packages", f => Path.Combine(f.UserProfile, ".nuget", "packages"), false, "dotnet.exe", CleanerCategory.Developer),
    ];

    /// <summary>Children of INetCache that hold copies of documents an app may still have open.</summary>
    public static readonly IReadOnlySet<string> InetCacheExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content.Outlook", "Content.MSO", "Content.Word", "Low",
    };

    /// <summary>Default names Windows gives screenshots: Win+PrtScn and the Snipping Tool's auto-save.</summary>
    public static bool IsDefaultScreenshotName(string name, DateTime createdLocal)
    {
        if (NumberedScreenshotPattern().IsMatch(name))
        {
            return true;
        }

        var dated = DatedScreenshotPattern().Match(name);
        if (!dated.Success
            || !DateTime.TryParseExact(dated.Groups["stamp"].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            return false;
        }

        // The stamp must match when the file was created: a copied or renamed file does not qualify.
        return Math.Abs((stamp - createdLocal).TotalMinutes) <= 5;
    }

    /// <summary>
    /// Forgotten: neither created nor modified for <paramref name="days"/>.
    /// Windows cannot tell whether a screenshot was opened (last-access
    /// updates are usually off), unlike macOS.
    /// </summary>
    public static bool IsForgotten(FsEntry entry, int days, DateTime nowUtc) =>
        days > 0 && nowUtc - entry.TouchedUtc >= TimeSpan.FromDays(days);

    /// <summary>A package family name: "Name_publisherid" with a 13-character Crockford base32 publisher id.</summary>
    public static bool IsPackageFamilyName(string name) => PackageFamilyPattern().IsMatch(name);

    /// <summary>Top-level temp entries untouched for 24 h (the newest write inside a folder counts).</summary>
    public static bool IsStaleTemp(DateTime newestWriteUtc, DateTime createdUtc, DateTime nowUtc)
    {
        var touched = newestWriteUtc > createdUtc ? newestWriteUtc : createdUtc;
        return nowUtc - touched >= TempMinimumAge;
    }

    /// <summary>
    /// The files a startup command runs: the program, or for a wrapper
    /// (rundll32, cmd /c, PowerShell -File, wscript/cscript) the file it
    /// hands over. Empty when the command is not an absolute path (it may be
    /// found through PATH or App Paths, so it is never proven missing).
    /// </summary>
    public static IReadOnlyList<string> ExecutablesOf(string command, Func<string, string> expand, Func<string, bool> exists)
    {
        var text = expand(command).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var (program, rest) = SplitProgram(text, exists);
        if (program.Length == 0 || !SafePaths.IsAbsolute(program))
        {
            return [];
        }

        var fileName = SafePaths.FileName(program).ToLowerInvariant();
        switch (fileName)
        {
            case "rundll32.exe" or "rundll32":
                var dll = FirstArgument(rest, stopAtComma: true);
                return dll is not null && SafePaths.IsAbsolute(dll) ? [dll] : [];
            case "cmd.exe" or "cmd":
                var c = Regex.Match(rest, @"(?i)(?:^|\s)/[ck]\s+(.*)$");
                return c.Success ? ExecutablesOf(c.Groups[1].Value.Trim().Trim('"'), expand, exists) : [];
            case "powershell.exe" or "pwsh.exe":
                var file = Regex.Match(rest, "(?i)-file\\s+(\"[^\"]+\"|\\S+)");
                if (file.Success)
                {
                    var script = file.Groups[1].Value.Trim('"');
                    return SafePaths.IsAbsolute(script) ? [script] : [];
                }

                return [];
            case "wscript.exe" or "cscript.exe":
                var target = FirstArgument(rest, stopAtComma: false);
                return target is not null && SafePaths.IsAbsolute(target) ? [target] : [];
            default:
                return [program];
        }
    }

    /// <summary>
    /// A startup entry is orphaned only when every file it runs is missing
    /// from a fixed drive (an unplugged or network drive proves nothing) and
    /// none of them belongs to Windows itself.
    /// </summary>
    public static bool IsOrphanedCommand(IReadOnlyList<string> executables, Func<string, bool> exists, Func<string, DriveKind> driveKind, string windowsFolder)
    {
        if (executables.Count == 0)
        {
            return false;
        }

        foreach (var executable in executables)
        {
            if (SafePaths.IsInside(executable, windowsFolder) || exists(executable) || driveKind(executable) != DriveKind.Fixed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// "Device Name" and "Last Backup Date" from an iTunes/Apple Devices
    /// backup's Info.plist (XML plists; binary ones yield null).
    /// </summary>
    public static (string? Device, DateTime? LastBackup) ReadBackupInfo(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.TrimStart().StartsWith('<'))
        {
            return (null, null);
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            var document = XDocument.Load(reader);
            var dict = document.Root?.Element("dict");
            if (dict is null)
            {
                return (null, null);
            }

            string? device = null;
            DateTime? date = null;
            var elements = dict.Elements().ToList();
            for (var i = 0; i + 1 < elements.Count; i++)
            {
                if (elements[i].Name != "key")
                {
                    continue;
                }

                var key = elements[i].Value;
                var value = elements[i + 1];
                if (key == "Device Name" && value.Name == "string")
                {
                    device = value.Value.Trim();
                }
                else if (key == "Last Backup Date" && value.Name == "date"
                         && DateTime.TryParse(value.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    date = parsed;
                }
            }

            return (string.IsNullOrEmpty(device) ? null : device, date);
        }
        catch (XmlException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// A per-user install folder (%LOCALAPPDATA%\Programs\X) is a leftover
    /// when no Add/Remove Programs entry points at or inside it, it holds no
    /// executable any more, and nothing changed there for a week.
    /// </summary>
    public static bool IsOrphanedProgramsFolder(string folder, IEnumerable<string> installLocations, bool containsExecutable, DateTime newestWriteUtc, DateTime nowUtc)
    {
        if (containsExecutable || nowUtc - newestWriteUtc < ProgramsFolderMinimumAge)
        {
            return false;
        }

        foreach (var location in installLocations)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            if (SafePaths.Equal(location, folder) || SafePaths.IsInside(location, folder) || SafePaths.IsInside(folder, location))
            {
                return false;
            }
        }

        return true;
    }

    private static (string Program, string Remainder) SplitProgram(string text, Func<string, bool> exists)
    {
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? (text.Trim('"'), string.Empty) : (text[1..close], text[(close + 1)..].Trim());
        }

        // Unquoted paths with spaces ("C:\Program Files\App\app.exe /x"): the
        // shortest prefix that ends in .exe or exists wins.
        for (var i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
        {
            var prefix = text[..i];
            if (prefix.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exists(prefix))
            {
                return (prefix, text[(i + 1)..].Trim());
            }
        }

        var space = text.IndexOf(' ');
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exists(text) || space < 0)
        {
            return (text, string.Empty);
        }

        return (text[..space], text[(space + 1)..].Trim());
    }

    private static string? FirstArgument(string rest, bool stopAtComma)
    {
        var text = rest.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string value;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            value = close < 0 ? text.Trim('"') : text[1..close];
        }
        else
        {
            var end = text.IndexOfAny(stopAtComma ? [' ', ','] : [' ']);
            value = end < 0 ? text : text[..end];
        }

        if (stopAtComma)
        {
            var comma = value.IndexOf(',');
            if (comma >= 0)
            {
                value = value[..comma];
            }
        }

        return value.Length == 0 ? null : value;
    }

    [GeneratedRegex(@"^Screenshot \(\d{1,6}\)\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedScreenshotPattern();

    [GeneratedRegex(@"^Screenshot (?<stamp>\d{4}-\d{2}-\d{2} \d{6})(?: \(\d{1,4}\))?\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex DatedScreenshotPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{1,49}_[a-hj-km-np-tv-z0-9]{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageFamilyPattern();
}
