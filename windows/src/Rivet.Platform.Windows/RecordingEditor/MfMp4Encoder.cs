// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>
/// Writes the export MP4 with the Media Foundation Sink Writer (spec 02 §6.16
/// step 8, §7 #11): H.264 High at the preset's average bit rate, key frames
/// at the preset's interval, no B-frames, and one AAC-LC 48 kHz stereo
/// 160 kb/s track. Frames arrive as top-down BGRA; the sink writer converts
/// RGB32 to the encoder's NV12 itself, and when it cannot, frames are
/// converted to NV12 here (BT.709, limited range).
/// </summary>
internal sealed class MfMp4Encoder : IVideoEncoder
{
    // CODECAPI properties passed to the encoder through SetInputMediaType (codecapi.h).
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    private const uint ProgressiveInterlace = 2;   // MFVideoInterlace_Progressive
    private const uint H264ProfileHigh = 100;      // eAVEncH264VProfile_High
    private const uint H264ProfileMain = 77;
    private const uint UnconstrainedVbr = 2;       // eAVEncCommonRateControlMode_UnconstrainedVBR

    private readonly IMFSinkWriter _writer;
    private readonly VideoEncoderSettings _settings;
    private readonly int _videoStream;
    private readonly int _audioStream = -1;
    private readonly bool _nv12;
    private byte[] _pcm = [];
    private byte[] _nv12Buffer = [];
    private bool _finished;
    private bool _disposed;

    public MfMp4Encoder(string path, VideoEncoderSettings settings)
    {
        MediaFoundationRuntime.EnsureStarted();
        _settings = settings;
        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true);
            _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        }

        try
        {
            _videoStream = AddVideoStream(H264ProfileHigh);
            try
            {
                SetVideoInput(VideoFormatGuids.Rgb32, withParameters: true);
            }
            catch (SharpGenException first)
            {
                Log.Info("recording-editor", $"RGB32 input refused (0x{first.ResultCode.Code:X8}); converting to NV12.");
                try
                {
                    SetVideoInput(VideoFormatGuids.NV12, withParameters: true);
                }
                catch (SharpGenException)
                {
                    SetVideoInput(VideoFormatGuids.NV12, withParameters: false);
                }

                _nv12 = true;
            }

            if (settings.IncludeAudio)
            {
                _audioStream = AddAudioStream();
            }

            _writer.BeginWriting();
        }
        catch
        {
            _writer.Dispose();
            throw;
        }
    }

    private int AddVideoStream(uint profile)
    {
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_settings.BitRate);
        output.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveInterlace);
        output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)_settings.Width, (uint)_settings.Height));
        output.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(_settings.FrameRate, 1));
        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
        output.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile);
        output.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)Math.Max(1, _settings.KeyFrameIntervalFrames));
        try
        {
            return _writer.AddStream(output);
        }
        catch (SharpGenException) when (profile == H264ProfileHigh)
        {
            // Some hardware encoders only accept Main.
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileMain);
            return _writer.AddStream(output);
        }
    }

    private void SetVideoInput(Guid subtype, bool withParameters)
    {
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        input.Set(MediaTypeAttributeKeys.Subtype, subtype);
        input.Set(MediaTypeAttributeKeys.InterlaceMode, ProgressiveInterlace);
        input.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)_settings.Width, (uint)_settings.Height));
        input.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(_settings.FrameRate, 1));
        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
        if (subtype == VideoFormatGuids.Rgb32)
        {
            // A positive stride means top-down rows (no vertical flip).
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(_settings.Width * 4));
        }

        if (!withParameters)
        {
            _writer.SetInputMediaType(_videoStream, input, null!);
            return;
        }

        using var parameters = MediaFactory.MFCreateAttributes(4);
        parameters.Set(RateControlMode, UnconstrainedVbr);
        parameters.Set(MeanBitRate, (uint)_settings.BitRate);
        parameters.Set(GopSize, (uint)Math.Max(1, _settings.KeyFrameIntervalFrames));
        parameters.Set(BPictureCount, 0u);
        _writer.SetInputMediaType(_videoStream, input, parameters);
    }

    private int AddAudioStream()
    {
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_settings.AudioSampleRate);
        output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_settings.AudioChannels);
        output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_settings.AudioBitRate / 8));
        output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
        output.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
        var stream = _writer.AddStream(output);

        // The Microsoft AAC encoder takes 16-bit PCM.
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)_settings.AudioSampleRate);
        input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)_settings.AudioChannels);
        input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(_settings.AudioChannels * 2));
        input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(_settings.AudioSampleRate * _settings.AudioChannels * 2));
        _writer.SetInputMediaType(stream, input, null!);
        return stream;
    }

    public void WriteVideoFrame(PixelBuffer frame, double timestamp, double duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var width = _settings.Width;
        var height = _settings.Height;
        var size = _nv12 ? width * height * 3 / 2 : width * height * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        buffer.Lock(out var data, out _, out _);
        try
        {
            if (_nv12)
            {
                if (_nv12Buffer.Length < size)
                {
                    _nv12Buffer = new byte[size];
                }

                Nv12.FromBgra(frame, width, height, _nv12Buffer);
                Marshal.Copy(_nv12Buffer, 0, data, size);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(frame.Pixels, y * frame.Stride, data + (y * width * 4), width * 4);
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = size;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = MediaFoundationRuntime.ToHns(timestamp);
        sample.SampleDuration = MediaFoundationRuntime.ToHns(duration);
        _writer.WriteSample(_videoStream, sample);
    }

    public void WriteAudio(ReadOnlySpan<float> interleaved, double timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_audioStream < 0 || interleaved.Length == 0)
        {
            return;
        }

        var bytes = interleaved.Length * 2;
        if (_pcm.Length < bytes)
        {
            _pcm = new byte[bytes];
        }

        for (var i = 0; i < interleaved.Length; i++)
        {
            var v = (short)Math.Clamp((int)Math.Round(interleaved[i] * 32767f), short.MinValue, short.MaxValue);
            _pcm[i * 2] = (byte)(v & 0xFF);
            _pcm[(i * 2) + 1] = (byte)((v >> 8) & 0xFF);
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out var data, out _, out _);
        try
        {
            Marshal.Copy(_pcm, 0, data, bytes);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = bytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        var frames = interleaved.Length / _settings.AudioChannels;
        sample.SampleTime = MediaFoundationRuntime.ToHns(timestamp);
        sample.SampleDuration = MediaFoundationRuntime.ToHns(frames / (double)_settings.AudioSampleRate);
        _writer.WriteSample(_audioStream, sample);
    }

    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_finished)
        {
            _writer.Finalize();
            _finished = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is SharpGenException or COMException)
        {
            Log.Warn("recording-editor", "Closing the sink writer failed.", ex);
        }
    }
}

/// <summary>BGRA → NV12 (BT.709, limited range), used when the sink writer will not convert RGB32 itself.</summary>
internal static class Nv12
{
    public static void FromBgra(PixelBuffer source, int width, int height, byte[] destination)
    {
        var uvOffset = width * height;
        for (var y = 0; y < height; y++)
        {
            var row = y * source.Stride;
            for (var x = 0; x < width; x++)
            {
                var o = row + (x * 4);
                int b = source.Pixels[o], g = source.Pixels[o + 1], r = source.Pixels[o + 2];
                destination[(y * width) + x] = (byte)Math.Clamp(((47 * r) + (157 * g) + (16 * b) + 128 >> 8) + 16, 0, 255);
            }
        }

        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var o = ((y + dy) * source.Stride) + ((x + dx) * 4);
                        b += source.Pixels[o];
                        g += source.Pixels[o + 1];
                        r += source.Pixels[o + 2];
                    }
                }

                r /= 4;
                g /= 4;
                b /= 4;
                var u = ((-26 * r) - (87 * g) + (112 * b) + 128 >> 8) + 128;
                var v = ((112 * r) - (102 * g) - (10 * b) + 128 >> 8) + 128;
                var index = uvOffset + ((y / 2) * width) + x;
                destination[index] = (byte)Math.Clamp(u, 0, 255);
                destination[index + 1] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }
}

/// <summary>Creates MP4 encoders; unavailable when Media Foundation is missing (Windows N without the Media Feature Pack).</summary>
internal sealed class MfMp4EncoderFactory : IVideoEncoderFactory
{
    public bool IsAvailable => MediaFoundationRuntime.IsAvailable;

    public IVideoEncoder Create(string path, VideoEncoderSettings settings)
    {
        try
        {
            return new MfMp4Encoder(path, settings);
        }
        catch (SharpGenException ex)
        {
            throw new IOException("The video encoder could not be started.", ex);
        }
        catch (COMException ex)
        {
            throw new IOException("The video encoder could not be started.", ex);
        }
    }
}
