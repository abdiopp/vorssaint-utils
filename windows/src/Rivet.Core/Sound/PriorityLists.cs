// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Sound;

/// <summary>The audio-priority list rules (spec §3.14, §6.10), as pure functions over device ids.</summary>
public static class PriorityLists
{
    /// <summary>A new list: the current device first, then the rest stably sorted by tier (built-in, hardware, virtual).</summary>
    public static IReadOnlyList<string> Initial(IReadOnlyList<AudioDevice> available, string? current)
    {
        var result = new List<string>();
        if (current is not null && available.Any(d => d.Id == current))
        {
            result.Add(current);
        }

        result.AddRange(available
            .Where(d => d.Id != current)
            .Select((d, i) => (d, i))
            .OrderBy(x => (int)x.d.Tier)
            .ThenBy(x => x.i)
            .Select(x => x.d.Id));
        return Cap(result, []);
    }

    /// <summary>
    /// Where a newly seen device goes: first when it is current (the OS or the
    /// person just picked it); otherwise just above the first virtual entry, or
    /// last when it is virtual itself or no virtual entry exists.
    /// </summary>
    public static IReadOnlyList<string> Place(IReadOnlyList<string> list, string deviceId, AudioDeviceTier tier, bool isCurrent, Func<string, AudioDeviceTier?> tierOf, ISet<string> available)
    {
        if (list.Contains(deviceId))
        {
            return list;
        }

        var result = list.ToList();
        if (isCurrent)
        {
            result.Insert(0, deviceId);
        }
        else
        {
            var firstVirtual = tier == AudioDeviceTier.Virtual ? -1 : result.FindIndex(id => tierOf(id) == AudioDeviceTier.Virtual);
            if (firstVirtual >= 0)
            {
                result.Insert(firstVirtual, deviceId);
            }
            else
            {
                result.Add(deviceId);
            }
        }

        return Cap(result, available);
    }

    /// <summary>The first connected device in list order; null when none is connected.</summary>
    public static string? Target(IReadOnlyList<string> list, ICollection<string> available) =>
        list.FirstOrDefault(available.Contains);

    /// <summary>
    /// Whether enforcement should switch: the target differs from the current
    /// device, and the current device is ranked (while an unranked device is
    /// current, enforcement does nothing).
    /// </summary>
    public static string? SwitchTarget(IReadOnlyList<string> list, ICollection<string> available, string? current)
    {
        if (current is null || !list.Contains(current))
        {
            return null;
        }

        var target = Target(list, available);
        return target is not null && target != current ? target : null;
    }

    /// <summary>Moves the entry at <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> list, int from, int to)
    {
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to)
        {
            return list;
        }

        var result = list.ToList();
        var item = result[from];
        result.RemoveAt(from);
        result.Insert(to, item);
        return result;
    }

    /// <summary>Keeps last-known names only for ids still in a list; refreshes names of connected devices.</summary>
    public static IReadOnlyDictionary<string, string> UpdateNames(IReadOnlyDictionary<string, string> names, IEnumerable<string> listed, IEnumerable<AudioDevice> connected)
    {
        var keep = listed.ToHashSet(StringComparer.Ordinal);
        var result = names.Where(kv => keep.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var device in connected)
        {
            if (keep.Contains(device.Id) && !string.IsNullOrWhiteSpace(device.Name))
            {
                result[device.Id] = device.Name;
            }
        }

        return result;
    }

    /// <summary>Lists hold at most 64 entries; disconnected entries at the end go first.</summary>
    private static IReadOnlyList<string> Cap(List<string> list, ICollection<string> available)
    {
        while (list.Count > SoundSettings.MaxPriorityEntries)
        {
            var dropAt = list.FindLastIndex(id => !available.Contains(id));
            list.RemoveAt(dropAt >= 0 ? dropAt : list.Count - 1);
        }

        return list;
    }
}
