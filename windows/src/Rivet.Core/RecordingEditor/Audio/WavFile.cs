// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Rivet.Core.RecordingEditor.Audio;

/// <summary>
/// Random-access reader for the take's PCM WAV tracks (<c>system.wav</c>,
/// <c>mic.wav</c>): 16/24/32-bit integer or 32-bit float, any channel count,
/// read as stereo float. A file whose header sizes were never finalized
/// (crash while recording) is read up to its real length. Not thread-safe.
/// </summary>
public sealed class WavFile : IDisposable
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly FileStream _stream;
    private readonly long _dataOffset;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private byte[] _buffer = new byte[64 * 1024];

    private WavFile(FileStream stream, long dataOffset, long frameCount, int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        _stream = stream;
        _dataOffset = dataOffset;
        FrameCount = frameCount;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        _bytesPerSample = bitsPerSample / 8;
        _isFloat = isFloat;
    }

    public long FrameCount { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    public double Duration => FrameCount / (double)SampleRate;

    /// <summary>Opens a WAV file; null when it is missing, not a WAV, or in an unsupported encoding.</summary>
    public static WavFile? Open(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
            var header = new byte[12];
            if (stream.Read(header, 0, 12) != 12 || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                stream.Dispose();
                return null;
            }

            ushort format = 0;
            int channels = 0, sampleRate = 0, bits = 0;
            var chunk = new byte[8];
            while (stream.Read(chunk, 0, 8) == 8)
            {
                var id = chunk.AsSpan(0, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                if (id.SequenceEqual("fmt "u8))
                {
                    var fmt = new byte[Math.Max(16, (int)Math.Min(size, 64))];
                    if (stream.Read(fmt, 0, fmt.Length) < 16)
                    {
                        break;
                    }

                    format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                    sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                    if (format == FormatExtensible && fmt.Length >= 26)
                    {
                        // SubFormat GUID starts at offset 24; its first two bytes are the format tag.
                        format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                    }

                    stream.Position += Math.Max(0, size - fmt.Length) + (size & 1);
                }
                else if (id.SequenceEqual("data"u8))
                {
                    if (channels <= 0 || sampleRate <= 0 || bits is not (16 or 24 or 32) || format is not (FormatPcm or FormatFloat)
                        || (format == FormatFloat && bits != 32))
                    {
                        break;
                    }

                    var dataOffset = stream.Position;
                    var available = stream.Length - dataOffset;
                    var declared = size is 0 or uint.MaxValue ? available : Math.Min(size, available);
                    var frameBytes = channels * (bits / 8);
                    return new WavFile(stream, dataOffset, declared / frameBytes, sampleRate, channels, bits, format == FormatFloat);
                }
                else
                {
                    stream.Position += size + (size & 1);
                }
            }

            stream.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Reads <paramref name="frames"/> stereo frames starting at
    /// <paramref name="startFrame"/> (file rate); frames outside the file are silence.
    /// </summary>
    public void ReadStereo(long startFrame, Span<float> interleaved, int frames)
    {
        interleaved[..(frames * 2)].Clear();
        var first = Math.Max(0, startFrame);
        var last = Math.Min(FrameCount, startFrame + frames);
        if (last <= first)
        {
            return;
        }

        var frameBytes = Channels * _bytesPerSample;
        var outIndex = (int)(first - startFrame);
        var position = first;
        while (position < last)
        {
            var chunkFrames = (int)Math.Min(last - position, _buffer.Length / frameBytes);
            if (chunkFrames <= 0)
            {
                _buffer = new byte[frameBytes * 1024];
                continue;
            }

            _stream.Position = _dataOffset + (position * frameBytes);
            var wanted = chunkFrames * frameBytes;
            var read = 0;
            while (read < wanted)
            {
                var n = _stream.Read(_buffer, read, wanted - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            var gotFrames = read / frameBytes;
            for (var f = 0; f < gotFrames; f++)
            {
                var offset = f * frameBytes;
                var left = Sample(offset);
                var right = Channels > 1 ? Sample(offset + _bytesPerSample) : left;
                interleaved[(outIndex + f) * 2] = left;
                interleaved[((outIndex + f) * 2) + 1] = right;
            }

            if (gotFrames < chunkFrames)
            {
                return;
            }

            outIndex += chunkFrames;
            position += chunkFrames;
        }
    }

    private float Sample(int offset)
    {
        var span = _buffer.AsSpan(offset);
        if (_isFloat)
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(span);
            return float.IsFinite(v) ? v : 0;
        }

        return _bytesPerSample switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768f,
            3 => ((span[0] << 8) | (span[1] << 16) | (span[2] << 24)) / 2147483648f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648f,
        };
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Writes a 32-bit float stereo WAV (tests and the sample take).</summary>
    public static void WriteFloatStereo(string path, ReadOnlySpan<float> interleaved, int sampleRate)
    {
        using var stream = File.Create(path);
        var dataBytes = interleaved.Length * 4;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], FormatFloat);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(sampleRate * 8));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        stream.Write(header);
        var bytes = new byte[4];
        foreach (var sample in interleaved)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, sample);
            stream.Write(bytes);
        }
    }
}
