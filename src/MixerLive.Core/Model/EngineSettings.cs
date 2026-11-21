using MixerLive.Core.Audio;

namespace MixerLive.Core.Model;

/// <summary>
/// The audio-engine settings as one unit: sample rate, latency preset, resampler and the buffer
/// overrides. It is what the Audio settings window edits, what an exported profile carries
/// ("Save profile as…"), and the single thing the app applies to the running engine.
/// Per-PC preferences (lock, tray behaviour, sign-in task) are deliberately not part of it.
/// </summary>
public sealed class EngineSettings
{
    /// <summary>Rates offered in the Audio settings window and accepted from profiles.</summary>
    public static readonly int[] StandardRates = [44100, 48000, 96000, 192000, 384000, 768000];

    public int SampleRate { get; set; } = 48000;
    public LatencyPreset Latency { get; set; } = LatencyPreset.Normal;
    public ResampleQuality Resampler { get; set; } = ResampleQuality.Best;

    /// <summary>Buffer size and expert buffer overrides; null = the defaults and the preset's values.</summary>
    public AdvancedEngineSettings? Advanced { get; set; }

    public static EngineSettings From(AppSettings s) => new()
    {
        SampleRate = s.SampleRate,
        Latency = s.Latency,
        Resampler = s.Resampler,
        Advanced = s.Advanced?.Trimmed()
    };

    public static EngineSettings From(EngineOptions o) => new()
    {
        SampleRate = o.SampleRate,
        Latency = o.Latency,
        Resampler = o.Resampler,
        Advanced = o.Advanced?.Trimmed()
    };

    public bool Matches(AppSettings s) =>
        SampleRate == s.SampleRate && Latency == s.Latency && Resampler == s.Resampler
        && AdvancedEngineSettings.Same(Advanced, s.Advanced);

    /// <summary>Writes these values into the saved settings.</summary>
    public void ApplyTo(AppSettings s)
    {
        s.SampleRate = SampleRate;
        s.Latency = Latency;
        s.Resampler = Resampler;
        s.Advanced = Advanced?.Trimmed();
    }

    /// <summary>Writes these values into the engine's options (call <see cref="AudioEngine.ApplyOptions"/> afterwards).</summary>
    public void ApplyTo(EngineOptions o)
    {
        o.SampleRate = SampleRate;
        o.Latency = Latency;
        o.Resampler = Resampler;
        o.Advanced = Advanced?.Trimmed();
    }

    /// <summary>"Rate 768 kHz · Latency Normal · Resampler Best", plus " · Advanced: block 2 ms" when overridden.</summary>
    public string Describe()
    {
        string s = $"Rate {WaveFormats.RateText(SampleRate)} · Latency {Latency} · Resampler {Resampler}";
        if (Advanced is { IsEmpty: false } a) s += $" · Advanced: {a.Describe()}";
        return s;
    }
}
