using MixerLive.Core.Import;
using Xunit;

namespace MixerLive.Tests;

public class VoiceEqImportTests
{
    /// <summary>A commented voice EQ (high-pass, peaks, high shelf) imports cell for cell.</summary>
    [Fact]
    public void Commented_voice_eq_file_imports_all_six_filters()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mixerlive-voice-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, """
            # Starting voice EQ
            # 1  High-pass 80 Hz    removes rumble
            Preamp: 0 dB
            Filter 1: ON HPQ Fc 80 Hz Q 0.71
            Filter 2: ON PK Fc 250 Hz Gain -3.0 dB Q 1.00
            Filter 3: ON PK Fc 500 Hz Gain -1.5 dB Q 1.40
            Filter 4: ON PK Fc 3500 Hz Gain 2.0 dB Q 1.00
            Filter 5: ON HSC Fc 10000 Hz Gain 1.5 dB Q 0.71
            Filter 6: ON PK Fc 6500 Hz Gain -2.0 dB Q 3.00
            """);
        try
        {
            var r = EqualizerApoImporter.ImportFile(path);
            Assert.Equal(6, r.Bands.Count);
            Assert.Equal(0, r.PreampDb, 3);
            Assert.Empty(r.Warnings);

            Assert.Equal(3, r.Bands[0].Type);          // high-pass
            Assert.Equal(80, r.Bands[0].Freq, 3);
            Assert.Equal(0.71, r.Bands[0].Q, 3);
            Assert.Equal(0, r.Bands[1].Type);          // peak
            Assert.Equal(-3.0, r.Bands[1].GainDb, 3);
            Assert.Equal(6, r.Bands[4].Type);          // high shelf
            Assert.Equal(10000, r.Bands[4].Freq, 3);
            Assert.Equal(1.5, r.Bands[4].GainDb, 3);
            Assert.Equal(3.0, r.Bands[5].Q, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
