using MixerLive.Core.Audio;
using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class AdvancedSettingsTests
{
    [Theory]
    [InlineData(48000, 5.0, 240)]
    [InlineData(768000, 5.0, 3840)]
    [InlineData(768000, 2.0, 1536)]
    [InlineData(48000, 1.0, 48)]
    [InlineData(8000, 1.0, 32)]     // never below 32 frames
    [InlineData(44100, 10.0, 441)]
    public void Block_frames_follow_rate_and_block_length(int rate, double ms, int frames)
        => Assert.Equal(frames, EngineOptions.BlockFrames(rate, ms));

    [Fact]
    public void Options_use_the_preset_when_nothing_is_overridden()
    {
        var o = new EngineOptions { SampleRate = 768000, Latency = LatencyPreset.Low };
        Assert.Equal(5, o.BlockMs);
        Assert.Equal(3840, o.BlockFramesFor(768000));
        Assert.Equal(12, o.StripRingTargetMs);
        Assert.Equal(20, o.BusRingTargetMs);
        Assert.Equal(15, o.RenderBufferMs);
        Assert.Null(o.ExclusiveBufferMs);

        o.Latency = LatencyPreset.Safe;
        Assert.Equal(40, o.StripRingTargetMs);
        Assert.Equal(50, o.BusRingTargetMs);
        Assert.Equal(40, o.RenderBufferMs);

        o.Advanced = new AdvancedEngineSettings();   // empty object behaves like null
        Assert.Equal(40, o.StripRingTargetMs);
        Assert.Equal("768000 Hz, 5 ms blocks (3840 frames), latency preset Safe, resampler Best", o.Describe());
    }

    [Fact]
    public void Overrides_replace_only_the_fields_they_set()
    {
        var o = new EngineOptions
        {
            SampleRate = 48000,
            Latency = LatencyPreset.Normal,
            Advanced = new AdvancedEngineSettings { BlockMs = 2, InputBufferMs = 8, ExclusiveBufferMs = 4 }
        };
        Assert.Equal(2, o.BlockMs);
        Assert.Equal(96, o.BlockFramesFor(48000));
        Assert.Equal(8, o.StripRingTargetMs);
        Assert.Equal(25, o.BusRingTargetMs);        // still the preset
        Assert.Equal(20, o.RenderBufferMs);         // still the preset
        Assert.Equal(4, o.ExclusiveBufferMs);
        Assert.Equal("48000 Hz, 2 ms blocks (96 frames), latency preset Normal, resampler Best, advanced: block 2 ms, input 8 ms, exclusive 4 ms", o.Describe());
    }

    [Fact]
    public void Values_outside_the_allowed_ranges_are_pulled_back()
    {
        var a = new AdvancedEngineSettings { BlockMs = 0.1, InputBufferMs = 10_000, OutputBufferMs = 0, SharedOutputBufferMs = 1, ExclusiveBufferMs = 0 };
        a.Normalize();
        Assert.Equal(AdvancedEngineSettings.MinBlockMs, a.BlockMs);
        Assert.Equal(AdvancedEngineSettings.MaxRingMs, a.InputBufferMs);
        Assert.Equal(AdvancedEngineSettings.MinRingMs, a.OutputBufferMs);
        Assert.Equal(AdvancedEngineSettings.MinSharedOutputMs, a.SharedOutputBufferMs);
        Assert.Equal(AdvancedEngineSettings.MinExclusiveMs, a.ExclusiveBufferMs);

        // The engine clamps too, in case a settings file skipped Normalize.
        var o = new EngineOptions { Advanced = new AdvancedEngineSettings { BlockMs = 500, InputBufferMs = -5 } };
        Assert.Equal(AdvancedEngineSettings.MaxBlockMs, o.BlockMs);
        Assert.Equal(AdvancedEngineSettings.MinRingMs, o.StripRingTargetMs);

        var nan = new AdvancedEngineSettings { BlockMs = double.NaN };
        nan.Normalize();
        Assert.Null(nan.BlockMs);
    }

    [Fact]
    public void Empty_overrides_trim_to_null_and_compare_equal()
    {
        Assert.Null(new AdvancedEngineSettings().Trimmed());
        Assert.True(AdvancedEngineSettings.Same(null, new AdvancedEngineSettings()));
        Assert.True(AdvancedEngineSettings.Same(null, null));

        var a = new AdvancedEngineSettings { BlockMs = 2 };
        var b = new AdvancedEngineSettings { BlockMs = 2 };
        Assert.True(AdvancedEngineSettings.Same(a, b));
        b.OutputBufferMs = 30;
        Assert.False(AdvancedEngineSettings.Same(a, b));
        Assert.NotNull(a.Trimmed());
        Assert.Equal("block 2 ms", a.Describe());
        Assert.Equal("block 2 ms, output 30 ms", b.Describe());
    }
}
