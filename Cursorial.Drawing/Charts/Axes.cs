using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing.Charts;

/// <summary>The inset plot rectangle an <see cref="Axes"/> leaves for the data, plus the nice-rounded
/// ranges it used (pass these to the chart so axis ticks and data align).</summary>
public readonly record struct PlotLayout(Rect Plot, AxisRange X, AxisRange Y);

/// <summary>
/// Draws a two-axis frame — a Y axis down the left and an X axis along the bottom, meeting at a corner —
/// with numeric tick labels and optional gridlines, then returns the inset <see cref="PlotLayout"/> for
/// the data. Axes are ordinary <see cref="Pen"/> box-strokes, so the corner and gridline crossings
/// resolve as junctions through the existing engine. Typical use:
/// <code>
/// var axes = new Axes(AxisRange.FromValues(xs), AxisRange.FromValues(ys)) { XAxis = new() { Gridlines = true } };
/// var layout = axes.Render(ctx, area);
/// new LineChart(points, color) { XRange = layout.X, YRange = layout.Y }.Render(ctx, layout.Plot);
/// </code>
/// </summary>
public sealed class Axes
{
    private readonly AxisRange _xData;
    private readonly AxisRange _yData;

    /// <summary>Create axes over the given data ranges (use <see cref="AxisRange.FromValues"/> to derive them).</summary>
    public Axes(AxisRange xData, AxisRange yData)
    {
        _xData = xData;
        _yData = yData;
    }

    /// <summary>The X (horizontal) axis configuration.</summary>
    public Axis XAxis { get; init; } = new();

    /// <summary>The Y (vertical) axis configuration.</summary>
    public Axis YAxis { get; init; } = new();

    /// <summary>The pen for the axis lines (default light).</summary>
    public Pen Pen { get; init; } = Pens.Light;

    /// <summary>The pen for gridlines (default light triple-dash).</summary>
    public Pen GridPen { get; init; } = Pens.Light.WithDash(LineDash.Triple);

    /// <summary>The tick-label color (default the terminal foreground).</summary>
    public Color LabelColor { get; init; } = Colors.Default;

    /// <summary>Draw the frame into <paramref name="area"/> and return the inset plot + nice ranges.</summary>
    public PlotLayout Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (xRange, _, xTicks) = (XAxis.Range ?? _xData).Nice(XAxis.TickCount);
        var (yRange, _, yTicks) = (YAxis.Range ?? _yData).Nice(YAxis.TickCount);
        Func<double, string> xFormat = XAxis.Format ?? DefaultFormat;
        Func<double, string> yFormat = YAxis.Format ?? DefaultFormat;

        int yLabelWidth = 0;
        foreach (double t in yTicks)
            yLabelWidth = Math.Max(yLabelWidth, yFormat(t).Length);

        int leftGutter = yLabelWidth + 1;        // Y labels + one space before the axis
        const int bottomGutter = 1;              // one row for X labels

        int axisColumn = area.Column + leftGutter;
        int axisRow = area.RowEnd - 1 - bottomGutter;
        int plotLeft = axisColumn + 1;
        int plotWidth = area.ColumnEnd - plotLeft;
        int plotHeight = axisRow - area.Row;
        if (plotWidth <= 0 || plotHeight <= 0)
            return new PlotLayout(area, xRange, yRange);   // too small for a frame

        var plot = new Rect(plotLeft, area.Row, plotWidth, plotHeight);

        // Project ticks through the SAME 2×4 sub-cell projector the braille charts use, so ticks,
        // gridlines, markers, and the curve all land in the same cells.
        var projector = new PlotProjector(plot, xRange, yRange, 2, 4);

        // Gridlines first (faint, under everything). They extend to the axis cells so they junction
        // the axes (├ on the Y axis, ┴ on the X axis) — that's the tick look.
        if (XAxis.Gridlines)
            foreach (double t in xTicks)
            {
                int c = projector.ToCell(t, yRange.Min).Column;
                if (c >= plotLeft && c < area.ColumnEnd)
                    context.DrawLine(c, plot.Row, c, axisRow, GridPen);
            }
        if (YAxis.Gridlines)
            foreach (double t in yTicks)
            {
                int r = projector.ToCell(xRange.Min, t).Row;
                if (r >= plot.Row && r < axisRow)
                    context.DrawLine(axisColumn, r, area.ColumnEnd - 1, r, GridPen);
            }

        // The axes (meeting at a └ corner via the junction engine).
        context.DrawLine(axisColumn, plot.Row, axisColumn, axisRow, Pen);            // Y
        context.DrawLine(axisColumn, axisRow, area.ColumnEnd - 1, axisRow, Pen);     // X

        // Y tick labels: right-aligned in the left gutter at the tick row.
        foreach (double t in yTicks)
        {
            int r = projector.ToCell(xRange.Min, t).Row;
            if (r < plot.Row || r >= axisRow) continue;
            string label = yFormat(t);
            context.DrawText(axisColumn - 1 - label.Length, r, label, LabelColor);
        }

        // X tick labels: centered under the tick column, on the bottom gutter row.
        foreach (double t in xTicks)
        {
            int c = projector.ToCell(t, yRange.Min).Column;
            if (c < plotLeft || c >= area.ColumnEnd) continue;
            string label = xFormat(t);
            context.DrawText(c - label.Length / 2, axisRow + 1, label, LabelColor);
        }

        return new PlotLayout(plot, xRange, yRange);
    }

    private static string DefaultFormat(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
