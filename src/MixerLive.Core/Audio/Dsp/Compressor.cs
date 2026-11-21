using MixerLive.Core.Model;

namespace MixerLive.Core.Audio.Dsp;

/// <summary>Feed-forward peak compressor with soft knee and optional automatic make-up gain.</summary>
public sealed class Compressor
{
    private double _thresholdDb = -40;
    private double _ratio = 1;
    private double _kneeDb = 6;
    private double _attack = 0.01, _release = 0.001;
    private double _makeupDb;
    private double _gainIn = 1;

    private double _grDb; // smoothed gain reduction (<= 0)

    public double CurrentGainReductionDb => _grDb;

    public void Configure(double fs, CompSettings s)
    {
        _thresholdDb = s.ThresholdDb;
        _ratio = Math.Max(1, s.Ratio);
        _kneeDb = Math.Clamp(s.Knee, 0, 1) * 12;
        _attack = DspUtil.Coef(Math.Max(0.1, s.AttackMs), fs);
        _release = DspUtil.Coef(Math.Max(1, s.ReleaseMs), fs);
        _gainIn = DspUtil.DbToLin(s.GainInDb);
        _makeupDb = (s.AutoMakeup ? -_thresholdDb * (1 - 1 / _ratio) * 0.5 : 0) + s.GainOutDb;
    }

    public void Reset()
    {
        _grDb = 0;
    }

    public void Process(Span<float> buf, int frames)
    {
        double grDb = _grDb;
        double halfKnee = _kneeDb / 2;
        int idx = 0;
        for (int n = 0; n < frames; n++)
        {
            float l = (float)(buf[idx] * _gainIn);
            float r = (float)(buf[idx + 1] * _gainIn);
            double x = Math.Max(Math.Abs(l), Math.Abs(r));
            double xDb = x > 1e-6 ? 20 * Math.Log10(x) : -120;

            double over = xDb - _thresholdDb;
            double yDb;
            if (_kneeDb > 0 && Math.Abs(over) <= halfKnee)
            {
                double t = over + halfKnee;
                yDb = xDb + (1 / _ratio - 1) * t * t / (2 * _kneeDb);
            }
            else if (over > 0)
            {
                yDb = _thresholdDb + over / _ratio;
            }
            else
            {
                yDb = xDb;
            }

            double gr = yDb - xDb;
            grDb += (gr < grDb ? _attack : _release) * (gr - grDb);

            float g = (float)DspUtil.DbToLin(grDb + _makeupDb);
            buf[idx++] = l * g;
            buf[idx++] = r * g;
        }
        _grDb = grDb;
    }
}
