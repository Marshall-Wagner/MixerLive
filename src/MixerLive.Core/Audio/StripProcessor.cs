using MixerLive.Core.Audio.Dsp;
using MixerLive.Core.Model;

namespace MixerLive.Core.Audio;

/// <summary>
/// Per-strip processing: keyboard-click suppressor → gate → compressor → 3-band EQ → mono →
/// parametric EQ + preamp → (meter) → fader/mute/solo.
/// Parameter changes are picked up at block boundaries by comparing against cached values.
/// </summary>
public sealed class StripProcessor
{
    private const double LowShelfHz = 200;
    private const double MidPeakHz = 1000;
    private const double HighShelfHz = 5000;

    private readonly double _fs;
    private readonly Biquad _lowL = new(), _lowR = new(), _midL = new(), _midR = new(), _highL = new(), _highR = new();
    private readonly Gate _gate = new();
    private readonly Compressor _comp = new();
    private readonly KeyClickSuppressor _keys = new();
    private readonly ParametricEq _peq;

    private double _eqLow = double.NaN, _eqMid = double.NaN, _eqHigh = double.NaN;
    private bool _eqActive;

    private double _gThr = double.NaN, _gDamp, _gAtk, _gHold, _gRel;
    private bool _gateWasOn;

    private double _cRatio = double.NaN, _cThr, _cAtk, _cRel, _cKnee, _cIn, _cOut;
    private bool _cAuto, _compWasOn;

    private double _kDepth = double.NaN, _kSens, _kHold, _kRel, _kLook;
    private bool _keysWasOn;

    private float _lastGain, _lastPreamp = 1f;

    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public double GateGainDb => DspUtil.LinToDb(_gate.CurrentGain);
    public double CompReductionDb => _comp.CurrentGainReductionDb;

    /// <summary>1 while the click suppressor passes audio, smaller while it is holding a click down.</summary>
    public double KeysGain => _keys.CurrentGain;

    public StripProcessor(double sampleRate)
    {
        _fs = sampleRate;
        _peq = new ParametricEq(sampleRate);
    }

    public void Process(Span<float> buf, int frames, StripSettings s, bool anySolo)
    {
        UpdateDynamics(s);
        UpdateEq(s);
        UpdateKeys(s);

        // Clicks are removed first so that the gate does not open on them.
        if (s.Keys.Enabled)
        {
            _keys.Process(buf, frames);
            _keysWasOn = true;
        }
        else if (_keysWasOn)
        {
            _keys.Reset();
            _keysWasOn = false;
        }

        if (s.Gate.Enabled)
        {
            _gate.Process(buf, frames);
            _gateWasOn = true;
        }
        else if (_gateWasOn)
        {
            _gate.Reset();
            _gateWasOn = false;
        }

        if (s.Comp.Enabled)
        {
            _comp.Process(buf, frames);
            _compWasOn = true;
        }
        else if (_compWasOn)
        {
            _comp.Reset();
            _compWasOn = false;
        }

        if (_eqActive)
        {
            _lowL.Process(buf, frames, 0, 2);
            _lowR.Process(buf, frames, 1, 2);
            _midL.Process(buf, frames, 0, 2);
            _midR.Process(buf, frames, 1, 2);
            _highL.Process(buf, frames, 0, 2);
            _highR.Process(buf, frames, 1, 2);
        }

        if (s.Mono) DspUtil.ToMono(buf, frames);

        // Parametric EQ after the mono sum: a mono strip (microphone) then needs only the left cells.
        _peq.Process(buf, frames, s.EqOn, s.EqLeft, s.Mono ? s.EqLeft : s.EqRight);
        float preamp = s.EqOn ? (float)DspUtil.DbToLin(s.EqPreampDb) : 1f;
        DspUtil.ApplyGainRamp(buf, frames, _lastPreamp, preamp);
        _lastPreamp = preamp;

        DspUtil.Peaks(buf, frames, out float pl, out float pr);
        PeakL = pl;
        PeakR = pr;

        bool silent = s.Mute || (anySolo && !s.Solo);
        float target = silent ? 0f : DspUtil.FaderToLin(s.GainDb);
        DspUtil.ApplyGainRamp(buf, frames, _lastGain, target);
        _lastGain = target;
    }

    private void UpdateKeys(StripSettings s)
    {
        var k = s.Keys;
        if (k.DepthDb == _kDepth && k.SensitivityDb == _kSens && k.HoldMs == _kHold && k.ReleaseMs == _kRel && k.LookaheadMs == _kLook)
            return;
        _kDepth = k.DepthDb; _kSens = k.SensitivityDb; _kHold = k.HoldMs; _kRel = k.ReleaseMs; _kLook = k.LookaheadMs;
        _keys.Configure(_fs, k);
    }

    private void UpdateEq(StripSettings s)
    {
        if (s.EqLowDb == _eqLow && s.EqMidDb == _eqMid && s.EqHighDb == _eqHigh) return;
        _eqLow = s.EqLowDb;
        _eqMid = s.EqMidDb;
        _eqHigh = s.EqHighDb;

        bool active = Math.Abs(_eqLow) > 0.01 || Math.Abs(_eqMid) > 0.01 || Math.Abs(_eqHigh) > 0.01;
        if (!active)
        {
            if (_eqActive)
            {
                foreach (var b in new[] { _lowL, _lowR, _midL, _midR, _highL, _highR })
                {
                    b.SetIdentity();
                    b.Reset();
                }
            }
            _eqActive = false;
            return;
        }

        _lowL.Configure(FilterType.LowShelf, _fs, LowShelfHz, _eqLow, 0.707);
        _lowR.Configure(FilterType.LowShelf, _fs, LowShelfHz, _eqLow, 0.707);
        _midL.Configure(FilterType.Peak, _fs, MidPeakHz, _eqMid, 1.0);
        _midR.Configure(FilterType.Peak, _fs, MidPeakHz, _eqMid, 1.0);
        _highL.Configure(FilterType.HighShelf, _fs, HighShelfHz, _eqHigh, 0.707);
        _highR.Configure(FilterType.HighShelf, _fs, HighShelfHz, _eqHigh, 0.707);
        _eqActive = true;
    }

    private void UpdateDynamics(StripSettings s)
    {
        var g = s.Gate;
        if (g.ThresholdDb != _gThr || g.DampingDb != _gDamp || g.AttackMs != _gAtk || g.HoldMs != _gHold || g.ReleaseMs != _gRel)
        {
            _gThr = g.ThresholdDb; _gDamp = g.DampingDb; _gAtk = g.AttackMs; _gHold = g.HoldMs; _gRel = g.ReleaseMs;
            _gate.Configure(_fs, g);
        }

        var c = s.Comp;
        if (c.Ratio != _cRatio || c.ThresholdDb != _cThr || c.AttackMs != _cAtk || c.ReleaseMs != _cRel ||
            c.Knee != _cKnee || c.GainInDb != _cIn || c.GainOutDb != _cOut || c.AutoMakeup != _cAuto)
        {
            _cRatio = c.Ratio; _cThr = c.ThresholdDb; _cAtk = c.AttackMs; _cRel = c.ReleaseMs;
            _cKnee = c.Knee; _cIn = c.GainInDb; _cOut = c.GainOutDb; _cAuto = c.AutoMakeup;
            _comp.Configure(_fs, c);
        }
    }
}
