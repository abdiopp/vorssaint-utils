// SPDX-License-Identifier: GPL-3.0-or-later
using Rivet.Core.Diagnostics;

namespace Rivet.Core.Recording.Engine.Audio;

/// <summary>
/// Writes one audio source into its take WAV on the shared timeline. Each
/// packet's host time goes through the <see cref="PauseClock"/> (packets that
/// start before the origin or touch a pause are dropped whole, spec §6.2);
/// its target position is then compared with what was written so far:
/// <list type="bullet">
/// <item>a gap larger than the tolerance is filled with silence (loopback
/// delivers nothing while the PC is quiet, and the first packets arrive after
/// t = 0, so every track starts at t = 0);</item>
/// <item>an overlap larger than the tolerance trims the packet's head;</item>
/// <item>anything within the tolerance is appended contiguously, absorbing
/// timestamp jitter. The same rule corrects slow device-clock drift in
/// steps no larger than the tolerance.</item>
/// </list>
/// <see cref="Finish"/> pads (or trims) the file to the recording's length so
/// every track ends on the shared timeline. Thread-safe.
/// </summary>
public sealed class AudioTrackWriter : IDisposable
{
    /// <summary>Jitter absorbed before padding or trimming (the spec's sync budget is 25 ms).</summary>
    public const double DefaultTolerance = 0.020;

    private readonly object _gate = new();
    private readonly PauseClock _clock;
    private readonly WavFileWriter _wav;
    private readonly long _toleranceFrames;
    private PcmConverter? _converter;
    private short[] _buffer = new short[4096];
    private bool _finished;
    private bool _loggedFailure;

    public AudioTrackWriter(string path, PauseClock clock, double toleranceSeconds = DefaultTolerance)
    {
        _clock = clock;
        _wav = new WavFileWriter(path);
        _toleranceFrames = (long)Math.Round(Math.Max(0, toleranceSeconds) * PcmFormat.Take.SampleRate);
    }

    public string Path => _wav.Path;

    /// <summary>Packets accepted so far (not dropped by the clock).</summary>
    public int PacketsAccepted { get; private set; }

    public long FramesWritten
    {
        get
        {
            lock (_gate)
            {
                return _wav.FramesWritten;
            }
        }
    }

    /// <summary>Matches <see cref="AudioPacketHandler"/>.</summary>
    public void Append(ReadOnlySpan<byte> data, PcmFormat format, double hostTime, bool silent)
    {
        if (!format.IsValid || data.Length < format.BlockAlign)
        {
            return;
        }

        var inputFrames = data.Length / format.BlockAlign;
        var duration = inputFrames / (double)format.SampleRate;
        var time = _clock.SampleTime(hostTime, duration);
        if (time is not { } t)
        {
            return;
        }

        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            try
            {
                if (_converter is null || _converter.Source != format)
                {
                    _converter = new PcmConverter(format);
                }

                int frames;
                if (silent)
                {
                    frames = _converter.EstimateOutputFrames(inputFrames);
                    if (_buffer.Length < frames * 2)
                    {
                        _buffer = new short[frames * 2];
                    }

                    Array.Clear(_buffer, 0, frames * 2);
                }
                else
                {
                    frames = _converter.Convert(data, ref _buffer);
                }

                WriteAligned(t, frames);
                PacketsAccepted++;
            }
            catch (IOException ex)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Log.Error("recorder", $"Writing {System.IO.Path.GetFileName(Path)} failed.", ex);
                }
            }
        }
    }

    /// <summary>Pads or trims to <paramref name="endTime"/> recording seconds and closes the file.</summary>
    public void Finish(double endTime)
    {
        lock (_gate)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                var target = (long)Math.Round(Math.Max(0, endTime) * PcmFormat.Take.SampleRate);
                var written = _wav.FramesWritten;
                if (target > written)
                {
                    _wav.WriteSilence(target - written);
                }
                else if (target < written)
                {
                    _wav.TruncateTo(target);
                }
            }
            finally
            {
                _wav.Complete();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _finished = true;
            _wav.Dispose();
        }
    }

    private void WriteAligned(double time, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        var target = (long)Math.Round(time * PcmFormat.Take.SampleRate);
        var delta = target - _wav.FramesWritten;
        var skip = 0;
        if (delta > _toleranceFrames)
        {
            _wav.WriteSilence(delta);
        }
        else if (delta < -_toleranceFrames)
        {
            var overlap = -delta;
            if (overlap >= frames)
            {
                return;
            }

            skip = (int)overlap;
        }

        _wav.WriteFrames(_buffer.AsSpan(skip * 2, (frames - skip) * 2));
    }
}
