// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;
using SkiaSharp;

namespace Rivet.Imaging.Tests.RecordingEditor;

/// <summary>Synthetic sources and an in-memory encoder for pipeline tests.</summary>
internal sealed class SyntheticFactory(SyntheticScene scene, int width, int height, double duration, double fps) : IVideoFrameSourceFactory
{
    public int Opened { get; private set; }

    public IVideoFrameSource Open(string path, VideoOpenOptions? options = null)
    {
        Opened++;
        return new SyntheticVideoSource(width, height, duration, fps, scene, options);
    }
}

internal sealed class MemoryEncoder : IVideoEncoder
{
    public List<(PixelBuffer Frame, double Time, double Duration)> Frames { get; } = [];

    public List<float> Audio { get; } = [];

    public List<double> AudioTimes { get; } = [];

    public bool Finished { get; private set; }

    public bool Disposed { get; private set; }

    public VideoEncoderSettings? Settings { get; set; }

    public Action<int>? OnFrame { get; set; }

    public void WriteVideoFrame(PixelBuffer frame, double timestamp, double duration)
    {
        Frames.Add((new PixelBuffer(frame.Width, frame.Height, (byte[])frame.Pixels.Clone(), frame.Stride), timestamp, duration));
        OnFrame?.Invoke(Frames.Count);
    }

    public void WriteAudio(ReadOnlySpan<float> interleaved, double timestamp)
    {
        AudioTimes.Add(timestamp);
        Audio.AddRange(interleaved.ToArray());
    }

    public void Finish() => Finished = true;

    public void Dispose() => Disposed = true;
}

internal sealed class MemoryEncoderFactory : IVideoEncoderFactory
{
    public MemoryEncoder? Last { get; private set; }

    public Action<int>? OnFrame { get; init; }

    public bool IsAvailable => true;

    public IVideoEncoder Create(string path, VideoEncoderSettings settings)
    {
        Last = new MemoryEncoder { Settings = settings, OnFrame = OnFrame };
        return Last;
    }
}

internal static class TestTakes
{
    /// <summary>A take folder with a manifest, optional pointer track and optional audio.</summary>
    public static TakeData Create(string folder, int width, int height, double duration, int fps = 60, PointerTrack? pointer = null, IEnumerable<(TakeAudioSource Source, float[] Data)>? audio = null)
    {
        Directory.CreateDirectory(folder);
        var tracks = new List<TakeAudio>();
        foreach (var (source, data) in audio ?? [])
        {
            var file = source == TakeAudioSource.System ? "system.wav" : "mic.wav";
            Core.RecordingEditor.Audio.WavFile.WriteFloatStereo(Path.Combine(folder, file), data, 48000);
            tracks.Add(new TakeAudio { Source = source, File = file });
        }

        new TakeManifest
        {
            Capture = new TakeCapture
            {
                Kind = TakeCaptureKind.Area,
                Fps = fps,
                Monitor = new TakeMonitor { Device = "d", RectPx = [0, 0, width, height] },
                RegionPx = [0, 0, width, height],
            },
            Video = new TakeVideo { Width = width, Height = height, DurationSeconds = duration },
            Audio = tracks,
            PointerTrack = pointer is null ? null : new TakePointerTrack(),
        }.Write(folder);
        pointer?.Write(Path.Combine(folder, PointerTrack.FileName));
        return TakeData.Load(folder);
    }

    public static string TempFolder(string name) => Path.Combine(Path.GetTempPath(), $"rivet-{name}-{Guid.NewGuid():N}");

    /// <summary>RMS contrast (standard deviation of luma) inside a rectangle of a frame.</summary>
    public static double Contrast(PixelBuffer frame, SKRectI rect)
    {
        double sum = 0, sum2 = 0;
        var n = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var o = (y * frame.Stride) + (x * 4);
                var luma = (0.114 * frame.Pixels[o]) + (0.587 * frame.Pixels[o + 1]) + (0.299 * frame.Pixels[o + 2]);
                sum += luma;
                sum2 += luma * luma;
                n++;
            }
        }

        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, (sum2 / n) - (mean * mean)));
    }

    public static SKImage ToImage(PixelBuffer buffer) => Skia.SkiaConvert.ToImage(buffer);
}
