using MixerLive.Core.Audio;
using Xunit;

namespace MixerLive.Tests;

public class VolumeScaleTests
{
    [Theory]
    [InlineData(0, "100")]
    [InlineData(-10, "50")]
    [InlineData(-20, "25")]
    [InlineData(-33.22, "10")]
    [InlineData(12, "230")]
    [InlineData(-40, "6.3")]
    [InlineData(-60, "0")]
    public void Percent_follows_perceived_loudness(double db, string expected)
    {
        Assert.Equal(expected, VolumeScale.FormatPercent(db));
    }

    [Theory]
    [InlineData("50", -10)]
    [InlineData("50 %", -10)]
    [InlineData("100", 0)]
    [InlineData("25%", -20)]
    [InlineData("0", -60)]
    [InlineData("-5", -60)]
    [InlineData("1000", 12)]
    [InlineData("12,5", -30)]
    public void Typed_percent_maps_to_gain(string text, double expectedDb)
    {
        Assert.True(VolumeScale.TryParsePercent(text, out double p));
        Assert.Equal(expectedDb, VolumeScale.DbFromPercent(p), 2);
    }

    [Theory]
    [InlineData("-6", -6)]
    [InlineData("-6,5", -6.5)]
    [InlineData("+3 dB", 3)]
    [InlineData("-inf", -60)]
    [InlineData("off", -60)]
    [InlineData("40", 12)]
    [InlineData("-100", -60)]
    public void Typed_db_is_parsed_and_clamped(string text, double expected)
    {
        Assert.True(VolumeScale.TryParseDb(text, out double db));
        Assert.Equal(expected, db, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("  ")]
    public void Garbage_is_rejected(string text)
    {
        Assert.False(VolumeScale.TryParseDb(text, out _));
        Assert.False(VolumeScale.TryParsePercent(text, out _));
    }

    [Fact]
    public void Typed_percent_round_trips_through_the_stored_gain()
    {
        // The view model rounds the gain to 0.01 dB; the box must show what was typed.
        Assert.Equal("1.6", VolumeScale.FormatPercent(VolumeScale.DbFromPercent(1))); // quiet, not muted

        for (int p = 2; p <= 230; p++)
        {
            double db = Math.Round(VolumeScale.DbFromPercent(p), 2);
            string shown = VolumeScale.FormatPercent(db);
            double back = double.Parse(shown, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(Math.Abs(back - p) <= (p < 10 ? 0.05 : 0.5), $"{p} % → {db} dB → {shown} %");
        }
    }

    [Fact]
    public void Wheel_steps_in_percent_snap_to_an_even_grid()
    {
        double db = 0;                                   // 100 %
        db = VolumeScale.StepPercent(db, +1, 2);
        Assert.Equal("102", VolumeScale.FormatPercent(db));
        db = VolumeScale.StepPercent(db, -1, 2);
        Assert.Equal("100", VolumeScale.FormatPercent(db));
        db = VolumeScale.StepPercent(db, -1, 2);
        Assert.Equal("98", VolumeScale.FormatPercent(db));

        db = VolumeScale.DbFromPercent(33.1);
        Assert.Equal("34", VolumeScale.FormatPercent(VolumeScale.StepPercent(db, +1, 2)));
        Assert.Equal("32", VolumeScale.FormatPercent(VolumeScale.StepPercent(db, -1, 2)));

        // Down to off and back up.
        db = VolumeScale.DbFromPercent(2);
        db = VolumeScale.StepPercent(db, -1, 2);
        Assert.Equal(-60, db);
        db = VolumeScale.StepPercent(db, +1, 2);
        Assert.Equal("2.0", VolumeScale.FormatPercent(db));

        // Every notch from 0 to the top changes the displayed value.
        db = -60;
        string last = VolumeScale.FormatPercent(db);
        for (int i = 0; i < 200 && db < 12; i++)
        {
            db = VolumeScale.StepPercent(db, +1, 2);
            string now = VolumeScale.FormatPercent(db);
            Assert.NotEqual(last, now);
            last = now;
        }
        Assert.Equal(12, db);
    }

    [Fact]
    public void Device_volume_steps_are_linear_and_clamped()
    {
        Assert.Equal(22, VolumeScale.StepLinear(20, +1, 2, 0, 100));
        Assert.Equal(18, VolumeScale.StepLinear(20, -1, 2, 0, 100));
        Assert.Equal(22, VolumeScale.StepLinear(20.3, +1, 2, 0, 100));   // snaps to the even grid
        Assert.Equal(20.5, VolumeScale.StepLinear(20, +1, 0.5, 0, 100));
        Assert.Equal(100, VolumeScale.StepLinear(99.5, +1, 2, 0, 100));
        Assert.Equal(0, VolumeScale.StepLinear(1, -1, 2, 0, 100));
    }

    [Fact]
    public void Wheel_steps_in_db_snap_and_clamp()
    {
        Assert.Equal(-12, VolumeScale.StepDb(-12.35, +1, 1));
        Assert.Equal(-13, VolumeScale.StepDb(-12.35, -1, 1));
        Assert.Equal(1, VolumeScale.StepDb(0, +1, 1));
        Assert.Equal(-59, VolumeScale.StepDb(-60, +1, 1));
        Assert.Equal(-60, VolumeScale.StepDb(-60, -1, 1));
        Assert.Equal(12, VolumeScale.StepDb(11.5, +1, 1));
        Assert.Equal(-0.1, VolumeScale.StepDb(0, -1, 0.1), 3);
        Assert.Equal(0.1, VolumeScale.StepDb(0.004, +1, 0.1), 3);
    }
}
