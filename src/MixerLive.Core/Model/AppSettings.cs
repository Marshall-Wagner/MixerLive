namespace MixerLive.Core.Model;

public enum LatencyPreset
{
    Low,
    Normal,
    Safe
}

/// <summary>Interpolator used for drift correction and rate conversion.</summary>
public enum ResampleQuality
{
    /// <summary>4-point Hermite: negligible CPU, ~50 dB image rejection, no anti-alias filter.</summary>
    Fast,
    /// <summary>128-tap windowed sinc: band-limited, ~100 dB, anti-aliasing; a few % CPU per stream at 768 kHz.</summary>
    Best
}

public sealed class AppSettings
{
    /// <summary>Engine sample rate. 48000 matches Windows' default shared-mode rate on most devices.</summary>
    public int SampleRate { get; set; } = 48000;

    public LatencyPreset Latency { get; set; } = LatencyPreset.Normal;

    public ResampleQuality Resampler { get; set; } = ResampleQuality.Best;

    /// <summary>Overrides of the Latency preset's numbers from the Advanced dialog; null = none.</summary>
    public AdvancedEngineSettings? Advanced { get; set; }

    public bool AutoStartEngine { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Set once the first-run import prompt has been shown.</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>Mixer controls are locked against changes (like Voicemeeter's "Lock GUI").</summary>
    public bool Locked { get; set; }
}
