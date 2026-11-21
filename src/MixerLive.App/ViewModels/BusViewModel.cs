using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MixerLive.Core.Audio;
using MixerLive.Core.Audio.Dsp;
using MixerLive.Core.Import;
using MixerLive.Core.Logging;
using MixerLive.Core.Model;

namespace MixerLive.App.ViewModels;

public sealed class BusViewModel : ViewModelBase, IGainControl
{
    private readonly BusSettings _b;
    private readonly MainViewModel _owner;
    private bool _suppressDeviceChange;
    private DeviceOption? _selectedDevice;
    private string _status = "";
    private StatusKind _statusKind;
    private string _xrunText = "";

    public BusViewModel(BusSettings settings, MainViewModel owner)
    {
        _b = settings;
        _owner = owner;
        Eq = new EqEditorViewModel(settings, Title, owner, isMono: () => false);
        OpenEqCommand = new RelayCommand(() => owner.OpenEq(Eq));
    }

    public BusSettings Settings => _b;
    public int Index => _b.Index;
    public bool IsVirtual => _b.IsVirtual;
    public string BusName => MixerProfile.BusName(_b.Index);
    public string Title => IsVirtual ? $"VIRTUAL OUTPUT {BusName}" : $"HARDWARE OUTPUT {BusName}";
    public string DeviceHint => IsVirtual
        ? "Playback device that acts as a virtual cable (e.g. VB-CABLE \"CABLE Input\"); apps record its other end."
        : "Playback device (speakers, headphones).";

    public ObservableCollection<DeviceOption> Devices { get; } = new();
    public ICommand OpenEqCommand { get; }

    /// <summary>Parametric EQ editor state (the same editor the hardware strips use).</summary>
    public EqEditorViewModel Eq { get; }

    public DeviceOption? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value) || _suppressDeviceChange) return;
            if (value == null || value.Id == null && !value.IsMissing)
            {
                _b.Device.Clear();
            }
            else
            {
                _b.Device.Id = value.Id;
                _b.Device.Name = value.Name;
            }
            _owner.MarkDirty();
            _owner.Engine.RequestRebind();
            UpdateDeviceVolume();
        }
    }

    public string Label
    {
        get => _b.Label;
        set => SetModel(_b.Label, value ?? "", v => _b.Label = v);
    }

    public double GainDb
    {
        get => _b.GainDb;
        set
        {
            double v = Math.Round(Math.Clamp(value, -60, 12), 2);
            if (SetModel(_b.GainDb, v, x => _b.GainDb = x)) RaiseGainTexts();
        }
    }

    /// <summary>Editable dB value under the fader ("-inf" when off).</summary>
    public string GainDbText
    {
        get => VolumeScale.FormatDb(_b.GainDb);
        set
        {
            if (VolumeScale.TryParseDb(value, out double db)) GainDb = db;
            RaiseGainTexts(); // also reverts the box when the text was not a number
        }
    }

    /// <summary>Editable volume % next to the dB value (100 % = 0 dB, 50 % = half as loud).</summary>
    public string VolumePercentText
    {
        get => VolumeScale.FormatPercent(_b.GainDb);
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

    public bool Mute { get => _b.Mute; set => SetModel(_b.Mute, value, v => _b.Mute = v); }
    public bool Mono { get => _b.Mono; set => SetModel(_b.Mono, value, v => _b.Mono = v); }

    /// <summary>WASAPI exclusive mode for this bus's device; the engine reopens the device on change.</summary>
    public bool Exclusive
    {
        get => _b.Exclusive;
        set
        {
            if (!SetModel(_b.Exclusive, value, v => _b.Exclusive = v)) return;
            _owner.Engine.RequestRebind();
            OnPropertyChanged(nameof(DeviceVolumeEffective));
            OnPropertyChanged(nameof(DeviceVolumeHint));
        }
    }

    // ---- device volume (the output device's own Windows volume; hardware on most USB DACs) ----

    private EndpointVolumeControl? _devVol;
    private double _deviceVolume;
    private DateTime _lastDeviceVolumeSet;

    public bool HasDeviceVolume => _devVol != null;

    /// <summary>False only for a device without hardware volume in exclusive mode, where Windows' volume is bypassed.</summary>
    public bool DeviceVolumeEffective => _devVol != null && (_devVol.HardwareVolume || !_b.Exclusive);

    public string DeviceVolumeHint
    {
        get
        {
            if (_devVol == null) return "";
            const string keys = " Mouse wheel or ↑/↓: ±2 % (Ctrl: ±0.5 %). Type a value and press Enter.";
            if (_devVol.HardwareVolume)
                return $"Device volume of \"{_devVol.DeviceName}\": the DAC's own hardware volume, the same slider as in Windows Sound settings. " +
                       "It works in shared and exclusive mode and is separate from the fader." + keys;
            return _b.Exclusive
                ? $"\"{_devVol.DeviceName}\" has no hardware volume, so its Windows volume does nothing in exclusive mode. Use the fader instead."
                : $"Device volume of \"{_devVol.DeviceName}\" (Windows applies it in software; it stops working if you turn on EXCL)." + keys;
        }
    }

    /// <summary>Device volume in % (Windows' own scale).</summary>
    public double DeviceVolume
    {
        get => _deviceVolume;
        set
        {
            double v = Math.Round(Math.Clamp(value, 0, 100), 1);
            if (!Set(ref _deviceVolume, v)) return;
            OnPropertyChanged(nameof(DeviceVolumeText));
            if (_devVol == null) return;
            _lastDeviceVolumeSet = DateTime.UtcNow;
            try
            {
                _devVol.Percent = v;
            }
            catch (Exception ex)
            {
                Log.Warn($"Bus {BusName}: setting device volume failed: {ex.Message}");
            }
        }
    }

    public string DeviceVolumeText
    {
        get => _deviceVolume.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (VolumeScale.TryParsePercent(value, out double p)) DeviceVolume = Math.Min(100, p);
            OnPropertyChanged(nameof(DeviceVolumeText));
        }
    }

    public void StepDeviceVolume(int direction, bool fine)
        => DeviceVolume = VolumeScale.StepLinear(DeviceVolume, direction, fine ? 0.5 : 2.0, 0, 100);

    /// <summary>Opens (or re-uses) the volume control of the selected device. Hardware buses only.</summary>
    private void UpdateDeviceVolume()
    {
        if (IsVirtual) return;
        var sel = SelectedDevice;
        if (sel == null || sel.Id == null || sel.IsMissing)
        {
            CloseDeviceVolume();
            return;
        }

        if (_devVol == null || _devVol.DeviceId != sel.Id)
        {
            CloseDeviceVolume();
            var ctl = EndpointVolumeControl.Open(_owner.Engine.Devices, new DeviceRef { Id = sel.Id, Name = sel.Name });
            if (ctl == null) return;
            ctl.Changed += pct => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnDeviceVolumeNotified(ctl, pct)));
            _devVol = ctl;
            OnPropertyChanged(nameof(HasDeviceVolume));
            OnPropertyChanged(nameof(DeviceVolumeEffective));
            OnPropertyChanged(nameof(DeviceVolumeHint));
        }

        try
        {
            _deviceVolume = Math.Round(_devVol.Percent, 1);
        }
        catch (Exception ex)
        {
            Log.Warn($"Bus {BusName}: reading device volume failed: {ex.Message}");
        }
        OnPropertyChanged(nameof(DeviceVolume));
        OnPropertyChanged(nameof(DeviceVolumeText));
    }

    private void OnDeviceVolumeNotified(EndpointVolumeControl source, double percent)
    {
        if (!ReferenceEquals(source, _devVol)) return;
        // Our own slider moves cause notifications too; ignore the echoes while dragging.
        if ((DateTime.UtcNow - _lastDeviceVolumeSet).TotalMilliseconds < 300) return;
        _deviceVolume = Math.Round(percent, 1);
        OnPropertyChanged(nameof(DeviceVolume));
        OnPropertyChanged(nameof(DeviceVolumeText));
    }

    public void CloseDeviceVolume()
    {
        var old = _devVol;
        if (old == null) return;
        _devVol = null;
        old.Dispose();
        OnPropertyChanged(nameof(HasDeviceVolume));
        OnPropertyChanged(nameof(DeviceVolumeEffective));
        OnPropertyChanged(nameof(DeviceVolumeHint));
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string XrunText { get => _xrunText; private set => Set(ref _xrunText, value); }
    /// <summary>Output level after the EQ and fader: what is sent to the device.</summary>
    public MeterViewModel Meter { get; } = new();

    public void RefreshDevices(IReadOnlyList<DeviceInfo> devices)
    {
        _suppressDeviceChange = true;
        try
        {
            Devices.Clear();
            Devices.Add(DeviceOption.None);
            foreach (var d in devices) Devices.Add(new DeviceOption(d.Id, d.Name));

            var candidates = devices.Select(d => (d.Id, d.Name)).ToList();
            int idx = DeviceService.Match(candidates, _b.Device);
            if (idx >= 0)
            {
                SelectedDevice = Devices[idx + 1];
            }
            else if (!_b.Device.IsEmpty)
            {
                var missing = new DeviceOption(_b.Device.Id, _b.Device.Name ?? _b.Device.Id ?? "?", IsMissing: true);
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
        UpdateDeviceVolume();
    }

    public void Tick(ChannelStatus st)
    {
        Meter.Update(st);

        Status = st.Text;
        StatusKind = st.IsError ? StatusKind.Error : st.IsWaiting ? StatusKind.Waiting : st.IsBound ? StatusKind.Ok : StatusKind.None;
        XrunText = st.IsBound && st.Underruns + st.Overruns > 0 ? $"buffer resyncs: {st.Underruns + st.Overruns}" : "";
    }

    private bool SetModel<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        OnPropertyChanged(name);
        _owner.MarkDirty();
        return true;
    }
}

