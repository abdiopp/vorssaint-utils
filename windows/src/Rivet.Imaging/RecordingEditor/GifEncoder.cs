// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// An animated GIF89a writer (spec 02 §6.18): one local adaptive palette per
/// frame, optional dithering, LZW compression, a NETSCAPE2.0 loop block
/// (0 = forever), a fixed delay per frame and consistent "do not dispose"
/// frames without transparency. Encodes into memory; the caller writes the
/// file once at the end, so a cancel never leaves a partial GIF behind.
/// </summary>
public sealed class GifEncoder
{
    private readonly MemoryStream _stream = new();
    private readonly GifQuantizer _quantizer = new();
    private readonly LzwEncoder _lzw = new();
    private bool _finished;

    public GifEncoder(int width, int height, int loopCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _stream.Write("GIF89a"u8);
        WriteUInt16(width);
        WriteUInt16(height);
        _stream.WriteByte(0x70);   // no global colour table, 8-bit colour resolution
        _stream.WriteByte(0);      // background colour index
        _stream.WriteByte(0);      // pixel aspect ratio
        // NETSCAPE2.0 application extension: loop count.
        _stream.Write([0x21, 0xFF, 0x0B]);
        _stream.Write("NETSCAPE2.0"u8);
        _stream.Write([0x03, 0x01]);
        WriteUInt16(loopCount);
        _stream.WriteByte(0);
    }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount { get; private set; }

    /// <summary>Adds an opaque BGRA frame of exactly <see cref="Width"/> × <see cref="Height"/>.</summary>
    public void AddFrame(PixelBuffer frame, int delayCentiseconds, bool dither = true)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The GIF is already finished.");
        }

        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException("Frame size differs from the GIF size.", nameof(frame));
        }

        var indices = _quantizer.Quantize(frame.Pixels, frame.Width, frame.Height, frame.Stride, dither);
        var palette = _quantizer.Palette;
        var entries = palette.Length / 3;
        var tableBits = 1;
        while ((1 << tableBits) < entries)
        {
            tableBits++;
        }

        // Graphic control extension: disposal 1 (leave in place), no transparency.
        _stream.Write([0x21, 0xF9, 0x04, 0x04]);
        WriteUInt16(Math.Clamp(delayCentiseconds, 0, ushort.MaxValue));
        _stream.Write([0x00, 0x00]);

        // Image descriptor with a local colour table.
        _stream.WriteByte(0x2C);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt16(Width);
        WriteUInt16(Height);
        _stream.WriteByte((byte)(0x80 | (tableBits - 1)));
        var table = new byte[3 * (1 << tableBits)];
        Array.Copy(palette, table, palette.Length);
        _stream.Write(table);

        var minCodeSize = Math.Max(2, tableBits);
        _stream.WriteByte((byte)minCodeSize);
        _lzw.Encode(indices, minCodeSize, _stream);
        _stream.WriteByte(0);   // block terminator
        FrameCount++;
    }

    /// <summary>Writes the trailer and returns the file's bytes.</summary>
    public byte[] Finish()
    {
        if (!_finished)
        {
            _stream.WriteByte(0x3B);
            _finished = true;
        }

        return _stream.ToArray();
    }

    private void WriteUInt16(int value)
    {
        _stream.WriteByte((byte)(value & 0xFF));
        _stream.WriteByte((byte)((value >> 8) & 0xFF));
    }

    /// <summary>
    /// GIF-flavoured LZW (variable code width up to 12 bits, no early change),
    /// following the classic encoder's code-size bookkeeping, packed LSB-first
    /// into sub-blocks of at most 255 bytes.
    /// </summary>
    private sealed class LzwEncoder
    {
        private const int MaxBits = 12;
        private const int MaxMaxCode = 1 << MaxBits;
        private readonly int[] _codes = new int[MaxMaxCode * 256];
        private readonly int[] _stamps = new int[MaxMaxCode * 256];
        private readonly byte[] _block = new byte[256];
        private int _stamp;
        private int _blockLength;
        private int _accumulator;
        private int _accumulatedBits;
        private Stream _output = Stream.Null;
        private int _bits;
        private int _maxCode;
        private int _initBits;
        private int _clearCode;
        private int _eofCode;
        private int _freeEntry;
        private bool _clearFlag;

        public void Encode(byte[] pixels, int minCodeSize, Stream output)
        {
            _output = output;
            _blockLength = 0;
            _accumulator = 0;
            _accumulatedBits = 0;
            _initBits = minCodeSize + 1;
            _bits = _initBits;
            _maxCode = (1 << _bits) - 1;
            _clearCode = 1 << minCodeSize;
            _eofCode = _clearCode + 1;
            _freeEntry = _clearCode + 2;
            _clearFlag = false;
            ResetTable();

            Output(_clearCode);
            if (pixels.Length == 0)
            {
                Output(_eofCode);
                FlushBits();
                return;
            }

            var prefix = (int)pixels[0];
            for (var i = 1; i < pixels.Length; i++)
            {
                int c = pixels[i];
                var key = (prefix << 8) | c;
                if (_stamps[key] == _stamp)
                {
                    prefix = _codes[key];
                    continue;
                }

                Output(prefix);
                prefix = c;
                if (_freeEntry < MaxMaxCode)
                {
                    _codes[key] = _freeEntry++;
                    _stamps[key] = _stamp;
                }
                else
                {
                    ResetTable();
                    _freeEntry = _clearCode + 2;
                    _clearFlag = true;
                    Output(_clearCode);
                }
            }

            Output(prefix);
            Output(_eofCode);
            FlushBits();
        }

        private void ResetTable()
        {
            _stamp++;
            if (_stamp == int.MaxValue)
            {
                Array.Clear(_stamps);
                _stamp = 1;
            }
        }

        private void Output(int code)
        {
            _accumulator |= code << _accumulatedBits;
            _accumulatedBits += _bits;
            while (_accumulatedBits >= 8)
            {
                AddByte((byte)(_accumulator & 0xFF));
                _accumulator >>= 8;
                _accumulatedBits -= 8;
            }

            if (_freeEntry > _maxCode || _clearFlag)
            {
                if (_clearFlag)
                {
                    _bits = _initBits;
                    _maxCode = (1 << _bits) - 1;
                    _clearFlag = false;
                }
                else
                {
                    _bits++;
                    _maxCode = _bits == MaxBits ? MaxMaxCode : (1 << _bits) - 1;
                }
            }
        }

        private void FlushBits()
        {
            while (_accumulatedBits > 0)
            {
                AddByte((byte)(_accumulator & 0xFF));
                _accumulator >>= 8;
                _accumulatedBits -= 8;
            }

            _accumulatedBits = 0;
            _accumulator = 0;
            if (_blockLength > 0)
            {
                WriteBlock();
            }
        }

        private void AddByte(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == 255)
            {
                WriteBlock();
            }
        }

        private void WriteBlock()
        {
            _output.WriteByte((byte)_blockLength);
            _output.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
