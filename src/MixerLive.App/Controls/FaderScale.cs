using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace MixerLive.App.Controls;

/// <summary>
/// dB numbers beside a vertical <see cref="Slider"/>: "+12", "0", "-12" … every <see cref="Step"/> dB,
/// right-aligned and lined up with where the slider's thumb centre sits for that value (the same
/// geometry WPF's own tick marks use), so they stay correct whatever the slider's height or thumb size.
/// </summary>
public sealed class FaderScale : FrameworkElement
{
    private const double FontSize = 8.5;

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(Slider), typeof(FaderScale),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTargetChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(FaderScale),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x90, 0x96, 0xA0)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private bool _retryQueued;

    public FaderScale()
    {
        IsHitTestVisible = false;
    }

    /// <summary>The slider to label.</summary>
    public Slider? Target { get => (Slider?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <summary>Spacing of the labels in slider units (dB).</summary>
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var scale = (FaderScale)d;
        if (e.OldValue is Slider old)
        {
            old.SizeChanged -= scale.OnTargetLayoutChanged;
            old.Loaded -= scale.OnTargetLayoutChanged;
        }
        if (e.NewValue is Slider slider)
        {
            slider.SizeChanged += scale.OnTargetLayoutChanged;
            slider.Loaded += scale.OnTargetLayoutChanged;
        }
    }

    private void OnTargetLayoutChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var slider = Target;
        double w = ActualWidth;
        if (slider == null || w <= 0 || ActualHeight <= 0 || Step <= 0) return;

        // The thumb's centre travels from half its height below the track's top to half above its bottom.
        var track = slider.Template?.FindName("PART_Track", slider) as Track;
        var thumb = track?.Thumb;
        if (track == null || thumb == null || thumb.ActualHeight <= 0 || track.ActualHeight <= 0)
        {
            Retry();   // the slider has not been laid out yet; draw once it has
            return;
        }

        Point trackTop;
        try
        {
            trackTop = track.TransformToVisual(this).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            Retry();   // not in the same visual tree yet
            return;
        }

        double range = slider.Maximum - slider.Minimum;
        if (range <= 0) return;
        double travel = track.ActualHeight - thumb.ActualHeight;
        double y0 = trackTop.Y + thumb.ActualHeight / 2;

        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double first = Math.Ceiling(slider.Minimum / Step) * Step;
        for (double v = first; v <= slider.Maximum + 1e-9; v += Step)
        {
            string label = v > 0 ? "+" + v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, FontSize, LabelBrush, null, TextFormattingMode.Display, dpi.PixelsPerDip);
            double y = y0 + (slider.Maximum - v) / range * travel;
            double tx = Math.Round((w - text.Width) * sx) / sx;
            double ty = Math.Round((y - text.Height / 2) * sy) / sy;
            dc.DrawText(text, new Point(tx, ty));
        }
    }

    private void Retry()
    {
        if (_retryQueued) return;
        _retryQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _retryQueued = false;
            InvalidateVisual();
        }));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
