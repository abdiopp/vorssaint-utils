// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor.Audio;

/// <summary>One source track of the mix: a WAV file and its live gain (0 when removed).</summary>
public sealed record AudioTrackInput(string Path, Func<double> Gain);

/// <summary>
/// The edited timeline's audio at 1×: only the kept ranges, joined, every
/// track at its gain, mixed to one stereo stream (spec 02 §6.16 step 7,
/// §6.17). Every track starts at source time 0, so a microphone that started
/// late stays late. Gains are read on every <see cref="Read"/>, so preview
/// volume changes apply without rebuilding anything.
/// </summary>
public sealed class EditedAudioMix : IAudioFeed, IDisposable
{
    private readonly List<(WavFile File, Func<double> Gain)> _tracks = [];
    private readonly (long Start, long Length)[] _ranges;
    private float[] _scratch = new float[4096 * 2];
    private float[] _resample = new float[4096 * 2];
    private int _range;
    private long _offset;

    public EditedAudioMix(IEnumerable<AudioTrackInput> tracks, IReadOnlyList<TimeRange> keptRanges, int sampleRate = ExportMath.AudioSampleRate)
    {
        SampleRate = sampleRate;
        foreach (var track in tracks)
        {
            if (WavFile.Open(track.Path) is { } file)
            {
                _tracks.Add((file, track.Gain));
            }
        }

        _ranges = keptRanges
            .Select(r =>
            {
                var start = (long)RecorderMath.Round(r.Start * sampleRate);
                var end = (long)RecorderMath.Round(r.End * sampleRate);
                return (start, Math.Max(0, end - start));
            })
            .Where(r => r.Item2 > 0)
            .ToArray();
        TotalFrames = _ranges.Sum(r => r.Length);
    }

    public int SampleRate { get; }

    /// <summary>Tracks that could be opened.</summary>
    public int TrackCount => _tracks.Count;

    public long TotalFrames { get; }

    public long Position
    {
        get
        {
            long p = 0;
            for (var i = 0; i < _range && i < _ranges.Length; i++)
            {
                p += _ranges[i].Length;
            }

            return p + _offset;
        }
    }

    public void Seek(double outputTime)
    {
        var target = (long)RecorderMath.Round(Math.Max(0, outputTime) * SampleRate);
        _range = 0;
        _offset = 0;
        while (_range < _ranges.Length && target >= _ranges[_range].Length)
        {
            target -= _ranges[_range].Length;
            _range++;
        }

        _offset = _range < _ranges.Length ? target : 0;
    }

    public int Read(Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        var written = 0;
        while (written < frames && _range < _ranges.Length)
        {
            var (start, length) = _ranges[_range];
            var chunk = (int)Math.Min(frames - written, length - _offset);
            var output = interleaved.Slice(written * 2, chunk * 2);
            MixInto(output, start + _offset, chunk);
            written += chunk;
            _offset += chunk;
            if (_offset >= length)
            {
                _range++;
                _offset = 0;
            }
        }

        return written;
    }

    /// <summary>Mixes frames [sourceFrame, sourceFrame + count) of every track (output-rate frame numbers).</summary>
    private void MixInto(Span<float> output, long sourceFrame, int count)
    {
        output.Clear();
        if (_scratch.Length < count * 2)
        {
            _scratch = new float[count * 2];
        }

        foreach (var (file, gainOf) in _tracks)
        {
            var gain = (float)Math.Clamp(gainOf(), 0, 1);
            if (gain <= 0)
            {
                continue;
            }

            var scratch = _scratch.AsSpan(0, count * 2);
            if (file.SampleRate == SampleRate)
            {
                file.ReadStereo(sourceFrame, scratch, count);
            }
            else
            {
                ReadResampled(file, sourceFrame, scratch, count);
            }

            for (var i = 0; i < count * 2; i++)
            {
                output[i] += scratch[i] * gain;
            }
        }

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = Math.Clamp(output[i], -1f, 1f);
        }
    }

    /// <summary>Linear resampling from the file's rate.</summary>
    private void ReadResampled(WavFile file, long outputFrame, Span<float> destination, int count)
    {
        var ratio = file.SampleRate / (double)SampleRate;
        var first = outputFrame * ratio;
        var firstFrame = (long)Math.Floor(first);
        var needed = (int)Math.Ceiling((count * ratio) + 2);
        if (_resample.Length < needed * 2)
        {
            _resample = new float[needed * 2];
        }

        var source = _resample.AsSpan(0, needed * 2);
        file.ReadStereo(firstFrame, source, needed);
        for (var i = 0; i < count; i++)
        {
            var position = first + (i * ratio) - firstFrame;
            var index = (int)Math.Floor(position);
            var frac = (float)(position - index);
            if (index + 1 >= needed)
            {
                index = needed - 2;
                frac = 1;
            }

            destination[i * 2] = source[index * 2] + ((source[(index + 1) * 2] - source[index * 2]) * frac);
            destination[(i * 2) + 1] = source[(index * 2) + 1] + ((source[((index + 1) * 2) + 1] - source[(index * 2) + 1]) * frac);
        }
    }

    public void Dispose()
    {
        foreach (var (file, _) in _tracks)
        {
            file.Dispose();
        }

        _tracks.Clear();
    }
}
