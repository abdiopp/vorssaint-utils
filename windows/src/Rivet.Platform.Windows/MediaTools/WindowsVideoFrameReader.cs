// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules.MediaTools;
using Rivet.Core.Platform;
using Rivet.Imaging.Skia;
using SharpGen.Runtime;
using SkiaSharp;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.MediaTools;

/// <summary>
/// GIF frames with the Media Foundation source reader: one sequential decode
/// to RGB32 (with the video processor enabled), picking the first frame at or
/// after each requested time, seeking ahead only across long gaps. Frames are
/// rotated upright (MF_MT_VIDEO_ROTATION) and scaled with SkiaSharp.
/// </summary>
public sealed class WindowsVideoFrameReader : IVideoFrameReader
{
    private static readonly object StartupGate = new();
    private static bool _started;

    public bool IsAvailable => true;

    public Task ReadFramesAsync(string input, IReadOnlyList<double> times, MediaSize size, Func<int, PixelBuffer, Task> onFrame, CancellationToken cancellationToken) =>
        Task.Run(() => Read(input, times, size, onFrame, cancellationToken), cancellationToken);

    private static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (!_started)
            {
                MediaFactory.MFStartup(useLightVersion: true).CheckError();
                _started = true;
            }
        }
    }

    private static void Read(string input, IReadOnlyList<double> times, MediaSize size, Func<int, PixelBuffer, Task> onFrame, CancellationToken cancellationToken)
    {
        if (times.Count == 0)
        {
            return;
        }

        EnsureStarted();
        IMFSourceReader reader;
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, true);
            reader = MediaFactory.MFCreateSourceReaderFromURL(input, attributes);
        }
        catch (SharpGenException ex)
        {
            throw new MediaJobException($"Video could not be read. (0x{ex.HResult:X8})");
        }

        using (reader)
        {
            int rotation;
            int width, height, stride;
            try
            {
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                using (var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
                {
                    rotation = native.GetUInt32(MediaTypeAttributeKeys.VideoRotation, out var value).Success ? (int)value : 0;
                }

                using (var wanted = MediaFactory.MFCreateMediaType())
                {
                    wanted.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    wanted.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
                    reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, wanted);
                }

                using var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
                var frameSize = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                width = (int)(frameSize >> 32);
                height = (int)(frameSize & 0xFFFFFFFF);
                stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out var strideValue).Success ? unchecked((int)strideValue) : width * 4;
            }
            catch (SharpGenException ex)
            {
                throw new MediaJobException($"This file has no video track. (0x{ex.HResult:X8})");
            }

            if (width <= 0 || height <= 0)
            {
                throw new MediaJobException("Video frame unavailable.");
            }

            var frame = new byte[width * height * 4];
            var index = 0;
            long lastTimestamp = -1;
            const long TicksPerSecond = 10_000_000;
            while (index < times.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = (long)(times[index] * TicksPerSecond);

                // Jump ahead across long gaps (the reader lands on the keyframe before the target).
                if (lastTimestamp >= 0 && target - lastTimestamp > 2 * TicksPerSecond)
                {
                    reader.SetCurrentPosition(target);
                }
                else if (lastTimestamp < 0 && target > TicksPerSecond)
                {
                    reader.SetCurrentPosition(target);
                }

                IMFSample? sample;
                SourceReaderFlag flags;
                long timestamp;
                try
                {
                    sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out flags, out timestamp);
                }
                catch (SharpGenException ex)
                {
                    throw new MediaJobException($"Video frame unavailable. (0x{ex.HResult:X8})");
                }

                if ((flags & SourceReaderFlag.EndOfStream) != 0 || sample is null)
                {
                    sample?.Dispose();
                    // Past the end: repeat the last frame for the remaining times.
                    if (lastTimestamp < 0)
                    {
                        throw new MediaJobException("Video frame unavailable.");
                    }

                    while (index < times.Count)
                    {
                        Emit(frame, width, height, rotation, size, index++, onFrame);
                    }

                    break;
                }

                using (sample)
                {
                    lastTimestamp = timestamp;
                    if (timestamp + (TicksPerSecond / 120) < target)
                    {
                        continue;
                    }

                    Copy(sample, frame, width, height, stride);
                }

                // This decoded frame serves every requested time it already covers.
                while (index < times.Count && (long)(times[index] * TicksPerSecond) <= timestamp + (TicksPerSecond / 120))
                {
                    Emit(frame, width, height, rotation, size, index++, onFrame);
                }
            }
        }
    }

    private static void Copy(IMFSample sample, byte[] frame, int width, int height, int stride)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var data, out _, out var length);
        try
        {
            var rowBytes = width * 4;
            var absStride = Math.Abs(stride);
            for (var y = 0; y < height; y++)
            {
                // A negative stride means bottom-up rows.
                var sourceRow = stride >= 0 ? y : height - 1 - y;
                var offset = (long)sourceRow * absStride;
                if (offset + rowBytes > length)
                {
                    break;
                }

                Marshal.Copy(data + (nint)offset, frame, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            buffer.Unlock();
        }

        // RGB32 has an undefined fourth byte: make it opaque.
        for (var i = 3; i < frame.Length; i += 4)
        {
            frame[i] = 255;
        }
    }

    private static void Emit(byte[] frame, int width, int height, int rotation, MediaSize size, int index, Func<int, PixelBuffer, Task> onFrame)
    {
        using var bitmap = SkiaConvert.ToBitmap(new PixelBuffer(width, height, frame));
        var swap = rotation is 90 or 270;
        var uprightWidth = swap ? height : width;
        var uprightHeight = swap ? width : height;
        using var surface = SKSurface.Create(SkiaConvert.InfoFor(size.Width, size.Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        canvas.Scale(size.Width / (float)uprightWidth, size.Height / (float)uprightHeight);
        switch (rotation)
        {
            case 90:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case 180:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case 270:
                canvas.Translate(0, width);
                canvas.RotateDegrees(270);
                break;
        }

        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        onFrame(index, SkiaConvert.ToPixelBuffer(snapshot)).GetAwaiter().GetResult();
    }
}
