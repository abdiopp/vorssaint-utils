// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Awake;

/// <summary>
/// Power requests (PowerCreateRequest/PowerSetRequest on Windows): the system
/// stays awake, and optionally the display. Idempotent; the reason string is
/// what <c>powercfg /requests</c> shows.
/// </summary>
public interface IPowerRequests
{
    void Apply(bool keepSystemAwake, bool keepDisplayOn, string reason);

    void Release();
}

/// <summary>Screen lock state (WTS session notifications).</summary>
public interface ISessionLockMonitor
{
    bool IsLocked { get; }

    /// <summary>Raised on the UI thread with the new state.</summary>
    event EventHandler<bool>? LockChanged;
}

/// <summary>AC line and battery charge.</summary>
public interface IPowerSource
{
    bool HasBattery { get; }

    /// <summary>On AC power; false on desktops without a battery report (spec: the Power condition never matches there).</summary>
    bool OnExternalPower { get; }

    /// <summary>Battery charge 0…100, null without a battery.</summary>
    int? BatteryPercent { get; }

    /// <summary>Raised on the UI thread after plug/unplug or charge changes.</summary>
    event EventHandler? Changed;
}

/// <summary>Whether a display other than the built-in panel is active.</summary>
public interface IDisplayTopology
{
    /// <summary>Null when the query failed (the last known value is reused).</summary>
    bool? ExternalDisplayConnected();

    /// <summary>Raised on the UI thread when displays change.</summary>
    event EventHandler? Changed;
}

/// <summary>Running executables, for the Applications condition.</summary>
public interface IRunningApps
{
    /// <summary>Executable file names of running processes, lower case.</summary>
    IReadOnlySet<string> RunningExecutables();
}

/// <summary>Moves the pointer by one pixel and back (spec §3.18.2 pointer jiggle).</summary>
public interface IPointerJiggler
{
    void Nudge();
}

/// <summary>The power plan's "When I close the lid" action.</summary>
public interface ILidActionController
{
    /// <summary>The PC has a lid.</summary>
    bool HasLid { get; }

    /// <summary>The lid is closed now (null when unknown).</summary>
    bool? IsLidClosed { get; }

    /// <summary>Reads the active scheme and its AC/DC lid actions.</summary>
    LidRecovery? ReadCurrent();

    /// <summary>Writes <paramref name="action"/> (0 = do nothing) for AC and DC on <paramref name="scheme"/> and applies it.</summary>
    bool Write(Guid scheme, uint ac, uint dc);

    /// <summary>Asks Windows to sleep now (used when restoring with the lid already closed).</summary>
    bool RequestSleep();

    /// <summary>Raised on the UI thread when the lid opens or closes.</summary>
    event EventHandler? LidChanged;
}

/// <summary>Clock and one-shot timers; the app's run on the UI thread, tests advance a manual clock.</summary>
public interface IKeepAwakeTimers
{
    DateTimeOffset Now { get; }

    IDisposable Schedule(TimeSpan dueIn, Action callback);
}
