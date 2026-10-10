// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Modules.CleaningMode;

public enum SessionChange
{
    /// <summary>Another user took the console (fast user switching) or the session disconnected.</summary>
    Disconnected,

    /// <summary>The workstation was locked (Win+L cannot be blocked by hooks).</summary>
    Locked,

    Unlocked,

    Reconnected,
}

/// <summary>
/// The Windows pieces Cleaning Mode needs beyond the shared input hooks:
/// session changes (the lock must end on a user switch), foreground changes
/// (keystrokes aimed at an elevated window bypass a non-elevated hook, so the
/// black overlay keeps the foreground) and a cheap "are the hooks still
/// alive?" probe based on the system's last-input time.
/// </summary>
public interface ICleaningPlatform
{
    /// <summary>Calls back on the UI thread when the session is switched away, locked or comes back.</summary>
    IDisposable WatchSession(Action<SessionChange> changed);

    /// <summary>Calls back on the UI thread with the new foreground window whenever it changes.</summary>
    IDisposable WatchForeground(Action<nint> changed);

    /// <summary>Full image path of the process that owns <paramref name="hwnd"/>, or null.</summary>
    string? ProcessPathOf(nint hwnd);

    /// <summary>
    /// <see cref="Environment.TickCount"/>-compatible time of the last keyboard or
    /// mouse input in this session, or null when the platform cannot tell.
    /// </summary>
    int? LastInputTick();

    /// <summary>The standard warning sound.</summary>
    void Beep();
}
