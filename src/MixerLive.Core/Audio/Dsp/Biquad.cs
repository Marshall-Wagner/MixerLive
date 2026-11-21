namespace MixerLive.Core.Audio.Dsp;

/// <summary>Filter types; numbering follows Voicemeeter's bus EQ cell types.</summary>
public enum FilterType
{
    Peak = 0,
    LowPass = 1,
    BandPass = 2,
    HighPass = 3,
    Notch = 4,
    LowShelf = 5,
    HighShelf = 6
}

/// <summary>Single-channel RBJ biquad (transposed direct form II) operating on interleaved buffers.</summary>
public sealed class Biquad
{
    private double _b0 = 1, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public bool IsIdentity { get; private set; } = true;

    public void SetIdentity()
    {
        _b0 = 1;
        _b1 = _b2 = _a1 = _a2 = 0;
        IsIdentity = true;
    }

    public void Reset()
    {
        _z1 = _z2 = 0;
    }

    public void Configure(FilterType type, double fs, double f0, double gainDb, double q)
    {
        f0 = Math.Clamp(f0, 10, fs * 0.45);
        q = Math.Clamp(q, 0.05, 30);
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * f0 / fs;
        double cs = Math.Cos(w0);
        double sn = Math.Sin(w0);
        double alpha = sn / (2 * q);
        double b0, b1, b2, a0, a1, a2;

        switch (type)
        {
            case FilterType.LowPass:
                b0 = (1 - cs) / 2; b1 = 1 - cs; b2 = (1 - cs) / 2;
                a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha;
                break;
            case FilterType.HighPass:
                b0 = (1 + cs) / 2; b1 = -(1 + cs); b2 = (1 + cs) / 2;
                a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha;
                break;
            case FilterType.BandPass:
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                b0 = 1; b1 = -2 * cs; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cs; a2 = 1 - alpha;
                break;
            case FilterType.LowShelf:
            {
                double sq = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cs + sq);
                b1 = 2 * a * ((a - 1) - (a + 1) * cs);
                b2 = a * ((a + 1) - (a - 1) * cs - sq);
                a0 = (a + 1) + (a - 1) * cs + sq;
                a1 = -2 * ((a - 1) + (a + 1) * cs);
                a2 = (a + 1) + (a - 1) * cs - sq;
                break;
            }
            case FilterType.HighShelf:
            {
                double sq = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cs + sq);
                b1 = -2 * a * ((a - 1) + (a + 1) * cs);
                b2 = a * ((a + 1) + (a - 1) * cs - sq);
                a0 = (a + 1) - (a - 1) * cs + sq;
                a1 = 2 * ((a - 1) - (a + 1) * cs);
                a2 = (a + 1) - (a - 1) * cs - sq;
                break;
            }
            default: // Peak
                b0 = 1 + alpha * a; b1 = -2 * cs; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cs; a2 = 1 - alpha / a;
                break;
        }

        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
        IsIdentity = false;
    }

    /// <summary>Filters one channel of an interleaved buffer in place.</summary>
    public void Process(Span<float> buf, int frames, int channel, int channels)
    {
        if (IsIdentity) return;
        double z1 = _z1, z2 = _z2;
        double b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;
        for (int n = 0, idx = channel; n < frames; n++, idx += channels)
        {
            double x = buf[idx] + 1e-20; // keeps the recursion out of denormal territory
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            buf[idx] = (float)y;
        }
        _z1 = z1;
        _z2 = z2;
    }
}
