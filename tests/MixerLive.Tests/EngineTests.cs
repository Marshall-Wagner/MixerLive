using MixerLive.Core.Audio;
using MixerLive.Core.Audio.Dsp;
using Xunit;

namespace MixerLive.Tests;

public class RingAndReaderTests
{
    [Fact]
    public void Reader_primes_then_reproduces_signal_at_unity_ratio()
    {
        var ring = new FloatRing(2, 8192);
        var reader = new AdaptiveReader(ring) { NominalRatio = 1.0, TargetFill = 480 };
        reader.Reset();

        // Nothing written yet: priming → silence.
        var outBuf = new float[240 * 2];
        Assert.False(reader.Read(outBuf, 240));

        // Feed a ramp in uneven packets.
        var src = new float[2048 * 2];
        for (int n = 0; n < 2048; n++)
        {
            src[2 * n] = n * 0.001f;
            src[2 * n + 1] = -n * 0.001f;
        }
        int pos = 0;
        foreach (int packet in new[] { 300, 180, 480, 60, 500, 528 })
        {
            ring.Write(src.AsSpan(pos * 2, packet * 2), packet);
            pos += packet;
        }

        Assert.True(reader.Read(outBuf, 240));
        Assert.True(reader.IsPrimed);

        // The ramp is linear, so Hermite interpolation reproduces it exactly: consecutive
        // samples differ by 0.001 and L == -R.
        for (int n = 1; n < 240; n++)
        {
            Assert.InRange(outBuf[2 * n] - outBuf[2 * (n - 1)], 0.0009, 0.0011);
            Assert.InRange(outBuf[2 * n] + outBuf[2 * n + 1], -1e-5, 1e-5);
        }
        Assert.Equal(0, reader.Underruns);
    }

    [Theory]
    [InlineData(1.0005)]   // producer +500 ppm fast
    [InlineData(0.9992)]   // producer 800 ppm slow
    public void Reader_tracks_clock_drift_without_resyncs(double drift)
    {
        var ring = new FloatRing(2, 1 << 15);
        var reader = new AdaptiveReader(ring) { NominalRatio = 1.0, SourceRate = 48000, TargetFill = 960 };
        reader.Reset();

        var packet = new float[2 * 1024];
        var outBuf = new float[2 * 240];
        double producerPhase = 0;
        int maxFill = 0;

        // Simulate 4 minutes: producer writes 480*drift frames per 10 ms, consumer reads 240 per 5 ms.
        int ticks = 240 * 200;
        for (int t = 0; t < ticks; t++)
        {
            if (t % 2 == 0)
            {
                producerPhase += 480 * drift;
                int frames = (int)producerPhase;
                producerPhase -= frames;
                ring.Write(packet, frames);
            }
            reader.Read(outBuf, 240);
            if (t > 400) maxFill = Math.Max(maxFill, reader.Fill);
        }

        Assert.True(reader.IsPrimed);
        Assert.Equal(0, reader.Overruns);
        Assert.InRange(reader.Underruns, 0, 1);
        // Never drifted far enough to need a hard resync (4× target), and settled back near target.
        Assert.True(maxFill < 960 * 3, $"max fill {maxFill}");
        Assert.InRange(reader.Fill, 960 - 480, 960 + 720);
        // Learned correction ≈ the real drift.
        double expectedPpm = (drift - 1) * 1e6;
        Assert.InRange(reader.CorrectionPpm, expectedPpm - 120, expectedPpm + 120);
    }

    [Fact]
    public void Reader_recovers_after_underrun()
    {
        var ring = new FloatRing(2, 8192);
        var reader = new AdaptiveReader(ring) { NominalRatio = 1.0, TargetFill = 480 };
        reader.Reset();
        var packet = new float[2 * 480];
        var outBuf = new float[2 * 240];

        ring.Write(packet, 480);
        ring.Write(packet, 480);
        Assert.True(reader.Read(outBuf, 240));
        Assert.True(reader.Read(outBuf, 240));
        Assert.True(reader.Read(outBuf, 240));
        Assert.False(reader.Read(outBuf, 240)); // starved
        Assert.Equal(1, reader.Underruns);

        ring.Write(packet, 480);
        ring.Write(packet, 480);
        Assert.True(reader.Read(outBuf, 240));
        Assert.True(reader.IsPrimed);
    }
}

public class DspTests
{
    private static float[] Sine(double freq, double fs, int frames, float amp = 0.5f)
    {
        var buf = new float[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            float v = amp * (float)Math.Sin(2 * Math.PI * freq * n / fs);
            buf[2 * n] = v;
            buf[2 * n + 1] = v;
        }
        return buf;
    }

    private static float Peak(float[] buf, int fromFrame)
    {
        float p = 0;
        for (int i = fromFrame * 2; i < buf.Length; i++) p = Math.Max(p, Math.Abs(buf[i]));
        return p;
    }

    [Fact]
    public void Lowpass_attenuates_high_frequencies_and_passes_low()
    {
        const double fs = 48000;
        var lp = new Biquad();
        lp.Configure(FilterType.LowPass, fs, 1000, 0, 0.707);

        var low = Sine(100, fs, 4800);
        var high = Sine(10000, fs, 4800);
        lp.Process(low, 4800, 0, 2);
        lp.Reset();
        lp.Process(high, 4800, 0, 2);

        Assert.InRange(Peak(LeftOnly(low), 2400), 0.45, 0.52);
        Assert.True(Peak(LeftOnly(high), 2400) < 0.01);
    }

    [Fact]
    public void Peak_filter_at_zero_gain_is_transparent_and_boost_boosts()
    {
        const double fs = 48000;
        var flat = new Biquad();
        flat.Configure(FilterType.Peak, fs, 1000, 0, 1);
        var sig = Sine(1000, fs, 4800);
        flat.Process(sig, 4800, 0, 2);
        Assert.InRange(Peak(LeftOnly(sig), 2400), 0.49, 0.51);

        var boost = new Biquad();
        boost.Configure(FilterType.Peak, fs, 1000, 6, 1);
        var sig2 = Sine(1000, fs, 4800);
        boost.Process(sig2, 4800, 0, 2);
        Assert.InRange(Peak(LeftOnly(sig2), 2400), 0.95, 1.05); // +6 dB ≈ ×2
    }

    [Fact]
    public void Gain_ramp_is_smooth_and_mute_is_silent()
    {
        var buf = new float[2 * 240];
        Array.Fill(buf, 1f);
        DspUtil.ApplyGainRamp(buf, 240, 1f, 0f);
        Assert.True(buf[0] < 1f && buf[0] > 0.99f);
        Assert.InRange(buf[2 * 239], -1e-6, 1e-6);

        var buf2 = new float[2 * 240];
        Array.Fill(buf2, 1f);
        DspUtil.ApplyGainRamp(buf2, 240, 0f, 0f);
        Assert.All(buf2, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Gate_closes_on_silence_and_opens_on_signal()
    {
        const double fs = 48000;
        var gate = new Gate();
        gate.Configure(fs, new Core.Model.GateSettings { ThresholdDb = -30, DampingDb = -60, AttackMs = 1, HoldMs = 10, ReleaseMs = 20 });

        var quiet = Sine(1000, fs, 9600, 0.001f); // -60 dBFS
        gate.Process(quiet, 9600);
        Assert.True(Peak(quiet, 4800) < 0.0001);

        var loud = Sine(1000, fs, 9600, 0.5f);
        gate.Process(loud, 9600);
        Assert.InRange(Peak(loud, 4800), 0.45, 0.52);
    }

    private static float[] LeftOnly(float[] stereo)
    {
        var l = new float[stereo.Length];
        for (int i = 0; i < stereo.Length; i += 2) l[i] = stereo[i];
        return l;
    }
}
