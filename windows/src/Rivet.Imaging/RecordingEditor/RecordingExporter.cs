// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.RecordingEditor;
using Rivet.Core.RecordingEditor.Audio;

namespace Rivet.Imaging.RecordingEditor;

/// <summary>What an export needs: the take, a snapshot of the document and the master's facts.</summary>
public sealed record RecordingExportInput
{
    public required TakeData Take { get; init; }

    public required EditDocument Document { get; init; }

    /// <summary>Master duration in source seconds.</summary>
    public required double Duration { get; init; }

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    /// <summary>The editor frame rate (30 or 60): the MP4 frame rate and the GIF plan's rate.</summary>
    public required int FrameRate { get; init; }
}

/// <summary>The GIF would exceed 300 frames (spec 02 §6.18).</summary>
public sealed class GifTooLongException(int maxSeconds) : Exception($"A GIF can be up to {maxSeconds} seconds long.")
{
    public int MaxSeconds { get; } = maxSeconds;
}

/// <summary>The master could not be read (spec 02 §6.16 step 10, "read failed").</summary>
public sealed class ExportReadException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The offline renderer behind every export (spec 02 §6.16, §6.18): one
/// decode pass, constant-frame-rate output frames composed by the same
/// <see cref="FrameCompositor"/> as the preview, and the mixed, time-stretched
/// audio. It never writes the bare recording: every frame goes through the
/// compositor, so blurs cannot be skipped. Cancellation is checked before
/// every frame.
/// </summary>
public static class RecordingExporter
{
    public static void ExportVideo(
        RecordingExportInput input,
        string outputPath,
        IVideoFrameSourceFactory sources,
        IVideoEncoderFactory encoders,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Action<PixelBuffer>? firstFrame = null)
    {
        var doc = input.Document.Sanitized(input.Duration);
        var timeline = EditTimeline.For(doc, input.Duration);
        if (timeline.OutputDuration <= 0)
        {
            throw new InvalidOperationException("Nothing is left to export.");
        }

        var fps = Math.Max(1, input.FrameRate);
        var preset = QualityPreset.Of(doc.Quality);
        var speed = ExportSpeed.Sanitize(doc.ExportSpeed);
        var exportDuration = timeline.OutputDuration / speed;
        var expected = ExportMath.ExpectedFrames(exportDuration, fps);

        IVideoFrameSource source;
        try
        {
            source = sources.Open(input.Take.VideoPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MediaUnavailableException)
        {
            throw new ExportReadException("The recording could not be read.", ex);
        }

        using (source)
        {
            var plan = FramePlan.Build(doc, input.Duration, input.SourceWidth, input.SourceHeight, fps, input.Take.Pointer, input.Take.PointerVersion, preset.Scale);
            using var assets = new CompositorAssets(input.Take.Pointer);
            using var compositor = new FrameCompositor(plan, assets);
            var width = plan.Layout.OutputWidth;
            var height = plan.Layout.OutputHeight;

            var tracks = AudioTracks(input.Take, doc);
            var settings = new VideoEncoderSettings
            {
                Width = width,
                Height = height,
                FrameRate = fps,
                BitRate = preset.BitRate(width, height, fps),
                KeyFrameIntervalFrames = preset.KeyFrameIntervalSeconds * fps,
                IncludeAudio = tracks.Count > 0,
            };

            using var encoder = encoders.Create(outputPath, settings);
            using var audio = tracks.Count > 0
                ? new AudioPump(new EditedAudioMix(tracks, timeline.KeptRanges), speed, (long)RecorderMath.Round(expected / (double)fps * ExportMath.AudioSampleRate))
                : null;
            var frameBuffer = new PixelBuffer(width, height);
            VideoFrame? last = null;
            for (var k = 0; k < expected; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exportTime = k / (double)fps;
                var editedTime = Math.Min(exportTime * speed, timeline.OutputDuration);
                var sourceTime = timeline.SourceTime(editedTime);
                VideoFrame? frame;
                try
                {
                    frame = source.GetFrame(sourceTime, cancellationToken) ?? last;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ExportReadException("The recording could not be read.", ex);
                }

                if (frame is null)
                {
                    throw new ExportReadException("The recording has no picture at " + sourceTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " s.");
                }

                last = frame;
                using (var pinned = new PinnedFrame(frame.Pixels))
                {
                    compositor.RenderInto(frameBuffer, pinned.Image, editedTime);
                }

                if (k == 0)
                {
                    firstFrame?.Invoke(frameBuffer);
                }

                encoder.WriteVideoFrame(frameBuffer, exportTime, 1.0 / fps);
                audio?.WriteUntil(encoder, exportTime + (1.0 / fps), cancellationToken);
                progress?.Report(ExportMath.VideoProgress(k + 1, expected));
            }

            audio?.WriteRest(encoder, cancellationToken);
            progress?.Report(ExportMath.FinalizingProgress);
            cancellationToken.ThrowIfCancellationRequested();
            encoder.Finish();
            progress?.Report(1.0);
        }
    }

    /// <summary>
    /// The GIF bytes (encoded fully in memory). Throws <see cref="GifTooLongException"/>
    /// before decoding anything when the export would exceed 300 frames.
    /// </summary>
    public static byte[] ExportGif(
        RecordingExportInput input,
        IVideoFrameSourceFactory sources,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Action<PixelBuffer>? firstFrame = null,
        bool dither = true)
    {
        var doc = input.Document.Sanitized(input.Duration);
        var timeline = EditTimeline.For(doc, input.Duration);
        if (timeline.OutputDuration <= 0)
        {
            throw new InvalidOperationException("Nothing is left to export.");
        }

        var gifFps = doc.GifFrameRate;
        var speed = ExportSpeed.Sanitize(doc.ExportSpeed);
        var exportDuration = timeline.OutputDuration / speed;
        var frames = ExportMath.GifFrameCount(exportDuration, gifFps);
        if (frames > ExportMath.MaxGifFrames)
        {
            throw new GifTooLongException(ExportMath.GifMaxSeconds(gifFps));
        }

        var full = CanvasLayout.Compute(input.SourceWidth, input.SourceHeight, doc.BackdropStyle, doc.Aspect, 1);
        var scale = ExportMath.GifScale(full.CanvasWidth, full.CanvasHeight, doc.GifSize);
        var plan = FramePlan.Build(doc, input.Duration, input.SourceWidth, input.SourceHeight, Math.Max(1, input.FrameRate), input.Take.Pointer, input.Take.PointerVersion, scale);

        IVideoFrameSource source;
        try
        {
            source = sources.Open(input.Take.VideoPath, DecodeOptions(input, plan));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MediaUnavailableException)
        {
            throw new ExportReadException("The recording could not be read.", ex);
        }

        using (source)
        {
            using var assets = new CompositorAssets(input.Take.Pointer);
            using var compositor = new FrameCompositor(plan, assets);
            var encoder = new GifEncoder(compositor.Width, compositor.Height, loopCount: 0);
            var buffer = new PixelBuffer(compositor.Width, compositor.Height);
            var delay = ExportMath.GifDelayCentiseconds(gifFps);
            for (var i = 0; i < frames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exportTime = Math.Min(exportDuration, i / (double)gifFps);
                var editedTime = Math.Min(exportTime * speed, timeline.OutputDuration);
                VideoFrame? frame;
                try
                {
                    frame = source.GetFrame(timeline.SourceTime(editedTime), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    frame = null;
                    if (i == frames - 1 && encoder.FrameCount == 0)
                    {
                        throw new ExportReadException("The recording could not be read.", ex);
                    }
                }

                if (frame is null)
                {
                    continue;   // a frame that fails to decode is skipped
                }

                using (var pinned = new PinnedFrame(frame.Pixels))
                {
                    compositor.RenderInto(buffer, pinned.Image, editedTime);
                }

                if (encoder.FrameCount == 0)
                {
                    firstFrame?.Invoke(buffer);
                }

                encoder.AddFrame(buffer, delay, dither);
                progress?.Report(ExportMath.GifProgress(i, frames));
            }

            if (encoder.FrameCount == 0)
            {
                throw new ExportReadException("No frame of the recording could be read.");
            }

            var bytes = encoder.Finish();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(1.0);
            return bytes;
        }
    }

    /// <summary>Kept tracks only, each at its gain (removed tracks are left out entirely).</summary>
    public static IReadOnlyList<AudioTrackInput> AudioTracks(TakeData take, EditDocument doc)
    {
        var tracks = new List<AudioTrackInput>();
        if (doc.KeepsSystemAudio && take.SystemAudioPath is { } system)
        {
            var gain = doc.SystemAudioGain;
            tracks.Add(new AudioTrackInput(system, () => gain));
        }

        if (doc.KeepsMicrophone && take.MicrophoneAudioPath is { } mic)
        {
            var gain = doc.MicrophoneGain;
            tracks.Add(new AudioTrackInput(mic, () => gain));
        }

        return tracks;
    }

    /// <summary>Decode no larger than the card needs at the plan's largest zoom.</summary>
    private static VideoOpenOptions? DecodeOptions(RecordingExportInput input, FramePlan plan)
    {
        var card = plan.Layout.Card;
        var needed = Math.Min(1, Math.Max(card.Width / (double)input.SourceWidth, card.Height / (double)input.SourceHeight) * plan.MaxZoom * 1.25);
        if (needed >= 0.95)
        {
            return null;
        }

        return new VideoOpenOptions
        {
            Width = RecorderMath.EvenSide(input.SourceWidth * needed),
            Height = RecorderMath.EvenSide(input.SourceHeight * needed),
        };
    }

    /// <summary>Feeds the encoder the mixed (and, for a speed ≠ 1, time-stretched) audio in step with the video.</summary>
    private sealed class AudioPump : IDisposable
    {
        private const int Block = 1024;
        private readonly EditedAudioMix _mix;
        private readonly TimeStretcher? _stretcher;
        private readonly long _total;
        private readonly float[] _buffer = new float[Block * 2];
        private readonly float[] _input = new float[Block * 2];
        private long _written;
        private bool _mixEnded;

        public AudioPump(EditedAudioMix mix, double speed, long totalFrames)
        {
            _mix = mix;
            _total = totalFrames;
            _stretcher = Math.Abs(speed - 1) > 1e-9 ? new TimeStretcher(speed) : null;
        }

        public void WriteUntil(IVideoEncoder encoder, double time, CancellationToken cancellationToken)
        {
            var target = Math.Min(_total, (long)RecorderMath.Round(time * ExportMath.AudioSampleRate));
            while (_written < target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frames = (int)Math.Min(Block, target - _written);
                var got = Pull(_buffer.AsSpan(0, frames * 2));
                if (got < frames)
                {
                    _buffer.AsSpan(got * 2, (frames - got) * 2).Clear();   // pad with silence
                }

                encoder.WriteAudio(_buffer.AsSpan(0, frames * 2), _written / (double)ExportMath.AudioSampleRate);
                _written += frames;
            }
        }

        public void WriteRest(IVideoEncoder encoder, CancellationToken cancellationToken) =>
            WriteUntil(encoder, _total / (double)ExportMath.AudioSampleRate, cancellationToken);

        private int Pull(Span<float> destination)
        {
            var frames = destination.Length / 2;
            if (_stretcher is null)
            {
                return _mix.Read(destination);
            }

            var filled = 0;
            while (filled < frames)
            {
                var n = _stretcher.Read(destination[(filled * 2)..]);
                filled += n;
                if (filled >= frames)
                {
                    break;
                }

                if (n == 0)
                {
                    if (_mixEnded)
                    {
                        break;
                    }

                    var got = _mix.Read(_input);
                    if (got == 0)
                    {
                        _mixEnded = true;
                        _stretcher.EndOfInput();
                    }
                    else
                    {
                        _stretcher.Write(_input.AsSpan(0, got * 2));
                    }
                }
            }

            return filled;
        }

        public void Dispose() => _mix.Dispose();
    }
}
