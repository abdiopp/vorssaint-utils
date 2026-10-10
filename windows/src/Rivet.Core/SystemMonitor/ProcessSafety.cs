// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>
/// Force Kill safety (spec §3.13): processes Windows needs to keep running,
/// and the app itself, are never offered for killing.
/// </summary>
public static class ProcessSafety
{
    private static readonly HashSet<string> ProtectedImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Memory Compression", "Secure System",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe", "dwm.exe",
    };

    public static bool IsProtected(int pid, string? imageName, string? displayName = null, int? ownPid = null)
    {
        if (pid <= 4 || pid == (ownPid ?? Environment.ProcessId))
        {
            return true;
        }

        return (imageName is not null && ProtectedImages.Contains(imageName))
               || (displayName is not null && ProtectedImages.Contains(displayName));
    }

    /// <summary>Force Kill is enabled only for rows with a verified start time that are not protected.</summary>
    public static bool CanKill(ProcessUsageRow row, int? ownPid = null) =>
        row.StartTime is not null && !IsProtected(row.Pid, row.ImageName, row.Name, ownPid);
}
