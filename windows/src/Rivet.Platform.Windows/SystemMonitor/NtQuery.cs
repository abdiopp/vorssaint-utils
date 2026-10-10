// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.SystemMonitor;

namespace Rivet.Platform.Windows.SystemMonitor;

/// <summary>
/// NtQuerySystemInformation, declared by hand: the documented headers (and
/// therefore CsWin32) hide the fields this module needs (per-process CPU
/// times, private working set, creation time). Layouts follow the long-stable
/// phnt definitions; sequential layout with nint/nuint gives the x64/ARM64 padding.
/// </summary>
internal static unsafe partial class NtQuery
{
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int SystemPageFileInformation = 18;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint systemInformationLength, uint* returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint NtQuerySystemInformationEx(int systemInformationClass, void* inputBuffer, uint inputBufferLength, void* systemInformation, uint systemInformationLength, uint* returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemProcessInfo
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorPerformance
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PageFileInfo
    {
        public uint NextEntryOffset;
        public uint TotalSize;
        public uint TotalInUse;
        public uint PeakUsage;
        public UnicodeString PageFileName;
    }

    /// <summary>Runs a query, growing the buffer until it fits. Returns the filled length, or -1.</summary>
    private static int Query(int informationClass, ref byte[] buffer, ushort? group = null)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            uint returned = 0;
            uint status;
            fixed (byte* p = buffer)
            {
                if (group is { } g)
                {
                    var input = g;
                    status = NtQuerySystemInformationEx(informationClass, &input, sizeof(ushort), p, (uint)buffer.Length, &returned);
                }
                else
                {
                    status = NtQuerySystemInformation(informationClass, p, (uint)buffer.Length, &returned);
                }
            }

            if (status == 0)
            {
                return (int)returned;
            }

            if (status is StatusInfoLengthMismatch or StatusBufferTooSmall)
            {
                buffer = new byte[Math.Max(buffer.Length * 2, (int)returned + 64 * 1024)];
                continue;
            }

            return -1;
        }

        return -1;
    }

    private static readonly object Gate = new();
    private static byte[] _processBuffer = new byte[512 * 1024];

    /// <summary>Working set of the "Memory Compression" process in the last snapshot.</summary>
    public static ulong? LastCompressionWorkingSet { get; private set; }

    /// <summary>One consistent process snapshot (a single kernel call, no handles opened).</summary>
    public static List<ProcessSample>? Processes()
    {
        lock (Gate)
        {
            var length = Query(SystemProcessInformation, ref _processBuffer);
            if (length <= 0)
            {
                return null;
            }

            var result = new List<ProcessSample>(400);
            ulong? compression = null;
            fixed (byte* start = _processBuffer)
            {
                var offset = 0;
                while (offset + sizeof(SystemProcessInfo) <= length)
                {
                    var info = (SystemProcessInfo*)(start + offset);
                    var pid = (int)info->UniqueProcessId;
                    var name = info->ImageName.Buffer != 0 && info->ImageName.Length > 0
                        ? new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2)
                        : pid == 0 ? "Idle" : "System";
                    if (name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
                    {
                        compression = info->WorkingSetSize;
                    }

                    result.Add(new ProcessSample
                    {
                        Pid = pid,
                        ParentPid = (int)info->InheritedFromUniqueProcessId,
                        ImageName = name,
                        CpuTime = info->KernelTime + info->UserTime,
                        PrivateWorkingSet = (ulong)Math.Max(0, info->WorkingSetPrivateSize),
                        CreateTime = info->CreateTime,
                    });
                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    offset += (int)info->NextEntryOffset;
                }
            }

            LastCompressionWorkingSet = compression;
            return result;
        }
    }

    /// <summary>The working set of one named process (e.g. the "Memory Compression" store).</summary>
    public static ulong? WorkingSetOf(string imageName)
    {
        lock (Gate)
        {
            var length = Query(SystemProcessInformation, ref _processBuffer);
            if (length <= 0)
            {
                return null;
            }

            fixed (byte* start = _processBuffer)
            {
                var offset = 0;
                while (offset + sizeof(SystemProcessInfo) <= length)
                {
                    var info = (SystemProcessInfo*)(start + offset);
                    if (info->ImageName.Buffer != 0
                        && new string((char*)info->ImageName.Buffer, 0, info->ImageName.Length / 2).Equals(imageName, StringComparison.OrdinalIgnoreCase))
                    {
                        return info->WorkingSetSize;
                    }

                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    offset += (int)info->NextEntryOffset;
                }
            }

            return null;
        }
    }

    /// <summary>Per logical processor idle/kernel/user times, all processor groups in order.</summary>
    public static List<CpuTicks>? ProcessorTimes(int groupCount)
    {
        var result = new List<CpuTicks>();
        var buffer = new byte[sizeof(ProcessorPerformance) * 64];
        for (ushort group = 0; group < Math.Max(1, groupCount); group++)
        {
            var length = groupCount > 1 ? Query(SystemProcessorPerformanceInformation, ref buffer, group) : Query(SystemProcessorPerformanceInformation, ref buffer);
            if (length <= 0)
            {
                return null;
            }

            fixed (byte* p = buffer)
            {
                var items = (ProcessorPerformance*)p;
                for (var i = 0; i < length / sizeof(ProcessorPerformance); i++)
                {
                    // Kernel time includes idle time.
                    var idle = (ulong)Math.Max(0, items[i].IdleTime);
                    var kernel = (ulong)Math.Max(0, items[i].KernelTime);
                    var user = (ulong)Math.Max(0, items[i].UserTime);
                    var total = kernel + user;
                    result.Add(new CpuTicks(total > idle ? total - idle : 0, total));
                }
            }
        }

        return result;
    }

    /// <summary>Page file space in use, in pages (null without a page file).</summary>
    public static ulong? PageFilePagesInUse()
    {
        var buffer = new byte[4096];
        var length = Query(SystemPageFileInformation, ref buffer);
        if (length < sizeof(PageFileInfo))
        {
            return null;
        }

        ulong pages = 0;
        fixed (byte* start = buffer)
        {
            var offset = 0;
            while (offset + sizeof(PageFileInfo) <= length)
            {
                var info = (PageFileInfo*)(start + offset);
                pages += info->TotalInUse;
                if (info->NextEntryOffset == 0)
                {
                    break;
                }

                offset += (int)info->NextEntryOffset;
            }
        }

        return pages;
    }
}
