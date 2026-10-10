// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.App.Shell;

/// <summary>
/// Order and visibility rules shared by the panel and the Settings editor:
/// the saved order wins (duplicates and unknown ids ignored); items missing
/// from it are inserted after the last saved item with a lower default order.
/// </summary>
public static class PanelLayout
{
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, string> id, Func<T, int> defaultOrder, string savedCsv)
    {
        var all = items.ToList();
        var byId = all.GroupBy(id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var saved = ParseCsv(savedCsv).Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        var result = saved.Select(s => byId[s]).ToList();
        foreach (var missing in all.Where(i => !saved.Contains(id(i), StringComparer.Ordinal)).OrderBy(defaultOrder))
        {
            var insertAt = 0;
            for (var i = 0; i < result.Count; i++)
            {
                if (defaultOrder(result[i]) <= defaultOrder(missing))
                {
                    insertAt = i + 1;
                }
            }

            result.Insert(insertAt, missing);
        }

        return result;
    }

    public static HashSet<string> ParseHidden(string csv) => ParseCsv(csv).ToHashSet(StringComparer.Ordinal);

    public static string ToCsv(IEnumerable<string> ids) => string.Join(',', ids);

    public static IEnumerable<string> ParseCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
