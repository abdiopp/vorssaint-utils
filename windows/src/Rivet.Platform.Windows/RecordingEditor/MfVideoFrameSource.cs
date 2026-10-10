// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>
/// Decodes the master with the Media Foundation Source Reader (spec 02 §7 #13):
/// software H.264 decode, the source reader's advanced video processor
/// converts (and, for the preview, scales) to RGB32, which is copied into a
/// BGRA buffer with an opaque alpha. Accurate seeking: seek to the key frame
/// before the time, then decode forward to the latest frame at or before it.
/// A small cache keeps recent frames for back-and-forth scrubbing.
/// </summary>
internal sealed class MfVideoFrameSource : IVideoFrameSource
{
    private const double Epsilon = 0.0005;
    private const double SeekForwardLimit = 1.5;

    private readonly IMFSourceReader _reader;
    private readonly int _cacheSize;
    private readonly LinkedList<CachedFrame> _cache = new();
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly (int X, int Y, int Width, int Height) _crop;
    private int _stride;
    private IMFSample? _nextSample;
    private double _nextTime = double.NaN;
    private CachedFrame? _current;
    private bool _endOfStream;
    private bool _disposed;

    public MfVideoFrameSource(string path, VideoOpenOptions? options)
    {
        MediaFoundationRuntime.EnsureStarted();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The recording is missing.", path);
        }

        _cacheSize = Math.Max(2, options?.CacheSize ?? 8);
        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
            _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        }

        try
        {
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            int nativeW, nativeH;
            (int X, int Y, int W, int H) aperture;
            using (var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                MediaFactory.MFGetAttributeSize(native, MediaTypeAttributeKeys.FrameSize, out var w, out var h).CheckError();
                nativeW = (int)w;
                nativeH = (int)h;
                aperture = Aperture(native, nativeW, nativeH);
                if (MediaFactory.MFGetAttributeRatio(native, MediaTypeAttributeKeys.FrameRate, out var num, out var den).Success && den > 0)
                {
                    NominalFrameRate = num / (double)den;
                }
            }

            VideoWidth = Math.Max(2, aperture.W & ~1);
            VideoHeight = Math.Max(2, aperture.H & ~1);

            // Scale the whole coded frame by the same factor and crop the aperture afterwards,
            // so a 1920×1088 coded frame with a 1080-line picture is never squeezed.
            var scale = 1.0;
            if (options?.Width is { } requestedW && requestedW > 0 && requestedW < VideoWidth)
            {
                scale = requestedW / (double)VideoWidth;
            }

            _outputWidth = Math.Max(2, (int)Math.Round(nativeW * scale) & ~1);
            _outputHeight = Math.Max(2, (int)Math.Round(nativeH * scale) & ~1);
            using (var output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)_outputWidth, (uint)_outputHeight));
                try
                {
                    _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
                }
                catch (SharpGenException) when (scale < 1)
                {
                    // Some processors refuse to scale: decode at the coded size instead.
                    scale = 1;
                    _outputWidth = nativeW;
                    _outputHeight = nativeH;
                    output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)nativeW, (uint)nativeH));
                    _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
                }
            }

            _crop = (
                (int)Math.Round(aperture.X * scale),
                (int)Math.Round(aperture.Y * scale),
                Math.Max(2, (int)Math.Round(VideoWidth * scale) & ~1),
                Math.Max(2, (int)Math.Round(VideoHeight * scale) & ~1));
            FrameWidth = Math.Min(_crop.Width, _outputWidth - _crop.X);
            FrameHeight = Math.Min(_crop.Height, _outputHeight - _crop.Y);
            ReadCurrentStride();

            var duration = _reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            Duration = duration.Value switch
            {
                ulong u => MediaFoundationRuntime.FromHns((long)u),
                long l => MediaFoundationRuntime.FromHns(l),
                _ => 0,
            };
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public int VideoWidth { get; }

    public int VideoHeight { get; }

    public int FrameWidth { get; }

    public int FrameHeight { get; }

    public double Duration { get; }

    public double NominalFrameRate { get; }

    public VideoFrame? GetFrame(double time, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        time = Math.Max(0, time);
        if (FromCache(time) is { } cached)
        {
            return cached;
        }

        if (_current is null || time < _current.Time - Epsilon || time - _current.Time > SeekForwardLimit)
        {
            Seek(time, cancellationToken);
        }

        return DecodeForward(time, cancellationToken);
    }

    /// <summary>
    /// Decodes forward until the next sample lies after <paramref name="time"/>;
    /// only the frame that is finally shown is copied out of its sample.
    /// </summary>
    private VideoFrame? DecodeForward(double time, CancellationToken cancellationToken)
    {
        IMFSample? candidate = null;
        var candidateTime = 0.0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_nextSample is null && !_endOfStream)
                {
                    ReadNext();
                }

                if (_nextSample is not null && _nextTime <= time + Epsilon)
                {
                    candidate?.Dispose();
                    candidate = _nextSample;
                    candidateTime = _nextTime;
                    _nextSample = null;
                    continue;
                }

                // The next sample (if any) is after the target: the candidate is the answer.
                if (candidate is not null)
                {
                    _current = Remember(Copy(candidate, candidateTime));
                    _current.ValidUntil = _nextSample is null ? double.PositiveInfinity : _nextTime;
                    return _current.Frame;
                }

                if (_current is not null)
                {
                    _current.ValidUntil = _nextSample is null ? double.PositiveInfinity : _nextTime;
                    return _current.Frame;
                }

                if (_nextSample is null)
                {
                    return null;
                }

                // Nothing at or before the target (it lies before the first frame): show the first frame.
                _current = Remember(Copy(_nextSample, _nextTime));
                _nextSample.Dispose();
                _nextSample = null;
                return _current.Frame;
            }
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    private void Seek(double time, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _nextSample?.Dispose();
        _nextSample = null;
        _current = null;
        _endOfStream = false;
        _reader.SetCurrentPosition(MediaFoundationRuntime.ToHns(time));
        ReadNext();
        if (_nextSample is not null && _nextTime > time + Epsilon && time > 0.05)
        {
            // The reader seeked past the target: go back further and decode forward.
            _nextSample.Dispose();
            _nextSample = null;
            _endOfStream = false;
            _reader.SetCurrentPosition(MediaFoundationRuntime.ToHns(Math.Max(0, time - 2)));
            ReadNext();
        }
    }

    private void ReadNext()
    {
        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                sample?.Dispose();
                throw new IOException("The recording could not be decoded.");
            }

            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                ReadCurrentStride();
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                sample?.Dispose();
                _endOfStream = true;
                _nextSample = null;
                _nextTime = double.PositiveInfinity;
                return;
            }

            if (sample is null)
            {
                continue;   // stream tick or gap
            }

            _nextSample = sample;
            _nextTime = MediaFoundationRuntime.FromHns(timestamp);
            return;
        }
    }

    private VideoFrame Copy(IMFSample sample, double time)
    {
        var buffer = new PixelBuffer(FrameWidth, FrameHeight);
        using var media = sample.ConvertToContiguousBuffer();
        using var buffer2D = media.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2D is not null)
        {
            buffer2D.Lock2D(out var scan0, out var pitch);
            try
            {
                CopyRows(scan0, pitch, buffer);
            }
            finally
            {
                buffer2D.Unlock2D();
            }
        }
        else
        {
            media.Lock(out var data, out _, out var length);
            try
            {
                var pitch = _stride != 0 ? _stride : _outputWidth * 4;
                var start = pitch < 0 ? data + ((_outputHeight - 1) * -pitch) : data;
                if ((long)Math.Abs(pitch) * _outputHeight <= length)
                {
                    CopyRows(start, pitch, buffer);
                }
            }
            finally
            {
                media.Unlock();
            }
        }

        return new VideoFrame(buffer, time);
    }

    private unsafe void CopyRows(IntPtr scan0, int pitch, PixelBuffer target)
    {
        var rowBytes = FrameWidth * 4;
        fixed (byte* destination = target.Pixels)
        {
            for (var y = 0; y < FrameHeight; y++)
            {
                var source = (byte*)scan0 + ((long)(y + _crop.Y) * pitch) + (_crop.X * 4);
                var row = destination + (y * target.Stride);
                Buffer.MemoryCopy(source, row, rowBytes, rowBytes);
                // RGB32 leaves the fourth byte undefined: make every pixel opaque.
                for (var x = 3; x < rowBytes; x += 4)
                {
                    row[x] = 255;
                }
            }
        }
    }

    private void ReadCurrentStride()
    {
        try
        {
            using var current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
            _stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var stride).Success ? unchecked((int)stride) : _outputWidth * 4;
        }
        catch (SharpGenException)
        {
            _stride = _outputWidth * 4;
        }
    }

    /// <summary>The visible picture inside the coded frame (MF_MT_MINIMUM_DISPLAY_APERTURE), or the whole frame.</summary>
    private static (int X, int Y, int W, int H) Aperture(IMFMediaType type, int width, int height)
    {
        try
        {
            if (type.GetBlobSize(MediaTypeAttributeKeys.MinimumDisplayAperture, out var size).Success && size >= 16)
            {
                var blob = type.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);
                var x = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(2));
                var y = BinaryPrimitives.ReadInt16LittleEndian(blob.AsSpan(6));
                var w = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8));
                var h = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(12));
                if (w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= width && y + h <= height)
                {
                    return (x, y, w, h);
                }
            }
        }
        catch (SharpGenException)
        {
        }

        return (0, 0, width, height);
    }

    private VideoFrame? FromCache(double time)
    {
        for (var node = _cache.First; node is not null; node = node.Next)
        {
            var entry = node.Value;
            if (time >= entry.Time - Epsilon && time < entry.ValidUntil - Epsilon)
            {
                _cache.Remove(node);
                _cache.AddFirst(node);
                return entry.Frame;
            }
        }

        return null;
    }

    private CachedFrame Remember(VideoFrame frame)
    {
        var entry = new CachedFrame(frame);
        _cache.AddFirst(entry);
        while (_cache.Count > _cacheSize)
        {
            _cache.RemoveLast();
        }

        return entry;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nextSample?.Dispose();
        _nextSample = null;
        try
        {
            _reader.Dispose();
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Log.Warn("recording-editor", "Closing the source reader failed.", ex);
        }

        _cache.Clear();
    }

    private sealed class CachedFrame(VideoFrame frame)
    {
        public VideoFrame Frame { get; } = frame;

        public double Time => Frame.Time;

        /// <summary>The next frame's time once known (the frame shows until then).</summary>
        public double ValidUntil { get; set; } = frame.Time + 1e-6;
    }
}

/// <summary>Opens masters with Media Foundation.</summary>
internal sealed class MfVideoFrameSourceFactory : IVideoFrameSourceFactory
{
    public IVideoFrameSource Open(string path, VideoOpenOptions? options = null)
    {
        try
        {
            return new MfVideoFrameSource(path, options);
        }
        catch (SharpGenException ex)
        {
            throw new IOException("The recording could not be opened.", ex);
        }
        catch (COMException ex)
        {
            throw new IOException("The recording could not be opened.", ex);
        }
    }
}
