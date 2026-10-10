// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.SystemMonitor;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.UI.Shell;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// Monotonic time from QueryUnbiasedInterruptTime (stops while the PC
/// sleeps); uptime from GetTickCount64 (includes sleep, like macOS).
/// </summary>
public sealed class WindowsMonitorClock : IMonitorClock
{
    public double Now => PInvoke.QueryUnbiasedInterruptTime(out var ticks) ? ticks / 1e7 : Environment.TickCount64 / 1000.0;

    public TimeSpan Uptime => TimeSpan.FromMilliseconds(PInvoke.GetTickCount64());
}

/// <summary>
/// CPU usage from GetSystemTimes (whole machine, all processor groups) and
/// per logical processor times from NtQuerySystemInformation. These are
/// "% Processor Time" semantics; Task Manager shows the frequency-scaled
/// "% Processor Utility", so at turbo clocks Task Manager can read higher.
/// </summary>
public sealed unsafe class WindowsCpuSensor : ICpuSensor
{
    private const ushort AllProcessorGroups = 0xFFFF;
    private readonly int _groups;
    private IReadOnlyList<CoreGroup>? _topology;

    public WindowsCpuSensor()
    {
        _groups = Math.Max(1, (int)PInvoke.GetActiveProcessorGroupCount());
        LogicalProcessorCount = Math.Max(1, (int)PInvoke.GetActiveProcessorCount(AllProcessorGroups));
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            ProcessorName = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    public int LogicalProcessorCount { get; }

    public string? ProcessorName { get; }

    public IReadOnlyList<CoreGroup> Topology => _topology ??= ReadTopology();

    public CpuTicks? ReadTotal()
    {
        if (!PInvoke.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        // Kernel time includes idle: busy = (kernel - idle) + user.
        var total = kernel + user;
        return new CpuTicks(total > idle ? total - idle : 0, total);
    }

    public IReadOnlyList<CpuTicks>? ReadPerCore() => NtQuery.ProcessorTimes(_groups);

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    /// <summary>EfficiencyClass per logical processor, in the same order as the per-core times.</summary>
    private IReadOnlyList<CoreGroup> ReadTopology()
    {
        try
        {
            uint length = 0;
            PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, null, &length);
            if (length == 0)
            {
                return [];
            }

            var buffer = new byte[length];
            var groupOffsets = new int[Math.Max(1, _groups)];
            for (var g = 1; g < groupOffsets.Length; g++)
            {
                groupOffsets[g] = groupOffsets[g - 1] + (int)PInvoke.GetActiveProcessorCount((ushort)(g - 1));
            }

            var classes = new int[LogicalProcessorCount];
            fixed (byte* p = buffer)
            {
                if (!PInvoke.GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)p, &length))
                {
                    return [];
                }

                var offset = 0;
                while (offset < length)
                {
                    var info = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)(p + offset);
                    if (info->Size == 0)
                    {
                        break;
                    }

                    if (info->Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                    {
                        var core = &info->Anonymous.Processor;
                        var masks = (GROUP_AFFINITY*)&core->GroupMask;
                        for (var m = 0; m < core->GroupCount; m++)
                        {
                            var group = masks[m].Group;
                            var mask = (ulong)masks[m].Mask;
                            for (var bit = 0; bit < 64; bit++)
                            {
                                if ((mask & (1UL << bit)) == 0)
                                {
                                    continue;
                                }

                                var logical = (group < groupOffsets.Length ? groupOffsets[group] : 0) + bit;
                                if (logical < classes.Length)
                                {
                                    classes[logical] = core->EfficiencyClass;
                                }
                            }
                        }
                    }

                    offset += (int)info->Size;
                }
            }

            return CoreTopologyBuilder.Build(classes);
        }
        catch (Exception ex)
        {
            Log.Warn("monitor", "Could not read the processor topology.", ex);
            return CoreTopologyBuilder.Build(new int[LogicalProcessorCount]);
        }
    }
}

/// <summary>
/// Memory: GlobalMemoryStatusEx (total, available), GetPerformanceInfo
/// (commit, standby + system cache), page file use from the kernel, Windows'
/// low-memory notification, and — every few seconds — the sum of private
/// working sets ("App Memory") and the Memory Compression store.
/// </summary>
public sealed class WindowsMemorySensor : IMemorySensor, IDisposable
{
    private static readonly TimeSpan ProcessFiguresMaxAge = TimeSpan.FromSeconds(4);
    private readonly SafeHandle? _lowMemory;
    private DateTime _processFiguresAt = DateTime.MinValue;
    private ulong? _appUsed;
    private ulong? _compressed;

    public WindowsMemorySensor()
    {
        var handle = PInvoke.CreateMemoryResourceNotification_SafeHandle(global::Windows.Win32.System.Memory.MEMORY_RESOURCE_NOTIFICATION_TYPE.LowMemoryResourceNotification);
        _lowMemory = handle.IsInvalid ? null : handle;
    }

    public unsafe MemorySample? Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!PInvoke.GlobalMemoryStatusEx(ref status))
        {
            return null;
        }

        var performance = new PERFORMANCE_INFORMATION { cb = (uint)sizeof(PERFORMANCE_INFORMATION) };
        var hasPerformance = PInvoke.GetPerformanceInfo(ref performance, performance.cb);
        var page = hasPerformance ? (ulong)performance.PageSize : 4096UL;
        var low = false;
        if (_lowMemory is not null && PInvoke.QueryMemoryResourceNotification(_lowMemory, out var state))
        {
            low = state;
        }

        if (DateTime.UtcNow - _processFiguresAt > ProcessFiguresMaxAge)
        {
            _processFiguresAt = DateTime.UtcNow;
            var processes = NtQuery.Processes();
            _appUsed = processes is null ? _appUsed : (ulong)processes.Where(p => p.Pid > 4).Sum(p => (double)p.PrivateWorkingSet);
            _compressed = processes is null ? _compressed : NtQuery.LastCompressionWorkingSet;
        }

        var swapPages = NtQuery.PageFilePagesInUse();
        return new MemorySample
        {
            Total = status.ullTotalPhys,
            Available = status.ullAvailPhys,
            CommitTotal = hasPerformance ? (ulong)performance.CommitTotal * page : 0,
            CommitLimit = hasPerformance ? (ulong)performance.CommitLimit * page : 0,
            Cached = hasPerformance ? (ulong)performance.SystemCache * page : null,
            SwapUsed = swapPages is { } pages ? pages * page : null,
            AppUsed = _appUsed,
            Compressed = _compressed,
            LowMemorySignaled = low,
        };
    }

    public void Dispose() => _lowMemory?.Dispose();
}

/// <summary>Full-screen apps, games and presentations via SHQueryUserNotificationState.</summary>
public sealed class WindowsFullScreenDetector : IFullScreenDetector
{
    public bool IsFullScreenAppInFront()
    {
        if (PInvoke.SHQueryUserNotificationState(out var state).Failed)
        {
            return false;
        }

        return state is QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
            or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
            or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }
}
