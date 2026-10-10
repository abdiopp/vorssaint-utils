// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.RecordingEditor;
using Rivet.Core.RecordingEditor.Audio;
using Xunit;

namespace Rivet.Core.Tests.RecordingEditor;

public sealed class AudioTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-audio-" + Guid.NewGuid());

    public AudioTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static float[] Sine(double seconds, double frequency, double amplitude, double startAt = 0)
    {
        var frames = (int)(seconds * Rate);
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)Rate;
            var v = t < startAt ? 0 : (float)(amplitude * Math.Sin(2 * Math.PI * frequency * t));
            data[i * 2] = v;
            data[(i * 2) + 1] = v;
        }

        return data;
    }

    private static double Rms(ReadOnlySpan<float> data)
    {
        double sum = 0;
        foreach (var v in data)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum / Math.Max(1, data.Length));
    }

    /// <summary>Frequency from upward zero crossings of the left channel.</summary>
    private static double Frequency(ReadOnlySpan<float> data, int skipFrames)
    {
        var frames = data.Length / 2;
        var crossings = new List<int>();
        for (var i = skipFrames + 1; i < frames - skipFrames; i++)
        {
            if (data[(i - 1) * 2] < 0 && data[i * 2] >= 0)
            {
                crossings.Add(i);
            }
        }

        return crossings.Count < 2 ? 0 : (crossings.Count - 1) * Rate / (double)(crossings[^1] - crossings[0]);
    }

    private string Write(string name, float[] data)
    {
        var path = Path.Combine(_folder, name);
        WavFile.WriteFloatStereo(path, data, Rate);
        return path;
    }

    [Fact]
    public void Wav_round_trips_and_reads_past_the_end_as_silence()
    {
        var path = Write("a.wav", Sine(0.5, 440, 0.5));
        using var file = WavFile.Open(path)!;
        Assert.Equal(Rate / 2, file.FrameCount);
        Assert.Equal(2, file.Channels);
        var buffer = new float[200];
        file.ReadStereo(file.FrameCount - 50, buffer, 100);
        Assert.All(buffer.AsSpan(100).ToArray(), v => Assert.Equal(0, v));
        Assert.Null(WavFile.Open(Path.Combine(_folder, "missing.wav")));
        File.WriteAllText(Path.Combine(_folder, "bad.wav"), "not a wav");
        Assert.Null(WavFile.Open(Path.Combine(_folder, "bad.wav")));
    }

    [Fact]
    public void Mix_keeps_only_kept_ranges_and_applies_gain()
    {
        // 0.4-amplitude sine at 50 % gain → RMS in [0.09, 0.18].
        var path = Write("sys.wav", Sine(4, 440, 0.4));
        var timeline = new EditTimeline(4, 0, 4, [new CutRange(1, 2)]);
        using var mix = new EditedAudioMix([new AudioTrackInput(path, () => 0.5)], timeline.KeptRanges);
        Assert.Equal(3 * Rate, mix.TotalFrames);
        var output = new float[mix.TotalFrames * 2];
        var read = mix.Read(output);
        Assert.Equal(mix.TotalFrames, read);
        Assert.InRange(Rms(output), 0.09, 0.18);
        Assert.Equal(0, mix.Read(new float[10]));
        mix.Seek(1.5);
        Assert.Equal((long)(1.5 * Rate), mix.Position);
    }

    [Fact]
    public void A_late_microphone_stays_late_and_a_muted_track_is_silent()
    {
        var system = Write("system.wav", Sine(3, 440, 0.4));
        var mic = Write("mic.wav", Sine(3, 660, 0.4, startAt: 1.3));
        var timeline = new EditTimeline(3, 0, 3, []);
        using var mix = new EditedAudioMix([new AudioTrackInput(system, () => 0), new AudioTrackInput(mic, () => 1)], timeline.KeptRanges);
        var output = new float[mix.TotalFrames * 2];
        mix.Read(output);
        Assert.True(Rms(output.AsSpan(0, (int)(1.25 * Rate) * 2)) < 1e-6, "silent before the microphone starts");
        Assert.True(Rms(output.AsSpan((int)(1.35 * Rate) * 2, Rate)) > 0.2);
    }

    [Fact]
    public void Gains_apply_live()
    {
        var path = Write("live.wav", Sine(2, 440, 0.4));
        var gain = 1.0;
        using var mix = new EditedAudioMix([new AudioTrackInput(path, () => gain)], new EditTimeline(2, 0, 2, []).KeptRanges);
        var first = new float[Rate];
        mix.Read(first);
        gain = 0;
        var second = new float[Rate];
        mix.Read(second);
        Assert.True(Rms(first) > 0.2);
        Assert.Equal(0, Rms(second));
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.37)]
    [InlineData(2)]
    [InlineData(4)]
    public void Time_stretch_keeps_pitch_level_and_exact_length(double speed)
    {
        var input = Sine(2, 880, 0.4);
        var output = TimeStretcher.Process(input, speed);
        var expectedFrames = (long)Math.Round(input.Length / 2 / speed, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedFrames * 2, output.Length);
        var frequency = Frequency(output, Rate / 20);
        Assert.InRange(frequency, 880 - 70, 880 + 70);
        var middle = output.AsSpan(output.Length / 4, output.Length / 2);
        Assert.InRange(Rms(middle), 0.4 / Math.Sqrt(2) * 0.8, 0.4 / Math.Sqrt(2) * 1.2);
    }

    [Fact]
    public void Time_stretch_streams_in_chunks_like_the_whole_buffer()
    {
        var input = Sine(1.5, 440, 0.3);
        var whole = TimeStretcher.Process(input, 1.5);
        var stretcher = new TimeStretcher(1.5);
        var output = new List<float>();
        var chunk = new float[1000 * 2];
        for (var offset = 0; offset < input.Length; offset += 3000)
        {
            stretcher.Write(input.AsSpan(offset, Math.Min(3000, input.Length - offset)));
            int n;
            while ((n = stretcher.Read(chunk)) > 0)
            {
                output.AddRange(chunk.AsSpan(0, n * 2).ToArray());
            }
        }

        stretcher.EndOfInput();
        int m;
        while ((m = stretcher.Read(chunk)) > 0)
        {
            output.AddRange(chunk.AsSpan(0, m * 2).ToArray());
        }

        Assert.Equal(whole.Length, output.Count);
        Assert.Equal(whole, output.ToArray());
    }

    [Fact]
    public void Waveform_has_220_peaks()
    {
        var path = Write("wave.wav", Sine(2, 220, 0.5, startAt: 1));
        var peaks = Waveform.Compute(path, 2)!;
        Assert.Equal(220, peaks.Length);
        Assert.True(peaks[10] < 0.01);
        Assert.InRange(peaks[200], 0.45, 0.51);
    }
}
