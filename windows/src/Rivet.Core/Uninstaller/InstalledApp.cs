// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Maintenance.Uninstaller;

public enum InstalledAppKind
{
    /// <summary>An Add/Remove Programs entry with an uninstaller executable.</summary>
    Win32,

    /// <summary>A Windows Installer product (removed with msiexec /x {ProductCode}).</summary>
    Msi,

    /// <summary>An MSIX/AppX package installed for the current user.</summary>
    Msix,
}

/// <summary>Which Uninstall key an entry came from.</summary>
public enum RegistryScope
{
    Machine64,
    Machine32,
    User,
}

/// <summary>An installed app as Add/Remove Programs or the package manager reports it.</summary>
public sealed record InstalledApp
{
    /// <summary>Stable id: "arp:&lt;scope&gt;:&lt;key name&gt;" or "msix:&lt;family name&gt;".</summary>
    public required string Key { get; init; }

    public required InstalledAppKind Kind { get; init; }

    public required string DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public DateOnly? InstallDate { get; init; }

    /// <summary>Install folder (from the entry, or derived from its icon or uninstaller path).</summary>
    public string? InstallLocation { get; init; }

    /// <summary><c>DisplayIcon</c> value ("path,index") or the package logo file.</summary>
    public string? Icon { get; init; }

    /// <summary>EstimatedSize × 1024, or the measured package folder size.</summary>
    public long? SizeBytes { get; init; }

    public string? UninstallString { get; init; }

    public string? QuietUninstallString { get; init; }

    /// <summary>MSI ProductCode (the Uninstall key name in braces).</summary>
    public string? ProductCode { get; init; }

    public RegistryScope? Scope { get; init; }

    /// <summary>Name of the subkey under ...\CurrentVersion\Uninstall.</summary>
    public string? RegistryKeyName { get; init; }

    public string? PackageFullName { get; init; }

    public string? PackageFamilyName { get; init; }

    public bool IsPerMachine => Scope is RegistryScope.Machine64 or RegistryScope.Machine32;

    public bool CanUninstall => Kind switch
    {
        InstalledAppKind.Msix => PackageFullName is not null,
        InstalledAppKind.Msi => ProductCode is not null || UninstallString is not null,
        _ => UninstallString is not null,
    };

    public bool HasQuietUninstall => Kind == InstalledAppKind.Msi || !string.IsNullOrWhiteSpace(QuietUninstallString);
}

/// <summary>Installed apps of this PC for the current user (ARP 64/32-bit, per-user, MSIX).</summary>
public interface IInstalledAppsProvider
{
    IReadOnlyList<InstalledApp> Enumerate();

    /// <summary>A small icon for the app (32 px), or null.</summary>
    PixelBuffer? LoadIcon(InstalledApp app, int size = 32);
}
