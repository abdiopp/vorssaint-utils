// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rivet.Core.Recording.Engine.Audio;

/// <summary>
/// A 48 kHz stereo 16-bit PCM WAV file written incrementally. The RIFF sizes
/// are refreshed every few seconds of audio, so a crash leaves a readable file
/// up to that point; <see cref="Complete"/> writes the final sizes. Files stop
/// growing at the 4 GB RIFF limit (about six hours).
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    public const int HeaderSize = 44;
    private const long MaxDataBytes = uint.MaxValue - HeaderSize - 8;
    private static readonly int BlockAlign = PcmFormat.Take.BlockAlign;

    private readonly FileStream _stream;
    private readonly long _headerInterval;
    private long _dataBytes;
    private long _bytesSinceHeader;
    private bool _completed;
    private readonly byte[] _zeros = new byte[16 * 1024];

    public WavFileWriter(string path, double headerRefreshSeconds = 5)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _headerInterval = (long)(Math.Max(0.5, headerRefreshSeconds) * PcmFormat.Take.SampleRate) * BlockAlign;
        WriteHeader();
    }

    public string Path { get; }

    public long FramesWritten => _dataBytes / BlockAlign;

    /// <summary>True once the 4 GB limit was reached; later audio is ignored.</summary>
    public bool IsFull { get; private set; }

    public void WriteFrames(ReadOnlySpan<short> interleaved)
    {
        var bytes = MemoryMarshal.AsBytes(interleaved);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("WAV writing assumes a little-endian CPU.");
        }

        WriteBytes(bytes);
    }

    public void WriteSilence(long frames)
    {
        var remaining = frames * BlockAlign;
        while (remaining > 0 && !IsFull)
        {
            var chunk = (int)Math.Min(remaining, _zeros.Length);
            WriteBytes(_zeros.AsSpan(0, chunk));
            remaining -= chunk;
        }
    }

    /// <summary>Drops audio after <paramref name="frames"/> (used when the last packets ran past the stop time).</summary>
    public void TruncateTo(long frames)
    {
        var bytes = Math.Max(0, frames) * BlockAlign;
        if (bytes >= _dataBytes)
        {
            return;
        }

        _stream.Flush();
        _stream.SetLength(HeaderSize + bytes);
        _stream.Position = HeaderSize + bytes;
        _dataBytes = bytes;
        IsFull = false;
    }

    /// <summary>Writes the final sizes and closes the file.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        UpdateHeader();
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
    }

    public void Dispose()
    {
        if (!_completed)
        {
            try
            {
                Complete();
            }
            catch (IOException)
            {
                _stream.Dispose();
            }
        }
    }

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (IsFull || bytes.IsEmpty)
        {
            return;
        }

        var room = MaxDataBytes - _dataBytes;
        if (bytes.Length > room)
        {
            bytes = bytes[..(int)(room - (room % BlockAlign))];
            IsFull = true;
        }

        _stream.Write(bytes);
        _dataBytes += bytes.Length;
        _bytesSinceHeader += bytes.Length;
        if (_bytesSinceHeader >= _headerInterval)
        {
            UpdateHeader();
        }
    }

    private void UpdateHeader()
    {
        _bytesSinceHeader = 0;
        var end = _stream.Position;
        _stream.Position = 4;
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)(36 + _dataBytes));
        _stream.Write(number);
        _stream.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)_dataBytes);
        _stream.Write(number);
        _stream.Position = end;
        _stream.Flush();
    }

    private void WriteHeader()
    {
        var format = PcmFormat.Take;
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1); // WAVE_FORMAT_PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)(format.SampleRate * format.BlockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], (ushort)format.BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0);
        _stream.Write(header);
    }
}
