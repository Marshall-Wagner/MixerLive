using System.Text.Json.Serialization;

namespace MixerLive.Core.Model;

public enum ChannelKind
{
    Hardware,
    Virtual
}

/// <summary>
/// Reference to a Windows audio endpoint. <see cref="Name"/> may be a truncated prefix
/// (Voicemeeter stores only the first 31 characters of MME device names), so matching is
/// done by exact endpoint ID first and friendly-name prefix second.
/// </summary>
public sealed class DeviceRef
{
    public string? Name { get; set; }
    public string? Id { get; set; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Id);

    public void Clear()
    {
        Name = null;
        Id = null;
    }

    public DeviceRef Clone() => new() { Name = Name, Id = Id };

    public override string ToString() => Name ?? Id ?? "(none)";
}

/// <summary>One parametric EQ cell of a bus (Voicemeeter Banana has 6 per channel).</summary>
public sealed class EqBand
{
    public bool On { get; set; } = true;

    /// <summary>
    /// Filter type using Voicemeeter's numbering (best-effort mapping):
    /// 0 = peaking, 1 = low-pass, 2 = band-pass, 3 = high-pass, 4 = notch, 5 = low shelf, 6 = high shelf.
    /// </summary>
    public int Type { get; set; }

    public double GainDb { get; set; }
    public double Freq { get; set; } = 1000;
    public double Q { get; set; } = 3;

    [JsonIgnore]
    public bool IsEffectivelyFlat => Type == 0 && Math.Abs(GainDb) < 0.01;

    public EqBand Clone() => new() { On = On, Type = Type, GainDb = GainDb, Freq = Freq, Q = Q };
}

public sealed class GateSettings
{
    public bool Enabled { get; set; }
    public double ThresholdDb { get; set; } = -50;
    /// <summary>Attenuation applied when the gate is closed (negative dB).</summary>
    public double DampingDb { get; set; } = -50;
    public double AttackMs { get; set; } = 20;
    public double HoldMs { get; set; } = 200;
    public double ReleaseMs { get; set; } = 1250;
}

public sealed class CompSettings
{
    public bool Enabled { get; set; }
    public double Ratio { get; set; } = 1;
    public double ThresholdDb { get; set; } = -40;
    public double AttackMs { get; set; } = 10;
    public double ReleaseMs { get; set; } = 60;
    /// <summary>0..1 (Voicemeeter's knee value); mapped to a 0..12 dB soft knee.</summary>
    public double Knee { get; set; } = 0.5;
    public bool AutoMakeup { get; set; } = true;
    public double GainInDb { get; set; }
    public double GainOutDb { get; set; }
}

/// <summary>Keyboard-click suppressor on a hardware strip (see KeyClickSuppressor).</summary>
public sealed class KeysSettings
{
    public bool Enabled { get; set; }

    /// <summary>Attenuation while a click is being suppressed (negative dB).</summary>
    public double DepthDb { get; set; } = -24;

    /// <summary>How much louder (dB) a sudden high-frequency burst must be than the running level to count as a click.</summary>
    public double SensitivityDb { get; set; } = 10;

    public double HoldMs { get; set; } = 30;
    public double ReleaseMs { get; set; } = 40;

    /// <summary>Delay on the signal so the attenuation is already in place when the click arrives.</summary>
    public double LookaheadMs { get; set; } = 3;
}

/// <summary>A channel with a parametric EQ: strips (hardware) and buses share the editor and the DSP.</summary>
public interface IEqSettings
{
    bool EqOn { get; set; }
    double EqPreampDb { get; set; }
    List<EqBand> EqLeft { get; set; }
    List<EqBand> EqRight { get; set; }
}

/// <summary>Cell-list helpers shared by strips and buses.</summary>
public static class EqCells
{
    public static List<EqBand> Default() => Pad(new List<EqBand>());

    /// <summary>Returns a copy padded to the minimum cell count and trimmed to the maximum.</summary>
    public static List<EqBand> Pad(List<EqBand> cells)
    {
        double[] defaults = { 50, 200, 800, 2000, 8000, 12000 };
        var list = new List<EqBand>(cells);
        while (list.Count < MixerProfile.EqCellCount)
            list.Add(new EqBand { Freq = defaults[list.Count], On = true });
        if (list.Count > MixerProfile.MaxEqCells)
            list.RemoveRange(MixerProfile.MaxEqCells, list.Count - MixerProfile.MaxEqCells);
        return list;
    }
}

public sealed class StripSettings : IEqSettings
{
    public int Index { get; set; }
    public ChannelKind Kind { get; set; }
    public string Label { get; set; } = "";

    /// <summary>
    /// Hardware strips: a capture endpoint (microphone, line in).
    /// Virtual strips: a render endpoint that is loopback-captured (e.g. "Voicemeeter Input").
    /// </summary>
    public DeviceRef Device { get; set; } = new();

    /// <summary>Fader, -60 (silence) .. +12 dB.</summary>
    public double GainDb { get; set; }

    public bool Mute { get; set; }
    public bool Solo { get; set; }
    public bool Mono { get; set; }

    /// <summary>
    /// Hardware strips only: open the device in WASAPI exclusive mode (native format, smallest
    /// driver buffer, no Windows mixing or APO effects). Ignored for virtual/loopback strips.
    /// </summary>
    public bool Exclusive { get; set; }

    public bool A1 { get; set; }
    public bool A2 { get; set; }
    public bool A3 { get; set; }
    public bool B1 { get; set; }
    public bool B2 { get; set; }

    /// <summary>Three-band EQ (Voicemeeter Banana exposes it on the virtual strips).</summary>
    public double EqLowDb { get; set; }
    public double EqMidDb { get; set; }
    public double EqHighDb { get; set; }

    public GateSettings Gate { get; set; } = new();
    public CompSettings Comp { get; set; } = new();

    /// <summary>Keyboard-click suppressor (hardware strips; the UI shows it as KEYS).</summary>
    public KeysSettings Keys { get; set; } = new();

    /// <summary>Parametric EQ (hardware strips; same editor as the buses). With MONO on, the left cells serve both channels.</summary>
    public bool EqOn { get; set; }
    public double EqPreampDb { get; set; }
    public List<EqBand> EqLeft { get; set; } = new();
    public List<EqBand> EqRight { get; set; } = new();

    [JsonIgnore]
    public bool IsVirtual => Kind == ChannelKind.Virtual;

    public void EnsureEqCells()
    {
        EqLeft = EqCells.Pad(EqLeft);
        EqRight = EqCells.Pad(EqRight);
    }

    public bool RoutedTo(int bus) => bus switch
    {
        0 => A1,
        1 => A2,
        2 => A3,
        3 => B1,
        4 => B2,
        _ => false
    };

    public void SetRoute(int bus, bool on)
    {
        switch (bus)
        {
            case 0: A1 = on; break;
            case 1: A2 = on; break;
            case 2: A3 = on; break;
            case 3: B1 = on; break;
            case 4: B2 = on; break;
        }
    }
}

public sealed class BusSettings : IEqSettings
{
    public int Index { get; set; }
    public ChannelKind Kind { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Render endpoint this bus plays to.</summary>
    public DeviceRef Device { get; set; } = new();

    public double GainDb { get; set; }
    public bool Mute { get; set; }
    public bool Mono { get; set; }

    /// <summary>
    /// Open the playback device in WASAPI exclusive mode: the bus writes the driver's native PCM
    /// format directly (e.g. 32-bit 768 kHz on a hi-res DAC) with the smallest buffer the driver
    /// allows. Windows mixing and APO effects (Equalizer APO) are bypassed on that device.
    /// </summary>
    public bool Exclusive { get; set; }

    public bool EqOn { get; set; }

    /// <summary>Gain applied together with the EQ (the "Preamp:" line of an EQ .txt file), dB.</summary>
    public double EqPreampDb { get; set; }

    /// <summary>
    /// EQ cells per channel. At least <see cref="MixerProfile.EqCellCount"/> (Voicemeeter layout),
    /// at most <see cref="MixerProfile.MaxEqCells"/>. Replace the list to change it; the audio
    /// thread reads the reference once per block.
    /// </summary>
    public List<EqBand> EqLeft { get; set; } = new();
    public List<EqBand> EqRight { get; set; } = new();

    [JsonIgnore]
    public bool IsVirtual => Kind == ChannelKind.Virtual;

    public void EnsureEqCells()
    {
        EqLeft = EqCells.Pad(EqLeft);
        EqRight = EqCells.Pad(EqRight);
    }

    public static List<EqBand> DefaultCells() => EqCells.Default();

    /// <summary>Returns a copy padded to the minimum cell count and trimmed to the maximum.</summary>
    public static List<EqBand> PadCells(List<EqBand> cells) => EqCells.Pad(cells);
}

/// <summary>
/// The complete mixer state: 5 input strips (3 hardware + 2 virtual) and 5 output buses
/// (A1..A3 hardware, B1..B2 virtual), mirroring Voicemeeter Banana's layout.
/// </summary>
public sealed class MixerProfile
{
    public const int StripCount = 5;
    public const int BusCount = 5;
    public const int HardwareStripCount = 3;
    public const int HardwareBusCount = 3;
    /// <summary>Minimum EQ cells per bus channel (Voicemeeter Banana's layout).</summary>
    public const int EqCellCount = 6;
    /// <summary>Maximum EQ cells per bus channel (enough for any AutoEq / PEQdB export).</summary>
    public const int MaxEqCells = 32;

    public string Name { get; set; } = "Default";
    public string? SourceFile { get; set; }
    public DateTime? ImportedAt { get; set; }

    public List<StripSettings> Strips { get; set; } = new();
    public List<BusSettings> Buses { get; set; } = new();

    /// <summary>
    /// Engine settings, present only in files written by "Save profile as…". Null in the
    /// auto-saved profile and in profiles from older versions.
    /// </summary>
    public EngineSettings? Engine { get; set; }

    public static string BusName(int index) => index switch
    {
        0 => "A1",
        1 => "A2",
        2 => "A3",
        3 => "B1",
        4 => "B2",
        _ => $"Bus{index + 1}"
    };

    public static string StripTitle(int index) => index < HardwareStripCount
        ? $"HARDWARE INPUT {index + 1}"
        : $"VIRTUAL INPUT {index - HardwareStripCount + 1}";

    public static MixerProfile CreateDefault()
    {
        var p = new MixerProfile();
        p.Normalize();

        // Sensible Banana-like defaults.
        p.Strips[0].Label = "MIC";
        p.Strips[0].B1 = true;
        p.Strips[1].Label = "HW IN 2";
        p.Strips[1].Mute = true;
        p.Strips[1].GainDb = -60;
        p.Strips[2].Label = "HW IN 3";
        p.Strips[2].Mute = true;
        p.Strips[2].GainDb = -60;

        p.Strips[3].Label = "SYSTEM";
        p.Strips[3].A1 = true;
        p.Strips[3].Device = new DeviceRef { Name = "Voicemeeter Input" };
        p.Strips[4].Label = "AUX";
        p.Strips[4].A1 = true;
        p.Strips[4].Device = new DeviceRef { Name = "Voicemeeter AUX Input" };

        p.Buses[3].Device = new DeviceRef { Name = "CABLE Input" };
        return p;
    }

    /// <summary>Makes sure the lists have the right shape after deserialization or import.</summary>
    public void Normalize()
    {
        while (Strips.Count < StripCount) Strips.Add(new StripSettings());
        while (Buses.Count < BusCount) Buses.Add(new BusSettings());
        if (Strips.Count > StripCount) Strips.RemoveRange(StripCount, Strips.Count - StripCount);
        if (Buses.Count > BusCount) Buses.RemoveRange(BusCount, Buses.Count - BusCount);
        Engine?.Advanced?.Normalize();

        for (int i = 0; i < StripCount; i++)
        {
            var s = Strips[i];
            s.Index = i;
            s.Kind = i < HardwareStripCount ? ChannelKind.Hardware : ChannelKind.Virtual;
            s.Device ??= new DeviceRef();
            s.Gate ??= new GateSettings();
            s.Comp ??= new CompSettings();
            s.Keys ??= new KeysSettings();
            s.Label ??= "";
            s.EqLeft ??= new List<EqBand>();
            s.EqRight ??= new List<EqBand>();
            s.EnsureEqCells();
            s.EqPreampDb = Math.Clamp(s.EqPreampDb, -40, 20);
            s.GainDb = Math.Clamp(s.GainDb, -60, 12);
            if (s.IsVirtual) s.Exclusive = false;
        }

        for (int i = 0; i < BusCount; i++)
        {
            var b = Buses[i];
            b.Index = i;
            b.Kind = i < HardwareBusCount ? ChannelKind.Hardware : ChannelKind.Virtual;
            b.Device ??= new DeviceRef();
            b.Label ??= "";
            b.EqLeft ??= new List<EqBand>();
            b.EqRight ??= new List<EqBand>();
            b.EnsureEqCells();
            b.GainDb = Math.Clamp(b.GainDb, -60, 12);
            b.EqPreampDb = Math.Clamp(b.EqPreampDb, -40, 20);
        }
    }
}
