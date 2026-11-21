using MixerLive.Core.Model;

namespace MixerLive.Core.Audio.Dsp;

/// <summary>Stereo-linked noise gate with hold and a damping floor (like Voicemeeter's gate).</summary>
public sealed class Gate
{
    private double _threshold = 0.003;
    private double _floor = 0.003;
    private double _detAttack = 0.1, _detRelease = 0.001;
    private double _gainAttack = 0.01, _gainRelease = 0.0005;
    private int _holdSamples;

    private double _env;
    private double _gain = 1;
    private int _hold;

    public double CurrentGain => _gain;

    public void Configure(double fs, GateSettings s)
    {
        _threshold = DspUtil.DbToLin(s.ThresholdDb);
        _floor = DspUtil.DbToLin(Math.Min(0, s.DampingDb));
        _detAttack = DspUtil.Coef(0.5, fs);
        _detRelease = DspUtil.Coef(30, fs);
        _gainAttack = DspUtil.Coef(Math.Max(0.1, s.AttackMs), fs);
        _gainRelease = DspUtil.Coef(Math.Max(1, s.ReleaseMs), fs);
        _holdSamples = (int)(Math.Max(0, s.HoldMs) * fs / 1000.0);
    }

    public void Reset()
    {
        _env = 0;
        _gain = 1;
        _hold = 0;
    }

    public void Process(Span<float> buf, int frames)
    {
        double env = _env, gain = _gain;
        int hold = _hold;
        int idx = 0;
        for (int n = 0; n < frames; n++)
        {
            float l = buf[idx];
            float r = buf[idx + 1];
            double peak = Math.Max(Math.Abs(l), Math.Abs(r));
            env += (peak > env ? _detAttack : _detRelease) * (peak - env);

            double target;
            if (env > _threshold)
            {
                target = 1;
                hold = _holdSamples;
            }
            else if (hold > 0)
            {
                hold--;
                target = 1;
            }
            else
            {
                target = _floor;
            }

            gain += (target > gain ? _gainAttack : _gainRelease) * (target - gain);
            buf[idx++] = (float)(l * gain);
            buf[idx++] = (float)(r * gain);
        }
        _env = env;
        _gain = gain;
        _hold = hold;
    }
}
