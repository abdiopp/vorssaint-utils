// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rivet.Core.Diagnostics;
using Rivet.Core.RecordingEditor;
using Rivet.Core.RecordingEditor.Audio;
using Rivet.Imaging.Skia;
using SkiaSharp;

namespace Rivet.App.Features.RecordingEditor;

/// <summary>Filmstrip thumbnails and audio waveforms, computed in the background (spec 02 §6.20).</summary>
public sealed partial class EditorSession
{
    public const int ThumbnailCount = 14;
    private readonly CancellationTokenSource _mediaCts = new();
    private readonly SKImage?[] _thumbnails = new SKImage?[ThumbnailCount];

    public IReadOnlyList<SKImage?> Thumbnails => _thumbnails;

    public float[]? SystemWaveform { get; private set; }

    public float[]? MicrophoneWaveform { get; private set; }

    /// <summary>Starts thumbnail and waveform extraction (cancelled when the editor closes).</summary>
    public void StartMediaPreviews()
    {
        if (!IsReady)
        {
            return;
        }

        var token = _mediaCts.Token;
        var factory = _services.GetRequiredService<IVideoFrameSourceFactory>();
        _ = Task.Run(() => LoadThumbnails(factory, token), token);
        if (Take.SystemAudioPath is { } system)
        {
            _ = Task.Run(() =>
            {
                var peaks = Waveform.Compute(system, Duration, token);
                Dispatcher.UIThread.Post(() =>
                {
                    SystemWaveform = peaks;
                    Raise(SessionChange.Media);
                });
            }, token);
        }

        if (Take.MicrophoneAudioPath is { } mic)
        {
            _ = Task.Run(() =>
            {
                var peaks = Waveform.Compute(mic, Duration, token);
                Dispatcher.UIThread.Post(() =>
                {
                    MicrophoneWaveform = peaks;
                    Raise(SessionChange.Media);
                });
            }, token);
        }
    }

    /// <summary>14 frames at the middle of equal slots, at most 240×240.</summary>
    private void LoadThumbnails(IVideoFrameSourceFactory factory, CancellationToken token)
    {
        try
        {
            var scale = Math.Min(1, 240.0 / Math.Max(SourceWidth, SourceHeight));
            using var source = factory.Open(Take.VideoPath, new VideoOpenOptions
            {
                Width = RecorderMath.EvenSide(SourceWidth * scale),
                Height = RecorderMath.EvenSide(SourceHeight * scale),
                CacheSize = 2,
            });
            for (var i = 0; i < ThumbnailCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var frame = source.GetFrame((i + 0.5) / ThumbnailCount * Duration, token);
                if (frame is null)
                {
                    continue;
                }

                var image = SkiaConvert.ToImage(frame.Pixels);
                var index = i;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposed)
                    {
                        image.Dispose();
                        return;
                    }

                    _thumbnails[index]?.Dispose();
                    _thumbnails[index] = image;
                    Raise(SessionChange.Media);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or MediaUnavailableException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn("recording-editor", "Filmstrip thumbnails failed.", ex);
        }
    }

    private void DisposeMedia()
    {
        _mediaCts.Cancel();
        for (var i = 0; i < _thumbnails.Length; i++)
        {
            _thumbnails[i]?.Dispose();
            _thumbnails[i] = null;
        }
    }
}
