// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.Modules;
using Rivet.Core.Recording;
using Rivet.Core.RecordingEditor;
using Rivet.Imaging.RecordingEditor;

namespace Rivet.Platform.Fake.RecordingEditor;

/// <summary>
/// Development stand-ins: the master is synthesized (a fake desktop sized
/// from the take's manifest), MP4 encoding is reported unavailable (no
/// encoder on this host — GIF export still works), the preview plays
/// silently and there are no wallpapers.
/// </summary>
public sealed class FakeRecordingEditorRegistrar : IPlatformRegistrar
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IVideoFrameSourceFactory, SyntheticVideoSourceFactory>();
        services.AddSingleton<IVideoEncoderFactory, UnavailableVideoEncoderFactory>();
        services.AddSingleton<IAudioPlaybackFactory, NoAudioPlaybackFactory>();
        services.AddSingleton<IRecordingEditorShell, FakeRecordingEditorShell>();
    }
}

/// <summary>Synthesizes the take's video from its manifest (size, duration, frame rate).</summary>
public sealed class SyntheticVideoSourceFactory : IVideoFrameSourceFactory
{
    public IVideoFrameSource Open(string path, VideoOpenOptions? options = null)
    {
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var manifest = TakeManifest.Read(folder);
        if (manifest is null && !File.Exists(path))
        {
            throw new FileNotFoundException("The recording is missing.", path);
        }

        var width = manifest?.Video.Width ?? 1280;
        var height = manifest?.Video.Height ?? 800;
        var duration = manifest?.Video.DurationSeconds is > 0 ? manifest.Video.DurationSeconds : 10;
        var fps = manifest?.Capture.Fps ?? 60;
        return new SyntheticVideoSource(width, height, duration, fps, SyntheticScene.Desktop, options);
    }
}

public sealed class UnavailableVideoEncoderFactory : IVideoEncoderFactory
{
    public bool IsAvailable => false;

    public IVideoEncoder Create(string path, VideoEncoderSettings settings) =>
        throw new MediaUnavailableException("MP4 encoding needs Media Foundation (Windows).");
}

public sealed class NoAudioPlaybackFactory : IAudioPlaybackFactory
{
    public IAudioPlayback? Create() => null;
}

public sealed class FakeRecordingEditorShell : IRecordingEditorShell
{
    public IReadOnlyList<string> CurrentWallpapers() => [];

    public void Beep() => Log.Info("recording-editor", "[fake] beep");
}
