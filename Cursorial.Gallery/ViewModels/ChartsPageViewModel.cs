using Cursorial.Animation;
using Cursorial.Drawing.Charts;
using Cursorial.Media;
using Cursorial.Rendering.Media;

namespace Cursorial.Gallery.ViewModels;

public class ChartsPageViewModel : PageViewModel
{
    public override string Title => "Charts";
    public override string Summary => "2D bar, line, multi-line, and scatter charts.";

    public override bool IsContentScrollable => false;

    public ChartsPageViewModel()
    {
        LineChartPoints = BuildLineChartPoints();
        ScatterChartPoints = BuildScatterChartPoints();
        BarValues = BuildBarValues();
        MultiLineChartSeries = BuildMultiLineChartSeries();

        LineChart = new LineChart(LineChartPoints, Brushes.Cyan)
                    {
                        Interpolation = CurveInterpolation.Linear,
                        XRange = new AxisRange(0, 1),
                        YRange = new AxisRange(0, 1),
                        ShowMarkers = false
                    };

        BarChart = new BarChart(BarValues, Brushes.Magenta)
                   {
                       Categories = BarValues.Select((_, i) => ((char) ('A' + i)).ToString()).ToArray(),
                       Gap = 1,
                       Orientation = BarOrientation.Vertical
                   };

        ScatterChart = new ScatterChart(ScatterChartPoints, Brushes.Yellow)
                       {
                           Marker = MarkerStyle.Cross
                       };

        MultiLineChart = new MultiLineChart(MultiLineChartSeries)
                         {
                             Interpolation = CurveInterpolation.MonotoneCubic
                         };
    }

    public IReadOnlyList<PointD> LineChartPoints
    {
        get;
        set => Set(ref field, value);
    }

    public IReadOnlyList<PointD> ScatterChartPoints
    {
        get;
        set => Set(ref field, value);
    }

    public IReadOnlyList<double> BarValues
    {
        get;
        set => Set(ref field, value);
    }

    public IReadOnlyList<ChartSeries> MultiLineChartSeries
    {
        get;
        set => Set(ref field, value);
    }

    public IChart? LineChart
    {
        get;
        set => Set(ref field, value);
    }

    public IChart? BarChart
    {
        get;
        set => Set(ref field, value);
    }

    public IChart? ScatterChart
    {
        get;
        set => Set(ref field, value);
    }

    public IChart? MultiLineChart
    {
        get;
        set => Set(ref field, value);
    }

    private IReadOnlyList<PointD> BuildLineChartPoints()
    {
        var pts = new PointD[33];
        var easing = Easings.CubicInOut;

        for (int i = 0; i < pts.Length; i++)
        {
            double x = i / (double) (pts.Length - 1);
            pts[i] = new PointD(x, easing(x));
        }

        return pts;
    }

    private IReadOnlyList<PointD> BuildScatterChartPoints()
    {
        return
        [
            new(0, 2), new(1, 5), new(2, 3), new(3, 8), new(4, 6), new(5, 9),
            new(6, 4), new(7, 7), new(2, 7), new(5, 2), new(6, 8), new(3, 1),
        ];
    }

    private IReadOnlyList<double> BuildBarValues() => [1, 17, 5, 7, 3, 11, 19, 13];

    private IReadOnlyList<ChartSeries> BuildMultiLineChartSeries()
    {
        return
        [
            new ChartSeries(
                [new(0, 2), new(1, 5), new(2, 5), new(3, 4), new(4, 7), new(5, 3)],
                SolidColorBrush.FromRgb(235, 110, 150))
            {
                FillArea = true,
                AreaBrush = new SolidColorBrush(Color.FromRgba(235, 110, 150, /*110*/255)) { Opacity = 0.35 }
            },
            new ChartSeries(
                [new(0, 6), new(1, 3), new(2, 5), new(3, 8), new(4, 4), new(5, 6)],
                SolidColorBrush.FromRgb(110, 200, 235))
            {
                FillArea = true,
                AreaBrush = new SolidColorBrush(Color.FromRgba(110, 200, 235, /*110*/255)) { Opacity = 0.35 }
            }
        ];
    }
}