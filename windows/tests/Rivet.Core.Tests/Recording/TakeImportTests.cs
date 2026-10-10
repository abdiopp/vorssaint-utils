// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public sealed class TakeImportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-import-" + Guid.NewGuid());

    public TakeImportTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Only_regular_non_empty_files_with_room_for_a_copy_are_accepted()
    {
        var movie = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(movie, new byte[1000]);
        TakeImportRules.Validate(movie, freeBytes: 10_000_000_000);
        TakeImportRules.Validate(movie, freeBytes: null); // unknown space: allowed

        Assert.Equal(TakeImportFailure.NotEnoughSpace, Assert.Throws<TakeImportException>(() => TakeImportRules.Validate(movie, 500_000_500)).Reason);
        Assert.Equal(TakeImportFailure.NotAFile, Assert.Throws<TakeImportException>(() => TakeImportRules.Validate(Path.Combine(_folder, "missing.mp4"), null)).Reason);
        Assert.Equal(TakeImportFailure.NotAFile, Assert.Throws<TakeImportException>(() => TakeImportRules.Validate(_folder, null)).Reason);

        var empty = Path.Combine(_folder, "empty.mp4");
        File.WriteAllBytes(empty, []);
        Assert.Equal(TakeImportFailure.NotAFile, Assert.Throws<TakeImportException>(() => TakeImportRules.Validate(empty, null)).Reason);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_folder, "link.mp4");
            File.CreateSymbolicLink(link, movie);
            Assert.Equal(TakeImportFailure.NotAFile, Assert.Throws<TakeImportException>(() => TakeImportRules.Validate(link, null)).Reason);
        }
    }

    [Fact]
    public void Imported_takes_keep_the_source_rate_and_have_no_side_tracks()
    {
        var manifest = TakeImportRules.Manifest("take.mov", "hevc", 1080, 1920, 23.976, 12.5, hasSound: true, "1.0.0");
        Assert.Equal(24, manifest.Capture.Fps); // not snapped to 30/60 (spec §8.3 fix)
        Assert.Equal("take.mov", manifest.Video.File);
        Assert.False(manifest.Video.Vfr);
        Assert.Equal(12.5, manifest.Video.DurationSeconds);
        Assert.Null(manifest.PointerTrack);
        Assert.Null(manifest.TypingTrack);
        Assert.Equal(TakeAudioSource.System, Assert.Single(manifest.Audio).Source);

        manifest.Write(_folder);
        Assert.Equal(1920, TakeManifest.Read(_folder)!.Video.Height);
    }
}
