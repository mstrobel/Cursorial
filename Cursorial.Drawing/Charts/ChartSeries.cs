using Cursorial.Media;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Charts;

/// <summary>One named-by-brush data series for a multi-series chart: its points and its color.</summary>
public sealed record ChartSeries
{
    /// <summary>Create a series over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public ChartSeries(IReadOnlyList<PointD> points, IBrush? brush = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        Points = points;
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a series over <paramref name="points"/> in a solid <paramref name="color"/>.</summary>
    public ChartSeries(IReadOnlyList<PointD> points, Color color) : this(points, new SolidColorBrush(color)) { }

    /// <summary>The series' data points.</summary>
    public IReadOnlyList<PointD> Points { get; }

    /// <summary>The series' brush (never null).</summary>
    public IBrush Brush { get; }

    /// <summary>Fill the area under this series (see <c>LineChart.FillArea</c>). Most useful via
    /// <c>MultiLineChart.ToLayers</c>, where translucent per-series fills alpha-blend across layers.</summary>
    public bool FillArea { get; init; }

    /// <summary>The brush for this series' filled area; null → the series <see cref="Brush"/>. Use a
    /// translucent brush so overlapping fills compose rather than occlude.</summary>
    public IBrush? AreaBrush { get; init; }
}
