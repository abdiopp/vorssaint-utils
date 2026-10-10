// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;
using SkiaSharp;
using Xunit;

namespace Rivet.Imaging.Tests.RecordingEditor;

public sealed class ExportPipelineTests : IDisposable
{
    private const int Fps = 60;
    private readonly string _folder = TestTakes.TempFolder("export");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static RecordingExportInput Input(TakeData take, EditDocument doc, int width, int height, double duration) => new()
    {
        Take = take,
        Document = doc,
        Duration = duration,
        SourceWidth = width,
        SourceHeight = height,
        FrameRate = Fps,
    };

    private static EditDocument Plain => new() { ShowsPointer = false, ZoomEnabled = false, ZoomsGenerated = true };

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.37)]
    [InlineData(4)]
    public void Constant_frame_rate_output_uses_the_right_source_frames(double speed)
    {
        const double duration = 6;
        var take = TestTakes.Create(_folder, 64, 48, duration);
        var doc = Plain with { TrimStart = 0.5, TrimEnd = 5.5, Cuts = [new CutRange(2, 3)], ExportSpeed = speed };
        var encoders = new MemoryEncoderFactory();
        RecordingExporter.ExportVideo(Input(take, doc, 64, 48, duration), "unused.mp4",
            new SyntheticFactory(SyntheticScene.FrameCode, 64, 48, duration, Fps), encoders, null, CancellationToken.None);

        var encoder = encoders.Last!;
        var timeline = EditTimeline.For(doc.Sanitized(duration), duration);
        var exportDuration = timeline.OutputDuration / speed;
        Assert.True(encoder.Finished);
        Assert.Equal(ExportMath.ExpectedFrames(exportDuration, Fps), encoder.Frames.Count);
        Assert.InRange(encoder.Frames.Count / (double)Fps, exportDuration - ((1.0 / Fps) + 0.001), exportDuration + (1.0 / Fps) + 0.001);
        for (var k = 0; k < encoder.Frames.Count; k++)
        {
            var (frame, time, length) = encoder.Frames[k];
            Assert.Equal(k / (double)Fps, time, 9);
            Assert.Equal(1.0 / Fps, length, 9);
            var source = timeline.SourceTime(Math.Min(k / (double)Fps * speed, timeline.OutputDuration));
            var expectedIndex = SyntheticVideoSource.FrameNumberAt(source, Fps);
            var actualIndex = frame.Pixels[2] | (frame.Pixels[1] << 8);
            Assert.Equal(expectedIndex, actualIndex);
            Assert.False(source is > 2.0 and < 3.0, "cut-out moments never appear");
        }
    }

    public static TheoryData<double, bool> BlurCases()
    {
        var data = new TheoryData<double, bool>();
        foreach (var speed in new[] { 0.25, 0.5, 1, 1.37, 4 })
        {
            data.Add(speed, false);
            data.Add(speed, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BlurCases))]
    public void Blurs_cover_their_last_frame_and_uncover_right_after(double speed, bool trimAndCut)
    {
        const double duration = 5;
        var take = TestTakes.Create(_folder, 160, 100, duration);
        var blur = new BlurRegion { Id = "b", Start = 1.0, End = 2.5, X = 0.25, Y = 0.25, Width = 0.5, Height = 0.5 };
        var doc = Plain with { Blurs = [blur], ExportSpeed = speed };
        if (trimAndCut)
        {
            // The blur ends inside the removed interval [2.2, 3.2].
            doc = doc with { TrimStart = 0.4, TrimEnd = 4.6, Cuts = [new CutRange(2.2, 3.2)] };
        }

        var encoders = new MemoryEncoderFactory();
        RecordingExporter.ExportVideo(Input(take, doc, 160, 100, duration), "unused.mp4",
            new SyntheticFactory(SyntheticScene.Checkerboard, 160, 100, duration, Fps), encoders, null, CancellationToken.None);

        var timeline = EditTimeline.For(doc.Sanitized(duration), duration);
        var plan = FramePlan.Build(doc, duration, 160, 100, Fps, PointerTrack.Empty, 4, 1);
        var rect = new SKRectI(41, 26, 119, 74);
        int? lastInside = null;
        int? firstAfter = null;
        var frames = encoders.Last!.Frames;
        for (var k = 0; k < frames.Count; k++)
        {
            var edited = Math.Min(k / (double)Fps * speed, timeline.OutputDuration);
            var source = timeline.SourceTime(edited);
            var contrast = TestTakes.Contrast(frames[k].Frame, rect);
            if (BlurMath.Covers(blur, source))
            {
                Assert.True(contrast < 40, $"frame {k} at {source:0.000}s must be obscured ({contrast:0.0})");
                lastInside = k;
            }
            else if (source > blur.End && firstAfter is null && !plan.BlurActive(blur, edited))
            {
                firstAfter = k;
                Assert.True(contrast > 100, $"frame {k} at {source:0.000}s must be clear ({contrast:0.0})");
            }
        }

        Assert.NotNull(lastInside);
        Assert.NotNull(firstAfter);
        // Nothing after the first clear frame is blurred again.
        for (var k = firstAfter!.Value; k < frames.Count; k++)
        {
            Assert.True(TestTakes.Contrast(frames[k].Frame, rect) > 100);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void Audio_is_mixed_stretched_and_as_long_as_the_video(double speed)
    {
        const double duration = 3;
        const int rate = 48000;
        var tone = new float[(int)(duration * rate) * 2];
        var late = new float[tone.Length];
        for (var i = 0; i < tone.Length / 2; i++)
        {
            var t = i / (double)rate;
            tone[i * 2] = tone[(i * 2) + 1] = (float)(0.4 * Math.Sin(2 * Math.PI * 880 * t));
            late[i * 2] = late[(i * 2) + 1] = t < 1.3 ? 0 : (float)(0.4 * Math.Sin(2 * Math.PI * 440 * t));
        }

        var take = TestTakes.Create(_folder, 32, 32, duration, audio: [(TakeAudioSource.System, tone), (TakeAudioSource.Microphone, late)]);
        var doc = Plain with { ExportSpeed = speed, KeepsMicrophone = false, SystemAudioGain = 0.5 };
        var encoders = new MemoryEncoderFactory();
        RecordingExporter.ExportVideo(Input(take, doc, 32, 32, duration), "unused.mp4",
            new SyntheticFactory(SyntheticScene.FrameCode, 32, 32, duration, Fps), encoders, null, CancellationToken.None);

        var encoder = encoders.Last!;
        Assert.True(encoder.Settings!.IncludeAudio);
        var audioFrames = encoder.Audio.Count / 2;
        Assert.Equal((long)Math.Round(encoder.Frames.Count / (double)Fps * rate, MidpointRounding.AwayFromZero), audioFrames);
        var middle = encoder.Audio.Skip(encoder.Audio.Count / 4).Take(encoder.Audio.Count / 2).ToArray();
        var rms = Math.Sqrt(middle.Select(v => (double)v * v).Average());
        Assert.InRange(rms, 0.09, 0.18);   // 50 % of a 0.4 sine; the removed microphone adds nothing

        var crossings = 0;
        for (var i = 1; i < middle.Length / 2; i++)
        {
            if (middle[(i - 1) * 2] < 0 && middle[i * 2] >= 0)
            {
                crossings++;
            }
        }

        var frequency = crossings / (middle.Length / 2 / (double)rate);
        Assert.InRange(frequency, 880 - 70, 880 + 70);
    }

    [Fact]
    public void No_kept_track_means_no_audio_stream()
    {
        var take = TestTakes.Create(_folder, 32, 32, 2, audio: [(TakeAudioSource.System, new float[2 * 48000 * 2])]);
        var encoders = new MemoryEncoderFactory();
        RecordingExporter.ExportVideo(Input(take, Plain with { KeepsSystemAudio = false }, 32, 32, 2), "unused.mp4",
            new SyntheticFactory(SyntheticScene.FrameCode, 32, 32, 2, Fps), encoders, null, CancellationToken.None);
        Assert.False(encoders.Last!.Settings!.IncludeAudio);
        Assert.Empty(encoders.Last.Audio);
    }

    [Fact]
    public void Cancel_lands_within_a_frame_and_never_finalizes()
    {
        var take = TestTakes.Create(_folder, 64, 48, 4);
        using var cts = new CancellationTokenSource();
        var encoders = new MemoryEncoderFactory { OnFrame = n => { if (n == 10) { cts.Cancel(); } } };
        Assert.ThrowsAny<OperationCanceledException>(() => RecordingExporter.ExportVideo(Input(take, Plain, 64, 48, 4), "unused.mp4",
            new SyntheticFactory(SyntheticScene.FrameCode, 64, 48, 4, Fps), encoders, null, cts.Token));
        Assert.False(encoders.Last!.Finished);
        Assert.True(encoders.Last.Disposed);
        Assert.Equal(10, encoders.Last.Frames.Count);
    }

    [Fact]
    public void Progress_never_parks_near_the_end_before_finalizing()
    {
        var take = TestTakes.Create(_folder, 32, 32, 1);
        var reports = new List<double>();
        RecordingExporter.ExportVideo(Input(take, Plain, 32, 32, 1), "unused.mp4",
            new SyntheticFactory(SyntheticScene.FrameCode, 32, 32, 1, Fps), new MemoryEncoderFactory(), new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(0.95, reports[^2]);
        Assert.True(reports.Take(reports.Count - 2).All(r => r <= 0.9));
    }

    [Fact]
    public void Gif_of_a_trimmed_double_speed_clip_has_exactly_six_frames()
    {
        var take = TestTakes.Create(_folder, 64, 48, 2);
        var doc = Plain with { TrimStart = 0.5, TrimEnd = 1.5, ExportSpeed = 2, GifFrameRate = 12 };
        var bytes = RecordingExporter.ExportGif(Input(take, doc, 64, 48, 2),
            new SyntheticFactory(SyntheticScene.FrameCode, 64, 48, 2, Fps), null, CancellationToken.None, dither: false);
        var gif = GifDecoder.Decode(bytes)!;
        Assert.Equal(6, gif.Frames.Count);
        Assert.Equal(0, gif.LoopCount);
        Assert.All(gif.Frames, f => Assert.Equal(8, f.DelayCentiseconds));
        foreach (var frame in gif.Frames)
        {
            var index = frame.Bgra[2] | (frame.Bgra[1] << 8);
            Assert.InRange(index, 30, 89);   // all from the trimmed span [0.5, 1.5)
        }
    }

    [Fact]
    public void Too_long_gif_fails_before_decoding_anything()
    {
        var take = TestTakes.Create(_folder, 64, 48, 40);
        var factory = new SyntheticFactory(SyntheticScene.FrameCode, 64, 48, 40, Fps);
        var error = Assert.Throws<GifTooLongException>(() => RecordingExporter.ExportGif(Input(take, Plain with { GifFrameRate = 12 }, 64, 48, 40), factory, null, CancellationToken.None));
        Assert.Equal(25, error.MaxSeconds);
        Assert.Equal(0, factory.Opened);
    }

    [Fact]
    public void Gif_size_follows_the_setting_and_never_upscales()
    {
        var take = TestTakes.Create(_folder, 1200, 800, 1);
        var bytes = RecordingExporter.ExportGif(Input(take, Plain with { GifSize = GifSize.Small }, 1200, 800, 1),
            new SyntheticFactory(SyntheticScene.Desktop, 1200, 800, 1, Fps), null, CancellationToken.None);
        var gif = GifDecoder.Decode(bytes, decodePixels: false)!;
        Assert.Equal((420, 280), (gif.Width, gif.Height));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
