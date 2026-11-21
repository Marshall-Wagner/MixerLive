using System.Globalization;

namespace MixerLive.Core.Audio;

/// <summary>
/// Converts fader gain (dB) to a "volume %" that follows perceived loudness, the way a Windows
/// volume slider feels: every 10 dB halves or doubles loudness, so 100 % = 0 dB, 50 % = -10 dB,
/// 25 % = -20 dB, 10 % ≈ -33 dB. Above 100 % is boost (+12 dB ≈ 230 %). The fader's -60 dB
/// floor means off and shows as 0 %.
/// </summary>
public static class VolumeScale
{
    public const double MinDb = -60;
    public const double MaxDb = 12;

    /// <summary>Wheel/arrow steps snap to the step grid; values this close (in steps) count as on the grid.</summary>
    private const double SnapTolerance = 0.25;

    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static double PercentFromDb(double db) => db <= MinDb ? 0 : 100 * Math.Pow(2, db / 10);

    /// <summary>0 % is off; any positive value stays audible (at least -59.9 dB ≈ 1.6 %) instead of muting.</summary>
    public static double DbFromPercent(double percent)
    {
        if (percent <= 0 || double.IsNaN(percent)) return MinDb;
        return Math.Clamp(10 * Math.Log2(percent / 100), MinDb + 0.1, MaxDb);
    }

    public static string FormatDb(double db) => db <= MinDb ? "-inf" : db.ToString("+0.0;-0.0;0.0", CI);

    public static string FormatPercent(double db)
    {
        double p = PercentFromDb(db);
        if (p <= 0) return "0";
        return p >= 9.95 ? p.ToString("0", CI) : p.ToString("0.0", CI);
    }

    /// <summary>Accepts "-6", "-6.5", "-6,5", "+3 dB", "-inf", "off".</summary>
    public static bool TryParseDb(string? text, out double db)
    {
        db = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().ToLowerInvariant().Replace(',', '.');
        if (t.EndsWith("db", StringComparison.Ordinal)) t = t[..^2].Trim();
        if (t is "-inf" or "inf" or "-∞" or "∞" or "off" or "mute")
        {
            db = MinDb;
            return true;
        }
        if (!double.TryParse(t, NumberStyles.Float, CI, out db) || double.IsNaN(db)) return false;
        db = Math.Clamp(db, MinDb, MaxDb);
        return true;
    }

    /// <summary>Accepts "50", "50 %", "12,5", "0".</summary>
    public static bool TryParsePercent(string? text, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().Replace(',', '.').TrimEnd('%').Trim();
        if (!double.TryParse(t, NumberStyles.Float, CI, out percent) || double.IsNaN(percent)) return false;
        percent = Math.Max(0, percent);
        return true;
    }

    /// <summary>One wheel notch in dB: snaps to the step grid (-12.35 up by 1 → -12), clamped to the fader range.</summary>
    public static double StepDb(double db, int direction, double step)
    {
        if (direction == 0) return db;
        double units = db / step;
        double next = direction > 0
            ? (Math.Floor(units + SnapTolerance) + 1) * step
            : (Math.Ceiling(units - SnapTolerance) - 1) * step;
        return Math.Clamp(Math.Round(next, 2), MinDb, MaxDb);
    }

    /// <summary>One wheel notch on a plain linear scale (device volume 0–100 %), snapping to the step grid.</summary>
    public static double StepLinear(double value, int direction, double step, double min, double max)
    {
        if (direction == 0) return value;
        double units = value / step;
        double next = direction > 0
            ? (Math.Floor(units + SnapTolerance) + 1) * step
            : (Math.Ceiling(units - SnapTolerance) - 1) * step;
        return Math.Clamp(Math.Round(next, 2), min, max);
    }

    /// <summary>One wheel notch in %: snaps to the step grid (33 % up by 2 → 34 %), returns the new gain in dB.</summary>
    public static double StepPercent(double db, int direction, double step)
    {
        if (direction == 0) return db;
        double units = PercentFromDb(db) / step;
        double next = direction > 0
            ? (Math.Floor(units + SnapTolerance) + 1) * step
            : (Math.Ceiling(units - SnapTolerance) - 1) * step;
        return Math.Round(DbFromPercent(Math.Max(0, next)), 2);
    }
}
