// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Platform;

/// <summary>Integer rectangle in physical (device) pixels, virtual-screen coordinates.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PixelPoint p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);
        return x2 > x1 && y2 > y1 ? new PixelRect(x1, y1, x2 - x1, y2 - y1) : default;
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x1 = Math.Min(X, other.X);
        var y1 = Math.Min(Y, other.Y);
        return new PixelRect(x1, y1, Math.Max(Right, other.Right) - x1, Math.Max(Bottom, other.Bottom) - y1);
    }
}

public readonly record struct PixelPoint(int X, int Y);

/// <summary>One monitor as Windows reports it.</summary>
public sealed record ScreenInfo
{
    /// <summary>Stable id for the session (the device name, e.g. <c>\\.\DISPLAY1</c>).</summary>
    public required string Id { get; init; }

    public required string FriendlyName { get; init; }

    /// <summary>Full monitor area in physical pixels.</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>Area without the taskbar and docked app bars, in physical pixels.</summary>
    public required PixelRect WorkArea { get; init; }

    /// <summary>DPI scale (1.0 = 100 %, 1.5 = 150 %).</summary>
    public required double Scale { get; init; }

    public required bool IsPrimary { get; init; }
}
