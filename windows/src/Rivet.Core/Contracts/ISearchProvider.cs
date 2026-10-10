// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Contracts;

public sealed record SearchResult
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>Fluent icon name, or null to use <see cref="IconPath"/>.</summary>
    public string? Icon { get; init; }

    /// <summary>A file whose shell icon to show (apps, files).</summary>
    public string? IconPath { get; init; }

    /// <summary>Higher sorts first.</summary>
    public double Score { get; init; }

    public string? Category { get; init; }

    public required Func<Task> Activate { get; init; }
}

/// <summary>A source of Command Bar results (apps, files, clipboard, snippets, actions, ...).</summary>
public interface ISearchProvider
{
    string Id { get; }

    /// <summary>The feature that must be installed for this provider to run.</summary>
    string FeatureId { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}
