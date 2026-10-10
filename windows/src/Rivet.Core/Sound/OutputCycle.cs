// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Sound;

public enum OutputCycleOutcome
{
    /// <summary>Switch to <see cref="OutputCycleResult.Target"/>.</summary>
    Switch,

    /// <summary>The only candidate is already current: success, nothing changes.</summary>
    Unchanged,

    /// <summary>No selected output is connected: "Select at least one available output."</summary>
    NoCandidates,

    /// <summary>The switch itself was refused (reported by the service, never by the rule).</summary>
    Failed,
}

public readonly record struct OutputCycleResult(OutputCycleOutcome Outcome, string? Target);

/// <summary>The output switcher's "next output" rule (spec §3.11, §6.9).</summary>
public static class OutputCycle
{
    /// <summary>
    /// Candidates are the selected outputs that are connected, in selection
    /// order (sanitized, de-duplicated). Current not among them → the first;
    /// one candidate that is current → unchanged; otherwise the next, wrapping.
    /// </summary>
    public static OutputCycleResult Next(IReadOnlyList<string>? selected, IReadOnlyCollection<string> connected, string? current)
    {
        var connectedSet = connected as ISet<string> ?? connected.ToHashSet(StringComparer.Ordinal);
        var candidates = SoundSettings.SanitizeIdList(selected, int.MaxValue).Where(connectedSet.Contains).ToList();
        if (candidates.Count == 0)
        {
            return new OutputCycleResult(OutputCycleOutcome.NoCandidates, null);
        }

        var index = current is null ? -1 : candidates.IndexOf(current);
        if (index < 0)
        {
            return new OutputCycleResult(OutputCycleOutcome.Switch, candidates[0]);
        }

        if (candidates.Count == 1)
        {
            return new OutputCycleResult(OutputCycleOutcome.Unchanged, current);
        }

        return new OutputCycleResult(OutputCycleOutcome.Switch, candidates[(index + 1) % candidates.Count]);
    }

    /// <summary>
    /// The stored selection after a checkbox changed: the checked outputs in
    /// visible order, followed by selected outputs that are not connected (kept).
    /// </summary>
    public static IReadOnlyList<string> UpdateSelection(IReadOnlyList<string>? selected, IReadOnlyList<string> visibleOutputs, string deviceId, bool isSelected)
    {
        var current = SoundSettings.SanitizeIdList(selected, int.MaxValue).ToHashSet(StringComparer.Ordinal);
        if (isSelected)
        {
            current.Add(deviceId);
        }
        else
        {
            current.Remove(deviceId);
        }

        var visible = visibleOutputs.ToHashSet(StringComparer.Ordinal);
        var ordered = visibleOutputs.Where(current.Contains).ToList();
        ordered.AddRange(SoundSettings.SanitizeIdList(selected, int.MaxValue).Where(id => !visible.Contains(id) && current.Contains(id)));
        return ordered;
    }
}
