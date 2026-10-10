// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

public enum SensorHelperState
{
    /// <summary>This PC cannot use the helper (not x64 Windows); nothing is offered.</summary>
    Unsupported,

    NotInstalled,

    Installed,
}

public enum SensorHelperResult
{
    Done,

    /// <summary>The person declined the administrator prompt.</summary>
    Cancelled,

    Failed,
}

/// <summary>
/// Setting up the elevated sensor helper: a small process Task Scheduler runs
/// with system rights to read the processor's own temperature sensors, so the
/// app itself stays a standard-user app. Installing and removing it asks for
/// administrator rights once each.
/// </summary>
public interface ISensorHelper
{
    SensorHelperState State { get; }

    /// <summary>The sensor driver (PawnIO) the helper reads through is installed.</summary>
    bool DriverInstalled { get; }

    /// <summary>Raised on any thread after an install or a removal.</summary>
    event EventHandler? StateChanged;

    Task<SensorHelperResult> InstallAsync();

    Task<SensorHelperResult> RemoveAsync();
}
