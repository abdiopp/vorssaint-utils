// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Contracts;

public enum CaptureKind
{
    Screenshot,
    Recording,
    Gif,
}

public sealed record CaptureRecord
{
    public required string Id { get; init; }

    public required CaptureKind Kind { get; init; }

    /// <summary>The saved file (may have been moved or deleted since).</summary>
    public required string FilePath { get; init; }

    public string? ThumbnailPath { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public TimeSpan? Duration { get; init; }
}

/// <summary>Ids of the shared actions the screenshot module registers for the history.</summary>
public static class RecentCaptureActions
{
    /// <summary>Shows the Recent captures palette.</summary>
    public const string ShowPalette = "captures.recent";
}

/// <summary>The shared history of screenshots and recordings. Owned by the screenshot module.</summary>
public interface IRecentCaptures
{
    IReadOnlyList<CaptureRecord> Items { get; }

    void Add(CaptureRecord record);

    void Remove(string id);

    event EventHandler? Changed;
}
