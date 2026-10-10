// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Xunit;

namespace Rivet.Core.Tests;

public class RecordingContractTests
{
    [Fact]
    public void Pointer_track_round_trips()
    {
        var track = new PointerTrack
        {
            SystemScale = 1.25f,
            DisplayScale = 1.5f,
            Samples = [new(0f, 0.1f, 0.2f, 0, true), new(0.008f, 0.11f, 0.21f, 1, true), new(0.016f, -0.2f, 1.3f, 1, false)],
            Clicks = [new(0.5f, true), new(0.62f, false)],
            Shapes = [new(1, 1, 32, 32, [1, 2, 3]), new(16, 16, 48, 48, [4, 5])],
        };
        var decoded = PointerTrack.Decode(track.Encode());
        Assert.Equal(track.Samples, decoded.Samples);
        Assert.Equal(track.Clicks, decoded.Clicks);
        Assert.Equal(2, decoded.Shapes.Count);
        Assert.Equal(new byte[] { 4, 5 }, decoded.Shapes[1].Png);
        Assert.Equal(1.5f, decoded.DisplayScale);
    }

    [Fact]
    public void Truncated_or_foreign_data_is_handled()
    {
        var track = new PointerTrack { Samples = [new(0, 0, 0, 7, true), new(1, 1, 1, 0, true)], Shapes = [new(0, 0, 8, 8, [9])] };
        var bytes = track.Encode();
        Assert.True(PointerTrack.Decode(bytes.AsSpan(0, 28 + 16)).Samples.Count == 1);
        Assert.Equal(0, PointerTrack.Decode(bytes).Samples[0].ShapeIndex);
        Assert.True(PointerTrack.Decode([1, 2, 3]).IsEmpty);
        bytes[0] = (byte)'X';
        Assert.True(PointerTrack.Decode(bytes).IsEmpty);
    }

    [Fact]
    public void Manifest_round_trips_through_the_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rivet-take-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var manifest = new TakeManifest
            {
                CreatedAt = DateTimeOffset.UtcNow,
                Capture = new TakeCapture
                {
                    Kind = TakeCaptureKind.Area,
                    Monitor = new TakeMonitor { Device = "\\\\.\\DISPLAY1", DpiScale = 1.5, RectPx = [0, 0, 2880, 1800] },
                    RegionPx = [120, 80, 1280, 720],
                },
                Video = new TakeVideo { Width = 1280, Height = 720, DurationSeconds = 12.5 },
                Audio = [new TakeAudio { Source = TakeAudioSource.Microphone, File = "mic.wav" }],
                PointerTrack = new TakePointerTrack(),
                TypingTrack = TypingTrack.FileName,
            };
            manifest.Write(folder);
            var read = TakeManifest.Read(folder);
            Assert.NotNull(read);
            Assert.Equal(TakeCaptureKind.Area, read!.Capture.Kind);
            Assert.Equal(1280, read.Video.Width);
            Assert.Equal(TakeAudioSource.Microphone, read.Audio[0].Source);
            Assert.Contains("\"microphone\"", manifest.ToJson(), StringComparison.Ordinal);

            new TypingTrack([0.5, 1.25]).Write(Path.Combine(folder, TypingTrack.FileName));
            Assert.Equal([0.5, 1.25], TypingTrack.Read(Path.Combine(folder, TypingTrack.FileName)).Times);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
