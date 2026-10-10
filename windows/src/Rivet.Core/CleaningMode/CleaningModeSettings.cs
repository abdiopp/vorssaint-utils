// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Settings;

namespace Rivet.Core.Modules.CleaningMode;

/// <summary>Cleaning Mode preferences (raw keys match the macOS app).</summary>
public static class CleaningModeSettings
{
    /// <summary>Corner indicator instead of a black screen. Read live while the lock is on.</summary>
    public static readonly Setting<bool> KeepScreenVisible = new("cleaningModeKeepScreenVisible", false);
}

/// <summary>The gesture constants. They are deliberately not user settings (spec 07 §4.4, §6.4).</summary>
public static class CleaningModeConstants
{
    /// <summary>Escape (VK_ESCAPE) unlocks.</summary>
    public const int UnlockKey = 0x1B;

    /// <summary>Presses needed in a row.</summary>
    public const int UnlockThreshold = 5;

    /// <summary>Each press must come within this long of the previous one (inclusive). Was 2 s; widened for slow pressers.</summary>
    public static readonly TimeSpan UnlockWindow = TimeSpan.FromSeconds(6.0);

    /// <summary>Teardown waits at most this long for mouse buttons held during the lock to be released.</summary>
    public static readonly TimeSpan ReleaseWaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>Launch delays from other surfaces.</summary>
    public static readonly TimeSpan LauncherDelay = TimeSpan.FromSeconds(0.1);

    public static readonly TimeSpan RadialDelay = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// Safety net: when the UI thread has not answered for this long while the
    /// keyboard is locked, the hook stops swallowing keys (the app is hung, and
    /// the overlay with its Unlock button can no longer be trusted).
    /// </summary>
    public static readonly TimeSpan UiHeartbeatLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Safety net: an unlock that was requested but never completed (UI hung
    /// during teardown) releases the keyboard after the release wait plus this grace.
    /// </summary>
    public static readonly TimeSpan PendingUnlockGrace = TimeSpan.FromSeconds(2);
}
