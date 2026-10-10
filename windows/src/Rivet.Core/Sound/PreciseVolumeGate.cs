// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Sound;

/// <summary>
/// The precise roller's gate against coarse wheel bursts (spec §3.15): a
/// press is accepted only if more than 30 ms passed since the last accepted
/// one, and a direction reversal within 300 ms of the last accepted press is
/// accepted only on the third consecutive press in the new direction. Not
/// thread-safe: the keyboard hook calls it from its one thread.
/// </summary>
public sealed class PreciseVolumeGate
{
    public const long MinSpacingMs = 30;
    public const long ReversalWindowMs = 300;
    public const int ReversalConfirmations = 3;

    private long? _lastAcceptedMs;
    private int _lastDirection;
    private int _reversalCount;

    /// <param name="direction">+1 for up, −1 for down.</param>
    /// <param name="nowMs">A monotonic timestamp in milliseconds.</param>
    public bool Accept(int direction, long nowMs)
    {
        direction = Math.Sign(direction);
        if (direction == 0)
        {
            return false;
        }

        if (_lastAcceptedMs is not { } last)
        {
            Commit(direction, nowMs);
            return true;
        }

        var elapsed = nowMs - last;
        if (elapsed <= MinSpacingMs)
        {
            return false;
        }

        if (direction != _lastDirection && elapsed < ReversalWindowMs)
        {
            _reversalCount++;
            if (_reversalCount < ReversalConfirmations)
            {
                return false;
            }
        }

        Commit(direction, nowMs);
        return true;
    }

    private void Commit(int direction, long nowMs)
    {
        _lastAcceptedMs = nowMs;
        _lastDirection = direction;
        _reversalCount = 0;
    }
}
