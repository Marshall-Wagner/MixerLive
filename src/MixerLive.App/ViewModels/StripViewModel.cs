using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MixerLive.Core.Audio;
using MixerLive.Core.Audio.Dsp;
using MixerLive.Core.Model;

namespace MixerLive.App.ViewModels;

public sealed class StripViewModel : ViewModelBase, IGainControl
{
    private readonly StripSettings _s;
    private readonly MainViewModel _owner;
    private bool _suppressDeviceChange;
    private DeviceOption? _selectedDevice;
    private string _status = "";
    private StatusKind _statusKind;
    private string _xrunText = "";

    public StripViewModel(StripSettings settings, MainViewModel owner)
    {
        _s = settings;
        _owner = owner;
        Eq = new EqEditorViewModel(settings, Title, owner, isMono: () => _s.Mono);
        OpenEqCommand = new RelayCommand(() => owner.OpenEq(Eq));
    }

    /// <summary>Parametric EQ (shown on hardware strips; the same editor as the buses).</summary>
    public EqEditorViewModel Eq { get; }
    public ICommand OpenEqCommand { get; }

    /// <summary>KEYS: keyboard-click suppressor on this input.</summary>
    public bool Keys { get => _s.Keys.Enabled; set => SetModel(_s.Keys.Enabled, value, v => _s.Keys.Enabled = v); }

    public int Index => _s.Index;
    public bool IsVirtual => _s.IsVirtual;
    public bool IsHardware => !_s.IsVirtual;
    public string Title => MixerProfile.StripTitle(_s.Index);
    public string DeviceHint => IsVirtual
        ? "Playback device to listen to (loopback). Apps that play to this device end up on this strip."
        : "Recording device (microphone, line in).";

    public ObservableCollection<DeviceOption> Devices { get; } = new();

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value) || _suppressDeviceChange) return;
            if (value == null || value.Id == null && !value.IsMissing)
            {
                _s.Device.Clear();
            }
            else
            {
                _s.Device.Id = value.Id;
                _s.Device.Name = value.Name;
            }
            _owner.MarkDirty();
            _owner.Engine.RequestRebind();
        }
    }

    public string Label
    {
        get => _s.Label;
        set => SetModel(_s.Label, value ?? "", v => _s.Label = v);
    }

    public double GainDb
    {
        get => _s.GainDb;
        set
        {
            double v = Math.Round(Math.Clamp(value, -60, 12), 2);
            if (SetModel(_s.GainDb, v, x => _s.GainDb = x)) RaiseGainTexts();
        }
    }

    /// <summary>Editable dB value under the fader ("-inf" when off).</summary>
    public string GainDbText
    {
        get => VolumeScale.FormatDb(_s.GainDb);
        set
        {
            if (VolumeScale.TryParseDb(value, out double db)) GainDb = db;
            RaiseGainTexts(); // also reverts the box when the text was not a number
        }
    }

    /// <summary>Editable volume % next to the dB value (100 % = 0 dB, 50 % = half as loud).</summary>
    public string VolumePercentText
    {
        get => VolumeScale.FormatPercent(_s.GainDb);
        set
        {
            if (VolumeScale.TryParsePercent(value, out double p)) GainDb = VolumeScale.DbFromPercent(p);
            RaiseGainTexts();
        }
    }

    private void RaiseGainTexts()
    {
        OnPropertyChanged(nameof(GainDbText));
        OnPropertyChanged(nameof(VolumePercentText));
    }

    public bool Mute { get => _s.Mute; set => SetModel(_s.Mute, value, v => _s.Mute = v); }
    public bool Solo { get => _s.Solo; set => SetModel(_s.Solo, value, v => _s.Solo = v); }
    public bool Mono { get => _s.Mono; set => SetModel(_s.Mono, value, v => _s.Mono = v); }

    /// <summary>WASAPI exclusive mode (hardware strips only); the engine reopens the device on change.</summary>
    public bool Exclusive
    {
        get => _s.Exclusive;
        set
        {
            if (SetModel(_s.Exclusive, value, v => _s.Exclusive = v)) _owner.Engine.RequestRebind();
        }
    }

    public bool A1 { get => _s.A1; set => SetModel(_s.A1, value, v => _s.A1 = v); }
    public bool A2 { get => _s.A2; set => SetModel(_s.A2, value, v => _s.A2 = v); }
    public bool A3 { get => _s.A3; set => SetModel(_s.A3, value, v => _s.A3 = v); }
    public bool B1 { get => _s.B1; set => SetModel(_s.B1, value, v => _s.B1 = v); }
    public bool B2 { get => _s.B2; set => SetModel(_s.B2, value, v => _s.B2 = v); }

    public double EqLowDb
    {
        get => _s.EqLowDb;
        set { if (SetModel(_s.EqLowDb, Round1(value), v => _s.EqLowDb = v)) OnPropertyChanged(nameof(EqLowText)); }
    }
    public double EqMidDb
    {
        get => _s.EqMidDb;
        set { if (SetModel(_s.EqMidDb, Round1(value), v => _s.EqMidDb = v)) OnPropertyChanged(nameof(EqMidText)); }
    }
    public double EqHighDb
    {
        get => _s.EqHighDb;
        set { if (SetModel(_s.EqHighDb, Round1(value), v => _s.EqHighDb = v)) OnPropertyChanged(nameof(EqHighText)); }
    }
    public string EqLowText => $"{_s.EqLowDb:+0.0;-0.0;0.0}";
    public string EqMidText => $"{_s.EqMidDb:+0.0;-0.0;0.0}";
    public string EqHighText => $"{_s.EqHighDb:+0.0;-0.0;0.0}";

    public bool GateEnabled { get => _s.Gate.Enabled; set => SetModel(_s.Gate.Enabled, value, v => _s.Gate.Enabled = v); }
    public double GateThresholdDb
    {
        get => _s.Gate.ThresholdDb;
        set { if (SetModel(_s.Gate.ThresholdDb, Math.Round(value), v => _s.Gate.ThresholdDb = v)) OnPropertyChanged(nameof(GateText)); }
    }
    public string GateText => $"{_s.Gate.ThresholdDb:0} dB";

    public bool CompEnabled { get => _s.Comp.Enabled; set => SetModel(_s.Comp.Enabled, value, v => _s.Comp.Enabled = v); }
    public double CompRatio
    {
        get => _s.Comp.Ratio;
        set { if (SetModel(_s.Comp.Ratio, Round1(value), v => _s.Comp.Ratio = v)) OnPropertyChanged(nameof(CompText)); }
    }
    public string CompText => $"{_s.Comp.Ratio:0.0}:1";

    public string Status { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string XrunText { get => _xrunText; private set => Set(ref _xrunText, value); }
    /// <summary>Input level, measured before the fader.</summary>
    public MeterViewModel Meter { get; } = new();

    /// <summary>Rebuilds the device list, keeping the configured device selected (or shown as missing).</summary>
    public void RefreshDevices(IReadOnlyList<DeviceInfo> devices)
    {
        _suppressDeviceChange = true;
        try
        {
            Devices.Clear();
            Devices.Add(DeviceOption.None);
            foreach (var d in devices) Devices.Add(new DeviceOption(d.Id, d.Name));

            var candidates = devices.Select(d => (d.Id, d.Name)).ToList();
            int idx = DeviceService.Match(candidates, _s.Device);
            if (idx >= 0)
            {
                SelectedDevice = Devices[idx + 1];
            }
            else if (!_s.Device.IsEmpty)
            {
                var missing = new DeviceOption(_s.Device.Id, _s.Device.Name ?? _s.Device.Id ?? "?", IsMissing: true);
                Devices.Add(missing);
                SelectedDevice = missing;
            }
            else
            {
                SelectedDevice = DeviceOption.None;
            }
        }
        finally
        {
            _suppressDeviceChange = false;
        }
    }

    /// <summary>Called ~30×/s from the UI timer.</summary>
    public void Tick(ChannelStatus st)
    {
        Meter.Update(st);

        Status = st.Text;
        StatusKind = st.IsError ? StatusKind.Error : st.IsWaiting ? StatusKind.Waiting : st.IsBound ? StatusKind.Ok : StatusKind.None;
        XrunText = st.IsBound && st.Underruns + st.Overruns > 0 ? $"buffer resyncs: {st.Underruns + st.Overruns}" : "";
    }

    private static double Round1(double v) => Math.Round(v, 1);

    private bool SetModel<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        OnPropertyChanged(name);
        _owner.MarkDirty();
        return true;
    }
}
