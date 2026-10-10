// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rivet.Core.Diagnostics;
using Rivet.Core.RecordingEditor;

namespace Rivet.Platform.Windows.RecordingEditor;

/// <summary>
/// Plays the preview's mixed audio on the default output device through
/// WASAPI shared mode (NAudio's WasapiPlayer). The device's played-position is the master
/// clock while playing, so the picture follows the sound.
/// </summary>
internal sealed class WasapiPlayback : IAudioPlayback
{
    private WasapiPlayer? _output;

    public void Start(IAudioFeed feed)
    {
        Stop();
        try
        {
            // Shared mode: the audio engine converts rate and channels to the device's mix format itself.
            var output = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(60).Build();
            output.Init(new FeedSampleProvider(feed).ToWaveProvider());
            output.Play();
            _output = output;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Log.Warn("recording-editor", "Audio playback could not start; the preview plays silently.", ex);
            _output = null;
        }
    }

    public void Stop()
    {
        var output = _output;
        _output = null;
        if (output is null)
        {
            return;
        }

        try
        {
            output.Stop();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn("recording-editor", "Stopping audio playback failed.", ex);
        }
        finally
        {
            output.Dispose();
        }
    }

    public double? PlayedSeconds
    {
        get
        {
            var output = _output;
            if (output is null || output.PlaybackState != PlaybackState.Playing)
            {
                return null;
            }

            try
            {
                var format = output.OutputWaveFormat;
                return format.AverageBytesPerSecond > 0 ? output.GetPosition() / (double)format.AverageBytesPerSecond : null;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
            {
                return null;
            }
        }
    }

    public void Dispose() => Stop();

    /// <summary>Pulls the edited timeline's mix; plays silence after its end so the device keeps running.</summary>
    private sealed class FeedSampleProvider(IAudioFeed feed) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(feed.SampleRate, 2);

        public int Read(Span<float> buffer)
        {
            var frames = feed.Read(buffer);
            buffer[(frames * 2)..].Clear();
            return buffer.Length;
        }
    }
}

internal sealed class WasapiPlaybackFactory : IAudioPlaybackFactory
{
    public IAudioPlayback? Create()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            return devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? new WasapiPlayback() : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn("recording-editor", "No audio output device.", ex);
            return null;
        }
    }
}
