// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.CleaningMode;

/// <summary>What one key event did to the unlock gesture.</summary>
public enum UnlockCounterResult
{
    /// <summary>Nothing changed (auto-repeat, or a key-up that does not count).</summary>
    Ignored,

    /// <summary>Progress went up but the threshold was not reached.</summary>
    Advanced,

    /// <summary>Progress went back to zero.</summary>
    Reset,

    /// <summary>The threshold was reached: unlock now.</summary>
    Unlock,
}

/// <summary>
/// The Esc ×5 unlock gesture (spec 07 §3.4.5, §6.4), ported as a pure state
/// machine with an injected monotonic clock:
/// <list type="bullet">
/// <item>auto-repeat neither counts nor resets;</item>
/// <item>any other key-down, or any modifier down or up, resets to zero;</item>
/// <item>an Esc within 6.0 s (inclusive) of the previous Esc counts +1, otherwise restarts at 1;</item>
/// <item>progress ≥ 5 requests the unlock.</item>
/// </list>
/// Not thread-safe; the input filter owns one instance on the hook thread.
/// </summary>
public sealed class CleaningUnlockCounter
{
    private TimeSpan? _lastEscape;

    public CleaningUnlockCounter(int unlockKey = CleaningModeConstants.UnlockKey, int threshold = CleaningModeConstants.UnlockThreshold, TimeSpan? window = null)
    {
        UnlockKey = unlockKey;
        Threshold = threshold;
        Window = window ?? CleaningModeConstants.UnlockWindow;
    }

    public int UnlockKey { get; }

    public int Threshold { get; }

    public TimeSpan Window { get; }

    public int Progress { get; private set; }

    /// <summary>A non-modifier key went down (repeat = the key was already held).</summary>
    public UnlockCounterResult KeyDown(int virtualKey, bool isRepeat, TimeSpan now)
    {
        if (isRepeat)
        {
            return UnlockCounterResult.Ignored;
        }

        if (virtualKey != UnlockKey)
        {
            return ResetProgress();
        }

        if (_lastEscape is { } last && now >= last && now - last <= Window)
        {
            Progress++;
        }
        else
        {
            Progress = 1;
        }

        _lastEscape = now;
        return Progress >= Threshold ? UnlockCounterResult.Unlock : UnlockCounterResult.Advanced;
    }

    /// <summary>A modifier or lock key changed (press or release, but not an auto-repeat).</summary>
    public UnlockCounterResult ModifierChanged() => ResetProgress();

    public void Reset()
    {
        Progress = 0;
        _lastEscape = null;
    }

    private UnlockCounterResult ResetProgress()
    {
        var hadProgress = Progress != 0;
        Progress = 0;
        _lastEscape = null;
        return hadProgress ? UnlockCounterResult.Reset : UnlockCounterResult.Ignored;
    }
}
