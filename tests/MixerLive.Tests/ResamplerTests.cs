using MixerLive.Core.Audio;
using MixerLive.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace MixerLive.Tests;

/// <summary>
/// Measures the two interpolators the way a resampler is specified: passband flatness, image
/// rejection when up-sampling and alias rejection when down-sampling. Levels are read with a
/// single-bin DFT under a Hann window, so numbers are in dB relative to the test tone.
/// </summary>
public class ResamplerTests
{
    private readonly ITestOutputHelper _out;

    public ResamplerTests(ITestOutputHelper output) => _out = output;

    /// <summary>Runs a sine through an AdaptiveReader in a producer/consumer simulation; returns the left channel.</summary>
    private static float[] Run(ResampleQuality quality, double ratio, double srcRate, double toneHz, int outFrames, float amp = 0.5f)
    {
        var ring = new FloatRing(2, 1 << 18);
        var reader = new AdaptiveReader(ring)
        {
            NominalRatio = ratio,
            SourceRate = (int)srcRate,
            TargetFill = 2048,
            Quality = quality
        };
        reader.Reset();

        // The producer must deliver exactly block*ratio source frames per output block on average;
        // a faster producer saturates the drift controller and shifts the output pitch by 0.2 %.
        const int block = 256;
        int maxPerBlock = (int)Math.Ceiling(block * ratio) + 1;
        var src = new float[maxPerBlock * 2];
        var dst = new float[block * 2];
        var result = new float[outFrames];
        long n = 0;
        int produced = 0;
        double carry = 0;

        while (produced < outFrames)
        {
            carry += block * ratio;
            int srcFrames = (int)carry;
            carry -= srcFrames;
            for (int i = 0; i < srcFrames; i++, n++)
            {
                float v = amp * (float)Math.Sin(2 * Math.PI * toneHz * n / srcRate);
                src[2 * i] = v;
                src[2 * i + 1] = v;
            }
            ring.Write(src, srcFrames);

            reader.Read(dst, block);
            int take = Math.Min(block, outFrames - produced);
            for (int i = 0; i < take; i++) result[produced + i] = dst[2 * i];
            produced += take;
        }
        return result;
    }

    /// <summary>Amplitude of one frequency in the last <paramref name="count"/> samples (Hann-windowed single-bin DFT).</summary>
    private static double Amplitude(float[] x, int count, double freq, double fs)
    {
        int start = x.Length - count;
        double w = 2 * Math.PI * freq / fs;
        double re = 0, im = 0, norm = 0;
        for (int i = 0; i < count; i++)
        {
            double win = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (count - 1));
            double v = x[start + i] * win;
            re += v * Math.Cos(w * i);
            im -= v * Math.Sin(w * i);
            norm += win;
        }
        return 2 * Math.Sqrt(re * re + im * im) / norm;
    }

    private static double Db(double amp, double reference) => 20 * Math.Log10(Math.Max(amp, 1e-12) / reference);

    [Theory]
    [InlineData(1000)]
    [InlineData(15000)]
    [InlineData(19000)]
    public void Best_passband_is_flat_near_unity_ratio(double toneHz)
    {
        // Slight drift (+300 ppm) so every fractional phase gets exercised.
        var y = Run(ResampleQuality.Best, 1.0003, 48000, toneHz, 60000);
        double gainDb = Db(Amplitude(y, 32768, toneHz * 1.0003, 48000), 0.5);
        _out.WriteLine($"sinc @ {toneHz} Hz: {gainDb:+0.000;-0.000} dB");
        Assert.InRange(gainDb, -0.1, 0.1);
    }

    [Fact]
    public void Best_rejects_images_when_upsampling_4x()
    {
        // 48 kHz → 192 kHz, 10 kHz tone. Images sit at 48k ± 10k and 96k ± 10k.
        const double fsOut = 192000;
        var best = Run(ResampleQuality.Best, 0.25, 48000, 10000, 80000);
        var fast = Run(ResampleQuality.Fast, 0.25, 48000, 10000, 80000);

        double toneBest = Amplitude(best, 32768, 10000, fsOut);
        double toneFast = Amplitude(fast, 32768, 10000, fsOut);
        Assert.InRange(Db(toneBest, 0.5), -0.1, 0.1);

        double worstBest = double.NegativeInfinity, worstFast = double.NegativeInfinity;
        foreach (double f in new[] { 38000.0, 58000.0, 86000.0 })
        {
            double b = Db(Amplitude(best, 32768, f, fsOut), toneBest);
            double h = Db(Amplitude(fast, 32768, f, fsOut), toneFast);
            _out.WriteLine($"image @ {f} Hz: sinc {b:0.0} dB, Hermite {h:0.0} dB");
            worstBest = Math.Max(worstBest, b);
            worstFast = Math.Max(worstFast, h);
        }
        Assert.True(worstBest < -90, $"sinc worst image {worstBest:0.0} dB");
        Assert.True(worstFast > worstBest + 20, "Hermite should be measurably worse than sinc");
    }

    [Fact]
    public void Best_suppresses_aliases_when_downsampling_4x()
    {
        // 192 kHz → 48 kHz. A 60 kHz tone lies above the new Nyquist (24 kHz) and would alias to 12 kHz.
        var best = Run(ResampleQuality.Best, 4.0, 192000, 60000, 60000);
        var fast = Run(ResampleQuality.Fast, 4.0, 192000, 60000, 60000);
        double aliasBest = Db(Amplitude(best, 32768, 12000, 48000), 0.5);
        double aliasFast = Db(Amplitude(fast, 32768, 12000, 48000), 0.5);
        _out.WriteLine($"alias of 60 kHz at 12 kHz: sinc {aliasBest:0.0} dB, Hermite {aliasFast:0.0} dB");
        Assert.True(aliasBest < -90, $"sinc alias {aliasBest:0.0} dB");
        Assert.True(aliasFast > -40, "Hermite has no anti-alias filter, so the alias should be clearly present");

        // In-band content passes untouched.
        var pass = Run(ResampleQuality.Best, 4.0, 192000, 5000, 60000);
        Assert.InRange(Db(Amplitude(pass, 32768, 5000, 48000), 0.5), -0.1, 0.1);
    }

    [Fact]
    public void Fast_passband_droops_at_high_frequencies()
    {
        // Documents the trade-off: Hermite loses level near the top of the band.
        var y = Run(ResampleQuality.Fast, 1.0003, 48000, 19000, 60000);
        double gainDb = Db(Amplitude(y, 32768, 19000 * 1.0003, 48000), 0.5);
        _out.WriteLine($"Hermite @ 19 kHz: {gainDb:+0.000;-0.000} dB");
        Assert.True(gainDb < -0.1, "expected visible droop from the cubic interpolator");
    }

    [Theory]
    [InlineData(ResampleQuality.Best, 1.0005)]
    [InlineData(ResampleQuality.Best, 0.9992)]
    [InlineData(ResampleQuality.Best, 0.25)]
    [InlineData(ResampleQuality.Best, 16.0)]
    public void Best_keeps_drift_control_stable(ResampleQuality quality, double ratio)
    {
        var ring = new FloatRing(2, 1 << 18);
        var reader = new AdaptiveReader(ring) { NominalRatio = ratio, SourceRate = 48000, TargetFill = 4096, Quality = quality };
        reader.Reset();

        var packet = new float[2 * 16384];
        var outBuf = new float[2 * 240];
        double producerPhase = 0;
        for (int t = 0; t < 8000; t++)
        {
            producerPhase += 240 * ratio * 1.0002;
            int frames = (int)producerPhase;
            producerPhase -= frames;
            ring.Write(packet, frames);
            reader.Read(outBuf, 240);
        }
        Assert.True(reader.IsPrimed);
        Assert.Equal(0, reader.Overruns);
        Assert.InRange(reader.Underruns, 0, 1);
        Assert.True(reader.UsesSinc);
    }
}
