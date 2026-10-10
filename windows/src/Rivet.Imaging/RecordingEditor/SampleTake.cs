// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor.Audio;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>
/// Writes a synthetic take folder (manifest, pointer track with cursor
/// bitmaps and clicks, typing times and two audio tracks) so the editor can
/// be opened without a recording engine: the development build's sample
/// action and the UI tests use it. The video itself comes from
/// <see cref="SyntheticVideoSource"/> (no take.mp4 is written).
/// </summary>
public static class SampleTake
{
    public static void Write(string folder, int width = 1280, int height = 800, double duration = 12, int fps = 60, bool withAudio = true)
    {
        Directory.CreateDirectory(folder);
        var audio = new List<TakeAudio>();
        if (withAudio)
        {
            WriteTone(Path.Combine(folder, "system.wav"), duration, 220, 0.18, 0);
            WriteTone(Path.Combine(folder, "mic.wav"), duration, 330, 0.25, 2.5);
            audio.Add(new TakeAudio { Source = TakeAudioSource.System, File = "system.wav" });
            audio.Add(new TakeAudio { Source = TakeAudioSource.Microphone, File = "mic.wav" });
        }

        var manifest = new TakeManifest
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Capture = new TakeCapture
            {
                Kind = TakeCaptureKind.Area,
                Fps = fps,
                Monitor = new TakeMonitor { Device = "\\\\.\\DISPLAY1", DpiScale = 1, RectPx = [0, 0, 1920, 1080] },
                RegionPx = [100, 100, width, height],
            },
            Video = new TakeVideo { Width = width, Height = height, DurationSeconds = duration, Vfr = true },
            Audio = audio,
            PointerTrack = new TakePointerTrack(),
            TypingTrack = TypingTrack.FileName,
        };
        manifest.Write(folder);
        Pointer(duration).Write(Path.Combine(folder, PointerTrack.FileName));
        new TypingTrack([5.4, 5.6, 5.8, 6.0, 6.3, 6.5, 6.8]).Write(Path.Combine(folder, TypingTrack.FileName));
    }

    /// <summary>A pointer that visits the window's text, clicks, types, rests, then clicks Save.</summary>
    public static PointerTrack Pointer(double duration)
    {
        (double T, double X, double Y)[] keys =
        [
            (0, 0.30, 0.70), (1.2, 0.40, 0.50), (2.0, 0.42, 0.30), (3.0, 0.43, 0.31),
            (4.5, 0.25, 0.62), (5.0, 0.26, 0.62), (7.5, 0.27, 0.63), (10.0, 0.80, 0.84),
            (Math.Max(10.5, duration), 0.80, 0.84),
        ];
        var samples = new List<PointerSample>();
        for (var t = 0.0; t <= duration; t += 0.008)
        {
            var k = 0;
            while (k + 1 < keys.Length - 1 && keys[k + 1].T <= t)
            {
                k++;
            }

            var a = keys[k];
            var b = keys[Math.Min(k + 1, keys.Length - 1)];
            var f = b.T > a.T ? Math.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 0;
            f = f * f * (3 - (2 * f));
            var typing = t is > 5 and < 7.4;
            samples.Add(new PointerSample((float)t, (float)(a.X + ((b.X - a.X) * f)), (float)(a.Y + ((b.Y - a.Y) * f)), (ushort)(typing ? 1 : 0), true));
        }

        return new PointerTrack
        {
            SystemScale = 1,
            DisplayScale = 1,
            Samples = samples,
            Clicks =
            [
                new PointerClick(2.1f, true), new PointerClick(2.2f, false),
                new PointerClick(5.05f, true), new PointerClick(5.15f, false),
                new PointerClick(10.2f, true), new PointerClick(10.3f, false),
            ],
            Shapes = [ArrowShape(), BeamShape()],
        };
    }

    private static PointerShape ArrowShape()
    {
        using var surface = SKSurface.Create(new SKImageInfo(32, 48, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        FrameCompositor.DrawFallbackArrow(surface.Canvas, 4.5f, 4f, 1, 1);
        using var image = surface.Snapshot();
        return new PointerShape(4.5f, 4f, 32, 48, SkiaConvert.EncodePng(image));
    }

    private static PointerShape BeamShape()
    {
        using var surface = SKSurface.Create(new SKImageInfo(16, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var outline = new SKPaint { Color = SKColors.White, StrokeWidth = 4, Style = SKPaintStyle.Stroke, IsAntialias = true };
        using var stroke = new SKPaint { Color = SKColors.Black, StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
        foreach (var paint in new[] { outline, stroke })
        {
            canvas.DrawLine(8, 4, 8, 28, paint);
            canvas.DrawLine(4, 4, 12, 4, paint);
            canvas.DrawLine(4, 28, 12, 28, paint);
        }

        using var image = surface.Snapshot();
        return new PointerShape(8, 16, 16, 32, SkiaConvert.EncodePng(image));
    }

    private static void WriteTone(string path, double duration, double frequency, double amplitude, double startAt)
    {
        const int rate = 48000;
        var frames = (int)(duration * rate);
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)rate;
            var envelope = t < startAt ? 0 : 0.5 + (0.5 * Math.Sin(t * 2.1));
            var v = (float)(amplitude * envelope * Math.Sin(2 * Math.PI * frequency * t));
            data[i * 2] = v;
            data[(i * 2) + 1] = v;
        }

        WavFile.WriteFloatStereo(path, data, rate);
    }
}
