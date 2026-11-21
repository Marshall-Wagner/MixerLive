using System.Globalization;
using MixerLive.Core.Audio.Dsp;

namespace MixerLive.Core.Audio;

/// <summary>
/// Meter ballistics for one stereo strip or bus, fed about 30 times a second with the block peaks
/// the engine collected since the previous update: a falling bar level, a peak hold per side and the
/// loudest recent peak for a numeric readout. Also defines the meter's scale and colour zones,
/// which follow OBS Studio's audio mixer (-60 … 0 dBFS, yellow from -20, red from -9).
/// </summary>
public sealed class PeakMeter
{
    /// <summary>Lowest level shown. Everything quieter (and silence) reads as this.</summary>
    public const double FloorDb = -60;

    /// <summary>Yellow ("getting loud") from this level up, as in OBS.</summary>
    public const double WarningDb = -20;

    /// <summary>Red ("close to clipping") from this level up, as in OBS.</summary>
    public const double ErrorDb = -9;

    /// <summary>Spacing of the scale labels (0, -6, … -60).</summary>
    public const double ScaleStepDb = 6;

    /// <summary>Bar fall-back per update (≈ 42 dB/s at the 30 Hz UI tick).</summary>
    public const double FallDbPerUpdate = 1.4;

    /// <summary>Updates a peak is held before it drops to the current level (≈ 2 s at 30 Hz).</summary>
    public const int HoldUpdates = 60;

    private int _ageL, _ageR;

    public double LevelL { get; private set; } = FloorDb;
    public double LevelR { get; private set; } = FloorDb;
    public double HoldL { get; private set; } = FloorDb;
    public double HoldR { get; private set; } = FloorDb;

    /// <summary>Loudest peak of either side within the hold time.</summary>
    public double Peak => Math.Max(HoldL, HoldR);

    /// <summary>True when the held peak reads 0.0 dBFS or more: at or over the digital maximum.</summary>
    public bool IsClipping => Peak >= -0.05;

    /// <param name="linL">Largest absolute left sample since the last update (1.0 = 0 dBFS).</param>
    /// <param name="linR">Same for the right side.</param>
    public void Update(float linL, float linR)
    {
        double dl = ToDb(linL), dr = ToDb(linR);
        LevelL = Math.Max(dl, LevelL - FallDbPerUpdate);
        LevelR = Math.Max(dr, LevelR - FallDbPerUpdate);
        HoldL = NextHold(HoldL, dl, ref _ageL);
        HoldR = NextHold(HoldR, dr, ref _ageR);
    }

    /// <summary>Height of a level on the meter: 0 at <see cref="FloorDb"/> (and for silence or NaN), 1 at 0 dBFS and above.</summary>
    public static double Fraction(double db) => db > FloorDb ? Math.Min(1, (db - FloorDb) / -FloorDb) : 0;

    /// <summary>"-12.3", "+1.5", "0.0", or "-inf" for silence.</summary>
    public static string Format(double db)
        => db <= FloorDb ? "-inf" : db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

    private static double ToDb(float lin) => Math.Max(FloorDb, DspUtil.LinToDb(lin));

    private static double NextHold(double hold, double now, ref int age)
    {
        if (now >= hold || ++age > HoldUpdates)
        {
            age = 0;
            return now;
        }
        return hold;
    }
}
