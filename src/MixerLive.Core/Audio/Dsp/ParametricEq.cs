using MixerLive.Core.Model;

namespace MixerLive.Core.Audio.Dsp;

/// <summary>
/// Up to <see cref="MixerProfile.MaxEqCells"/> biquad cells per channel, configured from
/// <see cref="EqBand"/> lists. Shared by strips and buses. Cells are re-configured only when their
/// values change (compared against a cache), so the audio thread never recomputes coefficients needlessly.
/// </summary>
public sealed class ParametricEq
{
    private readonly double _fs;
    private readonly Biquad[] _left = new Biquad[MixerProfile.MaxEqCells];
    private readonly Biquad[] _right = new Biquad[MixerProfile.MaxEqCells];
    private readonly CellCache[] _cacheL = new CellCache[MixerProfile.MaxEqCells];
    private readonly CellCache[] _cacheR = new CellCache[MixerProfile.MaxEqCells];
    private bool _wasOn;

    private struct CellCache
    {
        public bool On;
        public int Type;
        public double Gain, Freq, Q;
        public bool Active;
        public bool Initialized;
    }

    public ParametricEq(double sampleRate)
    {
        _fs = sampleRate;
        for (int i = 0; i < MixerProfile.MaxEqCells; i++)
        {
            _left[i] = new Biquad();
            _right[i] = new Biquad();
        }
    }

    /// <summary>
    /// Runs the cells on a stereo interleaved block. When <paramref name="on"/> is false the filter
    /// states are cleared once and the block passes untouched. Pass the same list twice to apply one
    /// set of cells to both channels (mono strips).
    /// </summary>
    public void Process(Span<float> buf, int frames, bool on, List<EqBand> left, List<EqBand> right)
    {
        if (!on)
        {
            if (_wasOn)
            {
                Reset();
                _wasOn = false;
            }
            return;
        }

        // The lists are replaced wholesale by the UI (never mutated in place), so reading the
        // reference once per block gives a consistent snapshot.
        UpdateCells(left, _left, _cacheL);
        UpdateCells(right, _right, _cacheR);
        for (int i = 0; i < MixerProfile.MaxEqCells; i++)
        {
            if (_cacheL[i].Active) _left[i].Process(buf, frames, 0, 2);
            if (_cacheR[i].Active) _right[i].Process(buf, frames, 1, 2);
        }
        _wasOn = true;
    }

    public void Reset()
    {
        foreach (var f in _left) f.Reset();
        foreach (var f in _right) f.Reset();
    }

    private void UpdateCells(List<EqBand> cells, Biquad[] filters, CellCache[] cache)
    {
        int n = Math.Min(cells.Count, MixerProfile.MaxEqCells);
        for (int i = 0; i < n; i++)
        {
            var c = cells[i];
            ref var k = ref cache[i];
            if (k.Initialized && k.On == c.On && k.Type == c.Type && k.Gain == c.GainDb && k.Freq == c.Freq && k.Q == c.Q)
                continue;

            k.Initialized = true;
            k.On = c.On; k.Type = c.Type; k.Gain = c.GainDb; k.Freq = c.Freq; k.Q = c.Q;
            k.Active = c.On && !c.IsEffectivelyFlat;
            if (k.Active)
            {
                var type = Enum.IsDefined(typeof(FilterType), c.Type) ? (FilterType)c.Type : FilterType.Peak;
                filters[i].Configure(type, _fs, c.Freq, c.GainDb, c.Q);
            }
            else
            {
                filters[i].SetIdentity();
                filters[i].Reset();
            }
        }

        // Cells that disappeared (list got shorter) must stop processing.
        for (int i = n; i < MixerProfile.MaxEqCells; i++)
        {
            ref var k = ref cache[i];
            if (!k.Initialized) continue;
            k.Initialized = false;
            k.Active = false;
            filters[i].SetIdentity();
            filters[i].Reset();
        }
    }
}
