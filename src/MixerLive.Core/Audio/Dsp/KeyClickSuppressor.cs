using MixerLive.Core.Model;

namespace MixerLive.Core.Audio.Dsp;

/// <summary>
/// Keyboard-click suppressor for a microphone strip ("KEYS"). A keystroke is a burst of
/// high-frequency energy that rises within a fraction of a millisecond, much faster than speech
/// (even an "s" or a "t" takes several milliseconds to build up). The detector high-passes the
/// signal at 2.5 kHz and compares a fast envelope with a slower one; when the fast one jumps far
/// above the slow one, the signal is attenuated for a short hold time. A few milliseconds of
/// look-ahead delay make sure the attenuation is already in place when the click arrives.
/// This is a transient suppressor, not a learned noise model: a click during speech dips the voice
/// for a few hundredths of a second instead of letting the click through.
/// </summary>
public sealed class KeyClickSuppressor
{
    private const double DetectorHz = 2500;
    private const double FloorDb = -50;     // HF level below which nothing counts as a click

    // Detection high-pass (RBJ 2nd order), transposed direct form II.
    private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;
    private double _fastAtk, _fastRel, _slowAtk, _slowRel, _gainAtk, _gainRel;
    private double _ratio = 3.16, _depth = 0.063, _floor = 0.003;
    private int _holdSamples, _lookahead;
    private float[] _delay = new float[2];

    private double _fast, _slow, _gain = 1;
    private int _hold, _di;

    /// <summary>1 = passing, smaller while a click is suppressed (for a UI indicator).</summary>
    public double CurrentGain => _gain;

    /// <summary>Samples of look-ahead delay the suppressor adds to the strip.</summary>
    public int LookaheadFrames => _lookahead;

    public void Configure(double fs, KeysSettings s)
    {
        double w0 = 2 * Math.PI * DetectorHz / fs;
        double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.7071);
        double a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;

        _fastAtk = DspUtil.Coef(0.15, fs);
        _fastRel = DspUtil.Coef(3, fs);
        _slowAtk = DspUtil.Coef(6, fs);
        _slowRel = DspUtil.Coef(150, fs);
        _gainAtk = DspUtil.Coef(0.3, fs);
        _gainRel = DspUtil.Coef(Math.Max(1, s.ReleaseMs), fs);

        _ratio = DspUtil.DbToLin(Math.Max(0, s.SensitivityDb));
        _depth = DspUtil.DbToLin(Math.Min(0, s.DepthDb));
        _floor = DspUtil.DbToLin(FloorDb);
        _holdSamples = (int)(Math.Max(0, s.HoldMs) * fs / 1000.0);

        int lookahead = (int)(Math.Clamp(s.LookaheadMs, 0, 20) * fs / 1000.0);
        if (lookahead != _lookahead || _delay.Length != Math.Max(2, lookahead * 2))
        {
            _lookahead = lookahead;
            _delay = new float[Math.Max(2, lookahead * 2)];
            _di = 0;
        }
    }

    public void Reset()
    {
        _z1 = _z2 = 0;
        _fast = _slow = 0;
        _gain = 1;
        _hold = 0;
        Array.Clear(_delay);
        _di = 0;
    }

    public void Process(Span<float> buf, int frames)
    {
        double fast = _fast, slow = _slow, gain = _gain, z1 = _z1, z2 = _z2;
        int hold = _hold, di = _di;
        var delay = _delay;
        bool delayed = _lookahead > 0;
        int idx = 0;

        for (int n = 0; n < frames; n++)
        {
            float l = buf[idx], r = buf[idx + 1];

            // Detector: high-passed mono, rectified, two envelopes.
            double x = 0.5 * (l + r);
            double y = _b0 * x + z1;
            z1 = _b1 * x - _a1 * y + z2;
            z2 = _b2 * x - _a2 * y;
            double a = Math.Abs(y);
            fast += (a > fast ? _fastAtk : _fastRel) * (a - fast);
            slow += (fast > slow ? _slowAtk : _slowRel) * (fast - slow);

            if (fast > _floor && fast > slow * _ratio) hold = _holdSamples;
            double target = 1;
            if (hold > 0)
            {
                hold--;
                target = _depth;
            }
            gain += (target < gain ? _gainAtk : _gainRel) * (target - gain);

            if (delayed)
            {
                float dl = delay[di], dr = delay[di + 1];
                delay[di] = l;
                delay[di + 1] = r;
                di += 2;
                if (di >= delay.Length) di = 0;
                l = dl;
                r = dr;
            }
            buf[idx++] = (float)(l * gain);
            buf[idx++] = (float)(r * gain);
        }

        _fast = fast;
        _slow = slow;
        _gain = gain;
        _z1 = z1;
        _z2 = z2;
        _hold = hold;
        _di = di;
    }
}
