// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Rivet.Core.Maintenance.Cleaner;

namespace Rivet.Core.Maintenance.Uninstaller;

/// <summary>Name tokens and the matching rules of leftovers (spec §3.2.3–3.2.5, adapted to Windows).</summary>
public static partial class AppTokens
{
    /// <summary>Words that never identify an app (macOS role words plus Windows ones and generic folder names).</summary>
    public static readonly IReadOnlySet<string> RoleWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "app", "mac", "macos", "osx", "helper", "agent", "daemon", "service", "desktop", "client", "launcher", "plugin",
        "extension", "web", "free", "pro", "lite", "plus", "ui", "xpc", "login", "updater", "installer", "renderer", "gpu",
        "worker", "crashpad", "broker", "utility", "alert", "network", "audio", "server", "shared", "core", "common",
        "framework", "bundle", "process", "handler", "tool", "runtime", "electron", "java", "python", "node", "mono", "wine",
        "setup", "uninstall", "update", "x64", "x86", "win32", "win64",
        "windows", "microsoft", "data", "cache", "caches", "log", "logs", "temp", "tmp", "settings", "config", "user",
        "users", "local", "roaming", "program", "programs", "files", "software", "system", "bin", "lib", "tools",
        "application", "applications", "default", "profile", "profiles", "backup", "plugins", "addins", "assets",
    };

    private static readonly HashSet<string> CorporateWords = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "corporation", "corp", "llc", "ltd", "limited", "gmbh", "ag", "bv", "sa", "sas", "sarl",
        "co", "company", "pty", "sro", "ab", "oy", "aps", "as", "kg", "spa", "srl", "plc", "kk", "the", "software",
        "technologies", "technology", "studios", "studio", "games", "labs", "systems", "group", "foundation",
        "contributors", "team", "project", "community", "development", "international", "holdings",
    };

    /// <summary>Unicode letters and digits only, lower case, minus a trailing ".exe".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = text.Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>A usable token: at least three characters and not a role word.</summary>
    public static bool IsToken(string normalized) => normalized.Length >= 3 && !RoleWords.Contains(normalized) && !normalized.All(char.IsDigit);

    /// <summary>"Mozilla Firefox (x64 en-US)" → "Mozilla Firefox"; "Foo 2024" → "Foo"; "Bar Nightly" → "Bar".</summary>
    public static string CleanDisplayName(string name)
    {
        var text = ParenthesizedTail().Replace(name.Trim(), string.Empty).Trim();
        var previous = string.Empty;
        while (previous != text)
        {
            previous = text;
            text = VersionTail().Replace(text, string.Empty).Trim();
        }

        return text.Length == 0 ? name.Trim() : text;
    }

    /// <summary>"Mozilla Corporation" → "mozilla"; "The Git Development Community" → "git"; "JetBrains s.r.o." → "jetbrains".</summary>
    public static string? PublisherToken(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var words = Regex.Split(publisher.ToLowerInvariant(), @"[\s,]+")
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        while (words.Count > 1 && CorporateWords.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        while (words.Count > 1 && CorporateWords.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        var token = string.Concat(words);
        return IsToken(token) ? token : null;
    }

    /// <summary>The display name without its leading publisher word(s): "Google Chrome" by Google → "Chrome".</summary>
    public static string? ProductName(string displayName, string? publisherToken)
    {
        if (publisherToken is null)
        {
            return null;
        }

        var words = CleanDisplayName(displayName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var take = 1; take < words.Length; take++)
        {
            if (Normalize(string.Concat(words.Take(take))) == publisherToken)
            {
                return string.Join(' ', words.Skip(take));
            }
        }

        return null;
    }

    /// <summary>"com.vendor.app" style names are technical ids, not display names.</summary>
    public static bool LooksReverseDns(string name) => ReverseDns().IsMatch(name);

    /// <summary>Crash-report naming: "_YYYY-MM-DD" or "-YYYY-MM-DD".</summary>
    public static bool LooksCrashDump(string name) => CrashDumpDate().IsMatch(name);

    /// <summary>Helper executables that never name the app ("unins000.exe", "Update.exe", "crashpad_handler.exe").</summary>
    public static bool IsHelperExecutable(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.StartsWith("unins", StringComparison.Ordinal) || lower.StartsWith("uninst", StringComparison.Ordinal)
               || lower.StartsWith("setup", StringComparison.Ordinal) || lower.StartsWith("update", StringComparison.Ordinal)
               || lower.StartsWith("crash", StringComparison.Ordinal) || lower.StartsWith("elevat", StringComparison.Ordinal)
               || lower.Contains("helper", StringComparison.Ordinal) || lower.StartsWith("notification", StringComparison.Ordinal)
               || lower is "squirrel.exe" or "createdump.exe" or "vc_redist.x64.exe" or "vc_redist.x86.exe";
    }

    /// <summary>
    /// A name matches the app: its normalized form equals a token (related),
    /// or — for names that are neither reverse-DNS nor crash-report shaped —
    /// starts with a token of at least five characters.
    /// </summary>
    public static string? Match(string name, IReadOnlySet<string> tokens)
    {
        if (name.StartsWith('.') || name.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = Normalize(System.IO.Path.HasExtension(name) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.LastIndexOf('.') > name.Length - 6
            ? System.IO.Path.GetFileNameWithoutExtension(name)
            : name);
        if (normalized.Length < 3)
        {
            return null;
        }

        if (tokens.Contains(normalized))
        {
            return normalized;
        }

        if (LooksReverseDns(name) || LooksCrashDump(name))
        {
            return null;
        }

        return tokens.Where(t => t.Length >= 5 && normalized.StartsWith(t, StringComparison.Ordinal)).OrderByDescending(t => t.Length).FirstOrDefault();
    }

    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex ParenthesizedTail();

    [GeneratedRegex(@"(?i)\s+(?:v?\d+(?:\.\d+)*|nightly|beta|alpha|dev|canary|preview|insider|stable|release|rc|lts|developer edition|technology preview|x64|x86|64-bit|32-bit)\s*$")]
    private static partial Regex VersionTail();

    [GeneratedRegex(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+){2,}$")]
    private static partial Regex ReverseDns();

    [GeneratedRegex(@"[_-]\d{4}-\d{2}-\d{2}")]
    private static partial Regex CrashDumpDate();
}

/// <summary>
/// Who the selected app is (spec §3.2.11 identity): its trusted install
/// folder, name tokens, executables, publisher and package family, and the
/// tokens of every other installed app for the exclusivity rule.
/// </summary>
public sealed class AppFingerprint
{
    private AppFingerprint(InstalledApp app)
    {
        App = app;
    }

    public InstalledApp App { get; }

    /// <summary>The install folder, when it lies in a trusted root (only then are leftovers claimed).</summary>
    public string? InstallFolder { get; private init; }

    /// <summary>Leftovers may be claimed: a trusted install folder, or an MSIX package.</summary>
    public bool Trusted { get; private init; }

    public IReadOnlySet<string> Tokens { get; private init; } = new HashSet<string>();

    /// <summary>Main executables ("code.exe"), lower case.</summary>
    public IReadOnlySet<string> Executables { get; private init; } = new HashSet<string>();

    public string? Publisher { get; private init; }

    /// <summary>Another installed app has the same publisher: vendor folders are offered only at product level.</summary>
    public bool PublisherShared { get; private init; }

    /// <summary>Tokens of all other installed apps: a token they share is no evidence.</summary>
    public IReadOnlySet<string> OtherTokens { get; private init; } = new HashSet<string>();

    public static AppFingerprint Build(InstalledApp app, IReadOnlyList<InstalledApp> installed, CleanerFolders folders, ICleanerFileSystem fs)
    {
        var trustedRoots = folders.TrustedInstallRoots().ToList();
        var folder = app.InstallLocation is { } location && trustedRoots.Any(root => SafePaths.IsInside(location, root)) ? SafePaths.Normalize(location) : null;
        var executables = folder is null ? new HashSet<string>() : MainExecutables(folder, fs);
        var publisher = AppTokens.PublisherToken(app.Publisher);
        var tokens = TokensOf(app, publisher, folder, executables);
        var others = installed.Where(a => a.Key != app.Key).ToList();
        var otherTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var other in others)
        {
            var otherFolder = other.InstallLocation is { } otherLocation && trustedRoots.Any(root => SafePaths.IsInside(otherLocation, root)) ? otherLocation : null;
            otherTokens.UnionWith(TokensOf(other, AppTokens.PublisherToken(other.Publisher), otherFolder, new HashSet<string>()));
        }

        return new AppFingerprint(app)
        {
            InstallFolder = folder,
            Trusted = folder is not null || app.Kind == InstalledAppKind.Msix,
            Tokens = tokens,
            Executables = executables,
            Publisher = publisher,
            PublisherShared = publisher is not null && others.Any(o => AppTokens.PublisherToken(o.Publisher) == publisher),
            OtherTokens = otherTokens,
        };
    }

    /// <summary>Tokens that identify this app and no other installed one.</summary>
    public IReadOnlySet<string> ExclusiveTokens => Tokens.Where(t => !OtherTokens.Contains(t)).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> TokensOf(InstalledApp app, string? publisher, string? folder, IReadOnlySet<string> executables)
    {
        var candidates = new List<string?>
        {
            app.DisplayName,
            AppTokens.CleanDisplayName(app.DisplayName),
            AppTokens.ProductName(app.DisplayName, publisher),
            folder is null ? null : SafePaths.FileName(folder),
        };
        candidates.AddRange(executables);
        if (app.PackageFamilyName is { } family)
        {
            var name = family.Split('_')[0];
            candidates.Add(name.Split('.')[^1]);
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var normalized = AppTokens.Normalize(candidate);
            if (AppTokens.IsToken(normalized) && normalized != publisher)
            {
                tokens.Add(normalized);
            }
        }

        return tokens;
    }

    private static HashSet<string> MainExecutables(string folder, ICleanerFileSystem fs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string path, int depth)
        {
            foreach (var entry in fs.List(path))
            {
                if (result.Count >= 32 || entry.IsReparsePoint)
                {
                    return;
                }

                if (!entry.IsDirectory && entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !AppTokens.IsHelperExecutable(entry.Name))
                {
                    result.Add(entry.Name.ToLowerInvariant());
                }
                else if (entry.IsDirectory && depth > 0 && entry.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    Collect(entry.Path, depth - 1);
                }
            }
        }

        Collect(folder, 1);
        return result;
    }
}
