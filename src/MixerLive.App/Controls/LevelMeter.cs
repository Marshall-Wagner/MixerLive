using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MixerLive.Core.Audio;

namespace MixerLive.App.Controls;

/// <summary>
/// Two-channel peak meter in the style of OBS Studio's audio mixer: a bar per channel over a dark
/// green / yellow / red background (yellow from -20 dB, red from -9 dB), the lit part in bright
/// colours up to the current level, a thin peak-hold line, and the dB scale (0, -6 … -60) on the
/// right. Everything is laid out in whole device pixels so it stays sharp at any scaling.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    private const double BarWidth = 6;      // per channel, DIPs
    private const double BarGap = 2;        // between the two bars
    private const double LabelGap = 4;      // bars → labels
    private const double LabelSize = 8.5;
    private const double PadY = 4;          // keeps the "0" and "-60" labels inside the control
    private const double HoldLine = 2;

    public static readonly DependencyProperty LeftDbProperty = Register(nameof(LeftDb));
    public static readonly DependencyProperty RightDbProperty = Register(nameof(RightDb));
    public static readonly DependencyProperty LeftHoldDbProperty = Register(nameof(LeftHoldDb));
    public static readonly DependencyProperty RightHoldDbProperty = Register(nameof(RightHoldDb));

    // Zone colours (0 = green, 1 = yellow, 2 = red): dark background and bright level, as in OBS.
    private static readonly Brush[] Background =
    [
        Solid(0x24, 0x6B, 0x2A),
        Solid(0x6E, 0x6A, 0x24),
        Solid(0x6E, 0x26, 0x26)
    ];
    private static readonly Brush[] Foreground =
    [
        Solid(0x4C, 0xE6, 0x55),
        Solid(0xF2, 0xE2, 0x4C),
        Solid(0xFF, 0x4C, 0x4C)
    ];
    private static readonly Brush LabelBrush = Solid(0xB4, 0xB9, 0xC2);
    private static readonly Typeface LabelFace = new("Segoe UI");

    private FormattedText[]? _labels;
    private double _labelsPixelsPerDip;

    /// <summary>Current left level in dBFS (<see cref="PeakMeter.FloorDb"/> = silence).</summary>
    public double LeftDb { get => (double)GetValue(LeftDbProperty); set => SetValue(LeftDbProperty, value); }
    public double RightDb { get => (double)GetValue(RightDbProperty); set => SetValue(RightDbProperty, value); }

    /// <summary>Held peak in dBFS, drawn as a thin line above the bar.</summary>
    public double LeftHoldDb { get => (double)GetValue(LeftHoldDbProperty); set => SetValue(LeftHoldDbProperty, value); }
    public double RightHoldDb { get => (double)GetValue(RightHoldDbProperty); set => SetValue(RightHoldDbProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

        // Horizontal layout in device pixels: [L bar][gap][R bar][gap][labels]
        int barPx = Math.Max(1, (int)Math.Round(BarWidth * sx));
        int gapPx = Math.Max(1, (int)Math.Round(BarGap * sx));
        double labelX = (2 * barPx + gapPx) / sx + LabelGap;

        // Vertical: 0 dB at the top of the bars, the floor (-60) at the bottom.
        int topPx = (int)Math.Round(PadY * sy);
        int bottomPx = (int)Math.Floor(h * sy) - (int)Math.Round(PadY * sy);
        int heightPx = bottomPx - topPx;
        if (heightPx < 10) return;
        int Y(double db) => bottomPx - (int)Math.Round(PeakMeter.Fraction(db) * heightPx);

        int yRed = Y(PeakMeter.ErrorDb), yYellow = Y(PeakMeter.WarningDb);
        int[] zoneTop = [yYellow, yRed, topPx];          // green, yellow, red
        int[] zoneBottom = [bottomPx, yYellow, yRed];
        int holdPx = Math.Max(1, (int)Math.Round(HoldLine * sy));

        DrawChannel(0, LeftDb, LeftHoldDb);
        DrawChannel(barPx + gapPx, RightDb, RightHoldDb);

        void DrawChannel(int xPx, double level, double hold)
        {
            double x = xPx / sx, bw = barPx / sx;
            int yLevel = Y(level);
            for (int z = 0; z < 3; z++)
            {
                Fill(Background[z], x, bw, zoneTop[z], zoneBottom[z]);
                Fill(Foreground[z], x, bw, Math.Max(zoneTop[z], yLevel), zoneBottom[z]);
            }
            if (hold > PeakMeter.FloorDb)
            {
                int yHold = Y(hold);
                int zone = hold >= PeakMeter.ErrorDb ? 2 : hold >= PeakMeter.WarningDb ? 1 : 0;
                Fill(Foreground[zone], x, bw, yHold, Math.Min(bottomPx, yHold + holdPx));
            }
        }

        void Fill(Brush brush, double x, double width, int yTopPx, int yBottomPx)
        {
            if (yBottomPx <= yTopPx) return;
            dc.DrawRectangle(brush, null, new Rect(x, yTopPx / sy, width, (yBottomPx - yTopPx) / sy));
        }

        // Scale: a label every 6 dB.
        var labels = Labels(dpi.PixelsPerDip);
        for (int i = 0; i < labels.Length; i++)
        {
            int y = Y(-i * PeakMeter.ScaleStepDb);
            var text = labels[i];
            double ty = Math.Round((y / sy - text.Height / 2) * sy) / sy;
            dc.DrawText(text, new Point(Math.Round(labelX * sx) / sx, ty));
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _labels = null;
        InvalidateVisual();
    }

    /// <summary>"0", "-6" … "-60", rebuilt only when the DPI changes.</summary>
    private FormattedText[] Labels(double pixelsPerDip)
    {
        if (_labels != null && _labelsPixelsPerDip == pixelsPerDip) return _labels;
        int count = (int)Math.Round(-PeakMeter.FloorDb / PeakMeter.ScaleStepDb) + 1;
        var labels = new FormattedText[count];
        for (int i = 0; i < count; i++)
        {
            labels[i] = new FormattedText(
                (-i * PeakMeter.ScaleStepDb).ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, LabelSize, LabelBrush, null, TextFormattingMode.Display, pixelsPerDip);
        }
        _labels = labels;
        _labelsPixelsPerDip = pixelsPerDip;
        return labels;
    }

    private static DependencyProperty Register(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(PeakMeter.FloorDb, FrameworkPropertyMetadataOptions.AffectsRender));

    private static Brush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
