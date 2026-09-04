using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ModularCoop.App.Controls;

/// <summary>
/// A history line with the session's normal range shaded behind it. Deliberately hand-drawn from a Polyline and a
/// Rectangle rather than a charting library: the app has no chart dependency, and CI cannot build this project
/// anyway, so adding one would be a package nobody can verify.
///
/// Colours come from the theme's brushes, never from hardcoded hex — the Compat column shipped broken once for
/// exactly that reason.
/// </summary>
public partial class Sparkline : UserControl
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new PropertyMetadata(null, (d, _) => ((Sparkline)d).Redraw()));

    public static readonly DependencyProperty BandLowProperty = DependencyProperty.Register(
        nameof(BandLow), typeof(double), typeof(Sparkline), new PropertyMetadata(0d, (d, _) => ((Sparkline)d).Redraw()));

    public static readonly DependencyProperty BandHighProperty = DependencyProperty.Register(
        nameof(BandHigh), typeof(double), typeof(Sparkline), new PropertyMetadata(0d, (d, _) => ((Sparkline)d).Redraw()));

    public static readonly DependencyProperty HasBandProperty = DependencyProperty.Register(
        nameof(HasBand), typeof(bool), typeof(Sparkline), new PropertyMetadata(false, (d, _) => ((Sparkline)d).Redraw()));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }
    public double BandLow { get => (double)GetValue(BandLowProperty); set => SetValue(BandLowProperty, value); }
    public double BandHigh { get => (double)GetValue(BandHighProperty); set => SetValue(BandHighProperty, value); }
    public bool HasBand { get => (bool)GetValue(HasBandProperty); set => SetValue(HasBandProperty, value); }

    public Sparkline()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    private void Redraw()
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Count < 2 || w <= 1 || h <= 1)
        {
            Line.Points = new PointCollection();
            Band.Visibility = Visibility.Collapsed;
            return;
        }

        // Scale to the data plus the band, so the shaded normal range is always visible rather than off-canvas.
        var min = values.Min();
        var max = values.Max();
        if (HasBand)
        {
            min = System.Math.Min(min, BandLow);
            max = System.Math.Max(max, BandHigh);
        }
        var span = max - min;
        if (span <= 0) span = 1;   // a perfectly flat metric draws down the middle rather than dividing by zero

        double Y(double v) => h - (v - min) / span * h;

        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
            points.Add(new Point(i / (double)(values.Count - 1) * w, Y(values[i])));
        Line.Points = points;

        if (HasBand)
        {
            var top = Y(BandHigh);
            var bottom = Y(BandLow);
            Canvas.SetTop(Band, top);
            Band.Height = System.Math.Max(1, bottom - top);
            Band.Width = w;
            Band.Visibility = Visibility.Visible;
        }
        else Band.Visibility = Visibility.Collapsed;
    }
}
