// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Platform;

namespace Rivet.Core.Sound;

/// <summary>One app in the mixer: every session of that app, on every endpoint.</summary>
public sealed record MixerRow
{
    /// <summary>Row and persistence id of the permanent System sounds row (Windows' analog of the macOS Finder row).</summary>
    public const string SystemSoundsId = "system:sounds";

    /// <summary>Persistence id, else <c>process:&lt;pid&gt;</c>.</summary>
    public required string RowId { get; init; }

    /// <summary>Key for saved preferences; null = the row is adjustable but nothing is stored.</summary>
    public string? PersistenceId { get; init; }

    public required string DisplayName { get; init; }

    public PixelBuffer? Icon { get; init; }

    public bool IsSystemSounds { get; init; }

    /// <summary>Any session is running (the green dot).</summary>
    public bool IsPlaying { get; init; }

    /// <summary>0…1.</summary>
    public double Volume { get; init; } = 1;

    /// <summary>Every session is muted (Windows' per-app mute).</summary>
    public bool Muted { get; init; }

    /// <summary>Saved per-app output (endpoint id); null = follows the default.</summary>
    public string? OutputDeviceId { get; init; }

    public bool IsPinned { get; init; }

    /// <summary>Taken off the list (still reported to the "Apps in the list" chooser).</summary>
    public bool IsHidden { get; init; }

    public IReadOnlyList<string> SessionKeys { get; init; } = [];

    public IReadOnlyList<int> ProcessIds { get; init; } = [];

    /// <summary>Rows without a persistence id cannot be hidden (nothing would remember it).</summary>
    public bool CanHide => PersistenceId is not null && !IsSystemSounds;

    /// <summary>Can be pinned and reordered.</summary>
    public bool CanArrange => PersistenceId is not null;

    /// <summary>Shows the crossed speaker.</summary>
    public bool IsSilent => Muted || VolumeMath.IsSilent(Volume);

    /// <summary>A custom setting is never hidden by "Hide inactive apps".</summary>
    public bool IsCustomized => !VolumeMath.IsUnity(Volume) || Muted || OutputDeviceId is not null;
}

/// <summary>Everything the row builder needs, gathered by the mixer service.</summary>
public sealed record MixerRowInputs
{
    public IReadOnlyList<AudioSessionInfo> Sessions { get; init; } = [];

    /// <summary>Saved volumes by persistence id (<see cref="SoundSettings.AppVolumes"/>).</summary>
    public IReadOnlyDictionary<string, double> SavedVolumes { get; init; } = new Dictionary<string, double>();

    /// <summary>Session-only volumes by row id, for rows without a persistence id.</summary>
    public IReadOnlyDictionary<string, double> SessionVolumes { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, string> SavedRoutes { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> SessionRoutes { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> HiddenApps { get; init; } = new Dictionary<string, string>();

    public bool ShowSystemSounds { get; init; } = true;

    public MixerArrangement Arrangement { get; init; } = MixerArrangement.Empty;

    /// <summary>This app's own process, never listed.</summary>
    public int OwnProcessId { get; init; }

    public string SystemSoundsName { get; init; } = "System sounds";

    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
}

/// <summary>An entry of the "Apps in the list" chooser.</summary>
public sealed record MixerListEntry(string Id, string DisplayName, bool IsShown, bool CanToggle, bool IsSystemSounds);

/// <summary>Grouping, identity and visibility rules of the mixer rows (spec §3.9.2), as pure functions.</summary>
public static class MixerRowBuilder
{
    /// <summary>
    /// Groups sessions into one row per app (merging same row ids), adds the
    /// permanent System sounds row, marks hidden rows, and sorts by display
    /// name (case-insensitive), ties by row id.
    /// </summary>
    public static IReadOnlyList<MixerRow> Build(MixerRowInputs inputs)
    {
        var rows = new List<MixerRow>();
        var groups = inputs.Sessions
            .Where(s => s.IsSystemSounds || s.ProcessId != inputs.OwnProcessId)
            .GroupBy(s => s.IsSystemSounds ? MixerRow.SystemSoundsId : s.App.GroupId, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var sessions = group.ToList();
            var isSystem = group.Key == MixerRow.SystemSoundsId;
            var persistenceId = isSystem ? MixerRow.SystemSoundsId : sessions.Select(s => s.App.PersistenceId).FirstOrDefault(id => id is not null);
            rows.Add(MakeRow(inputs, group.Key, persistenceId, isSystem, sessions,
                isSystem ? inputs.SystemSoundsName : sessions[0].App.DisplayName,
                sessions.Select(s => s.App.Icon).FirstOrDefault(i => i is not null)));
        }

        if (inputs.ShowSystemSounds && rows.All(r => !r.IsSystemSounds))
        {
            rows.Add(MakeRow(inputs, MixerRow.SystemSoundsId, MixerRow.SystemSoundsId, true, [], inputs.SystemSoundsName, null));
        }

        var comparer = StringComparer.Create(inputs.Culture, ignoreCase: true);
        return rows
            .OrderBy(r => r.DisplayName, comparer)
            .ThenBy(r => r.RowId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The list the user sees: hidden rows removed, the arrangement applied,
    /// then "Hide inactive apps" (a row stays when it plays or has a custom setting).
    /// </summary>
    public static IReadOnlyList<MixerRow> Visible(IReadOnlyList<MixerRow> rows, MixerArrangement arrangement, bool hideInactive)
    {
        var shown = rows.Where(r => !r.IsHidden).ToList();
        var arranged = arrangement.Apply(shown, r => r.PersistenceId);
        return hideInactive ? arranged.Where(r => r.IsPlaying || r.IsCustomized).ToList() : arranged;
    }

    /// <summary>
    /// "Apps in the list": hidden apps unchecked plus running apps checked,
    /// alphabetical, the System sounds entry mapped to its own switch; rows
    /// without a persistence id are listed but cannot be toggled.
    /// </summary>
    public static IReadOnlyList<MixerListEntry> ListEntries(IReadOnlyList<MixerRow> rows, IReadOnlyDictionary<string, string> hiddenApps, bool showSystemSounds, string systemSoundsName, CultureInfo culture)
    {
        var entries = new Dictionary<string, MixerListEntry>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var id = row.PersistenceId ?? row.RowId;
            entries[id] = row.IsSystemSounds
                ? new MixerListEntry(MixerRow.SystemSoundsId, systemSoundsName, showSystemSounds, true, true)
                : new MixerListEntry(id, row.DisplayName, !row.IsHidden, row.CanHide, false);
        }

        foreach (var (id, name) in hiddenApps)
        {
            entries.TryAdd(id, new MixerListEntry(id, name, false, true, false));
        }

        entries.TryAdd(MixerRow.SystemSoundsId, new MixerListEntry(MixerRow.SystemSoundsId, systemSoundsName, showSystemSounds, true, true));
        var comparer = StringComparer.Create(culture, ignoreCase: true);
        return entries.Values.OrderBy(e => e.DisplayName, comparer).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    private static MixerRow MakeRow(MixerRowInputs inputs, string rowId, string? persistenceId, bool isSystem, List<AudioSessionInfo> sessions, string displayName, PixelBuffer? icon)
    {
        double? saved = persistenceId is not null
            ? inputs.SavedVolumes.TryGetValue(persistenceId, out var v) ? v : null
            : inputs.SessionVolumes.TryGetValue(rowId, out var s) ? s : null;
        var representative = sessions.FirstOrDefault(x => x.IsActive) ?? sessions.FirstOrDefault();
        var volume = saved ?? representative?.Volume ?? 1.0;
        // The app's own saved route wins; otherwise show what Windows reports (a
        // route chosen in Windows Settings), so the row tells the truth.
        string? route = isSystem ? null
            : (persistenceId is not null
                  ? inputs.SavedRoutes.GetValueOrDefault(persistenceId)
                  : inputs.SessionRoutes.GetValueOrDefault(rowId))
              ?? sessions.Select(x => x.RoutedDeviceId).FirstOrDefault(r => r is not null);
        var hidden = isSystem ? !inputs.ShowSystemSounds : persistenceId is not null && inputs.HiddenApps.ContainsKey(persistenceId);

        return new MixerRow
        {
            RowId = rowId,
            PersistenceId = persistenceId,
            DisplayName = displayName,
            Icon = icon,
            IsSystemSounds = isSystem,
            IsPlaying = sessions.Any(x => x.IsActive),
            Volume = VolumeMath.ClampAppVolume(volume),
            Muted = sessions.Count > 0 && sessions.All(x => x.Muted),
            OutputDeviceId = route,
            IsPinned = inputs.Arrangement.IsPinned(persistenceId),
            IsHidden = hidden,
            SessionKeys = sessions.Select(x => x.Key).ToList(),
            ProcessIds = sessions.Select(x => x.ProcessId).Where(pid => pid > 0).Distinct().ToList(),
        };
    }
}
