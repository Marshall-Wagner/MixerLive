using MixerLive.Core.Audio;
using MixerLive.Core.Import;
using MixerLive.Core.Model;
using Xunit;

namespace MixerLive.Tests;

public class EqualizerApoImporterTests
{
    // Verbatim PEQdB export (Audio-Technica ATH-M50X on a FiiO KA15).
    private const string Peqdb = """
        Preamp: -10.4 dB
        Filter 1: ON LSC Fc 137 Hz Gain -6.8 dB Q 0.70
        Filter 2: ON LSC Fc 145 Hz Gain 10.1 dB Q 0.60
        Filter 3: ON PK Fc 159 Hz Gain -5.3 dB Q 1.11
        Filter 4: ON PK Fc 332 Hz Gain 3.3 dB Q 2.09
        Filter 5: ON PK Fc 561 Hz Gain -0.7 dB Q 1.78
        Filter 6: ON PK Fc 788 Hz Gain -1.8 dB Q 1.05
        Filter 7: ON PK Fc 1893 Hz Gain 1.6 dB Q 1.43
        Filter 8: ON PK Fc 2700 Hz Gain -7.6 dB Q 1.20
        Filter 9: ON PK Fc 3139 Hz Gain 5.8 dB Q 1.64
        Filter 10: ON HSC Fc 4000 Hz Gain 5.2 dB Q 0.70
        Filter 11: ON PK Fc 4062 Hz Gain -4.0 dB Q 3.57
        Filter 12: ON PK Fc 6274 Hz Gain -3.9 dB Q 1.98
        Filter 13: ON PK Fc 7500 Hz Gain 6.7 dB Q 1.33
        Filter 14: ON HSC Fc 10000 Hz Gain 4.8 dB Q 0.70
        Filter 15: ON HSC Fc 13500 Hz Gain -5.2 dB Q 0.71
        """;

    [Fact]
    public void Parses_peqdb_export()
    {
        var r = EqualizerApoImporter.Parse(Peqdb);

        Assert.Equal(-10.4, r.PreampDb, 3);
        Assert.Equal(15, r.Bands.Count);
        Assert.Empty(r.Warnings);

        var f1 = r.Bands[0];
        Assert.True(f1.On);
        Assert.Equal((int)FilterType_LowShelf, f1.Type);
        Assert.Equal(137, f1.Freq);
        Assert.Equal(-6.8, f1.GainDb, 3);
        Assert.Equal(0.70, f1.Q, 3);

        var f3 = r.Bands[2];
        Assert.Equal(0, f3.Type); // peak
        Assert.Equal(159, f3.Freq);
        Assert.Equal(-5.3, f3.GainDb, 3);
        Assert.Equal(1.11, f3.Q, 3);

        var f15 = r.Bands[14];
        Assert.Equal(6, f15.Type); // high shelf
        Assert.Equal(13500, f15.Freq);
        Assert.Equal(0.71, f15.Q, 3);
    }

    private const int FilterType_LowShelf = 5;

    [Fact]
    public void Handles_comments_off_filters_bandwidth_and_unsupported_lines()
    {
        var r = EqualizerApoImporter.Parse("""
            # headphone correction
            Preamp: -2 dB
            Preamp: -1.5 dB
            Channel: L
            Filter 1: OFF PK Fc 1000 Hz Gain 3 dB Q 2
            Filter 2: ON HP Fc 80 Hz
            Filter 3: ON PK Fc 2 kHz Gain -2 dB BW Oct 1
            Filter 4: ON AP Fc 500 Hz Q 1
            Filter 5: ON LS Fc 100 Hz Gain 2 dB
            GraphicEQ: 20 0; 20000 0
            """);

        Assert.Equal(-3.5, r.PreampDb, 3);
        Assert.Equal(4, r.Bands.Count);

        Assert.False(r.Bands[0].On);
        Assert.Equal(3, r.Bands[1].Type);                 // high-pass
        Assert.Equal(0.707, r.Bands[1].Q, 3);
        Assert.Equal(2000, r.Bands[2].Freq);              // kHz handled
        Assert.Equal(Math.Sqrt(2) / (2 - 1), r.Bands[2].Q, 3); // BW 1 oct → Q ≈ 1.414
        Assert.Equal(5, r.Bands[3].Type);                 // LS without Q
        Assert.Equal(0.707, r.Bands[3].Q, 3);

        Assert.Contains(r.Warnings, w => w.Contains("All-pass"));
        Assert.Contains(r.Warnings, w => w.Contains("Channel:"));
        Assert.Contains(r.Warnings, w => w.Contains("GraphicEQ:"));
    }

    [Fact]
    public void Reads_rew_filter_export_quietly()
    {
        // Room EQ Wizard "Save filter settings as text": header lines, padded columns, unused "None" slots.
        var r = EqualizerApoImporter.Parse("""
            Filter Settings file

            Room EQ V5.31
            Dated: 18-Nov-2026 07:30:00

            Notes:

            Equaliser: Generic
            Nov 18 07:30:00
            Filter  1: ON  PK       Fc   44.60 Hz  Gain -12.30 dB  Q  4.570
            Filter  2: ON  LS       Fc  105.0 Hz  Gain   3.50 dB  Q  0.700
            Filter  3: ON  None
            Filter  4: OFF None
            """);

        Assert.Equal(2, r.Bands.Count);
        Assert.Equal(44.6, r.Bands[0].Freq, 3);
        Assert.Equal(-12.3, r.Bands[0].GainDb, 3);
        Assert.Equal(4.57, r.Bands[0].Q, 3);
        Assert.Equal(5, r.Bands[1].Type); // low shelf
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Could not read"));
        var note = Assert.Single(r.Warnings);
        Assert.Contains("not EQ filters ignored", note);
    }

    [Fact]
    public void Follows_include_lines_relative_to_the_including_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mixerlive-eapo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.txt"), "Preamp: -1 dB\nInclude: sub\\eq.txt\n");
            File.WriteAllText(Path.Combine(dir, "sub", "eq.txt"), "Preamp: -2 dB\nFilter 1: ON PK Fc 100 Hz Gain 1 dB Q 1\nInclude: missing.txt\n");

            var r = EqualizerApoImporter.ImportFile(Path.Combine(dir, "config.txt"));
            Assert.Equal(-3, r.PreampDb, 3);
            Assert.Single(r.Bands);
            Assert.Equal(2, r.Files.Count);
            Assert.Contains(r.Warnings, w => w.Contains("not found"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Imported_bands_fit_into_a_bus_and_pad_to_the_minimum()
    {
        var r = EqualizerApoImporter.Parse("Filter 1: ON PK Fc 100 Hz Gain 1 dB Q 1\n");
        var cells = BusSettings.PadCells(r.Bands);
        Assert.Equal(MixerProfile.EqCellCount, cells.Count);
        Assert.Equal(100, cells[0].Freq);
        Assert.True(cells[5].IsEffectivelyFlat);

        var big = EqualizerApoImporter.Parse(string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Filter {i}: ON PK Fc {100 + i} Hz Gain 1 dB Q 1")));
        Assert.Equal(MixerProfile.MaxEqCells, big.Bands.Count);
        Assert.Contains(big.Warnings, w => w.Contains("first 32"));
    }
}

public class WaveFormatsTests
{
    [Fact]
    public void Pcm_extensible_formats_are_laid_out_correctly()
    {
        var f = WaveFormats.PcmExtensible(768000, 2, 32, 32);
        Assert.Equal(768000, f.SampleRate);
        Assert.Equal(2, f.Channels);
        Assert.Equal(32, f.BitsPerSample);
        Assert.Equal(8, f.BlockAlign);
        Assert.Equal(768000 * 8, f.AverageBytesPerSecond);
        var ext = Assert.IsType<NAudio.Wave.WaveFormatExtensible>(f);
        Assert.Equal(WaveFormats.SubtypePcm, ext.SubFormat);
        Assert.False(WaveFormats.IsFloat(f));
        Assert.Equal(SampleKind.Pcm32, SampleConvert.Detect(f));
        Assert.Equal("32-bit PCM 768 kHz", WaveFormats.Describe(f, 32));

        var f24 = WaveFormats.PcmExtensible(192000, 2, 32, 24);
        Assert.Equal("24-in-32-bit PCM 192 kHz", WaveFormats.Describe(f24, 24));
        Assert.Equal(SampleKind.Pcm32, SampleConvert.Detect(f24));

        var f441 = WaveFormats.PcmExtensible(44100, 2, 16, 16);
        Assert.Equal("16-bit PCM 44.1 kHz", WaveFormats.Describe(f441, 16));
        Assert.Equal(SampleKind.Pcm16, SampleConvert.Detect(f441));
    }
}
