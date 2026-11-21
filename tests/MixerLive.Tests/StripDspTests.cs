using MixerLive.Core.Audio;
using MixerLive.Core.Audio.Dsp;
using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class StripDspTests
{
    private const int Fs = 48000;

    private static float[] Tone(double hz, double amp, int frames, int fs = Fs)
    {
        var buf = new float[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            float v = (float)(amp * Math.Sin(2 * Math.PI * hz * n / fs));
            buf[2 * n] = v;
            buf[2 * n + 1] = v;
        }
        return buf;
    }

    private static double PeakDb(ReadOnlySpan<float> buf, int fromFrame, int toFrame)
    {
        float p = 0;
        for (int n = fromFrame; n < toFrame; n++) p = Math.Max(p, Math.Max(Math.Abs(buf[2 * n]), Math.Abs(buf[2 * n + 1])));
        return DspUtil.LinToDb(p);
    }

    [Fact]
    public void Strip_parametric_eq_cuts_a_high_tone_and_leaves_a_low_one()
    {
        var s = new StripSettings { Kind = ChannelKind.Hardware, EqOn = true };
        s.EnsureEqCells();
        s.EqLeft[0].Type = (int)FilterType.HighShelf;
        s.EqLeft[0].Freq = 3000;
        s.EqLeft[0].GainDb = -12;
        s.EqLeft[0].Q = 0.7;
        s.EqRight = s.EqLeft.Select(c => c.Clone()).ToList();

        var dsp = new StripProcessor(Fs);
        int frames = Fs / 2;

        var high = Tone(10000, 0.5, frames);
        dsp.Process(high, frames, s, false);
        Assert.InRange(PeakDb(high, frames / 2, frames), -6 - 12 - 1.5, -6 - 12 + 1.5);   // -18 dB ± 1.5

        var low = Tone(200, 0.5, frames);
        dsp.Process(low, frames, s, false);
        Assert.InRange(PeakDb(low, frames / 2, frames), -6 - 0.5, -6 + 0.5);
    }

    [Fact]
    public void Mono_strip_uses_the_left_cells_for_both_channels()
    {
        var s = new StripSettings { Kind = ChannelKind.Hardware, EqOn = true, Mono = true };
        s.EnsureEqCells();
        s.EqLeft[0].Type = (int)FilterType.HighShelf;
        s.EqLeft[0].Freq = 3000;
        s.EqLeft[0].GainDb = -12;
        s.EqLeft[0].Q = 0.7;
        // Right cells stay flat: with MONO on they must not matter.

        var dsp = new StripProcessor(Fs);
        int frames = Fs / 2;
        var buf = Tone(10000, 0.5, frames);
        dsp.Process(buf, frames, s, false);
        double l = 0, r = 0;
        for (int n = frames / 2; n < frames; n++)
        {
            l = Math.Max(l, Math.Abs(buf[2 * n]));
            r = Math.Max(r, Math.Abs(buf[2 * n + 1]));
        }
        Assert.InRange(DspUtil.LinToDb(l), -19.5, -16.5);
        Assert.Equal(l, r, 6);
    }

    [Fact]
    public void Eq_preamp_scales_the_strip_before_the_meter()
    {
        var s = new StripSettings { Kind = ChannelKind.Hardware, EqOn = true, EqPreampDb = -6 };
        s.EnsureEqCells();
        var dsp = new StripProcessor(Fs);
        int frames = Fs / 4;
        var buf = Tone(1000, 0.5, frames);
        dsp.Process(buf, frames, s, false);           // ramps from 0 dB to -6 dB within this block
        buf = Tone(1000, 0.5, frames);
        dsp.Process(buf, frames, s, false);
        Assert.InRange(DspUtil.LinToDb(dsp.PeakL), -12.3, -11.7);
    }

    [Fact]
    public void Key_click_suppressor_ducks_a_click_but_not_steady_speech_like_tone()
    {
        var settings = new KeysSettings { Enabled = true };
        var keys = new KeyClickSuppressor();
        keys.Configure(Fs, settings);

        int frames = Fs;                            // 1 s
        var buf = Tone(300, 0.1, frames);           // "voice": -20 dBFS low tone throughout
        // A keystroke: 8 ms burst of broadband noise at -6 dBFS, 500 ms in.
        var rnd = new Random(1);
        int clickStart = Fs / 2, clickLen = Fs * 8 / 1000;
        for (int n = clickStart; n < clickStart + clickLen; n++)
        {
            float v = (float)(0.5 * (rnd.NextDouble() * 2 - 1));
            buf[2 * n] += v;
            buf[2 * n + 1] += v;
        }

        keys.Process(buf, frames);

        int look = keys.LookaheadFrames;
        // Before the click: untouched (within the lookahead delay the tone is the same tone).
        Assert.InRange(PeakDb(buf, Fs / 10, clickStart - Fs / 100), -20.5, -19.5);
        // The click itself (now sitting 'look' frames later) is held down by at least 15 dB.
        Assert.True(PeakDb(buf, clickStart + look, clickStart + look + clickLen) < -6 - 15,
            $"click peak {PeakDb(buf, clickStart + look, clickStart + look + clickLen):0.0} dB");
        // 200 ms later the tone is back to normal.
        Assert.InRange(PeakDb(buf, clickStart + Fs / 5, frames - look), -20.5, -19.5);
    }

    [Fact]
    public void Key_click_suppressor_recovers_during_a_sustained_high_tone()
    {
        var keys = new KeyClickSuppressor();
        keys.Configure(Fs, new KeysSettings { Enabled = true });
        int frames = Fs;
        var buf = Tone(6000, 0.3, frames);          // a long "sss": starts abruptly, then steady
        keys.Process(buf, frames);
        // Whatever happened at the onset, after 300 ms the suppressor must have let go.
        Assert.InRange(PeakDb(buf, Fs * 3 / 10, frames - keys.LookaheadFrames), -10.5 - 0.3, -10.5 + 0.3);
    }

    [Fact]
    public void Strip_eq_cells_are_normalized_and_exported()
    {
        var p = MixerProfile.CreateDefault();
        p.Strips[0].EqOn = true;
        p.Strips[0].EqLeft = new List<EqBand> { new() { Freq = 120, Type = (int)FilterType.HighPass, Q = 0.7 } };
        p.Strips[0].Keys.Enabled = true;
        p.Normalize();
        Assert.Equal(MixerProfile.EqCellCount, p.Strips[0].EqLeft.Count);
        Assert.Equal(MixerProfile.EqCellCount, p.Strips[0].EqRight.Count);

        var copy = ProfileStore.Clone(p);
        Assert.True(copy.Strips[0].EqOn);
        Assert.True(copy.Strips[0].Keys.Enabled);
        Assert.Equal(120, copy.Strips[0].EqLeft[0].Freq);
        Assert.Equal(-24, copy.Strips[0].Keys.DepthDb);
    }
}
