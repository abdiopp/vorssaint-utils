// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Recording.Engine.Audio;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public sealed class AudioTrackWriterTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-audio-" + Guid.NewGuid());

    public AudioTrackWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Starts_at_zero_with_silence_before_the_first_packet()
    {
        var clock = Started(origin: 100);
        var path = Path.Combine(_folder, "a.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(4800, 1000), PcmFormat.Take, 100.5, false); // arrives half a second in
            writer.Finish(1.0);
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(Rate, frames);
        Assert.All(samples.Take(24_000 * 2), s => Assert.Equal(0, s));
        Assert.Equal(1000, samples[24_000 * 2]);
        Assert.All(samples.Skip(28_800 * 2), s => Assert.Equal(0, s)); // padded to the stop time
    }

    [Fact]
    public void Gaps_are_filled_and_jitter_is_absorbed()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "b.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(480, 1), PcmFormat.Take, 0.000, false);
            writer.Append(Packet(480, 2), PcmFormat.Take, 0.015, false); // 5 ms late: contiguous
            writer.Append(Packet(480, 3), PcmFormat.Take, 1.000, false); // loopback was quiet: a real gap
            writer.Finish(1.010);
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(48_480, frames);
        Assert.Equal(2, samples[480 * 2]);                // second packet right after the first
        Assert.Equal(0, samples[960 * 2]);                // silence in the gap
        Assert.Equal(3, samples[48_000 * 2]);             // third packet at exactly 1.000 s
    }

    [Fact]
    public void Overlaps_beyond_the_tolerance_trim_the_head()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "c.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(4800, 1), PcmFormat.Take, 0.0, false);  // 0.0 – 0.1 s
            writer.Append(Packet(4800, 2), PcmFormat.Take, 0.05, false); // claims 0.05 s: 50 ms overlap
            writer.Finish(0.15);
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(7200, frames);
        Assert.Equal(1, samples[4799 * 2]);
        Assert.Equal(2, samples[4800 * 2]);
    }

    [Fact]
    public void Packets_touching_a_pause_are_dropped_and_the_track_stays_continuous()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "d.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(48_000, 1), PcmFormat.Take, 0.0, false); // 0–1 s
            clock.Pause(1.0);
            writer.Append(Packet(48_000, 9), PcmFormat.Take, 1.0, false); // during the pause
            clock.Resume(3.0);
            writer.Append(Packet(48_000, 2), PcmFormat.Take, 3.0, false); // 1–2 s on the timeline
            Assert.Equal(2, writer.PacketsAccepted);
            writer.Finish(clock.Elapsed(4.0));
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(2 * Rate, frames);
        Assert.DoesNotContain((short)9, samples);
        Assert.Equal(1, samples[(Rate - 1) * 2]);
        Assert.Equal(2, samples[Rate * 2]);
    }

    [Fact]
    public void Packets_before_the_origin_are_dropped()
    {
        var clock = Started(10);
        var path = Path.Combine(_folder, "e.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(480, 5), PcmFormat.Take, 9.995, false);
            Assert.Equal(0, writer.PacketsAccepted);
            writer.Finish(0.5);
        }

        var samples = ReadWav(path, out _);
        Assert.All(samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public void Silent_packets_write_zeros_and_audio_past_the_stop_is_trimmed()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "f.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(Packet(4800, 7), PcmFormat.Take, 0.0, silent: true);
            writer.Append(Packet(48_000, 8), PcmFormat.Take, 0.1, false);
            writer.Finish(0.5);
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(24_000, frames);
        Assert.All(samples.Take(4800 * 2), s => Assert.Equal(0, s));
        Assert.Equal(8, samples[4800 * 2]);
    }

    [Fact]
    public void Float_44k_mono_is_converted_to_48k_stereo_pcm()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "g.wav");
        var format = new PcmFormat(44_100, 1, 32, true);
        var packet = new byte[4410 * 4];
        for (var i = 0; i < 4410; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(i * 4), 0.5f);
        }

        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Append(packet, format, 0, false);
            writer.Finish(0.1);
        }

        var samples = ReadWav(path, out var frames);
        Assert.Equal(4800, frames);
        Assert.InRange(samples[100 * 2], 16_380, 16_386);
        Assert.Equal(samples[100 * 2], samples[(100 * 2) + 1]); // both channels
    }

    [Fact]
    public void The_header_describes_48k_stereo_16_bit_pcm()
    {
        var clock = Started(0);
        var path = Path.Combine(_folder, "h.wav");
        using (var writer = new AudioTrackWriter(path, clock))
        {
            writer.Finish(0.25);
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal((uint)(bytes.Length - 44), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(12_000 * 4, bytes.Length - 44);
    }

    private static PauseClock Started(double origin)
    {
        var clock = new PauseClock();
        clock.Begin(origin);
        return clock;
    }

    private static byte[] Packet(int frames, short value)
    {
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames * 2; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), value);
        }

        return bytes;
    }

    private static short[] ReadWav(string path, out long frames)
    {
        var bytes = File.ReadAllBytes(path);
        var dataBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        Assert.Equal(bytes.Length - 44, dataBytes);
        frames = dataBytes / 4;
        var samples = new short[dataBytes / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (i * 2)));
        }

        return samples;
    }
}

public class PcmConverterTests
{
    [Fact]
    public void Folds_surround_layouts_to_stereo()
    {
        var (l, r) = PcmConverter.Fold([1f, 0f, 0.5f, 1f, 0.2f, 0f]); // 5.1: FL FR FC LFE BL BR
        Assert.Equal(1 + (0.7071f * 0.5f) + (0.7071f * 0.2f), l, 3);
        Assert.Equal(0.7071f * 0.5f, r, 3);
        Assert.Equal((0.25f, 0.25f), PcmConverter.Fold([0.25f]));
    }

    [Fact]
    public void Reads_24_bit_samples()
    {
        var converter = new PcmConverter(new PcmFormat(48_000, 2, 24, false));
        byte[] packet = [0x00, 0x00, 0x40, 0x00, 0x00, 0xC0]; // +0.5, -0.5
        var output = new short[2];
        Assert.Equal(1, converter.Convert(packet, ref output));
        Assert.InRange(output[0], 16_382, 16_384);
        Assert.InRange(output[1], -16_384, -16_382);
    }

    [Fact]
    public void Resampling_keeps_the_frame_count_over_many_packets()
    {
        var converter = new PcmConverter(new PcmFormat(44_100, 2, 16, false));
        var output = new short[16];
        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            total += converter.Convert(new byte[441 * 4], ref output); // 10 ms each
        }

        Assert.InRange(total, 47_990, 48_000);
    }
}
