// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Rivet.Core.Maintenance.Processes;

/// <summary>Which processes may never be ended from the app.</summary>
public static class ProcessProtection
{
    /// <summary>
    /// Core Windows processes (killing most of them signs the user out or
    /// stops Windows). Compared case-insensitively with the image name.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedImageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Idle", "Secure System", "Registry", "Memory Compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "LsaIso.exe", "dwm.exe", "fontdrvhost.exe",
    };

    /// <param name="pid">The process id.</param>
    /// <param name="imageName">Image name from the snapshot.</param>
    /// <param name="ownPid">This app's PID (never killable).</param>
    /// <param name="identityKnown">False when the creation time is unknown: no identity, no kill.</param>
    /// <param name="isCritical">ProcessBreakOnTermination is set.</param>
    /// <param name="ownName">The app's own image name (e.g. "Rivet.exe").</param>
    public static bool IsProtected(int pid, string imageName, int ownPid, bool identityKnown, bool isCritical, string? ownName = null)
    {
        if (pid is 0 or 4 || pid == ownPid || !identityKnown || isCritical)
        {
            return true;
        }

        if (ProtectedImageNames.Contains(imageName))
        {
            return true;
        }

        return ownName is not null && string.Equals(imageName, ownName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Parent links, process trees and grouping.</summary>
public static class ProcessTree
{
    /// <summary>Kill-tree walk cap (the macOS constant).</summary>
    public const int MaxTreeSize = 4096;

    /// <summary>
    /// Windows keeps a child's parent PID after the parent exits and reuses
    /// PIDs, so a parent link only counts when the parent existed first.
    /// </summary>
    public static bool IsValidParent(RawProcess child, RawProcess parent) =>
        parent.Pid != child.Pid && parent.CreationTime > 0 && child.CreationTime > 0 && parent.CreationTime <= child.CreationTime;

    /// <summary>
    /// Descendants of <paramref name="root"/>, breadth first with a visited
    /// set, then reversed so the deepest come first (the root is not included).
    /// </summary>
    public static IReadOnlyList<RawProcess> DescendantsDeepestFirst(RawProcess root, IReadOnlyList<RawProcess> snapshot)
    {
        var children = new Dictionary<int, List<RawProcess>>();
        foreach (var process in snapshot)
        {
            if (!children.TryGetValue(process.ParentPid, out var list))
            {
                children[process.ParentPid] = list = [];
            }

            list.Add(process);
        }

        var order = new List<RawProcess>();
        var visited = new HashSet<int> { root.Pid };
        var queue = new Queue<RawProcess>();
        queue.Enqueue(root);
        while (queue.Count > 0 && order.Count < MaxTreeSize)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent.Pid, out var kids))
            {
                continue;
            }

            foreach (var child in kids)
            {
                if (order.Count >= MaxTreeSize || !visited.Add(child.Pid) || !IsValidParent(child, parent))
                {
                    continue;
                }

                order.Add(child);
                queue.Enqueue(child);
            }
        }

        order.Reverse();
        return order;
    }

    /// <summary>
    /// The group owner of every process: its highest validated ancestor that
    /// runs the same executable (Chromium and Electron apps start dozens of
    /// helper processes from one exe). Processes without such an ancestor own
    /// themselves. Windows has no "responsible process" API, so this is a heuristic.
    /// </summary>
    public static IReadOnlyDictionary<int, int> GroupOwners(IReadOnlyList<RawProcess> snapshot, Func<RawProcess, string?> pathOf)
    {
        var byPid = new Dictionary<int, RawProcess>();
        foreach (var process in snapshot)
        {
            byPid.TryAdd(process.Pid, process);
        }

        var owners = new Dictionary<int, int>();
        foreach (var process in snapshot)
        {
            var path = pathOf(process);
            var owner = process;
            var guard = 0;
            while (path is not null
                   && guard++ < 64
                   && byPid.TryGetValue(owner.ParentPid, out var parent)
                   && IsValidParent(owner, parent)
                   && string.Equals(pathOf(parent), path, StringComparison.OrdinalIgnoreCase))
            {
                owner = parent;
            }

            owners[process.Pid] = owner.Pid;
        }

        return owners;
    }
}

/// <summary>
/// CPU share between two snapshots: Δ(kernel + user time) / (Δwall × logical
/// processors), so 100 % is the whole machine like Task Manager (macOS
/// reports per-core percentages). The first sample of a process has no value.
/// </summary>
public sealed class CpuSampler
{
    private Dictionary<ProcessIdentity, long> _previous = [];
    private long _previousTicks;

    /// <summary>Records a snapshot taken at <paramref name="timestampTicks"/> (Stopwatch-independent 100 ns ticks).</summary>
    public IReadOnlyDictionary<ProcessIdentity, double> Sample(IReadOnlyList<RawProcess> snapshot, long timestampTicks, int processors)
    {
        var result = new Dictionary<ProcessIdentity, double>();
        var current = new Dictionary<ProcessIdentity, long>(snapshot.Count);
        var wall = timestampTicks - _previousTicks;
        foreach (var process in snapshot)
        {
            var identity = process.Identity;
            current[identity] = process.CpuTime;
            if (_previousTicks > 0 && wall > 0 && _previous.TryGetValue(identity, out var before))
            {
                var used = Math.Max(0, process.CpuTime - before);
                result[identity] = Math.Clamp(100.0 * used / (wall * (double)Math.Max(1, processors)), 0, 100);
            }
        }

        _previous = current;
        _previousTicks = timestampTicks;
        return result;
    }
}

/// <summary>Sorting and filtering of the Kill Process list.</summary>
public static class ProcessListRules
{
    public static ProcessSortColumn ParseSort(string value) => value switch
    {
        "memory" => ProcessSortColumn.Memory,
        "name" => ProcessSortColumn.Name,
        "pid" => ProcessSortColumn.Pid,
        _ => ProcessSortColumn.Cpu,
    };

    public static string SortKey(ProcessSortColumn column) => column switch
    {
        ProcessSortColumn.Memory => "memory",
        ProcessSortColumn.Name => "name",
        ProcessSortColumn.Pid => "pid",
        _ => "cpu",
    };

    /// <summary>Names sort ascending first; numbers descending first.</summary>
    public static bool NaturalAscending(ProcessSortColumn column) => column == ProcessSortColumn.Name;

    /// <summary>
    /// A header click: the active column flips direction, another column
    /// becomes active in its natural direction.
    /// </summary>
    public static (ProcessSortColumn Column, bool Ascending) Click(ProcessSortColumn active, bool ascending, ProcessSortColumn clicked) =>
        clicked == active ? (active, !ascending) : (clicked, NaturalAscending(clicked));

    public static IReadOnlyList<ProcessRow> Sort(IEnumerable<ProcessRow> rows, ProcessSortColumn column, bool ascending)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        IOrderedEnumerable<ProcessRow> ordered = column switch
        {
            ProcessSortColumn.Memory => ascending ? rows.OrderBy(r => r.MemoryBytes) : rows.OrderByDescending(r => r.MemoryBytes),
            ProcessSortColumn.Name => ascending ? rows.OrderBy(r => r.Name, comparer) : rows.OrderByDescending(r => r.Name, comparer),
            ProcessSortColumn.Pid => ascending ? rows.OrderBy(r => r.Pid) : rows.OrderByDescending(r => r.Pid),
            _ => ascending ? rows.OrderBy(r => r.CpuPercent ?? -1) : rows.OrderByDescending(r => r.CpuPercent ?? -1),
        };
        return ordered.ThenBy(r => r.Name, comparer).ThenBy(r => r.Pid).ToList();
    }

    /// <summary>Name contains the text (case-insensitive), or the text equals the PID.</summary>
    public static bool Matches(ProcessRow row, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var text = filter.Trim();
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid == row.Pid)
        {
            return true;
        }

        return row.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || row.ImageName.Contains(text, StringComparison.CurrentCultureIgnoreCase);
    }
}
