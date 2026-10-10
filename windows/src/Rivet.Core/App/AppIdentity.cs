// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Rivet.Core.Update;

namespace Rivet.Core.App;

/// <summary>
/// Product identity baked in at build time from <c>Directory.Build.props</c>.
/// Nothing else in the code base should hard-code the product name.
/// </summary>
public static class AppIdentity
{
    static AppIdentity()
    {
        var assembly = typeof(AppIdentity).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Value is not null)
            .GroupBy(a => a.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value!);

        DisplayName = metadata.GetValueOrDefault("ProductDisplayName", "Rivet");
        Id = metadata.GetValueOrDefault("ProductId", "Rivet");
        Company = metadata.GetValueOrDefault("ProductCompany", DisplayName);
        UpdateRepository = metadata.GetValueOrDefault("ProductUpdateRepository", string.Empty);
        UpdateAssetPrefix = metadata.GetValueOrDefault("ProductUpdateAssetPrefix", Id);

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = informational.IndexOf('+');
        VersionString = plus >= 0 ? informational[..plus] : informational;
        Version = SemVer.TryParse(VersionString, out var parsed) ? parsed : new SemVer(0, 0, 0);
    }

    /// <summary>User-visible product name.</summary>
    public static string DisplayName { get; }

    /// <summary>Stable machine identifier: data folders, mutex and pipe names, registry values.</summary>
    public static string Id { get; }

    public static string Company { get; }

    /// <summary><c>owner/repo</c> on GitHub whose releases feed the updater.</summary>
    public static string UpdateRepository { get; }

    /// <summary>Release asset names start with this prefix: <c>&lt;prefix&gt;-&lt;version&gt;-win-&lt;arch&gt;-setup.exe</c>.</summary>
    public static string UpdateAssetPrefix { get; }

    public static SemVer Version { get; }

    public static string VersionString { get; }

    public static bool IsPrerelease => Version.IsPrerelease;

    /// <summary>AppUserModelID used for toasts, the taskbar and the Start menu shortcut.</summary>
    public static string AppUserModelId => $"{Id}.Desktop";

    /// <summary>Name of the per-session single-instance mutex.</summary>
    public static string InstanceMutexName => $"Local\\{Id}.Instance";

    /// <summary>Name of the named pipe a second launch uses to talk to the running instance.</summary>
    public static string InstancePipeName => $"{Id}.Instance.{Environment.UserName}";
}
