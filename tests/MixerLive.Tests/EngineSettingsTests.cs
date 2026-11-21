using MixerLive.Core.Audio;
using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class EngineSettingsTests
{
    [Fact]
    public void One_settings_object_round_trips_through_saved_settings_and_engine_options()
    {
        var wanted = new EngineSettings
        {
            SampleRate = 768000,
            Latency = LatencyPreset.Low,
            Resampler = ResampleQuality.Fast,
            Advanced = new AdvancedEngineSettings { BlockMs = 1024 * 1000.0 / 768000, ExclusiveBufferMs = 4 }
        };

        var saved = new AppSettings();
        Assert.False(wanted.Matches(saved));
        wanted.ApplyTo(saved);
        Assert.True(wanted.Matches(saved));
        Assert.Equal(768000, saved.SampleRate);
        Assert.Equal(LatencyPreset.Low, saved.Latency);
        Assert.Equal(ResampleQuality.Fast, saved.Resampler);

        var options = EngineOptions.From(EngineSettings.From(saved));
        Assert.Equal(1024, options.BlockFramesFor(768000));
        Assert.Equal(4, options.ExclusiveBufferMs);
        Assert.Equal(LatencyPreset.Low, options.Latency);

        var back = EngineSettings.From(options);
        Assert.True(back.Matches(saved));
    }

    [Fact]
    public void Lock_and_tray_preferences_are_not_engine_settings()
    {
        var saved = new AppSettings { SampleRate = 48000, Locked = true, CloseToTray = false };
        var e = EngineSettings.From(saved);
        saved.Locked = false;
        saved.CloseToTray = true;
        Assert.True(e.Matches(saved));
    }

    [Fact]
    public void Standard_rates_are_sorted_and_include_the_hi_res_ones()
    {
        Assert.Equal(new[] { 44100, 48000, 96000, 192000, 384000, 768000 }, EngineSettings.StandardRates);
    }
}
