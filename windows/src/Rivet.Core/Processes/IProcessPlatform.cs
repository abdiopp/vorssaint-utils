// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Maintenance.Processes;

/// <summary>
/// Process access for Kill Process, Port Manager and the Uninstaller
/// (closing an app before its uninstaller runs). Every method is safe to call
/// off the UI thread and never throws for an inaccessible process.
/// </summary>
public interface IProcessPlatform
{
    int CurrentProcessId { get; }

    int LogicalProcessorCount { get; }

    /// <summary>All processes in one system call. Empty when the snapshot failed.</summary>
    IReadOnlyList<RawProcess> Snapshot();

    /// <summary>Full image path, or null when Windows refuses (protected and some system processes).</summary>
    string? ImagePath(ProcessIdentity identity);

    /// <summary>Version resource of an executable (cached by the caller).</summary>
    ExecutableDescription? Describe(string path);

    /// <summary>"DOMAIN\user" of the process token, or null when it cannot be read.</summary>
    string? UserName(ProcessIdentity identity);

    /// <summary>The process is marked critical: ending it stops Windows with a bug check.</summary>
    bool IsCritical(ProcessIdentity identity);

    /// <summary>PIDs that own visible, unowned top-level windows (one window enumeration).</summary>
    IReadOnlySet<int> ProcessesWithWindows();

    /// <summary>Asks the process's windows to close (WM_CLOSE), after checking its identity.</summary>
    KillOutcome CloseWindows(ProcessIdentity identity);

    /// <summary>Ends the process (TerminateProcess) after checking its identity on the same handle.</summary>
    KillOutcome Terminate(ProcessIdentity identity);

    /// <summary>Waits until the process exits; false on timeout.</summary>
    Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>The full command line (image plus arguments), or null.</summary>
    string? CommandLine(ProcessIdentity identity);

    /// <summary>Starts a program as the signed-in user (never elevated).</summary>
    bool Launch(string path, string? arguments, string? workingDirectory);
}

/// <summary>Administrator rights for the few places that truly need them.</summary>
public interface IElevationService
{
    /// <summary>The app runs with an elevated (administrator) token.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Starts an elevated copy (UAC prompt) that takes over once this copy has
    /// quit, then quits this copy. Returns false (and keeps running) when the
    /// prompt was declined or the copy could not start.
    /// </summary>
    bool RestartElevated();
}
