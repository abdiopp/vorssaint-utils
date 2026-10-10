// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Rivet.Core.Diagnostics;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Vortice.MediaFoundation;

namespace Rivet.Platform.Windows.Recording;

/// <summary>
/// "Save straight away" without the editor: the master's H.264 is copied
/// sample by sample (no re-encoding) into a new MP4 together with one AAC
/// track (160 kb/s) that mixes the take's system-audio and microphone WAVs.
/// The take keeps its separate tracks for the editor; the saved file is what
/// a player expects (one picture, one sound). There is no pointer in it: the
/// pointer is only ever drawn by the editor.
/// </summary>
public sealed class MediaFoundationTakeExporter : IRawTakeExporter
{
    private const int ChunkFrames = 4_800; // 100 ms of 48 kHz audio

    public Task<bool> ExportAsync(string takeFolder, TakeManifest manifest, string outputPath, CancellationToken cancellationToken) =>
        Task.Run(() => Export(takeFolder, manifest, outputPath, cancellationToken), cancellationToken);

    private static bool Export(string takeFolder, TakeManifest manifest, string outputPath, CancellationToken cancellationToken)
    {
        var videoPath = Path.Combine(takeFolder, manifest.Video.File);
        if (!File.Exists(videoPath))
        {
            return false;
        }

        var audioPaths = manifest.Audio.Select(a => Path.Combine(takeFolder, a.File)).Where(File.Exists).ToList();
        MediaFoundationRuntime.Startup();
        var completed = false;
        try
        {
            using var mixer = audioPaths.Count > 0 ? new WavMixer(audioPaths) : null;
            using (var reader = MediaFactory.MFCreateSourceReaderFromURL(videoPath, null))
            {
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
                using var videoType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, videoType);

                using var attributes = MediaFactory.MFCreateAttributes(1);
                attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
                using var writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attributes);
                var videoStream = writer.AddStream(videoType);
                writer.SetInputMediaType(videoStream, videoType, null);

                var audioStream = -1;
                if (mixer is not null)
                {
                    using var aac = MediaFactory.MFCreateMediaType();
                    aac.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    aac.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                    aac.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)PcmFormat.Take.SampleRate);
                    aac.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)PcmFormat.Take.Channels);
                    aac.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    aac.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 20_000u); // 160 kb/s
                    aac.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                    aac.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                    audioStream = writer.AddStream(aac);

                    using var pcm = MediaFactory.MFCreateMediaType();
                    pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                    pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                    pcm.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)PcmFormat.Take.SampleRate);
                    pcm.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)PcmFormat.Take.Channels);
                    pcm.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    pcm.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)PcmFormat.Take.BlockAlign);
                    pcm.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(PcmFormat.Take.SampleRate * PcmFormat.Take.BlockAlign));
                    writer.SetInputMediaType(audioStream, pcm, null);
                }

                writer.BeginWriting();
                long audioFrames = 0;
                var chunk = new byte[ChunkFrames * PcmFormat.Take.BlockAlign];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out var flags, out var timestamp);
                    if ((flags & SourceReaderFlag.Error) != 0)
                    {
                        throw new InvalidOperationException("Reading the master failed.");
                    }

                    if (sample is not null)
                    {
                        // Keep the two streams roughly interleaved in time.
                        while (mixer is not null && audioFrames * 10_000_000L / PcmFormat.Take.SampleRate <= timestamp && WriteAudio(writer, audioStream, mixer, chunk, ref audioFrames))
                        {
                        }

                        writer.WriteSample(videoStream, sample);
                    }

                    if ((flags & SourceReaderFlag.EndOfStream) != 0)
                    {
                        break;
                    }
                }

                while (mixer is not null && WriteAudio(writer, audioStream, mixer, chunk, ref audioFrames))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                writer.Finalize();
            }

            completed = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("recorder", "Writing the MP4 with sound failed.", ex);
            return false;
        }
        finally
        {
            MediaFoundationRuntime.Shutdown();
            if (!completed)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool WriteAudio(IMFSinkWriter writer, int stream, WavMixer mixer, byte[] chunk, ref long writtenFrames)
    {
        var frames = mixer.Read(chunk);
        if (frames <= 0)
        {
            return false;
        }

        var bytes = frames * PcmFormat.Take.BlockAlign;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out var destination, out _, out _);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(chunk, 0, destination, bytes);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = bytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = writtenFrames * 10_000_000L / PcmFormat.Take.SampleRate;
        sample.SampleDuration = frames * 10_000_000L / PcmFormat.Take.SampleRate;
        writer.WriteSample(stream, sample);
        writtenFrames += frames;
        return true;
    }

    /// <summary>Reads the take's 48 kHz stereo 16-bit WAVs in step and sums them (clamped).</summary>
    private sealed class WavMixer : IDisposable
    {
        private readonly List<FileStream> _streams = [];
        private readonly byte[] _scratch = new byte[ChunkFrames * 4];

        public WavMixer(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                var stream = File.OpenRead(path);
                if (SeekToData(stream))
                {
                    _streams.Add(stream);
                }
                else
                {
                    stream.Dispose();
                }
            }
        }

        /// <summary>Fills <paramref name="output"/> with mixed frames; returns how many (0 at the end).</summary>
        public int Read(byte[] output)
        {
            var frames = 0;
            Array.Clear(output);
            foreach (var stream in _streams)
            {
                var read = ReadFully(stream, _scratch, output.Length);
                var count = read / 4;
                frames = Math.Max(frames, count);
                for (var i = 0; i < count * 2; i++)
                {
                    var mixed = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 2)) + BinaryPrimitives.ReadInt16LittleEndian(_scratch.AsSpan(i * 2));
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)Math.Clamp(mixed, short.MinValue, short.MaxValue));
                }
            }

            return frames;
        }

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        /// <summary>Walks the RIFF chunks to the start of "data".</summary>
        private static bool SeekToData(FileStream stream)
        {
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            {
                return false;
            }

            Span<byte> chunk = stackalloc byte[8];
            while (stream.Read(chunk) == 8)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                if (chunk[..4].SequenceEqual("data"u8))
                {
                    return true;
                }

                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }

            return false;
        }
    }
}
