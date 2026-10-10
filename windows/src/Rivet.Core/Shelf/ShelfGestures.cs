// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.Shelf;

/// <summary>
/// The horizontal shake that summons the shelf during a drag (spec 07 §3.1.6):
/// samples of the last 0.5 s (at least 5), travel = Σ|dx|, a sample's direction
/// is ±1 beyond 6 DIP, each change of non-zero direction is a reversal;
/// trigger at ≥ 3 reversals, more than 220 DIP of travel and more than 1 s
/// since the last summon. Coordinates in DIPs.
/// </summary>
public sealed class ShakeDetector
{
    private readonly List<(TimeSpan Time, double X)> _samples = [];
    private TimeSpan? _lastSummon;

    public void Reset() => _samples.Clear();

    /// <summary>Feeds one drag position; returns true when the shake triggers (samples are then cleared).</summary>
    public bool Add(TimeSpan time, double x, bool contentDrag = true, bool sourceAllowed = true)
    {
        _samples.Add((time, x));
        _samples.RemoveAll(s => time - s.Time > ShelfConstants.ShakeWindow);
        if (_samples.Count < ShelfConstants.ShakeMinimumSamples)
        {
            return false;
        }

        double travel = 0;
        var reversals = 0;
        var previousDirection = 0;
        for (var i = 1; i < _samples.Count; i++)
        {
            var dx = _samples[i].X - _samples[i - 1].X;
            travel += Math.Abs(dx);
            var direction = dx > ShelfConstants.ShakeDirectionThreshold ? 1 : dx < -ShelfConstants.ShakeDirectionThreshold ? -1 : 0;
            if (direction == 0)
            {
                continue;
            }

            if (previousDirection != 0 && direction != previousDirection)
            {
                reversals++;
            }

            previousDirection = direction;
        }

        var cooled = _lastSummon is not { } last || time - last > ShelfConstants.ShakeCooldown;
        if (reversals >= ShelfConstants.ShakeReversals && travel > ShelfConstants.ShakeTravel && cooled && contentDrag && sourceAllowed)
        {
            _lastSummon = time;
            _samples.Clear();
            return true;
        }

        return false;
    }
}

public enum DragGesturePhase
{
    Idle,

    /// <summary>Primary button down, not moved past the system drag threshold yet.</summary>
    Pressed,

    /// <summary>Moved past the threshold with the button held: a potential content drag.</summary>
    Dragging,
}

/// <summary>
/// Windows has no global "a drag started" notification (spec 07 §3.1.5). This
/// tracks the primary button from the low-level mouse hook: a press, then
/// movement beyond the system drag threshold (SM_CXDRAG/SM_CYDRAG) with the
/// button still held, makes a <em>potential</em> drag. Window moves and
/// resizes are excluded by the caller (move/size WinEvents), and drags that
/// start on the shelf's own windows are ignored. Thread-safe enough for the
/// hook thread: one writer.
/// </summary>
public sealed class DragGestureTracker
{
    private int _startX;
    private int _startY;

    public DragGesturePhase Phase { get; private set; }

    /// <summary>Executable of the window under the press (for the automatic-exception list).</summary>
    public string? SourceApp { get; private set; }

    /// <summary>The press landed on one of the app's own windows.</summary>
    public bool StartedOnOwnWindow { get; private set; }

    public int ThresholdX { get; set; } = 4;

    public int ThresholdY { get; set; } = 4;

    public void Press(int x, int y, string? sourceApp, bool onOwnWindow)
    {
        Phase = DragGesturePhase.Pressed;
        _startX = x;
        _startY = y;
        SourceApp = sourceApp;
        StartedOnOwnWindow = onOwnWindow;
    }

    /// <summary>Returns true exactly once, when the gesture becomes a potential drag.</summary>
    public bool Move(int x, int y)
    {
        if (Phase != DragGesturePhase.Pressed)
        {
            return false;
        }

        if (Math.Abs(x - _startX) > ThresholdX || Math.Abs(y - _startY) > ThresholdY)
        {
            Phase = DragGesturePhase.Dragging;
            return !StartedOnOwnWindow;
        }

        return false;
    }

    /// <summary>Button up (or the watchdog saw the button up). Returns whether a drag was in progress.</summary>
    public bool Release()
    {
        var wasDragging = Phase == DragGesturePhase.Dragging;
        Phase = DragGesturePhase.Idle;
        StartedOnOwnWindow = false;
        return wasDragging;
    }

    /// <summary>A window move/resize loop started: this press is not a content drag.</summary>
    public void Cancel()
    {
        Phase = DragGesturePhase.Idle;
        StartedOnOwnWindow = false;
    }

    public bool IsAutomaticDrag => Phase == DragGesturePhase.Dragging && !StartedOnOwnWindow;
}

/// <summary>Automatic opens (shake, dock, edge) honour the "Automatic exceptions" list.</summary>
public static class ShelfExclusions
{
    /// <summary>
    /// An unknown or empty source is allowed. An entry with a directory matches
    /// the full path; a bare name ("photoshop.exe" or "photoshop") matches the
    /// file name. Comparisons ignore case.
    /// </summary>
    public static bool AllowsAutomaticOpen(string? sourceApp, IReadOnlyList<string> exclusions)
    {
        if (string.IsNullOrWhiteSpace(sourceApp) || exclusions.Count == 0)
        {
            return true;
        }

        // Windows paths are compared on any OS (tests run elsewhere).
        var fileName = sourceApp[(sourceApp.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        foreach (var entry in exclusions)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var hasDirectory = trimmed.Contains('\\') || trimmed.Contains('/');
            if (hasDirectory ? string.Equals(trimmed, sourceApp, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(trimmed, fileName, StringComparison.OrdinalIgnoreCase) || string.Equals(trimmed, stem, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
