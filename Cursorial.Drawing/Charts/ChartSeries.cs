using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>One named-by-brush data series for a multi-series chart: its points and its color.</summary>
public sealed record ChartSeries
{
    /// <summary>Create a series over <paramref name="points"/> painted with <paramref name="brush"/>.</summary>
    public ChartSeries(IReadOnlyList<PointD> points, IBrush brush)
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
}
