// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Rivet.Core.Sound;

/// <summary>
/// Pins and custom order of the mixer rows (spec §3.9.6), stored as
/// <c>{"order":[ids],"pinned":[ids]}</c> in <see cref="SoundSettings.AppArrangement"/>.
/// Apps that are not running keep their slots; refreshing audio never rewrites it.
/// </summary>
public sealed record MixerArrangement(IReadOnlyList<string> Order, IReadOnlyList<string> Pinned)
{
    public static MixerArrangement Empty { get; } = new([], []);

    /// <summary>Invalid JSON gives an empty arrangement; duplicates and empty ids are dropped.</summary>
    public static MixerArrangement Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            return new MixerArrangement(ReadIds(document.RootElement, "order"), ReadIds(document.RootElement, "pinned"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<string>>
    {
        ["order"] = Order,
        ["pinned"] = Pinned,
    });

    public bool IsPinned(string? persistenceId) => persistenceId is not null && Pinned.Contains(persistenceId, StringComparer.Ordinal);

    public MixerArrangement Pin(string persistenceId) =>
        IsPinned(persistenceId) ? this : this with { Pinned = [.. Pinned, persistenceId] };

    public MixerArrangement Unpin(string persistenceId) =>
        IsPinned(persistenceId) ? this with { Pinned = Pinned.Where(id => id != persistenceId).ToList() } : this;

    /// <summary>
    /// Orders rows that arrive sorted alphabetically: pinned rows first; inside
    /// each group by index in <see cref="Order"/>; rows not in the order (and
    /// rows without a persistence id) after the known ones, keeping their
    /// alphabetical order.
    /// </summary>
    public IReadOnlyList<T> Apply<T>(IReadOnlyList<T> alphabetical, Func<T, string?> persistenceId)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Order.Count; i++)
        {
            index.TryAdd(Order[i], i);
        }

        IEnumerable<T> Arrange(IEnumerable<T> group)
        {
            var list = group.Select((row, position) => (Row: row, Position: position)).ToList();
            var known = list.Where(x => persistenceId(x.Row) is { } id && index.ContainsKey(id))
                .OrderBy(x => index[persistenceId(x.Row)!]);
            var unknown = list.Where(x => persistenceId(x.Row) is not { } id || !index.ContainsKey(id))
                .OrderBy(x => x.Position);
            return known.Concat(unknown).Select(x => x.Row);
        }

        var pinned = alphabetical.Where(r => IsPinned(persistenceId(r)));
        var others = alphabetical.Where(r => !IsPinned(persistenceId(r)));
        return Arrange(pinned).Concat(Arrange(others)).ToList();
    }

    /// <summary>
    /// Moves <paramref name="moved"/> before or after <paramref name="target"/>
    /// inside its pin group. <paramref name="visibleGroupIds"/> are the
    /// persistence ids of the group's visible rows in display order. The new
    /// sequence is written back only into the slots those ids occupy in
    /// order ∪ visible, so hidden, closed and other-group apps keep their
    /// remembered positions. Moving never pins or unpins.
    /// </summary>
    public MixerArrangement Move(IReadOnlyList<string> visibleGroupIds, string moved, string target, bool after)
    {
        if (moved == target || !visibleGroupIds.Contains(moved) || !visibleGroupIds.Contains(target))
        {
            return this;
        }

        var sequence = visibleGroupIds.Where(id => id != moved).ToList();
        var targetIndex = sequence.IndexOf(target);
        sequence.Insert(after ? targetIndex + 1 : targetIndex, moved);

        var combined = Order.ToList();
        foreach (var id in visibleGroupIds)
        {
            if (!combined.Contains(id))
            {
                combined.Add(id);
            }
        }

        var visible = visibleGroupIds.ToHashSet(StringComparer.Ordinal);
        var slot = 0;
        for (var i = 0; i < combined.Count && slot < sequence.Count; i++)
        {
            if (visible.Contains(combined[i]))
            {
                combined[i] = sequence[slot++];
            }
        }

        return this with { Order = combined };
    }

    private static List<string> ReadIds(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } id && SoundSettings.IsValidId(id) && seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }
}
