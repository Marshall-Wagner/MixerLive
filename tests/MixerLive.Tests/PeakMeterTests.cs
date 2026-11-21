using MixerLive.Core.Audio;
using Xunit;

namespace MixerLive.Tests;

public class PeakMeterTests
{
    [Fact]
    public void Scale_and_zones_follow_obs()
    {
        Assert.Equal(-60, PeakMeter.FloorDb);
        Assert.Equal(-20, PeakMeter.WarningDb);
        Assert.Equal(-9, PeakMeter.ErrorDb);
        Assert.Equal(0, -PeakMeter.FloorDb % PeakMeter.ScaleStepDb);   // labels land on 0 and on the floor
    }

    [Theory]
    [InlineData(-60.0, 0.0)]     // silence: empty bar
    [InlineData(-120.0, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(-30.0, 0.5)]
    [InlineData(-9.0, 0.85)]
    [InlineData(0.0, 1.0)]       // full bar at 0 dBFS
    [InlineData(4.0, 1.0)]       // over 0 dBFS stays full (the readout turns red)
    public void Bar_height_is_linear_in_dB(double db, double fraction)
        => Assert.Equal(fraction, PeakMeter.Fraction(db), 6);

    [Fact]
    public void Peak_is_held_for_two_seconds_then_drops()
    {
        var m = new PeakMeter();
        m.Update(0.5f, 0.25f);   // -6.02 / -12.04 dBFS
        Assert.Equal(-6.02, m.HoldL, 2);
        Assert.Equal(-12.04, m.HoldR, 2);
        Assert.Equal("-6.0", PeakMeter.Format(m.Peak));

        for (int i = 0; i < PeakMeter.HoldUpdates; i++) m.Update(0, 0);
        Assert.Equal(-6.02, m.HoldL, 2);   // still held after 60 silent updates (≈ 2 s)

        m.Update(0, 0);
        Assert.Equal(PeakMeter.FloorDb, m.HoldL);
        Assert.Equal(PeakMeter.FloorDb, m.HoldR);
        Assert.Equal("-inf", PeakMeter.Format(m.Peak));
    }

    [Fact]
    public void A_louder_peak_replaces_the_hold_immediately()
    {
        var m = new PeakMeter();
        m.Update(0.1f, 0.1f);    // -20 dB
        m.Update(0.5f, 0.1f);    // -6 dB on the left
        Assert.Equal(-6.02, m.HoldL, 2);
        Assert.Equal(-20.0, m.HoldR, 2);
    }

    [Fact]
    public void Bar_falls_back_at_the_fall_rate()
    {
        var m = new PeakMeter();
        m.Update(1f, 1f);
        Assert.Equal(0, m.LevelL, 6);
        m.Update(0, 0);
        Assert.Equal(-PeakMeter.FallDbPerUpdate, m.LevelL, 6);
        for (int i = 0; i < 100; i++) m.Update(0, 0);
        Assert.Equal(PeakMeter.FloorDb, m.LevelL);
    }

    [Fact]
    public void Clipping_means_a_held_peak_of_0_dBFS_or_more()
    {
        var m = new PeakMeter();
        m.Update(0.99f, 0.5f);   // -0.09 dBFS
        Assert.False(m.IsClipping);
        m.Update(1.2f, 0.5f);    // +1.6 dBFS (floating point can exceed full scale)
        Assert.True(m.IsClipping);
        Assert.Equal("+1.6", PeakMeter.Format(m.Peak));
    }

    [Theory]
    [InlineData(-12.34, "-12.3")]
    [InlineData(1.5, "+1.5")]
    [InlineData(-0.04, "0.0")]
    [InlineData(0.0, "0.0")]
    [InlineData(-60.0, "-inf")]
    public void Readout_format(double db, string text) => Assert.Equal(text, PeakMeter.Format(db));
}
