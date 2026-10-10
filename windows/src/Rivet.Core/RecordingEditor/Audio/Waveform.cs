// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor.Audio;

/// <summary>Audio lane waveform: 220 peak buckets over the whole recording (spec 02 §6.20).</summary>
public static class Waveform
{
    public const int BucketCount = 220;

    /// <summary>
    /// Peak of |sample| across channels per bucket, clamped to 1. Buckets cover
    /// the source duration <paramref name="duration"/> (the video's), so the
    /// waveform lines up with the filmstrip. Null when the file is unreadable.
    /// </summary>
    public static float[]? Compute(string path, double duration, CancellationToken cancellationToken = default, int count = BucketCount)
    {
        using var file = WavFile.Open(path);
        if (file is null)
        {
            return null;
        }

        var peaks = new float[count];
        var total = duration > 0 ? duration : file.Duration;
        if (total <= 0)
        {
            return peaks;
        }

        const int block = 8192;
        var buffer = new float[block * 2];
        for (long start = 0; start < file.FrameCount; start += block)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frames = (int)Math.Min(block, file.FrameCount - start);
            file.ReadStereo(start, buffer, frames);
            for (var f = 0; f < frames; f++)
            {
                var t = (start + f) / (double)file.SampleRate;
                var bucket = Math.Min(count - 1, Math.Max(0, (int)Math.Floor(t / total * count)));
                var peak = Math.Max(Math.Abs(buffer[f * 2]), Math.Abs(buffer[(f * 2) + 1]));
                if (peak > peaks[bucket])
                {
                    peaks[bucket] = Math.Min(1f, peak);
                }
            }
        }

        return peaks;
    }
}
