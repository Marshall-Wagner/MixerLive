using MixerLive.Core.Audio;

namespace MixerLive.App.ViewModels;

/// <summary>Level meter of one strip or bus: LED ladder levels, peak-hold blocks and the numeric peak readout.</summary>
public sealed class MeterViewModel : ViewModelBase
{
    private readonly PeakMeter _meter = new();
    private double _levelL = PeakMeter.FloorDb, _levelR = PeakMeter.FloorDb;
    private double _holdL = PeakMeter.FloorDb, _holdR = PeakMeter.FloorDb;
    private string _peakText = PeakMeter.Format(PeakMeter.FloorDb);
    private bool _isClipping;

    public double LevelL { get => _levelL; private set => Set(ref _levelL, value); }
    public double LevelR { get => _levelR; private set => Set(ref _levelR, value); }
    public double HoldL { get => _holdL; private set => Set(ref _holdL, value); }
    public double HoldR { get => _holdR; private set => Set(ref _holdR, value); }

    /// <summary>Loudest peak of the last 2 seconds in dBFS, e.g. "-12.3", or "-inf" when silent.</summary>
    public string PeakText { get => _peakText; private set => Set(ref _peakText, value); }

    /// <summary>The held peak is at or over 0 dBFS (the readout turns red).</summary>
    public bool IsClipping { get => _isClipping; private set => Set(ref _isClipping, value); }

    /// <summary>Called ~30×/s with the engine's peaks; resets them for the next interval.</summary>
    public void Update(ChannelStatus st)
    {
        float pl = st.PeakL;
        st.PeakL = 0;
        float pr = st.PeakR;
        st.PeakR = 0;

        _meter.Update(pl, pr);
        LevelL = _meter.LevelL;
        LevelR = _meter.LevelR;
        HoldL = _meter.HoldL;
        HoldR = _meter.HoldR;
        PeakText = PeakMeter.Format(_meter.Peak);
        IsClipping = _meter.IsClipping;
    }
}
