// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Rivet.Core.Settings;

namespace Rivet.Core.Sound;

/// <summary>
/// What "Mute microphone" has done to the devices: which it claimed and the
/// levels to give back. <see cref="Claimed"/> null means "never tracked"
/// (restored settings, older versions), which is not the same as empty.
/// </summary>
public sealed record MicMuteRecord
{
    public IReadOnlyList<string>? Claimed { get; init; }

    public IReadOnlyDictionary<string, double> SavedVolumes { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> SavedChannels { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);

    /// <summary>The legacy single saved level (fallback before 0.75).</summary>
    public double LegacySaved { get; init; } = MicMuteEngine.FallbackRestoreLevel;

    public bool HasOutstandingClaims => Claimed is { Count: > 0 };

    public static MicMuteRecord Load(ISettingsStore settings) => new()
    {
        Claimed = settings.Get(SoundSettings.MicMuteMutedDevices),
        SavedVolumes = settings.Get(SoundSettings.MicMuteSavedVolumes),
        SavedChannels = settings.Get(SoundSettings.MicMuteSavedChannelVolumes),
        LegacySaved = settings.Get(SoundSettings.MicMuteSavedVolume),
    };

    public void Save(ISettingsStore settings)
    {
        if (Claimed is null)
        {
            settings.Reset(SoundSettings.MicMuteMutedDevices.Key);
        }
        else
        {
            settings.Set(SoundSettings.MicMuteMutedDevices, Claimed);
        }

        settings.Set(SoundSettings.MicMuteSavedVolumes, SavedVolumes);
        settings.Set(SoundSettings.MicMuteSavedChannelVolumes, SavedChannels);
    }

    public bool SameAs(MicMuteRecord other) =>
        SameList(Claimed, other.Claimed)
        && SavedVolumes.Count == other.SavedVolumes.Count && SavedVolumes.All(kv => other.SavedVolumes.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && SavedChannels.Count == other.SavedChannels.Count && SavedChannels.All(kv => other.SavedChannels.TryGetValue(kv.Key, out var c) && c.Count == kv.Value.Count && kv.Value.All(x => c.TryGetValue(x.Key, out var l) && l == x.Value));

    private static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);
}

/// <summary>Result of one sweep over the microphones.</summary>
public sealed record MicMuteSweep(MicMuteRecord Record, bool Partial, int Reached);

/// <summary>
/// The claim semantics of "Mute all microphones" (spec §3.13.3), run on the
/// audio thread so every sweep reads and writes the record in order. Mute
/// reaches every active capture endpoint (an app can record from a headset
/// that is not the default); unmute touches only what this app claimed.
/// </summary>
public static class MicMuteEngine
{
    /// <summary>At or below this a microphone counts as silent.</summary>
    public const double SilenceThreshold = 0.01;

    /// <summary>Level restored when nothing was saved.</summary>
    public const double FallbackRestoreLevel = 0.75;

    /// <summary>
    /// Already silent → left alone (still claimed only if claimed before);
    /// else the mute switch, counted only when it reads back as set; else the
    /// level goes to 0 with the old levels saved, claimed only if it reads back
    /// silent, otherwise the result is partial when an app records from it.
    /// Claims of absent devices are carried forward.
    /// </summary>
    public static MicMuteSweep Mute(IAudioEndpointAccess access, MicMuteRecord record)
    {
        var previouslyClaimed = (record.Claimed ?? []).ToHashSet(StringComparer.Ordinal);
        var claimed = new List<string>();
        var saved = record.SavedVolumes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var savedChannels = record.SavedChannels.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var partial = false;
        var reached = 0;

        foreach (var device in access.ActiveDevices(AudioFlow.Capture))
        {
            var id = device.Id;
            present.Add(id);
            var volume = access.ReadVolume(id);
            if (volume is null)
            {
                if (previouslyClaimed.Contains(id))
                {
                    claimed.Add(id);
                }

                continue;
            }

            reached++;
            if (volume.Muted || volume.Scalar <= SilenceThreshold)
            {
                if (previouslyClaimed.Contains(id))
                {
                    claimed.Add(id);
                }

                continue;
            }

            if (access.TrySetMute(id, true) && access.ReadVolume(id)?.Muted == true)
            {
                claimed.Add(id);
                continue;
            }

            // The mute switch did not take: fall back to level 0, remembering the levels.
            var channels = access.ReadChannelVolumes(id);
            saved[id] = volume.Scalar;
            if (channels is { Count: > 0 })
            {
                var levels = new Dictionary<string, double>(StringComparer.Ordinal);
                for (var channel = 0; channel < Math.Min(2, channels.Count); channel++)
                {
                    levels[channel.ToString(CultureInfo.InvariantCulture)] = channels[channel];
                }

                savedChannels[id] = levels;
            }

            if (volume.CanSetVolume && access.TrySetVolume(id, 0f) && access.ReadVolume(id) is { } after && after.Scalar <= SilenceThreshold)
            {
                claimed.Add(id);
            }
            else
            {
                saved.Remove(id);
                savedChannels.Remove(id);
                if (access.IsCapturing(id))
                {
                    partial = true;
                }
            }
        }

        foreach (var id in previouslyClaimed)
        {
            if (!present.Contains(id) && !claimed.Contains(id))
            {
                claimed.Add(id);
            }
        }

        return new MicMuteSweep(record with { Claimed = claimed, SavedVolumes = saved, SavedChannels = savedChannels }, partial, reached);
    }

    /// <summary>
    /// Restores only claimed devices (a missing claim list restores every
    /// present device; an empty one restores nothing): clears the mute switch
    /// if set, else gives back the saved level (per device, else the legacy
    /// value, else 0.75) with each channel's own level. A device that cannot be
    /// read keeps its claim; claims of absent devices are carried forward and
    /// released when they return.
    /// </summary>
    public static MicMuteSweep Unmute(IAudioEndpointAccess access, MicMuteRecord record)
    {
        var present = access.ActiveDevices(AudioFlow.Capture).Select(d => d.Id).ToList();
        var presentSet = present.ToHashSet(StringComparer.Ordinal);
        var targets = record.Claimed is null ? present : record.Claimed.Where(presentSet.Contains).ToList();
        var remaining = new List<string>();
        var saved = record.SavedVolumes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var savedChannels = record.SavedChannels.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var partial = false;
        var reached = 0;

        foreach (var id in targets)
        {
            var volume = access.ReadVolume(id);
            if (volume is null)
            {
                remaining.Add(id);
                continue;
            }

            reached++;
            if (volume.Muted)
            {
                if (!access.TrySetMute(id, false) || access.ReadVolume(id)?.Muted != false)
                {
                    remaining.Add(id);
                    partial = true;
                    continue;
                }
            }
            else if (volume.Scalar <= SilenceThreshold)
            {
                var level = saved.TryGetValue(id, out var own) && own > SilenceThreshold
                    ? own
                    : record.LegacySaved > SilenceThreshold ? record.LegacySaved : FallbackRestoreLevel;
                access.TrySetVolume(id, (float)level);
                if (savedChannels.TryGetValue(id, out var channels))
                {
                    foreach (var (channel, channelLevel) in channels)
                    {
                        if (int.TryParse(channel, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        {
                            access.TrySetChannelVolume(id, index, (float)channelLevel);
                        }
                    }
                }

                if (access.ReadVolume(id) is not { } after || after.Scalar <= SilenceThreshold)
                {
                    remaining.Add(id);
                    partial = true;
                    continue;
                }
            }

            saved.Remove(id);
            savedChannels.Remove(id);
        }

        if (record.Claimed is not null)
        {
            remaining.AddRange(record.Claimed.Where(id => !presentSet.Contains(id) && !remaining.Contains(id)));
        }

        return new MicMuteSweep(record with { Claimed = remaining, SavedVolumes = saved, SavedChannels = savedChannels }, partial, reached);
    }
}
