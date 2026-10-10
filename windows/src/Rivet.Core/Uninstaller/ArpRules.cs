// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Rivet.Core.App;
using Rivet.Core.Maintenance.Cleaner;

namespace Rivet.Core.Maintenance.Uninstaller;

/// <summary>One raw Add/Remove Programs entry (the values of a ...\Uninstall\&lt;key&gt; key).</summary>
public sealed record ArpEntry(string KeyName, RegistryScope Scope, IReadOnlyDictionary<string, object?> Values)
{
    public string? String(string name) =>
        Values.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public long? Number(string name) => Values.TryGetValue(name, out var value) ? value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    } : null;
}

/// <summary>An uninstaller to start: program, arguments, and whether it is msiexec.</summary>
public sealed record UninstallInvocation(string FileName, string Arguments, bool IsMsi);

/// <summary>
/// Which Add/Remove Programs entries are offered, and how their values are
/// read (spec §3.2.11 app list). Pure functions so the rules are unit tested.
/// </summary>
public static partial class ArpRules
{
    private static readonly HashSet<string> HelperFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "uninstall", "uninstaller", "uninst", "_uninst", "installer", "setup", "x64", "x86", "app",
    };

    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update", "Hotfix", "Security Update", "Service Pack", "ServicePack",
    };

    public static bool IsProductCode(string? value) => value is not null && ProductCodePattern().IsMatch(value);

    /// <summary>
    /// The app an entry describes, or null when it is not offered: hidden
    /// system components, updates and hotfixes, entries without a name or a
    /// way to uninstall, and this app itself.
    /// </summary>
    public static InstalledApp? ToApp(ArpEntry entry, IEnumerable<string> trustedRoots, string? ownInstallFolder = null)
    {
        var name = entry.String("DisplayName");
        if (name is null || entry.Number("SystemComponent") == 1 || entry.String("ParentKeyName") is not null)
        {
            return null;
        }

        if (entry.String("ReleaseType") is { } releaseType && UpdateReleaseTypes.Contains(releaseType))
        {
            return null;
        }

        var isMsi = entry.Number("WindowsInstaller") == 1 && IsProductCode(entry.KeyName);
        var uninstall = entry.String("UninstallString");
        if (uninstall is null && !isMsi)
        {
            return null;
        }

        if (string.Equals(name, AppIdentity.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var roots = trustedRoots.ToList();
        var location = CleanFolder(entry.String("InstallLocation"));
        if (location is null)
        {
            location = DeriveLocation(entry.String("DisplayIcon"), roots) ?? DeriveLocation(uninstall, roots);
        }

        if (ownInstallFolder is not null && location is not null && SafePaths.Equal(location, ownInstallFolder))
        {
            return null;
        }

        var size = entry.Number("EstimatedSize");
        return new InstalledApp
        {
            Key = $"arp:{entry.Scope}:{entry.KeyName}",
            Kind = isMsi ? InstalledAppKind.Msi : InstalledAppKind.Win32,
            DisplayName = name,
            Publisher = entry.String("Publisher"),
            Version = entry.String("DisplayVersion"),
            InstallDate = ParseInstallDate(entry.String("InstallDate")),
            InstallLocation = location,
            Icon = entry.String("DisplayIcon"),
            SizeBytes = size is > 0 and < 64L * 1024 * 1024 * 1024 ? size * 1024 : null,
            UninstallString = uninstall,
            QuietUninstallString = entry.String("QuietUninstallString"),
            ProductCode = isMsi ? entry.KeyName : null,
            Scope = entry.Scope,
            RegistryKeyName = entry.KeyName,
        };
    }

    /// <summary>"20240315" → 2024-03-15; anything else → null.</summary>
    public static DateOnly? ParseInstallDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Drops duplicate entries (the same app registered in two views), keeping the first.</summary>
    public static IReadOnlyList<InstalledApp> Deduplicate(IEnumerable<InstalledApp> apps) =>
        apps.GroupBy(a => (a.DisplayName.ToLowerInvariant(), a.Version ?? string.Empty, a.InstallLocation is null ? string.Empty : SafePaths.Normalize(a.InstallLocation).ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// The uninstaller to run. MSI products run <c>msiexec /x {ProductCode}</c>
    /// (the entry's own string is often <c>/I</c>, which opens a repair dialog);
    /// quiet mode adds <c>/qb</c> or uses QuietUninstallString. Paths may be
    /// quoted or not, with or without arguments.
    /// </summary>
    public static UninstallInvocation? Invocation(InstalledApp app, bool quiet, Func<string, bool> fileExists, Func<string, string> expand)
    {
        if (app.Kind == InstalledAppKind.Msix)
        {
            return null;
        }

        var msiCode = app.Kind == InstalledAppKind.Msi && IsProductCode(app.ProductCode) ? app.ProductCode : null;
        var command = quiet && app.QuietUninstallString is { } q ? q : app.UninstallString;
        if (msiCode is null && command is not null && MsiExecPattern().Match(command) is { Success: true } match)
        {
            msiCode = match.Groups["code"].Value;
        }

        if (msiCode is not null)
        {
            return new UninstallInvocation("msiexec.exe", $"/x {msiCode.ToUpperInvariant()}" + (quiet ? " /qb" : string.Empty), true);
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = expand(command).Trim();
        string program;
        string arguments;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            program = close < 0 ? text.Trim('"') : text[1..close];
            arguments = close < 0 ? string.Empty : text[(close + 1)..].Trim();
        }
        else
        {
            (program, arguments) = SplitUnquoted(text, fileExists);
        }

        return program.Length == 0 ? null : new UninstallInvocation(program, arguments, false);
    }

    /// <summary>The program's folder when it lies in a trusted install root (Program Files, %LOCALAPPDATA%\Programs).</summary>
    public static string? DeriveLocation(string? commandOrIcon, IReadOnlyList<string> trustedRoots)
    {
        if (string.IsNullOrWhiteSpace(commandOrIcon))
        {
            return null;
        }

        var text = commandOrIcon.Trim();
        string path;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            path = close < 0 ? text.Trim('"') : text[1..close];
        }
        else
        {
            var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? text[..(exe + 4)] : text.Split(',')[0];
        }

        if (!SafePaths.IsAbsolute(path) || SafePaths.Parent(path) is not { } folder)
        {
            return null;
        }

        foreach (var root in trustedRoots)
        {
            if (!SafePaths.IsInside(folder, root))
            {
                continue;
            }

            // Uninstallers and binaries often sit one folder below the install folder.
            var candidate = SafePaths.Normalize(folder);
            while (HelperFolders.Contains(SafePaths.FileName(candidate))
                   && SafePaths.Parent(candidate) is { } parent && SafePaths.IsInside(parent, root))
            {
                candidate = parent;
            }

            return candidate;
        }

        return null;
    }

    private static string? CleanFolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().Trim('"').Trim();
        return text.Length < 3 || !SafePaths.IsAbsolute(text) ? null : SafePaths.Normalize(text);
    }

    private static (string Program, string Arguments) SplitUnquoted(string text, Func<string, bool> fileExists)
    {
        // "C:\Program Files\App\uninst.exe /S": the shortest prefix ending in .exe that exists,
        // else the shortest ending in .exe, else the first token.
        string? firstExe = null;
        for (var index = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); index >= 0; index = text.IndexOf(".exe", index + 4, StringComparison.OrdinalIgnoreCase))
        {
            var end = index + 4;
            if (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                continue;
            }

            var candidate = text[..end];
            firstExe ??= candidate;
            if (fileExists(candidate))
            {
                return (candidate, text[end..].Trim());
            }
        }

        if (firstExe is not null)
        {
            return (firstExe, text[firstExe.Length..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCodePattern();

    [GeneratedRegex(@"(?i)msiexec(?:\.exe)?""?\s.*?/[ix]\s*(?<code>\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\})", RegexOptions.CultureInvariant)]
    private static partial Regex MsiExecPattern();
}
