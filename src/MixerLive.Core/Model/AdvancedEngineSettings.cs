namespace MixerLive.Core.Model;

/// <summary>
/// Optional overrides of the numbers behind the Latency preset, for people who want to tune latency
/// themselves (the "Advanced…" button). A null field means "use the preset's value". Saved in
/// settings.json and carried inside exported profiles.
/// </summary>
public sealed class AdvancedEngineSettings
{
    /// <summary>Mixer block length when nothing else is chosen.</summary>
    public const double DefaultBlockMs = 5;

    /// <summary>Block lengths offered in the UI (ms). Shorter blocks wake the mixer more often.</summary>
    public static readonly double[] BlockChoicesMs = [1, 2, 3, 5, 10, 20];

    public const double MinBlockMs = 1, MaxBlockMs = 50;
    public const int MinRingMs = 2, MaxRingMs = 500;
    public const int MinSharedOutputMs = 10, MaxSharedOutputMs = 500;
    public const int MinExclusiveMs = 1, MaxExclusiveMs = 100;

    /// <summary>Mixer block length in ms (how much audio is processed per mixer wake-up).</summary>
    public double? BlockMs { get; set; }

    /// <summary>Captured audio kept ahead of the mixer per input strip, in ms (the strip ring target).</summary>
    public int? InputBufferMs { get; set; }

    /// <summary>Mixed audio kept ahead of each output stream, in ms (the bus ring target).</summary>
    public int? OutputBufferMs { get; set; }

    /// <summary>Buffer asked from Windows for a shared-mode output, in ms.</summary>
    public int? SharedOutputBufferMs { get; set; }

    /// <summary>Buffer asked from the driver for an exclusive-mode device, in ms; the driver's minimum wins if larger.</summary>
    public int? ExclusiveBufferMs { get; set; }

    /// <summary>True when every field is null, i.e. the preset is used unchanged.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => BlockMs == null && InputBufferMs == null && OutputBufferMs == null
                           && SharedOutputBufferMs == null && ExclusiveBufferMs == null;

    public AdvancedEngineSettings Clone() => (AdvancedEngineSettings)MemberwiseClone();

    /// <summary>Equal field by field; a null object counts as empty.</summary>
    public static bool Same(AdvancedEngineSettings? a, AdvancedEngineSettings? b)
    {
        a ??= new AdvancedEngineSettings();
        b ??= new AdvancedEngineSettings();
        return a.BlockMs == b.BlockMs && a.InputBufferMs == b.InputBufferMs && a.OutputBufferMs == b.OutputBufferMs
               && a.SharedOutputBufferMs == b.SharedOutputBufferMs && a.ExclusiveBufferMs == b.ExclusiveBufferMs;
    }

    /// <summary>Pulls every set value into its allowed range (settings files are hand-editable).</summary>
    public void Normalize()
    {
        if (BlockMs is double b) BlockMs = double.IsFinite(b) ? Math.Clamp(b, MinBlockMs, MaxBlockMs) : null;
        if (InputBufferMs is int i) InputBufferMs = Math.Clamp(i, MinRingMs, MaxRingMs);
        if (OutputBufferMs is int o) OutputBufferMs = Math.Clamp(o, MinRingMs, MaxRingMs);
        if (SharedOutputBufferMs is int s) SharedOutputBufferMs = Math.Clamp(s, MinSharedOutputMs, MaxSharedOutputMs);
        if (ExclusiveBufferMs is int x) ExclusiveBufferMs = Math.Clamp(x, MinExclusiveMs, MaxExclusiveMs);
    }

    /// <summary>Null when nothing is overridden, otherwise a normalized copy: what gets saved.</summary>
    public AdvancedEngineSettings? Trimmed()
    {
        var c = Clone();
        c.Normalize();
        return c.IsEmpty ? null : c;
    }

    /// <summary>"block 2 ms, input 8 ms" — only the overridden fields.</summary>
    public string Describe()
    {
        var parts = new List<string>(5);
        if (BlockMs is double b) parts.Add($"block {b:0.#} ms");
        if (InputBufferMs is int i) parts.Add($"input {i} ms");
        if (OutputBufferMs is int o) parts.Add($"output {o} ms");
        if (SharedOutputBufferMs is int s) parts.Add($"shared output {s} ms");
        if (ExclusiveBufferMs is int x) parts.Add($"exclusive {x} ms");
        return string.Join(", ", parts);
    }
}
