using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MixerLive.Core.Import;
using MixerLive.Core.Model;

namespace MixerLive.App.ViewModels;

/// <summary>
/// Editor state of one parametric EQ: a hardware strip's (microphone) or a bus's (headphones).
/// Both share the same DSP, model shape and editor window.
/// </summary>
public sealed class EqEditorViewModel : ViewModelBase
{
    private readonly IEqSettings _eq;
    private readonly MainViewModel _owner;
    private readonly Func<bool> _isMono;
    private string _eqInfo = "";

    public EqEditorViewModel(IEqSettings eq, string title, MainViewModel owner, Func<bool> isMono)
    {
        _eq = eq;
        Title = title;
        _owner = owner;
        _isMono = isMono;
        RebuildCells();
    }

    /// <summary>Channel name shown in the window title, e.g. "HARDWARE INPUT 1".</summary>
    public string Title { get; }

    public ObservableCollection<EqCellViewModel> EqLeft { get; } = new();
    public ObservableCollection<EqCellViewModel> EqRight { get; } = new();

    public bool EqOn { get => _eq.EqOn; set => SetModel(_eq.EqOn, value, v => _eq.EqOn = v); }

    public double EqPreampDb
    {
        get => _eq.EqPreampDb;
        set
        {
            if (SetModel(_eq.EqPreampDb, Math.Round(Math.Clamp(value, -40, 20), 1), v => _eq.EqPreampDb = v))
                OnPropertyChanged(nameof(EqPreampText));
        }
    }

    public string EqPreampText => $"{_eq.EqPreampDb:+0.0;-0.0;0.0} dB";

    /// <summary>Last import summary shown in the EQ window.</summary>
    public string EqInfo { get => _eqInfo; private set => Set(ref _eqInfo, value); }

    /// <summary>Shown when the channel is summed to mono before the EQ: only the left cells act.</summary>
    public string MonoNote => _isMono()
        ? "MONO is on for this input, so the LEFT cells shape the whole signal and the right cells are ignored."
        : "";

    public void RefreshMonoNote() => OnPropertyChanged(nameof(MonoNote));

    public bool CanAddCells => _eq.EqLeft.Count < MixerProfile.MaxEqCells || _eq.EqRight.Count < MixerProfile.MaxEqCells;

    // ---- cell management (lists are replaced, never mutated in place: the audio thread reads them) ----

    private void RebuildCells()
    {
        EqLeft.Clear();
        EqRight.Clear();
        bool canRemoveL = _eq.EqLeft.Count > MixerProfile.EqCellCount;
        bool canRemoveR = _eq.EqRight.Count > MixerProfile.EqCellCount;
        for (int i = 0; i < _eq.EqLeft.Count; i++) EqLeft.Add(new EqCellViewModel(_eq.EqLeft[i], i, 0, canRemoveL, this, _owner));
        for (int i = 0; i < _eq.EqRight.Count; i++) EqRight.Add(new EqCellViewModel(_eq.EqRight[i], i, 1, canRemoveR, this, _owner));
        OnPropertyChanged(nameof(CanAddCells));
    }

    public void AddCell(int channel)
    {
        var src = channel == 0 ? _eq.EqLeft : _eq.EqRight;
        if (src.Count >= MixerProfile.MaxEqCells) return;
        var list = new List<EqBand>(src) { new EqBand { Freq = 1000, Q = 1.0, On = true } };
        if (channel == 0) _eq.EqLeft = list; else _eq.EqRight = list;
        RebuildCells();
        _owner.MarkDirty();
    }

    public void RemoveCell(EqCellViewModel cell)
    {
        var src = cell.Channel == 0 ? _eq.EqLeft : _eq.EqRight;
        if (src.Count <= MixerProfile.EqCellCount || cell.Index >= src.Count) return;
        var list = new List<EqBand>(src);
        list.RemoveAt(cell.Index);
        if (cell.Channel == 0) _eq.EqLeft = list; else _eq.EqRight = list;
        RebuildCells();
        _owner.MarkDirty();
    }

    public void CopyLeftEqToRight()
    {
        _eq.EqRight = _eq.EqLeft.Select(c => c.Clone()).ToList();
        RebuildCells();
        _owner.MarkDirty();
    }

    public void ResetEq()
    {
        _eq.EqLeft = EqCells.Default();
        _eq.EqRight = EqCells.Default();
        _eq.EqPreampDb = 0;
        OnPropertyChanged(nameof(EqPreampDb));
        OnPropertyChanged(nameof(EqPreampText));
        EqInfo = "";
        RebuildCells();
        _owner.MarkDirty();
    }

    /// <summary>
    /// Loads a parametric EQ .txt file (Preamp / Filter lines, as saved by PEQdB, AutoEq, REW or
    /// Equalizer APO) into both channels and switches the EQ on.
    /// </summary>
    public void ImportEqFile(string path)
    {
        var r = EqualizerApoImporter.ImportFile(path);
        if (r.Bands.Count == 0)
        {
            EqInfo = $"Nothing imported from {Path.GetFileName(path)}: {string.Join(" ", r.Warnings)}";
            return;
        }

        _eq.EqLeft = EqCells.Pad(r.Bands.Select(b => b.Clone()).ToList());
        _eq.EqRight = EqCells.Pad(r.Bands.Select(b => b.Clone()).ToList());
        _eq.EqPreampDb = Math.Clamp(r.PreampDb, -40, 20);
        _eq.EqOn = true;
        OnPropertyChanged(nameof(EqOn));
        OnPropertyChanged(nameof(EqPreampDb));
        OnPropertyChanged(nameof(EqPreampText));
        RebuildCells();
        _owner.MarkDirty();

        EqInfo = $"Imported {r.Summary}." + (r.Warnings.Count > 0 ? " " + string.Join(" ", r.Warnings) : "");
    }

    public string? ActiveEqualizerApoConfig => EqualizerApoImporter.ActiveConfigPath;

    private bool SetModel<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        apply(value);
        OnPropertyChanged(name);
        _owner.MarkDirty();
        return true;
    }
}

public sealed class EqCellViewModel : ViewModelBase
{
    public static readonly string[] TypeNames =
    {
        "Peak", "Low-pass", "Band-pass", "High-pass", "Notch", "Low shelf", "High shelf"
    };

    private readonly EqBand _band;
    private readonly MainViewModel _owner;

    public EqCellViewModel(EqBand band, int index, int channel, bool canRemove, EqEditorViewModel editor, MainViewModel owner)
    {
        _band = band;
        _owner = owner;
        Index = index;
        Channel = channel;
        CanRemove = canRemove;
        RemoveCommand = new RelayCommand(() => editor.RemoveCell(this), () => CanRemove);
    }

    public int Index { get; }
    public int Channel { get; }
    public int Number => Index + 1;
    public bool CanRemove { get; }
    public ICommand RemoveCommand { get; }
    public IReadOnlyList<string> Types => TypeNames;

    public bool On { get => _band.On; set => SetModel(_band.On, value, v => _band.On = v); }

    public int Type
    {
        get => Math.Clamp(_band.Type, 0, TypeNames.Length - 1);
        set => SetModel(_band.Type, Math.Clamp(value, 0, TypeNames.Length - 1), v => _band.Type = v);
    }

    public double GainDb
    {
        get => _band.GainDb;
        set { if (SetModel(_band.GainDb, Math.Round(Math.Clamp(value, -30, 30), 1), v => _band.GainDb = v)) OnPropertyChanged(nameof(GainText)); }
    }

    public string GainText => $"{_band.GainDb:+0.0;-0.0;0.0} dB";

    public double Freq
    {
        get => _band.Freq;
        set => SetModel(_band.Freq, Math.Clamp(value, 10, 22000), v => _band.Freq = v);
    }

    public double Q
    {
        get => _band.Q;
        set => SetModel(_band.Q, Math.Clamp(value, 0.1, 30), v => _band.Q = v);
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
