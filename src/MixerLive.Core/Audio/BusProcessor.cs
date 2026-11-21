using MixerLive.Core.Audio.Dsp;
using MixerLive.Core.Model;

namespace MixerLive.Core.Audio;

/// <summary>Per-bus processing: parametric EQ (up to 32 cells per channel) + preamp → mono → fader/mute → meter.</summary>
public sealed class BusProcessor
{
    private readonly ParametricEq _eq;
    private float _lastGain;

    public float PeakL { get; private set; }
    public float PeakR { get; private set; }

    public BusProcessor(double sampleRate)
    {
        _eq = new ParametricEq(sampleRate);
    }

    public void Process(Span<float> buf, int frames, BusSettings b)
    {
        _eq.Process(buf, frames, b.EqOn, b.EqLeft, b.EqRight);

        if (b.Mono) DspUtil.ToMono(buf, frames);

        float preamp = b.EqOn ? (float)DspUtil.DbToLin(b.EqPreampDb) : 1f;
        float target = b.Mute ? 0f : DspUtil.FaderToLin(b.GainDb) * preamp;
        DspUtil.ApplyGainRamp(buf, frames, _lastGain, target);
        _lastGain = target;

        DspUtil.Peaks(buf, frames, out float pl, out float pr);
        PeakL = pl;
        PeakR = pr;
    }
}
