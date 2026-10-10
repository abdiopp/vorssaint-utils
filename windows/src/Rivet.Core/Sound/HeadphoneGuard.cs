// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.Sound;

public enum HeadphoneGuardStepKind
{
    None,

    /// <summary>Lower <see cref="HeadphoneGuardStep.DeviceId"/> to <see cref="HeadphoneGuardStep.Level"/>.</summary>
    Lower,

    /// <summary>Give the pending record back (headphones are the default again).</summary>
    Restore,
}

public readonly record struct HeadphoneGuardStep(HeadphoneGuardStepKind Kind, string? DeviceId = null, double Level = 0);

/// <summary>
/// "Lower volume when headphones disconnect" (spec §3.12). Fed every device
/// snapshot; decides what to do and leaves the I/O to the caller, which then
/// reports back with <see cref="Lowered"/>. Windows can only see it when
/// headphones are their own endpoint: on many onboard codecs the jack and the
/// speakers are one endpoint switched internally, and nothing changes.
/// </summary>
public sealed class HeadphoneGuard
{
    /// <summary>Within this of the applied level, the speaker still holds the app's value.</summary>
    public const double Tolerance = 0.005;

    private string? _previousDefault;
    private bool _previousWasHeadphones;
    private string? _loweredDevice;

    /// <summary>
    /// Lower when: the previous default was headphones, it is no longer present
    /// as headphones, the new default exists and is not headphones, and this
    /// device was not already lowered since. Restore when headphones are the
    /// default and the remembered speaker is present (an absent speaker keeps
    /// the record for its return). Restoring happens even with the option off:
    /// what the app changed it gives back.
    /// </summary>
    public HeadphoneGuardStep Observe(AudioSnapshot snapshot, bool enabled, double level, HeadphoneGuardRestore? pending)
    {
        if (!snapshot.IsLive)
        {
            return default;
        }

        var currentId = snapshot.DefaultOutputId;
        var current = snapshot.Outputs.FirstOrDefault(d => d.Id == currentId);
        var step = default(HeadphoneGuardStep);

        if (current?.IsHeadphones == true)
        {
            _loweredDevice = null;
            if (pending is not null && snapshot.Outputs.Any(d => d.Id == pending.DeviceId))
            {
                step = new HeadphoneGuardStep(HeadphoneGuardStepKind.Restore, pending.DeviceId, pending.PreviousLevel);
            }
        }
        else if (enabled
                 && _previousWasHeadphones
                 && _previousDefault is not null
                 && !snapshot.Outputs.Any(d => d.Id == _previousDefault && d.IsHeadphones)
                 && current is not null
                 && _loweredDevice != current.Id)
        {
            step = new HeadphoneGuardStep(HeadphoneGuardStepKind.Lower, current.Id, Math.Clamp(level, 0.1, 1.0));
        }

        _previousDefault = currentId;
        _previousWasHeadphones = current?.IsHeadphones == true;
        return step;
    }

    /// <summary>The caller handled a lower step for <paramref name="deviceId"/> (whether or not it wrote).</summary>
    public void Lowered(string deviceId) => _loweredDevice = deviceId;

    /// <summary>Forgets transitions (observation restarted).</summary>
    public void Reset()
    {
        _previousDefault = null;
        _previousWasHeadphones = false;
        _loweredDevice = null;
    }

    /// <summary>
    /// The lower write, run on the audio thread. Only a higher level is
    /// lowered: a speaker already at or below the target is left alone (the
    /// macOS app raises it; this port deliberately never raises). Returns the
    /// record to remember, or null when nothing was written.
    /// </summary>
    public static HeadphoneGuardRestore? ApplyLower(IAudioEndpointAccess access, string deviceId, double level)
    {
        var volume = access.ReadVolume(deviceId);
        if (volume is null || !volume.CanSetVolume || volume.Scalar <= level + Tolerance)
        {
            return null;
        }

        return access.TrySetVolume(deviceId, (float)level)
            ? new HeadphoneGuardRestore(deviceId, volume.Scalar, level)
            : null;
    }

    /// <summary>
    /// The give-back, run on the audio thread: the previous level returns only
    /// if the speaker still holds the applied level (a manual change wins).
    /// Returns true when the record is settled (restored or forgotten), false
    /// when the speaker could not be read and the record should be kept.
    /// </summary>
    public static bool ApplyRestore(IAudioEndpointAccess access, HeadphoneGuardRestore record)
    {
        var volume = access.ReadVolume(record.DeviceId);
        if (volume is null)
        {
            return false;
        }

        if (Math.Abs(volume.Scalar - record.AppliedLevel) < Tolerance)
        {
            access.TrySetVolume(record.DeviceId, (float)record.PreviousLevel);
        }

        return true;
    }
}
