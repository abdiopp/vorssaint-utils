// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Platform;
using Rivet.Core.Recording;
using Rivet.Core.Recording.Engine;
using Xunit;

namespace Rivet.Core.Tests.Recording;

public sealed class RecordingSessionTests : IDisposable
{
    private static readonly ScreenInfo Monitor = new()
    {
        Id = "\\\\.\\DISPLAY1",
        FriendlyName = "Test",
        Bounds = new PixelRect(0, 0, 1920, 1080),
        WorkArea = new PixelRect(0, 0, 1920, 1040),
        Scale = 1.25,
        IsPrimary = true,
    };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rivet-session-" + Guid.NewGuid(), "Take-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
    private readonly ManualClock _host = new(2000);
    private readonly TestVideoBackend _video = new();
    private readonly TestAudioBackend _audio = new();
    private readonly TestCursor _cursor = new();
    private readonly TestHooks _hooks = new();
    private readonly TestSystem _system = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_full_recording_writes_every_track_then_the_manifest()
    {
        var manifestExistedAtFinish = true;
        _video.OnFinish = folder => manifestExistedAtFinish = File.Exists(Path.Combine(folder, TakeManifest.FileName));
        var session = Create(systemAudio: true, microphone: true);
        await session.StartAsync();
        Assert.True(_video.Session!.Started);
        Assert.True(_audio.System!.Started);
        Assert.True(_audio.Microphone!.Started);
        Assert.Equal(1, _hooks.MouseSubscribers);
        Assert.Equal(1, _hooks.KeyboardSubscribers);

        _audio.System.Deliver(new byte[4800 * 4], _host.Now + 0.1);
        _hooks.Key(0x41, KeyAction.Down);
        _host.Advance(1);
        _hooks.Mouse(MouseHookKind.LeftDown);
        Assert.True(session.Pause());
        _host.Advance(5); // paused: not part of the recording
        Assert.True(session.Resume());
        _host.Advance(1);
        Thread.Sleep(50); // let the sampler thread take a few samples

        var outcome = await session.StopAsync();
        Assert.True(outcome.Written);
        Assert.Equal(2, outcome.Duration, 6);
        Assert.False(manifestExistedAtFinish);
        Assert.Equal(2, _video.Session.FinishedAt!.Value, 6);
        Assert.True(_audio.System.Stopped && _audio.Microphone.Stopped);
        Assert.Equal(0, _hooks.MouseSubscribers);
        Assert.Equal(0, _hooks.KeyboardSubscribers);

        var manifest = TakeManifest.Read(_folder);
        Assert.NotNull(manifest);
        Assert.Equal(TakeCaptureKind.Area, manifest!.Capture.Kind);
        Assert.Equal(30, manifest.Capture.Fps);
        Assert.Equal([100, 200, 640, 480], manifest.Capture.RegionPx);
        Assert.Equal(1.25, manifest.Capture.Monitor.DpiScale);
        Assert.Equal(2, manifest.Video.DurationSeconds, 6);
        Assert.True(manifest.Video.Vfr);
        Assert.Equal([TakeAudioSource.System, TakeAudioSource.Microphone], manifest.Audio.Select(a => a.Source));
        Assert.Equal(TypingTrack.FileName, manifest.TypingTrack);

        // Both WAVs last exactly the recording, starting at t = 0.
        foreach (var audio in manifest.Audio)
        {
            var length = new FileInfo(Path.Combine(_folder, audio.File)).Length - 44;
            Assert.Equal(2 * 48_000 * 4, length);
        }

        Assert.Equal([0.0], TypingTrack.Read(Path.Combine(_folder, TypingTrack.FileName)).Times);
        var pointer = PointerTrack.Read(Path.Combine(_folder, PointerTrack.FileName));
        Assert.Equal([(1f, true)], pointer.Clicks.Select(c => (c.Time, c.IsDown)));
        Assert.NotNull(manifest.PointerTrack);
        Assert.False(pointer.IsEmpty);
        Assert.Equal(1.25f, pointer.DisplayScale);
    }

    [Fact]
    public async Task A_stop_during_start_waits_for_start_and_nothing_is_written()
    {
        _video.Gate = new TaskCompletionSource();
        var session = Create(systemAudio: true, microphone: false);
        var start = session.StartAsync();
        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted); // waits for the start to unwind
        _video.Gate.SetResult();
        await start;
        var outcome = await stop;
        Assert.False(outcome.Written);
        Assert.True(_video.Session!.Cancelled);
        Assert.False(_video.Session.Started);
        Assert.Null(_audio.System); // start stopped before opening anything else
        Assert.False(File.Exists(Path.Combine(_folder, TakeManifest.FileName)));
    }

    [Fact]
    public async Task A_failed_start_cleans_up_and_stop_reports_nothing_written()
    {
        _video.CreateFailure = new InvalidOperationException("no capture");
        var session = Create(systemAudio: false, microphone: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
        var outcome = await session.StopAsync();
        Assert.False(outcome.Written);
        Assert.Equal("no capture", outcome.Error);
    }

    [Fact]
    public async Task A_blocked_microphone_is_reported_and_the_recording_goes_on()
    {
        _audio.MicrophoneFails = true;
        _audio.SystemFails = true;
        var session = Create(systemAudio: true, microphone: true);
        var microphoneReported = 0;
        var systemReported = 0;
        session.MicrophoneUnavailable += (_, _) => microphoneReported++;
        session.SystemAudioUnavailable += (_, _) => systemReported++;
        await session.StartAsync();
        Assert.Equal(1, microphoneReported);
        Assert.Equal(1, systemReported);
        Assert.False(session.HasMicrophone);
        _host.Advance(0.5);
        var outcome = await session.StopAsync();
        Assert.True(outcome.Written);
        Assert.Empty(outcome.Manifest!.Audio);
        Assert.False(File.Exists(Path.Combine(_folder, RecordingSession.MicrophoneFile)));
    }

    [Fact]
    public async Task Capture_ending_on_its_own_is_raised_once_and_stop_is_idempotent()
    {
        var session = Create(systemAudio: false, microphone: false, kind: RecordingTargetKind.Window);
        var ended = new List<CaptureEndReason>();
        session.CaptureEnded += (_, e) => ended.Add(e.Reason);
        await session.StartAsync();
        _video.Session!.RaiseEnded(CaptureEndReason.WindowClosed);
        _video.Session.RaiseEnded(CaptureEndReason.WindowClosed);
        Assert.Equal([CaptureEndReason.WindowClosed], ended);
        _host.Advance(1);
        var first = session.StopAsync();
        var second = session.StopAsync();
        Assert.Same(first, second);
        var outcome = await first;
        Assert.True(outcome.Written);
        Assert.Equal(TakeCaptureKind.Window, outcome.Manifest!.Capture.Kind);
        Assert.Equal("notepad", outcome.Manifest.Capture.Window!.Process);
        Assert.True(_video.Session.Disposed);
    }

    [Fact]
    public async Task Discard_never_finalizes()
    {
        var session = Create(systemAudio: true, microphone: false);
        await session.StartAsync();
        _host.Advance(1);
        var outcome = await session.DiscardAsync();
        Assert.False(outcome.Written);
        Assert.True(_video.Session!.Cancelled);
        Assert.False(File.Exists(Path.Combine(_folder, TakeManifest.FileName)));
    }

    private RecordingSession Create(bool systemAudio, bool microphone, RecordingTargetKind kind = RecordingTargetKind.Area)
    {
        _system.Window = new WindowRects(new PixelRect(100, 200, 640, 480), new PixelRect(100, 200, 640, 480));
        return new RecordingSession(
            new RecordingSessionOptions
            {
                Target = new RecordingTarget { Kind = kind, Monitor = Monitor, Region = new PixelRect(100, 200, 640, 480), Window = kind == RecordingTargetKind.Window ? 42 : 0 },
                TakeFolder = _folder,
                FrameRate = 30,
                SystemAudio = systemAudio,
                Microphone = microphone,
                AppVersion = "1.2.3",
            },
            new RecordingServices(_video, _audio, _cursor, _system, _hooks, _host));
    }
}

public sealed class TakeSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rivet-sweep-" + Guid.NewGuid());

    public TakeSweeperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Old_finished_takes_go_recent_and_owned_ones_stay()
    {
        var now = DateTime.UtcNow;
        var old = Take(now - TimeSpan.FromHours(30));
        var recent = Take(now - TimeSpan.FromHours(2));
        var owned = Take(now - TimeSpan.FromHours(48));
        var editedRecently = Take(now - TimeSpan.FromHours(48));
        File.WriteAllText(Path.Combine(editedRecently, "edit.json"), "{}");
        var other = Directory.CreateDirectory(Path.Combine(_root, "Take-not-a-guid")).FullName;

        var deleted = TakeSweeper.Sweep(_root, [owned], now);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(owned));
        Assert.True(Directory.Exists(editedRecently));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void Unfinished_takes_expire_after_an_hour()
    {
        var folder = Path.Combine(_root, TakeFolders.Prefix + Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(folder);
        Assert.False(TakeSweeper.IsExpired(folder, DateTime.UtcNow));
        Assert.True(TakeSweeper.IsExpired(folder, DateTime.UtcNow + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void A_take_whose_master_is_open_is_in_use()
    {
        var folder = Take(DateTime.UtcNow - TimeSpan.FromDays(3));
        using (new FileStream(Path.Combine(folder, RecordingSession.VideoFile), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (TakeSweeper.IsInUse(folder))
            {
                Assert.Equal(0, TakeSweeper.Sweep(_root, [], DateTime.UtcNow));
            }
        }

        Assert.False(TakeSweeper.IsInUse(folder));
        Assert.Equal(1, TakeSweeper.Sweep(_root, [], DateTime.UtcNow));
    }

    private string Take(DateTime masterTime)
    {
        var folder = Path.Combine(_root, TakeFolders.Prefix + Guid.NewGuid().ToString("D").ToUpperInvariant());
        Directory.CreateDirectory(folder);
        var master = Path.Combine(folder, RecordingSession.VideoFile);
        File.WriteAllBytes(master, [1, 2, 3]);
        File.SetLastWriteTimeUtc(master, masterTime);
        return folder;
    }
}
