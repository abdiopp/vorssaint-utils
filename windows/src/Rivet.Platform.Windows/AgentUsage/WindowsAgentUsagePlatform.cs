// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Agents;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Threading;

namespace Rivet.Platform.Windows.Agents;

/// <summary>
/// Windows facts for the agent-usage reader: NTFS file ids (volume serial and
/// file index, through a handle opened with full sharing because agents keep
/// their logs open), process liveness for Claude Code's session registry
/// (access denied means the process exists), and awake time that excludes
/// sleep (QueryUnbiasedInterruptTime) for the offline grace.
/// </summary>
public sealed unsafe class WindowsAgentUsagePlatform : PortableAgentUsagePlatform
{
    private const uint StillActive = 259;

    public override string PidDomain => "win32";

    public override TimeSpan AwakeTime
    {
        get
        {
            ulong ticks;
            return PInvoke.QueryUnbiasedInterruptTime(&ticks) ? TimeSpan.FromTicks((long)ticks) : base.AwakeTime;
        }
    }

    public override ulong FileIdentity(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (!PInvoke.GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                return 0;
            }

            // The index is unique per volume; the serial tells volumes apart.
            var index = ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow;
            return index ^ ((ulong)info.dwVolumeSerialNumber << 48);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public override AgentProcessState ProcessState(int pid, DateTimeOffset? startedBy = null)
    {
        if (pid <= 0)
        {
            return AgentProcessState.Dead;
        }

        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsNull)
        {
            return Marshal.GetLastWin32Error() switch
            {
                5 => AgentProcessState.Alive, // ERROR_ACCESS_DENIED: it exists, we may not look.
                87 => AgentProcessState.Dead, // ERROR_INVALID_PARAMETER: no such process.
                _ => AgentProcessState.Unknown,
            };
        }

        try
        {
            uint code;
            if (!PInvoke.GetExitCodeProcess(process, &code))
            {
                return AgentProcessState.Unknown;
            }

            if (code != StillActive)
            {
                return AgentProcessState.Dead;
            }

            // Guard against pid reuse: a process created after its record was written is another one.
            if (startedBy is { } limit)
            {
                System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
                if (PInvoke.GetProcessTimes(process, &created, &exited, &kernel, &user))
                {
                    var start = DateTime.FromFileTimeUtc(((long)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime);
                    if (start > limit.UtcDateTime + PidReuseTolerance)
                    {
                        return AgentProcessState.Dead;
                    }
                }
            }

            return AgentProcessState.Alive;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
