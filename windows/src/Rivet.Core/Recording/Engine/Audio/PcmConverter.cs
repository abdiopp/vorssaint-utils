// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Rivet.Core.Recording.Engine.Audio;

/// <summary>
/// Converts captured PCM of any common layout (8/16/24/32-bit integer or
/// 32/64-bit float, 1–32 channels, any rate) to the take format: 48 kHz
/// stereo 16-bit. WASAPI is asked for that format directly, so this is
/// normally a straight copy; it matters when an endpoint refuses the
/// conversion. Multichannel layouts are folded down with the usual
/// coefficients (centre and surrounds at −3 dB, LFE dropped); the rate is
/// changed by linear interpolation, carried across packets.
/// </summary>
public sealed class PcmConverter
{
    private const float Minus3dB = 0.70710678f;
    private float[] _stereo = new float[2048];
    private double _position;
    private float _previousLeft;
    private float _previousRight;

    public PcmConverter(PcmFormat source)
    {
        if (!source.IsValid)
        {
            throw new ArgumentException($"Unsupported PCM format {source}.", nameof(source));
        }

        Source = source;
    }

    public PcmFormat Source { get; }

    public static PcmFormat Target => PcmFormat.Take;

    /// <summary>Output frames <paramref name="inputFrames"/> input frames produce (approximately, for silence).</summary>
    public int EstimateOutputFrames(int inputFrames) =>
        Source.SampleRate == Target.SampleRate ? inputFrames : (int)Math.Round(inputFrames * (double)Target.SampleRate / Source.SampleRate);

    /// <summary>Converts one packet. <paramref name="output"/> grows as needed; returns the number of stereo frames written.</summary>
    public int Convert(ReadOnlySpan<byte> input, ref short[] output)
    {
        var frames = input.Length / Source.BlockAlign;
        if (frames <= 0)
        {
            return 0;
        }

        if (_stereo.Length < frames * 2)
        {
            _stereo = new float[frames * 2];
        }

        Downmix(input, frames, _stereo);

        if (Source.SampleRate == Target.SampleRate)
        {
            Ensure(ref output, frames);
            for (var i = 0; i < frames * 2; i++)
            {
                output[i] = ToShort(_stereo[i]);
            }

            return frames;
        }

        return Resample(frames, ref output);
    }

    private int Resample(int frames, ref short[] output)
    {
        var step = Source.SampleRate / (double)Target.SampleRate;
        Ensure(ref output, (int)Math.Ceiling((frames + 1) / step) + 2);
        var count = 0;
        while (true)
        {
            var i0 = (int)Math.Floor(_position);
            var i1 = i0 + 1;
            if (i1 > frames - 1)
            {
                break;
            }

            var frac = (float)(_position - i0);
            var (l0, r0) = i0 < 0 ? (_previousLeft, _previousRight) : (_stereo[i0 * 2], _stereo[(i0 * 2) + 1]);
            var l1 = _stereo[i1 * 2];
            var r1 = _stereo[(i1 * 2) + 1];
            output[count * 2] = ToShort(l0 + ((l1 - l0) * frac));
            output[(count * 2) + 1] = ToShort(r0 + ((r1 - r0) * frac));
            count++;
            _position += step;
        }

        // The last frame becomes index -1 of the next packet.
        _position -= frames;
        _previousLeft = _stereo[(frames - 1) * 2];
        _previousRight = _stereo[((frames - 1) * 2) + 1];
        return count;
    }

    private void Downmix(ReadOnlySpan<byte> input, int frames, float[] stereo)
    {
        var channels = Source.Channels;
        Span<float> frame = stackalloc float[Math.Min(channels, 32)];
        for (var f = 0; f < frames; f++)
        {
            var offset = f * Source.BlockAlign;
            for (var c = 0; c < frame.Length; c++)
            {
                frame[c] = ReadSample(input, offset + (c * Source.BytesPerSample));
            }

            var (left, right) = Fold(frame);
            stereo[f * 2] = left;
            stereo[(f * 2) + 1] = right;
        }
    }

    /// <summary>Standard WAVEFORMATEXTENSIBLE channel order: FL, FR, FC, LFE, BL, BR, SL, SR (fewer channels: the first ones).</summary>
    internal static (float Left, float Right) Fold(ReadOnlySpan<float> c)
    {
        switch (c.Length)
        {
            case 1:
                return (c[0], c[0]);
            case 2:
                return (c[0], c[1]);
            case 3: // FL FR FC
                return (c[0] + (Minus3dB * c[2]), c[1] + (Minus3dB * c[2]));
            case 4: // FL FR BL BR (quad)
                return (c[0] + (Minus3dB * c[2]), c[1] + (Minus3dB * c[3]));
            case 5: // FL FR FC BL BR
                return (c[0] + (Minus3dB * c[2]) + (Minus3dB * c[3]), c[1] + (Minus3dB * c[2]) + (Minus3dB * c[4]));
            case 6: // 5.1: FL FR FC LFE BL/SL BR/SR
                return (c[0] + (Minus3dB * c[2]) + (Minus3dB * c[4]), c[1] + (Minus3dB * c[2]) + (Minus3dB * c[5]));
            case 8: // 7.1: FL FR FC LFE BL BR SL SR
                return (c[0] + (Minus3dB * c[2]) + (Minus3dB * (c[4] + c[6])), c[1] + (Minus3dB * c[2]) + (Minus3dB * (c[5] + c[7])));
            default:
            {
                // Unknown layout: even channels to the left, odd to the right, averaged.
                float left = 0, right = 0;
                int nl = 0, nr = 0;
                for (var i = 0; i < c.Length; i++)
                {
                    if (i % 2 == 0)
                    {
                        left += c[i];
                        nl++;
                    }
                    else
                    {
                        right += c[i];
                        nr++;
                    }
                }

                return (nl > 0 ? left / nl : 0, nr > 0 ? right / nr : left / Math.Max(1, nl));
            }
        }
    }

    private float ReadSample(ReadOnlySpan<byte> data, int offset)
    {
        if (Source.IsFloat)
        {
            return Source.BitsPerSample == 64
                ? (float)BinaryPrimitives.ReadDoubleLittleEndian(data[offset..])
                : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        }

        return Source.BitsPerSample switch
        {
            8 => (data[offset] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768f,
            24 => ((data[offset] | (data[offset + 1] << 8) | ((sbyte)data[offset + 2] << 16))) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) / 2147483648f,
        };
    }

    private static short ToShort(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        var scaled = value * 32767f;
        return scaled >= 32767f ? short.MaxValue : scaled <= -32768f ? short.MinValue : (short)MathF.Round(scaled);
    }

    private static void Ensure(ref short[] buffer, int frames)
    {
        if (buffer.Length < frames * 2)
        {
            buffer = new short[Math.Max(frames * 2, buffer.Length * 2)];
        }
    }
}
