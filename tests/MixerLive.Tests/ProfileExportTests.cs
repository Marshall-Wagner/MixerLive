using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class ProfileExportTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"mixerlive-export-{Guid.NewGuid():N}.json");

    [Fact]
    public void Exported_profile_carries_engine_settings()
    {
        var profile = MixerProfile.CreateDefault();
        profile.Buses[0].Exclusive = true;
        profile.Buses[0].EqPreampDb = -10.4;
        profile.Engine = EngineSettings.From(new AppSettings
        {
            SampleRate = 768000,
            Latency = LatencyPreset.Safe,
            Resampler = ResampleQuality.Best,
            Locked = true
        });

        string path = TempFile();
        try
        {
            ProfileStore.SaveProfile(profile, path);
            string json = File.ReadAllText(path);
            Assert.Contains("\"SampleRate\": 768000", json);
            Assert.Contains("\"Latency\": \"Safe\"", json);
            Assert.Contains("\"Resampler\": \"Best\"", json);
            Assert.DoesNotContain("Locked", json);           // per-PC preference stays out

            var loaded = ProfileStore.LoadProfile(path);
            Assert.NotNull(loaded);
            Assert.NotNull(loaded!.Engine);
            Assert.Equal(768000, loaded.Engine!.SampleRate);
            Assert.Equal(LatencyPreset.Safe, loaded.Engine.Latency);
            Assert.Equal(ResampleQuality.Best, loaded.Engine.Resampler);
            Assert.True(loaded.Buses[0].Exclusive);
            Assert.Equal(-10.4, loaded.Buses[0].EqPreampDb, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Exported_profile_carries_advanced_overrides()
    {
        var profile = MixerProfile.CreateDefault();
        profile.Engine = EngineSettings.From(new AppSettings
        {
            SampleRate = 48000,
            Advanced = new AdvancedEngineSettings { BlockMs = 2, ExclusiveBufferMs = 4 }
        });
        Assert.Equal("Rate 48 kHz · Latency Normal · Resampler Best · Advanced: block 2 ms, exclusive 4 ms", profile.Engine.Describe());

        string path = TempFile();
        try
        {
            ProfileStore.SaveProfile(profile, path);
            Assert.Contains("\"BlockMs\": 2", File.ReadAllText(path));

            var loaded = ProfileStore.LoadProfile(path)!;
            Assert.Equal(2, loaded.Engine!.Advanced!.BlockMs);
            Assert.Equal(4, loaded.Engine.Advanced.ExclusiveBufferMs);
            Assert.Null(loaded.Engine.Advanced.InputBufferMs);

            Assert.False(loaded.Engine.Matches(new AppSettings { SampleRate = 48000 }));
            Assert.True(loaded.Engine.Matches(new AppSettings { SampleRate = 48000, Advanced = new AdvancedEngineSettings { BlockMs = 2, ExclusiveBufferMs = 4 } }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Profiles_without_engine_section_still_load()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, """
                { "Name": "old", "Strips": [ { "Label": "MIC", "GainDb": -3 } ], "Buses": [ { "Label": "PHONES" } ] }
                """);
            var loaded = ProfileStore.LoadProfile(path);
            Assert.NotNull(loaded);
            Assert.Null(loaded!.Engine);
            Assert.Equal("MIC", loaded.Strips[0].Label);
            Assert.Equal(MixerProfile.StripCount, loaded.Strips.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Engine_settings_compare_and_describe()
    {
        var current = new AppSettings { SampleRate = 768000, Latency = LatencyPreset.Normal, Resampler = ResampleQuality.Best };
        var same = EngineSettings.From(current);
        Assert.True(same.Matches(current));
        Assert.Equal("Rate 768 kHz · Latency Normal · Resampler Best", same.Describe());

        var other = new EngineSettings { SampleRate = 48000, Latency = LatencyPreset.Normal, Resampler = ResampleQuality.Best };
        Assert.False(other.Matches(current));
        Assert.Equal("Rate 48 kHz · Latency Normal · Resampler Best", other.Describe());

        // Changing only the lock does not count as an engine difference.
        current.Locked = true;
        Assert.True(same.Matches(current));
    }
}
