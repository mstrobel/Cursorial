using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>Which way a <see cref="BarChart"/>'s bars grow.</summary>
public enum BarOrientation
{
    /// <summary>Columns growing up from the bottom.</summary>
    Vertical,

    /// <summary>Rows growing right from the left.</summary>
    Horizontal
}

/// <summary>
/// A bar chart of a single value series, rendered with fractional Block-Elements glyphs (eighth
/// resolution per cell) so bar heights are smooth, not cell-quantized. Bars are anchored at the zero
/// baseline; this v1 renders <b>non-negative</b> values (a negative value draws as an empty bar —
/// signed bars need the sparse upper/right block ramps and are a later enhancement). The
/// <see cref="Brush"/> is sampled per cell against the chart area, so a gradient fills across the bars.
/// </summary>
public sealed class BarChart : IChart
{
    private readonly double[] _values;

    /// <summary>Create a bar chart over <paramref name="values"/> painted with <paramref name="brush"/>.</summary>
    public BarChart(ReadOnlySpan<double> values, IBrush brush)
    {
        _values = values.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a bar chart over <paramref name="values"/> in a solid <paramref name="color"/>.</summary>
    public BarChart(ReadOnlySpan<double> values, Color color) : this(values, new SolidColorBrush(color)) { }

    /// <summary>The bar values (defensively copied).</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>The bar fill brush (never null; null was replaced by <see cref="Brushes.Default"/>).</summary>
    public IBrush Brush { get; init; }

    /// <summary>Bar growth direction (default <see cref="BarOrientation.Vertical"/>).</summary>
    public BarOrientation Orientation { get; init; } = BarOrientation.Vertical;

    /// <summary>Cells of gap between adjacent bars (default 0).</summary>
    public int Gap { get; init; }

    /// <summary>The value range; null (default) auto-ranges from the data, anchored to include zero.</summary>
    public AxisRange? Range { get; init; }

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);
        int n = _values.Length;
        if (n == 0 || area.Columns <= 0 || area.Rows <= 0) return;

        // Bars are zero-anchored, so the range is [0, dataMax] — NOT FromValues().IncludingZero(),
        // whose all-equal/single ±1 padding (a min..max sparkline convention) would inflate the top and
        // keep a lone or all-equal bar from ever reaching full.
        double max;
        if (Range is { } range)
        {
            max = range.Max;
        }
        else
        {
            max = 0.0;
            foreach (double v in _values)
                if (double.IsFinite(v) && v > max) max = v;
        }
        if (max <= 0.0) max = 1.0;   // all-zero / non-positive → empty bars, no divide-by-zero

        int gap = Math.Max(0, Gap);
        bool vertical = Orientation == BarOrientation.Vertical;
        int lane = vertical ? area.Columns : area.Rows;     // cross-axis: where bars are laid side by side
        int depth = vertical ? area.Rows : area.Columns;    // value-axis: how far a full bar reaches
        int barThickness = Math.Max(1, (lane - gap * (n - 1)) / n);

        for (int b = 0; b < n; b++)
        {
            int laneStart = b * (barThickness + gap);
            if (laneStart >= lane) break;   // ran out of room

            double v = _values[b];
            if (!double.IsFinite(v) || v <= 0.0) continue;
            int totalEighths = (int) Math.Round(Math.Min(v / max, 1.0) * depth * 8);

            for (int t = 0; t < barThickness && laneStart + t < lane; t++)
            for (int d = 0; d < depth; d++)
            {
                int filled = Math.Clamp(totalEighths - d * 8, 0, 8);   // d = cells from the baseline
                if (filled == 0) break;

                // Map (lane, depth) back to cell coordinates; the value axis grows from the anchored edge.
                int col, row;
                if (vertical)
                {
                    col = area.Column + laneStart + t;
                    row = area.Row + area.Rows - 1 - d;     // up from the bottom
                }
                else
                {
                    col = area.Column + d;                  // right from the left
                    row = area.Row + laneStart + t;
                }

                Plot(context, col, row, BlockGlyphs.Glyph(filled, vertical ? BlockAxis.Vertical : BlockAxis.Horizontal), area);
            }
        }
    }

    // Write one block cell (foreground = sampled brush, transparent background) if it lies in the scene.
    private void Plot(DrawingContext context, int column, int row, string glyph, in Rect bounds)
    {
        if ((uint) column >= (uint) context.Bounds.Columns || (uint) row >= (uint) context.Bounds.Rows)
            return;

        var color = Brush.ColorAt(column, row, bounds);
        context.Set(column, row, glyph, Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
    }
}
