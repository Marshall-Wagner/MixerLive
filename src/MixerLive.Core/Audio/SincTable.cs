using System.Collections.Concurrent;

namespace MixerLive.Core.Audio;

/// <summary>
/// Coefficient table for band-limited (windowed-sinc) interpolation at an arbitrary, slowly
/// varying ratio. One row per fractional phase; rows are interpolated linearly at run time.
///
/// Design: Kaiser window (β = 10, ≈ 100 dB stop-band), 64 zero crossings per side at unity
/// ratio (128 taps). For down-sampling the kernel is stretched so the cut-off sits at 95 % of the
/// destination Nyquist frequency and the stop-band begins at Nyquist itself, which is what makes
/// the filter anti-aliasing as well as anti-imaging. Coefficients are stored duplicated
/// (c0,c0,c1,c1,…) so one SIMD vector multiplies several interleaved stereo frames at once.
/// </summary>
internal sealed class SincTable
{
    public const int Phases = 128;
    public const int ZeroCrossings = 64;
    public const double KaiserBeta = 10.0;

    private static readonly ConcurrentDictionary<long, SincTable> Cache = new();
    private static readonly double I0Beta = BesselI0(KaiserBeta);

    private readonly float[] _rows;

    /// <summary>Source frames per output sample (even, multiple of 16 so SIMD loops need no tail).</summary>
    public int Taps { get; }

    /// <summary>Source frames before the integer read position that belong to the window.</summary>
    public int Before { get; }

    /// <summary>Source frames at and after the integer read position (incl. look-ahead) the window needs.</summary>
    public int After => Taps - Before;

    public double Ratio { get; }
    public int RowLength => Taps * 2;

    public ReadOnlySpan<float> Row(int phase) => _rows.AsSpan(phase * RowLength, RowLength);

    /// <summary>Tables are expensive to build and identical for identical ratios, so share them.</summary>
    public static SincTable Get(double ratio)
    {
        long key = (long)Math.Round(Math.Max(1e-3, ratio) * 1e6);
        return Cache.GetOrAdd(key, _ => new SincTable(ratio));
    }

    private SincTable(double ratio)
    {
        Ratio = ratio;
        double stretch = Math.Max(1.0, ratio);                       // > 1 when down-sampling
        double cutoff = 0.475 / stretch;                              // cycles per source sample
        double half = Math.Min(ZeroCrossings * stretch, FloatRing.GuardFrames / 2.0 - 16);
        int taps = (int)Math.Ceiling(half) * 2;
        taps = (taps + 15) / 16 * 16;
        Taps = taps;
        Before = taps / 2 - 1;

        _rows = new float[(Phases + 1) * RowLength];
        var tmp = new double[taps];
        for (int p = 0; p <= Phases; p++)
        {
            double t = p / (double)Phases;
            double sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double x = (j - Before) - t;                          // distance from the output point
                double u = x / half;
                double v = Math.Abs(u) < 1
                    ? 2 * cutoff * Sinc(2 * cutoff * x) * BesselI0(KaiserBeta * Math.Sqrt(1 - u * u)) / I0Beta
                    : 0;
                tmp[j] = v;
                sum += v;
            }
            var row = _rows.AsSpan(p * RowLength, RowLength);
            for (int j = 0; j < taps; j++)
            {
                float c = (float)(tmp[j] / sum);                      // unity DC gain for every phase
                row[2 * j] = c;
                row[2 * j + 1] = c;
            }
        }
    }

    private static double Sinc(double y) => y == 0 ? 1 : Math.Sin(Math.PI * y) / (Math.PI * y);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 64; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < sum * 1e-14) break;
        }
        return sum;
    }
}
