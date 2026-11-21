namespace MixerLive.Core.Audio.Dsp;

public static class DspUtil
{
    /// <summary>Fader law: -60 dB and below is treated as silence.</summary>
    public static float FaderToLin(double db) => db <= -60 ? 0f : (float)Math.Pow(10, db / 20);

    public static double DbToLin(double db) => Math.Pow(10, db / 20);

    public static double LinToDb(double lin) => lin <= 1e-6 ? -120 : 20 * Math.Log10(lin);

    /// <summary>One-pole smoothing coefficient for a time constant in milliseconds.</summary>
    public static double Coef(double ms, double sampleRate)
    {
        if (ms <= 0) return 1;
        return 1 - Math.Exp(-1.0 / (ms * 0.001 * sampleRate));
    }

    /// <summary>Applies a linear gain ramp across the block (stereo interleaved).</summary>
    public static void ApplyGainRamp(Span<float> buf, int frames, float from, float to)
    {
        if (from == to)
        {
            if (to == 1f) return;
            if (to == 0f)
            {
                buf.Slice(0, frames * 2).Clear();
                return;
            }
            for (int i = 0; i < frames * 2; i++) buf[i] *= to;
            return;
        }

        float step = (to - from) / frames;
        float g = from;
        int idx = 0;
        for (int n = 0; n < frames; n++)
        {
            g += step;
            buf[idx++] *= g;
            buf[idx++] *= g;
        }
    }

    public static void ToMono(Span<float> buf, int frames)
    {
        int idx = 0;
        for (int n = 0; n < frames; n++)
        {
            float m = 0.5f * (buf[idx] + buf[idx + 1]);
            buf[idx++] = m;
            buf[idx++] = m;
        }
    }

    public static void Peaks(ReadOnlySpan<float> buf, int frames, out float peakL, out float peakR)
    {
        float pl = 0, pr = 0;
        int idx = 0;
        for (int n = 0; n < frames; n++)
        {
            float l = Math.Abs(buf[idx++]);
            float r = Math.Abs(buf[idx++]);
            if (l > pl) pl = l;
            if (r > pr) pr = r;
        }
        peakL = pl;
        peakR = pr;
    }
}
