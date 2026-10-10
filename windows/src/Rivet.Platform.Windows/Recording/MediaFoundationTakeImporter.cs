// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Rivet.Core.App;
using Rivet.Core.Diagnostics;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Rivet.Core.Recording.Engine.Audio;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// Imports a movie as a take (spec 02 §3.19) with Media Foundation: the file
/// is probed (it must have a video track), copied into a new take under its
/// own extension, and its first sound track is decoded to the take's 48 kHz
/// stereo WAV so the editor finds sound where every take keeps it.
/// </summary>
public sealed class MediaFoundationTakeImporter(AppPaths paths) : ITakeImporter
{
    private static readonly Guid PresentationDuration = new("6C990D33-BB8E-477A-8598-0D5D96FCD88A"); // MF_PD_DURATION

    public Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(sourcePath, cancellationToken), cancellationToken);

    private string Import(string sourcePath, CancellationToken cancellationToken)
    {
        TakeImportRules.Validate(sourcePath, DiskSpace.FreeBytes(TakeFolders.Root(paths)));
        MediaFoundationRuntime.Startup();
        string? folder = null;
        try
        {
            var probe = Probe(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
            folder = TakeFolders.Create(paths);
            var extension = Path.GetExtension(sourcePath);
            var videoFile = "take" + (string.IsNullOrEmpty(extension) ? ".mp4" : extension.ToLowerInvariant());
            File.Copy(sourcePath, Path.Combine(folder, videoFile));
            cancellationToken.ThrowIfCancellationRequested();
            var hasSound = ExtractSound(sourcePath, Path.Combine(folder, RecordingSession.SystemAudioFile), probe.Duration, cancellationToken);
            TakeImportRules.Manifest(videoFile, probe.Codec, probe.Width, probe.Height, probe.FrameRate, probe.Duration, hasSound, AppIdentity.VersionString).Write(folder);
            Log.Info("recorder", $"Imported {Path.GetFileName(sourcePath)} as {Path.GetFileName(folder)} ({probe.Width}×{probe.Height}, {probe.Duration:0.0} s, sound: {hasSound}).");
            return folder;
        }
        catch (Exception ex)
        {
            if (folder is not null)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (ex is TakeImportException or OperationCanceledException)
            {
                throw;
            }

            throw new TakeImportException(TakeImportFailure.Unsupported, "The movie could not be imported.", ex);
        }
        finally
        {
            MediaFoundationRuntime.Shutdown();
        }
    }

    private static (int Width, int Height, double FrameRate, double Duration, string Codec) Probe(string path)
    {
        IMFSourceReader reader;
        try
        {
            reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        }
        catch (Exception ex)
        {
            throw new TakeImportException(TakeImportFailure.Unsupported, "Windows cannot read this file.", ex);
        }

        using (reader)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            }
            catch (Exception ex)
            {
                throw new TakeImportException(TakeImportFailure.NoVideo, "The file has no video.", ex);
            }

            using (type)
            {
                var size = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                var width = (int)(size >> 32);
                var height = (int)(size & 0xFFFFFFFF);
                var rate = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
                var fps = (rate & 0xFFFFFFFF) == 0 ? 30 : (rate >> 32) / (double)(rate & 0xFFFFFFFF);
                var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
                var codec = subtype == VideoFormatGuids.Hevc ? "hevc" : subtype == new Guid("34363248-0000-0010-8000-00AA00389B71") ? "h264" : subtype.ToString("D");
                double duration = 0;
                try
                {
                    var value = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDuration).Value;
                    duration = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) / 10_000_000.0;
                }
                catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidCastException or FormatException)
                {
                }

                if (width <= 0 || height <= 0)
                {
                    throw new TakeImportException(TakeImportFailure.Unsupported, "The video has no size.");
                }

                // Even sizes everywhere downstream (encoders refuse odd ones).
                return (width & ~1, height & ~1, fps, duration, codec);
            }
        }
    }

    /// <summary>Decodes the first sound track to 48 kHz stereo 16-bit PCM; false when there is none.</summary>
    private static bool ExtractSound(string source, string wavPath, double duration, CancellationToken cancellationToken)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(source, null);
        try
        {
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using var pcm = MediaFactory.MFCreateMediaType();
            pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            pcm.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)PcmFormat.Take.SampleRate);
            pcm.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)PcmFormat.Take.Channels);
            pcm.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            pcm.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)PcmFormat.Take.BlockAlign);
            pcm.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(PcmFormat.Take.SampleRate * PcmFormat.Take.BlockAlign));
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, pcm);
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException)
        {
            return false; // no sound track, or one Windows cannot decode
        }

        using var wav = new WavFileWriter(wavPath);
        var scratch = Array.Empty<short>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream, 0, out _, out var flags, out var timestamp);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                break;
            }

            if (sample is not null)
            {
                var target = (long)Math.Round(timestamp / 10_000_000.0 * PcmFormat.Take.SampleRate);
                if (target > wav.FramesWritten + (PcmFormat.Take.SampleRate / 50))
                {
                    wav.WriteSilence(target - wav.FramesWritten);
                }

                using var buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out var data, out _, out var length);
                try
                {
                    var samples = length / 2;
                    if (scratch.Length < samples)
                    {
                        scratch = new short[samples];
                    }

                    Marshal.Copy(data, scratch, 0, samples);
                    wav.WriteFrames(scratch.AsSpan(0, samples - (samples % 2)));
                }
                finally
                {
                    buffer.Unlock();
                }
            }

            if ((flags & SourceReaderFlag.EndOfStream) != 0)
            {
                break;
            }
        }

        var total = (long)Math.Round(duration * PcmFormat.Take.SampleRate);
        if (total > wav.FramesWritten)
        {
            wav.WriteSilence(total - wav.FramesWritten);
        }

        wav.Complete();
        return true;
    }
}
