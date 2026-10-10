// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Core.Modules.Shelf;

/// <summary>Which outer edge a drag is dwelling near.</summary>
public readonly record struct ShelfEdgeMatch(string ScreenId, bool Left);

/// <summary>
/// Shelf placement and hit geometry (spec 07 §3.1.7–3.1.9, §6.1). Inputs and
/// outputs are physical pixels; DIP constants are multiplied by the monitor scale.
/// </summary>
public static class ShelfGeometry
{
    // ── Tile grid ──────────────────────────────────────────────────────

    /// <summary>max(1, ⌊(width − 8 + 10) / 88⌋): 3 in the 304-wide card.</summary>
    public static int Columns(double contentWidth) =>
        Math.Max(1, (int)Math.Floor((contentWidth - 8 + ShelfConstants.TileSpacing) / (ShelfConstants.TileWidth + ShelfConstants.TileSpacing)));

    public static int Rows(int count, int columns) => count <= 0 ? 0 : (count + columns - 1) / columns;

    /// <summary>Tile i in DIPs: index 3 → (4, 102), index 6 → (4, 200) with 3 columns.</summary>
    public static (double X, double Y, double Width, double Height) TileFrame(int index, int columns)
    {
        var col = index % columns;
        var row = index / columns;
        return (ShelfConstants.TileInset + (col * (ShelfConstants.TileWidth + ShelfConstants.TileSpacing)),
                ShelfConstants.TileInset + (row * (ShelfConstants.TileHeight + ShelfConstants.TileSpacing)),
                ShelfConstants.TileWidth,
                ShelfConstants.TileHeight);
    }

    /// <summary>Height of the tile grid content for <paramref name="count"/> tiles.</summary>
    public static double GridHeight(int count, int columns) =>
        count == 0 ? 0 : (2 * ShelfConstants.TileInset) + (Rows(count, columns) * ShelfConstants.TileHeight) + ((Rows(count, columns) - 1) * ShelfConstants.TileSpacing);

    // ── Classic card ───────────────────────────────────────────────────

    /// <summary>The card hangs 16 DIP below the pointer, centred, kept 8 DIP inside the work area.</summary>
    public static PixelPoint SummonPosition(PixelPoint pointer, int width, int height, PixelRect work, double scale)
    {
        var margin = (int)Math.Round(ShelfConstants.SummonClamp * scale);
        var x = pointer.X - (width / 2);
        var y = pointer.Y + (int)Math.Round(ShelfConstants.SummonOffset * scale);
        return Clamp(x, y, width, height, work, margin);
    }

    public static PixelPoint Clamp(int x, int y, int width, int height, PixelRect work, int margin)
    {
        var maxX = Math.Max(work.X + margin, work.Right - width - margin);
        var maxY = Math.Max(work.Y + margin, work.Bottom - height - margin);
        return new PixelPoint(Math.Clamp(x, work.X + margin, maxX), Math.Clamp(y, work.Y + margin, maxY));
    }

    // ── Docked shelf ───────────────────────────────────────────────────

    public enum TaskbarEdge
    {
        Bottom,
        Top,
        Left,
        Right,
    }

    /// <summary>Which side of the monitor the taskbar takes (bottom when it auto-hides).</summary>
    public static TaskbarEdge EdgeOf(ScreenInfo screen)
    {
        var b = screen.Bounds;
        var w = screen.WorkArea;
        if (w.Bottom < b.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (w.Y > b.Y)
        {
            return TaskbarEdge.Top;
        }

        if (w.X > b.X)
        {
            return TaskbarEdge.Left;
        }

        return w.Right < b.Right ? TaskbarEdge.Right : TaskbarEdge.Bottom;
    }

    /// <summary>
    /// The docked window frame. Tray placement: centred on the tray icon (or
    /// work.right − w − 12 without a trustworthy anchor), clamped 8 DIP from the
    /// work-area sides, sitting 4 DIP from the taskbar. Top centre: centred,
    /// 4 DIP below the top of the work area. The anchored edge stays fixed as
    /// the content grows.
    /// </summary>
    public static PixelRect DockFrame(ShelfDockPlacement placement, int width, int height, ScreenInfo screen, PixelRect? trayIcon)
    {
        var s = screen.Scale <= 0 ? 1 : screen.Scale;
        var work = screen.WorkArea;
        var inset = (int)Math.Round(ShelfConstants.DockInset * s);
        var side = (int)Math.Round(ShelfConstants.DockSideClamp * s);
        if (placement == ShelfDockPlacement.TopCenter)
        {
            return new PixelRect(work.X + ((work.Width - width) / 2), work.Y + inset, width, height);
        }

        var anchor = TrustedAnchor(trayIcon, screen);
        var edge = EdgeOf(screen);
        int x, y;
        if (edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            x = edge == TaskbarEdge.Left ? work.X + inset : work.Right - width - inset;
            y = anchor is { } a ? a.Y + (a.Height / 2) - (height / 2) : work.Bottom - height - (int)Math.Round(ShelfConstants.DockNoAnchorInset * s);
            y = Math.Clamp(y, work.Y + side, Math.Max(work.Y + side, work.Bottom - height - side));
            return new PixelRect(x, y, width, height);
        }

        x = anchor is { } icon ? icon.X + (icon.Width / 2) - (width / 2) : work.Right - width - (int)Math.Round(ShelfConstants.DockNoAnchorInset * s);
        x = Math.Clamp(x, work.X + side, Math.Max(work.X + side, work.Right - width - side));
        y = edge == TaskbarEdge.Top ? work.Y + inset : work.Bottom - height - inset;
        return new PixelRect(x, y, width, height);
    }

    /// <summary>The tray icon counts only when it lies on the monitor (not in the overflow flyout).</summary>
    public static PixelRect? TrustedAnchor(PixelRect? trayIcon, ScreenInfo screen) =>
        trayIcon is { } icon && !icon.IsEmpty && !icon.Intersect(screen.Bounds).IsEmpty ? icon : null;

    /// <summary>
    /// Pill → card needs the pointer inside this frame for 150 ms: the docked
    /// frame padded 16 DIP, unioned with the tray icon padded 16 DIP horizontally
    /// (tray placement). Without a frame yet, an estimated 72×32 pill is used.
    /// </summary>
    public static PixelRect TriggerFrame(PixelRect? dockFrame, ShelfDockPlacement placement, ScreenInfo screen, PixelRect? trayIcon)
    {
        var s = screen.Scale <= 0 ? 1 : screen.Scale;
        var margin = (int)Math.Round(ShelfConstants.DockTriggerMargin * s);
        var frame = dockFrame ?? DockFrame(placement, (int)Math.Round(ShelfConstants.FallbackPillWidth * s), (int)Math.Round(ShelfConstants.FallbackPillHeight * s), screen, trayIcon);
        var trigger = Inflate(frame, margin, margin);
        if (placement == ShelfDockPlacement.Tray && TrustedAnchor(trayIcon, screen) is { } icon)
        {
            trigger = trigger.Union(Inflate(icon, margin, 0));
        }

        return trigger;
    }

    /// <summary>Card → pill when the pointer leaves the card frame padded 32 DIP.</summary>
    public static PixelRect RetreatFrame(PixelRect cardFrame, double scale)
    {
        var margin = (int)Math.Round(ShelfConstants.DockRetreatMargin * scale);
        return Inflate(cardFrame, margin, margin);
    }

    public static PixelRect Inflate(PixelRect rect, int dx, int dy) =>
        new(rect.X - dx, rect.Y - dy, rect.Width + (2 * dx), rect.Height + (2 * dy));

    /// <summary>Contains, with the right/bottom edges inclusive (pointer on the last pixel row still counts).</summary>
    public static bool ContainsInclusive(PixelRect rect, PixelPoint point) =>
        point.X >= rect.X && point.X <= rect.Right && point.Y >= rect.Y && point.Y <= rect.Bottom;

    // ── Edge peek ──────────────────────────────────────────────────────

    /// <summary>
    /// An outer left/right edge within 200 DIP of the pointer: the pointer must be
    /// inside the work area horizontally (not over a side taskbar) and no other
    /// monitor may sit beyond the edge (a seam between monitors is never an edge).
    /// Screens are tried by distance from the pointer; top and bottom edges never count.
    /// </summary>
    public static ShelfEdgeMatch? MatchEdge(PixelPoint pointer, IReadOnlyList<ScreenInfo> screens)
    {
        foreach (var screen in screens.OrderBy(s => Distance(s.Bounds, pointer)))
        {
            var s = screen.Scale <= 0 ? 1 : screen.Scale;
            var trigger = (int)Math.Round(ShelfConstants.EdgeTrigger * s);
            var frame = screen.Bounds;
            if (!ContainsInclusive(Inflate(frame, trigger, trigger), pointer))
            {
                continue;
            }

            var probe = trigger + 1;
            if (pointer.X <= frame.X + trigger && pointer.X >= screen.WorkArea.X
                && !HasNeighbor(screens, screen, new PixelPoint(frame.X - probe, pointer.Y)))
            {
                return new ShelfEdgeMatch(screen.Id, Left: true);
            }

            if (pointer.X >= frame.Right - trigger && pointer.X <= screen.WorkArea.Right
                && !HasNeighbor(screens, screen, new PixelPoint(frame.Right + probe, pointer.Y)))
            {
                return new ShelfEdgeMatch(screen.Id, Left: false);
            }
        }

        return null;
    }

    /// <summary>Retract when the pointer is no longer within 330 DIP of the edge or outside the screen's vertical span ±330.</summary>
    public static bool ShouldRetreat(PixelPoint pointer, ScreenInfo screen, bool left)
    {
        var retreat = (int)Math.Round(ShelfConstants.EdgeRetreat * (screen.Scale <= 0 ? 1 : screen.Scale));
        var frame = screen.Bounds;
        var horizontal = left ? pointer.X - frame.X : frame.Right - pointer.X;
        return horizontal > retreat || pointer.Y < frame.Y - retreat || pointer.Y > frame.Bottom + retreat;
    }

    /// <summary>Only round(w/3) of the card on screen, centred on the pointer vertically.</summary>
    public static PixelPoint PeekPosition(ScreenInfo screen, bool left, int width, int height, int pointerY)
    {
        var s = screen.Scale <= 0 ? 1 : screen.Scale;
        var work = screen.WorkArea;
        var visible = (int)Math.Round(width / 3.0);
        var x = left ? work.X - width + visible : work.Right - visible;
        var margin = (int)Math.Round(ShelfConstants.PeekMargin * s);
        var y = Math.Clamp(pointerY - (height / 2), work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin));
        return new PixelPoint(x, y);
    }

    /// <summary>After a drop lands, the peek slides flush to the usable edge (8 DIP), keeping its y.</summary>
    public static PixelPoint RevealPosition(ScreenInfo screen, bool left, int width, int height, int currentY)
    {
        var s = screen.Scale <= 0 ? 1 : screen.Scale;
        var work = screen.WorkArea;
        var margin = (int)Math.Round(ShelfConstants.PeekMargin * s);
        var x = left ? work.X + margin : work.Right - margin - width;
        var y = Math.Clamp(currentY, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin));
        return new PixelPoint(x, y);
    }

    private static bool HasNeighbor(IReadOnlyList<ScreenInfo> screens, ScreenInfo self, PixelPoint probe) =>
        screens.Any(other => other.Id != self.Id && ContainsInclusive(Inflate(other.Bounds, 1, 1), probe));

    private static long Distance(PixelRect r, PixelPoint p)
    {
        var dx = Math.Max(Math.Max(r.X - p.X, 0), p.X - r.Right);
        var dy = Math.Max(Math.Max(r.Y - p.Y, 0), p.Y - r.Bottom);
        return ((long)dx * dx) + ((long)dy * dy);
    }
}

/// <summary>A condition that must persist for a dwell time (dock 150 ms, edge 150 ms).</summary>
public sealed class DwellTracker<T>
    where T : struct
{
    private readonly TimeSpan _dwell;
    private T? _current;
    private TimeSpan _since;

    public DwellTracker(TimeSpan dwell) => _dwell = dwell;

    public T? Current => _current;

    /// <summary>Feeds the current match (null = none). Returns the match once it held for the dwell time.</summary>
    public T? Update(T? match, TimeSpan now)
    {
        if (match is null)
        {
            _current = null;
            return null;
        }

        if (_current is null || !EqualityComparer<T>.Default.Equals(_current.Value, match.Value))
        {
            _current = match;
            _since = now;
            return null;
        }

        return now - _since >= _dwell ? match : null;
    }

    public void Reset() => _current = null;
}
