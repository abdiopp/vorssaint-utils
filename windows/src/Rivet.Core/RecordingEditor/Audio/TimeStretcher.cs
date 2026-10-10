// SPDX-License-Identifier: GPL-3.0-or-later
namespace Rivet.Core.RecordingEditor.Audio;

/// <summary>
/// Pitch-preserving time stretch for the export speed (spec 02 §6.16 step 7):
/// WSOLA (waveform-similarity overlap-add) on stereo float audio. 40 ms Hann
/// segments overlap by half; each segment is taken from around its nominal
/// input position (input advances <c>speed</c> times faster than the output)
/// where it best continues the previous one, so periodic sound keeps its pitch
/// and phase. Streaming: write input, read output. The output length is
/// exactly <c>round(input / speed)</c> frames once <see cref="EndOfInput"/> is called.
/// </summary>
public sealed class TimeStretcher
{
    private readonly double _tempo;
    private readonly int _n;
    private readonly int _hs;
    private readonly int _delta;
    private readonly int _corrLength;
    private readonly float[] _window;
    private readonly float[] _firstWindow;
    private readonly float[] _accumulator;
    private float[] _input = new float[1 << 16];
    private float[] _mono = new float[1 << 15];
    private int _inputFrames;
    private long _inputBase;
    private long _totalInput;
    private bool _ended;
    private long _segment;
    private long _previousPosition = -1;
    private long _emitted;
    private float[] _ready = new float[4096];
    private int _readyFrames;
    private int _readyOffset;

    public TimeStretcher(double tempo, int sampleRate = ExportMath.AudioSampleRate)
    {
        _tempo = ExportSpeed.Sanitize(tempo);
        _n = Math.Max(64, (int)RecorderMath.Round(0.040 * sampleRate) & ~1);
        _hs = _n / 2;
        _delta = Math.Max(8, (int)RecorderMath.Round(0.012 * sampleRate));
        _corrLength = _hs;
        _window = new float[_n];
        _firstWindow = new float[_n];
        for (var i = 0; i < _n; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _n)));
            _firstWindow[i] = i < _hs ? 1f : _window[i];
        }

        _accumulator = new float[_n * 2];
    }

    public double Tempo => _tempo;

    /// <summary>Total output frames once the input has ended.</summary>
    public long ExpectedOutput => (long)RecorderMath.Round(_totalInput / _tempo);

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        EnsureInput(_inputFrames + frames);
        interleaved[..(frames * 2)].CopyTo(_input.AsSpan(_inputFrames * 2));
        for (var i = 0; i < frames; i++)
        {
            _mono[_inputFrames + i] = (interleaved[i * 2] + interleaved[(i * 2) + 1]) * 0.5f;
        }

        _inputFrames += frames;
        _totalInput += frames;
    }

    public void EndOfInput() => _ended = true;

    /// <summary>Reads available output; 0 means more input is needed (or everything was read).</summary>
    public int Read(Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        var written = 0;
        while (written < frames)
        {
            if (_readyFrames - _readyOffset > 0)
            {
                var take = Math.Min(frames - written, _readyFrames - _readyOffset);
                _ready.AsSpan(_readyOffset * 2, take * 2).CopyTo(interleaved[(written * 2)..]);
                _readyOffset += take;
                written += take;
                continue;
            }

            if (_ended && _emitted >= ExpectedOutput)
            {
                break;
            }

            if (!ProduceSegment())
            {
                break;
            }
        }

        return written;
    }

    public bool IsFinished => _ended && _emitted >= ExpectedOutput && _readyFrames - _readyOffset == 0;

    /// <summary>Whole-buffer convenience (tests, short clips).</summary>
    public static float[] Process(ReadOnlySpan<float> interleaved, double tempo, int sampleRate = ExportMath.AudioSampleRate)
    {
        var stretcher = new TimeStretcher(tempo, sampleRate);
        stretcher.Write(interleaved);
        stretcher.EndOfInput();
        var output = new float[stretcher.ExpectedOutput * 2];
        var offset = 0;
        while (offset < output.Length)
        {
            var n = stretcher.Read(output.AsSpan(offset));
            if (n == 0)
            {
                break;
            }

            offset += n * 2;
        }

        return output;
    }

    /// <summary>Adds one segment and moves the finished half to the ready queue.</summary>
    private bool ProduceSegment()
    {
        var nominal = (long)Math.Round(_segment * _hs * _tempo);
        long position;
        if (_segment == 0)
        {
            position = 0;
            if (!HasInput(_n))
            {
                return false;
            }
        }
        else
        {
            var continuation = _previousPosition + _hs;
            var needed = Math.Max(nominal + _delta + _n, continuation + _corrLength);
            if (!HasInput(needed))
            {
                return false;
            }

            position = BestPosition(nominal, continuation);
        }

        var window = _segment == 0 ? _firstWindow : _window;
        for (var i = 0; i < _n; i++)
        {
            var index = (int)(position + i - _inputBase);
            float l = 0, r = 0;
            if (index >= 0 && index < _inputFrames)
            {
                l = _input[index * 2];
                r = _input[(index * 2) + 1];
            }

            _accumulator[i * 2] += l * window[i];
            _accumulator[(i * 2) + 1] += r * window[i];
        }

        // The first half is final: hand it out, shift the second half down.
        var emit = _hs;
        if (_ended)
        {
            emit = (int)Math.Min(emit, Math.Max(0, ExpectedOutput - _emitted));
        }

        if (_ready.Length < emit * 2)
        {
            _ready = new float[emit * 2];
        }

        Array.Copy(_accumulator, 0, _ready, 0, emit * 2);
        _readyFrames = emit;
        _readyOffset = 0;
        _emitted += emit;
        Array.Copy(_accumulator, _hs * 2, _accumulator, 0, _hs * 2);
        Array.Clear(_accumulator, _hs * 2, _hs * 2);

        _previousPosition = position;
        _segment++;
        Discard(Math.Min((long)Math.Round(_segment * _hs * _tempo) - _delta, _previousPosition + _hs) - 1);
        return true;
    }

    /// <summary>Coarse search every 4 samples, then refine around the best match.</summary>
    private long BestPosition(long nominal, long continuation)
    {
        var lo = Math.Max(0, nominal - _delta);
        var hi = nominal + _delta;
        var best = Math.Max(0, nominal);
        var bestScore = double.NegativeInfinity;
        for (var p = lo; p <= hi; p += 4)
        {
            var score = Correlation(continuation, p, 4);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        var refined = best;
        var refinedScore = double.NegativeInfinity;
        for (var p = Math.Max(lo, best - 3); p <= Math.Min(hi, best + 3); p++)
        {
            var score = Correlation(continuation, p, 2);
            if (score > refinedScore)
            {
                refinedScore = score;
                refined = p;
            }
        }

        return refined;
    }

    private double Correlation(long reference, long candidate, int stride)
    {
        double dot = 0, energy = 0;
        var a = (int)(reference - _inputBase);
        var b = (int)(candidate - _inputBase);
        for (var i = 0; i < _corrLength; i += stride)
        {
            var ia = a + i;
            var ib = b + i;
            var va = ia >= 0 && ia < _inputFrames ? _mono[ia] : 0f;
            var vb = ib >= 0 && ib < _inputFrames ? _mono[ib] : 0f;
            dot += va * vb;
            energy += vb * vb;
        }

        return energy > 1e-12 ? dot / Math.Sqrt(energy) : 0;
    }

    private bool HasInput(long absoluteEnd) => _ended || absoluteEnd <= _inputBase + _inputFrames;

    private void Discard(long beforeAbsolute)
    {
        var drop = (int)Math.Clamp(beforeAbsolute - _inputBase, 0, _inputFrames);
        if (drop < 8192)
        {
            return;
        }

        Array.Copy(_input, drop * 2, _input, 0, (_inputFrames - drop) * 2);
        Array.Copy(_mono, drop, _mono, 0, _inputFrames - drop);
        _inputFrames -= drop;
        _inputBase += drop;
    }

    private void EnsureInput(int frames)
    {
        if (_mono.Length >= frames)
        {
            return;
        }

        var size = Math.Max(frames, _mono.Length * 2);
        Array.Resize(ref _mono, size);
        Array.Resize(ref _input, size * 2);
    }
}
