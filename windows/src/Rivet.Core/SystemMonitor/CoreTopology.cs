// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.SystemMonitor;

/// <summary>
/// Groups logical processors by performance class. Windows reports an
/// EfficiencyClass per physical core (higher = faster). One class → a single
/// "CPU" group; otherwise the fastest class is "Performance" and every slower
/// class (E-cores and low-power E-cores) is merged into "Efficiency".
/// </summary>
public static class CoreTopologyBuilder
{
    public static IReadOnlyList<CoreGroup> Build(IReadOnlyList<int> efficiencyClassPerLogical)
    {
        if (efficiencyClassPerLogical.Count == 0)
        {
            return [];
        }

        var classes = efficiencyClassPerLogical.Distinct().OrderDescending().ToList();
        if (classes.Count == 1)
        {
            return [new CoreGroup(CoreClass.Uniform, Enumerable.Range(0, efficiencyClassPerLogical.Count).ToList())];
        }

        var top = classes[0];
        var performance = new List<int>();
        var efficiency = new List<int>();
        for (var i = 0; i < efficiencyClassPerLogical.Count; i++)
        {
            (efficiencyClassPerLogical[i] == top ? performance : efficiency).Add(i);
        }

        return [new CoreGroup(CoreClass.Performance, performance), new CoreGroup(CoreClass.Efficiency, efficiency)];
    }
}

/// <summary>One packed run of bars of a single core group.</summary>
public sealed record CoreSegment(CoreGroup Group, IReadOnlyList<int> Cores, double X, double Width, double BarWidth);

/// <summary>
/// The per-core matrix layout of spec §3.2: groups packed side by side 12
/// apart into rows; a group that cannot keep 14-wide bars is split into
/// balanced chunks; spare width is shared by weighted core count; the
/// tightest bar scale is applied to every segment.
/// </summary>
public static class CoreMatrixLayout
{
    public const double BaseBar = 14;
    public const double BarGap = 4;
    public const double GroupGap = 12;
    public const double MinSegment = 70;

    public static double Need(int count, double weight) =>
        Math.Max(MinSegment, (count * BaseBar * weight) + ((count - 1) * BarGap));

    public static List<List<CoreSegment>> Layout(IReadOnlyList<CoreGroup> groups, double width)
    {
        var pieces = new List<(CoreGroup Group, List<int> Cores)>();
        foreach (var group in groups.Where(g => g.Cores.Count > 0))
        {
            var maxChunk = Math.Max(1, Math.Min(12, (int)Math.Floor((width + BarGap) / ((BaseBar * group.Weight) + BarGap))));
            var count = group.Cores.Count;
            var chunks = count <= maxChunk && Need(count, group.Weight) <= width ? 1 : (int)Math.Ceiling(count / (double)maxChunk);
            var start = 0;
            for (var c = 0; c < chunks; c++)
            {
                var size = (count / chunks) + (c < count % chunks ? 1 : 0);
                pieces.Add((group, group.Cores.Skip(start).Take(size).ToList()));
                start += size;
            }
        }

        // Pack greedily into rows.
        var rows = new List<List<(CoreGroup Group, List<int> Cores)>>();
        var used = 0.0;
        foreach (var piece in pieces)
        {
            var need = Need(piece.Cores.Count, piece.Group.Weight);
            if (rows.Count == 0 || used + GroupGap + need > width)
            {
                rows.Add([piece]);
                used = need;
            }
            else
            {
                rows[^1].Add(piece);
                used += GroupGap + need;
            }
        }

        // Share spare width by weighted core count, then apply the tightest scale everywhere.
        var widths = new List<List<double>>();
        var scale = double.MaxValue;
        foreach (var row in rows)
        {
            var needs = row.Select(p => Need(p.Cores.Count, p.Group.Weight)).ToList();
            var spare = Math.Max(0, width - needs.Sum() - (GroupGap * (row.Count - 1)));
            var weightSum = row.Sum(p => p.Cores.Count * p.Group.Weight);
            var rowWidths = new List<double>();
            for (var i = 0; i < row.Count; i++)
            {
                var segment = needs[i] + (weightSum > 0 ? spare * row[i].Cores.Count * row[i].Group.Weight / weightSum : 0);
                rowWidths.Add(segment);
                var n = row[i].Cores.Count;
                var bar = (segment - ((n - 1) * BarGap)) / n;
                scale = Math.Min(scale, bar / (BaseBar * row[i].Group.Weight));
            }

            widths.Add(rowWidths);
        }

        if (scale == double.MaxValue)
        {
            scale = 1;
        }

        var result = new List<List<CoreSegment>>();
        for (var r = 0; r < rows.Count; r++)
        {
            var x = 0.0;
            var segments = new List<CoreSegment>();
            for (var i = 0; i < rows[r].Count; i++)
            {
                var (group, cores) = rows[r][i];
                segments.Add(new CoreSegment(group, cores, x, widths[r][i], BaseBar * group.Weight * scale));
                x += widths[r][i] + GroupGap;
            }

            result.Add(segments);
        }

        return result;
    }
}
