using System.Numerics;
using System.Runtime.InteropServices;
using MixerLive.Core.Model;

namespace MixerLive.Core.Audio;

/// <summary>
/// Consumer side of a <see cref="FloatRing"/> that resamples at a slowly varying ratio and nudges
/// its read rate (max ±0.2 %) to keep the buffer fill level near a target.
///
/// A slow PI controller acts on the smoothed fill error: the integral term learns the real
/// clock drift between the two devices, while the small proportional gain keeps the loop from
/// reacting to the ±1 block jitter that is inherent in packetised audio. The result absorbs
/// clock drift between independent audio devices without audible pitch changes, and also
/// performs nominal rate conversion when a device cannot run at the engine rate.
///
/// Two interpolators are available (<see cref="Quality"/>): 4-point Hermite (cheap, ~50 dB image
/// rejection, fine for drift trimming) and 128-tap windowed sinc (band-limited, ~100 dB, with
/// anti-aliasing when down-sampling).
/// </summary>
public sealed class AdaptiveReader
{
    private const double MaxCorrection = 0.002;   // ±2000 ppm
    private const double KpMs = 4.8e-5;           // ratio per millisecond of (smoothed) fill error
    private const double KiMs = 9.6e-7;           // ratio per millisecond·second of accumulated error
    private const double Alpha = 0.01;            // fill-error smoothing per call (~0.5–1 s)

    private readonly FloatRing _ring;
    private long _pos;
    private double _phase;
    private double _errAvg;
    private double _integral;
    private double _ratio = 1;
    private bool _priming = true;
    private SincTable? _sinc;

    public AdaptiveReader(FloatRing ring)
    {
        _ring = ring;
    }

    /// <summary>Source frames consumed per output frame before drift correction (srcRate / dstRate).</summary>
    public double NominalRatio { get; set; } = 1.0;

    /// <summary>Sample rate of the data in the ring (used to time the integral term).</summary>
    public int SourceRate { get; set; } = 48000;

    /// <summary>Desired number of buffered source frames, measured just before a read.</summary>
    public int TargetFill { get; set; } = 960;

    /// <summary>Interpolator; takes effect at the next <see cref="Reset"/>.</summary>
    public ResampleQuality Quality { get; set; } = ResampleQuality.Best;

    public int Underruns { get; private set; }
    public int Overruns { get; private set; }
    public bool IsPrimed => !_priming;
    public int Fill => (int)Math.Max(0, _ring.WritePos - _pos);
    public double CorrectionPpm => NominalRatio <= 0 ? 0 : (_ratio / NominalRatio - 1.0) * 1e6;
    public bool UsesSinc => _sinc != null;
    public string Description => _sinc != null ? $"sinc {_sinc.Taps}-tap" : "Hermite";

    /// <summary>Source frames beyond the read position that one output block may touch.</summary>
    private int LookAhead => _sinc != null ? _sinc.After + 1 : 3;

    public void Reset()
    {
        long w = _ring.WritePos;
        _pos = Math.Max(0, w - TargetFill);
        _phase = 0;
        _errAvg = 0;
        _integral = 0;
        _ratio = NominalRatio;
        _priming = true;
        _sinc = Quality == ResampleQuality.Best && _ring.Channels == 2 ? SincTable.Get(NominalRatio) : null;
    }

    /// <summary>
    /// Produces <paramref name="frames"/> interleaved output frames. Returns false (and writes
    /// silence) while the buffer is (re)filling after start-up or an underrun.
    /// </summary>
    public bool Read(Span<float> dst, int frames)
    {
        int ch = _ring.Channels;
        var outSpan = dst.Slice(0, frames * ch);

        long w = _ring.WritePos;
        long avail = w - _pos;

        if (avail < 0)
        {
            // Positions inconsistent (should not happen): start over at the live edge.
            _pos = w;
            avail = 0;
            _priming = true;
        }
        else if (avail > _ring.CapacityFrames - 8)
        {
            // Producer lapped us: jump to the newest data.
            Overruns++;
            _pos = Math.Max(0, w - TargetFill);
            _phase = 0;
            _errAvg = 0;
            _priming = true;
            avail = w - _pos;
        }

        long need = (long)Math.Ceiling(frames * _ratio + _phase) + LookAhead;

        // The fill target must leave room for one block plus the interpolator's look-ahead,
        // otherwise every read would starve (large down-sampling ratios with the sinc kernel).
        if (TargetFill < need + need / 4) TargetFill = (int)(need + need / 4);

        if (_priming)
        {
            if (avail < TargetFill)
            {
                outSpan.Clear();
                return false;
            }
            if (avail > TargetFill * 2L)
            {
                // Too much latency accumulated while waiting; start closer to the live edge.
                _pos = Math.Max(0, w - TargetFill);
                _phase = 0;
                avail = w - _pos;
            }
            _priming = false;
            _errAvg = 0;
        }

        if (avail < need)
        {
            Underruns++;
            _priming = true;
            outSpan.Clear();
            return false;
        }

        if (avail > TargetFill * 4L + need)
        {
            // Consumer stalled for a long time; drop the backlog instead of playing it late.
            Overruns++;
            _pos = Math.Max(0, w - TargetFill);
            _phase = 0;
            _errAvg = 0;
            avail = w - _pos;
        }

        // PI control on the smoothed fill error, measured in milliseconds so the loop behaves the
        // same at 48 kHz and at 768 kHz.
        double err = avail - TargetFill;
        _errAvg += Alpha * (err - _errAvg);
        double rate = Math.Max(1, SourceRate);
        double errMs = _errAvg * 1000.0 / rate;
        double dt = frames * NominalRatio / rate;
        _integral = Math.Clamp(_integral + errMs * dt, -MaxCorrection / KiMs, MaxCorrection / KiMs);
        double corr = Math.Clamp(KpMs * errMs + KiMs * _integral, -MaxCorrection, MaxCorrection);
        _ratio = NominalRatio * (1.0 + corr);

        long pos = _pos;
        double phase = _phase;
        double ratio = _ratio;

        if (_sinc != null)
            ReadSinc(outSpan, frames, ref pos, ref phase, ratio);
        else if (ch == 2)
            ReadHermiteStereo(outSpan, frames, ref pos, ref phase, ratio);
        else
            ReadHermite(outSpan, frames, ref pos, ref phase, ratio, ch);

        _pos = pos;
        _phase = phase;
        _ring.SetReadPos(pos);
        return true;
    }

    private void ReadHermiteStereo(Span<float> outSpan, int frames, ref long pos, ref double phase, double ratio)
    {
        int o = 0;
        for (int n = 0; n < frames; n++)
        {
            float t = (float)phase;
            outSpan[o++] = Interp(pos, 0, t);
            outSpan[o++] = Interp(pos, 1, t);
            phase += ratio;
            int adv = (int)phase;
            pos += adv;
            phase -= adv;
        }
    }

    private void ReadHermite(Span<float> outSpan, int frames, ref long pos, ref double phase, double ratio, int ch)
    {
        int o = 0;
        for (int n = 0; n < frames; n++)
        {
            float t = (float)phase;
            for (int c = 0; c < ch; c++) outSpan[o++] = Interp(pos, c, t);
            phase += ratio;
            int adv = (int)phase;
            pos += adv;
            phase -= adv;
        }
    }

    /// <summary>
    /// Band-limited interpolation: each output sample is the dot product of a window of source
    /// frames with a coefficient row picked (and linearly blended) by the fractional phase.
    /// Coefficients are duplicated per channel so one vector handles several stereo frames.
    /// </summary>
    private void ReadSinc(Span<float> outSpan, int frames, ref long pos, ref double phase, double ratio)
    {
        var table = _sinc!;
        int before = table.Before;
        int taps = table.Taps;
        int vc = Vector<float>.Count;
        int o = 0;

        for (int n = 0; n < frames; n++)
        {
            double tp = phase * SincTable.Phases;
            int p = (int)tp;
            float f = (float)(tp - p);
            var r0 = MemoryMarshal.Cast<float, Vector<float>>(table.Row(p));
            var r1 = MemoryMarshal.Cast<float, Vector<float>>(table.Row(p + 1));
            var win = MemoryMarshal.Cast<float, Vector<float>>(_ring.Window(pos - before, taps));

            var vf = new Vector<float>(f);
            var vg = new Vector<float>(1f - f);
            var acc = Vector<float>.Zero;
            int count = Math.Min(r0.Length, win.Length);
            for (int i = 0; i < count; i++)
                acc += (r0[i] * vg + r1[i] * vf) * win[i];

            float l = 0, r = 0;
            for (int i = 0; i < vc; i += 2)
            {
                l += acc[i];
                r += acc[i + 1];
            }
            outSpan[o++] = l;
            outSpan[o++] = r;

            phase += ratio;
            int adv = (int)phase;
            pos += adv;
            phase -= adv;
        }
    }

    private float Interp(long pos, int ch, float t)
    {
        float ym1 = _ring.Sample(pos - 1, ch);
        float y0 = _ring.Sample(pos, ch);
        float y1 = _ring.Sample(pos + 1, ch);
        float y2 = _ring.Sample(pos + 2, ch);
        float c1 = 0.5f * (y1 - ym1);
        float c2 = ym1 - 2.5f * y0 + 2f * y1 - 0.5f * y2;
        float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
        return ((c3 * t + c2) * t + c1) * t + y0;
    }
}
