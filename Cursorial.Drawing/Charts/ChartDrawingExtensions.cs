using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>One-liner chart sugar on <see cref="DrawingContext"/>, so the common cases need no chart object.</summary>
public static class ChartDrawingExtensions
{
    /// <summary>Render <paramref name="chart"/> into <paramref name="area"/>.</summary>
    public static void Chart(this DrawingContext context, in Rect area, IChart chart)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(chart);
        chart.Render(context, area);
    }

    /// <summary>Render a single-color bar chart of <paramref name="values"/> into <paramref name="area"/>.</summary>
    public static void BarChart(this DrawingContext context, in Rect area, ReadOnlySpan<double> values, Color color)
    {
        ArgumentNullException.ThrowIfNull(context);
        new BarChart(values, color).Render(context, area);
    }

    /// <summary>Render a single-row sparkline of <paramref name="values"/> across <paramref name="width"/> cells.</summary>
    public static void Sparkline(this DrawingContext context, int column, int row, int width,
                                 ReadOnlySpan<double> values, Color color)
    {
        ArgumentNullException.ThrowIfNull(context);
        new Sparkline(values, color).Render(context, column, row, width);
    }
}
