// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.PackageManager;

/// <summary>A package row from <c>winget list</c>, <c>upgrade</c> or <c>search</c>.</summary>
public sealed record WingetPackage
{
    public const char Ellipsis = '…';

    public required string Name { get; init; }

    /// <summary>Package identifier ("Git.Git", "9NBLGGH4NNS1"), or a local id ("ARP\Machine\X64\…", "MSIX\…").</summary>
    public required string Id { get; init; }

    public string Version { get; init; } = string.Empty;

    /// <summary>Newer version in the source, when winget knows one.</summary>
    public string? Available { get; init; }

    /// <summary>Source name ("winget", "msstore"); empty for local entries winget could not match.</summary>
    public string? Source { get; init; }

    /// <summary>Search only: why it matched ("Moniker: vscode", "Tag: editor").</summary>
    public string? Match { get; init; }

    /// <summary>
    /// <c>winget upgrade</c> lists pinned packages and packages that require
    /// explicit targeting in a second table; they are not offered by default.
    /// </summary>
    public bool RequiresExplicitUpgrade { get; init; }

    /// <summary>winget shortened the name to fit its table (ends with "…").</summary>
    public bool NameTruncated => Name.EndsWith(Ellipsis);

    /// <summary>winget shortened the id: it cannot be used with <c>--id</c> until resolved.</summary>
    public bool IdTruncated => Id.EndsWith(Ellipsis);

    public bool VersionTruncated => Version.EndsWith(Ellipsis) || (Available?.EndsWith(Ellipsis) ?? false);

    /// <summary>Installed entry that winget could not match to any source.</summary>
    public bool IsLocal => string.IsNullOrEmpty(Source);

    public bool IsStore => string.Equals(Source, "msstore", StringComparison.OrdinalIgnoreCase);

    public bool HasUpdate => !string.IsNullOrWhiteSpace(Available);
}

/// <summary>Details from <c>winget show</c>: every "Key: value" line, plus the ones the UI uses.</summary>
public sealed record WingetDetails
{
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    public string? Description { get; init; }

    public string? Homepage { get; init; }

    public string? License { get; init; }

    /// <summary>All top-level fields in order (localized keys stay as winget printed them).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; } = [];
}

/// <summary>A configured winget source.</summary>
public sealed record WingetSource(string Name, string Argument);

/// <summary>Whether winget can be used here.</summary>
public enum WingetAvailability
{
    Unknown,
    Missing,
    Available,
}
