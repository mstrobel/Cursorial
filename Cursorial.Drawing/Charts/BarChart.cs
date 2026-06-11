using System.Text;

using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

// ReSharper disable CheckNamespace

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
/// A bar chart of a single value series, rendered with fractional Block-Elements glyphs so bar lengths
/// are smooth, not cell-quantized. Bars are anchored at the zero baseline and <b>signed</b>: positives
/// grow toward the far edge (up / right) from the baseline using the complete lower/left eighth ramp;
/// negatives grow toward the near edge using the sparse upper/right partials (Unicode offers only ⅛ and ½
/// there, so the negative direction is coarser than the positive). When all values share a sign the
/// baseline sits at an edge and the chart looks like a classic single-direction bar chart. The
/// <see cref="Brush"/> is sampled per cell against the chart area, so a gradient fills across the bars.
/// </summary>
public sealed class BarChart : IChart
{
    private readonly double[] _values;

    /// <summary>Create a bar chart over <paramref name="values"/> painted with <paramref name="brush"/>.</summary>
    public BarChart(ReadOnlySpan<double> values, IBrush? brush = null)
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

    /// <summary>The value range; null (default) auto-ranges from the data. An explicit range is always
    /// expanded to include the zero baseline (e.g. [5, 10] becomes [0, 10]), since a bar axis that omits zero
    /// misrepresents magnitude — consistent with the zero-anchored, signed-bar design.</summary>
    public AxisRange? Range { get; init; }

    /// <summary>Optional category labels, one per value, centered under each bar. When set (vertical
    /// orientation), the area's bottom row is reserved for the labels and the bars fill the rows above.</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>Foreground color for the category labels (default: the terminal default).</summary>
    public Color LabelColor { get; init; } = Colors.Default;

    /// <inheritdoc/>
    public void Render(DrawingContext context, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(context);
        int n = _values.Length;
        if (n == 0 || area.Columns <= 0 || area.Rows <= 0) return;

        // Signed range spanning zero, so the baseline sits inside the chart and negatives draw on its far
        // side. Default: [min(0, dataMin), max(0, dataMax)] — zero is always included as the baseline.
        double lo, hi;
        if (Range is { } range)
        {
            lo = Math.Min(0.0, range.Min);
            hi = Math.Max(0.0, range.Max);
        }
        else
        {
            lo = 0.0;
            hi = 0.0;
            foreach (double v in _values)
                if (double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        }
        double span = hi - lo;
        if (span <= 0.0) span = 1.0;   // all-zero → flat baseline, no divide-by-zero

        int gap = Math.Max(0, Gap);
        bool vertical = Orientation == BarOrientation.Vertical;
        // Category labels (vertical only) reserve the bottom row; bars fill the rows above (the "plot" rect).
        bool showLabels = Categories is { Count: > 0 } && vertical && area.Rows > 1;
        Rect plot = showLabels ? new Rect(area.Column, area.Row, area.Columns, area.Rows - 1) : area;
        var blockAxis = vertical ? BlockAxis.Vertical : BlockAxis.Horizontal;
        int lane = vertical ? plot.Columns : plot.Rows;     // cross-axis: where bars are laid side by side
        int depth = vertical ? plot.Rows : plot.Columns;    // value-axis: how far the full span reaches
        int barThickness = Math.Max(1, (lane - gap * (n - 1)) / n);

        // The zero baseline's position along the value axis, in eighths from the near (bottom / left) edge.
        int baselineEighths = (int) Math.Round((0.0 - lo) / span * depth * 8);

        for (int b = 0; b < n; b++)
        {
            int laneStart = b * (barThickness + gap);
            if (laneStart >= lane) break;   // ran out of room

            double v = _values[b];
            if (!double.IsFinite(v)) continue;

            // The bar fills the eighth-range between the baseline and the value — above it for positives,
            // below for negatives.
            int valueEighths = (int) Math.Round((v - lo) / span * depth * 8);
            int loEighth = Math.Min(baselineEighths, valueEighths);
            int hiEighth = Math.Max(baselineEighths, valueEighths);
            if (hiEighth <= loEighth) continue;   // value sits on the baseline → nothing to draw

            for (int t = 0; t < barThickness && laneStart + t < lane; t++)
            for (int d = 0; d < depth; d++)
            {
                int cellNear = d * 8;                            // this cell spans eighths [cellNear, cellNear+8)
                int fillNear = Math.Max(loEighth, cellNear);
                int fillFar = Math.Min(hiEighth, cellNear + 8);
                if (fillFar <= fillNear) continue;               // bar doesn't reach this cell
                int fillEighths = fillFar - fillNear;

                // Anchor the partial at whichever cell edge the fill touches: the near edge (bottom / left)
                // uses the complete lower/left ramp; the far edge (top / right) uses the sparse upper/right
                // ramp. A whole-cell fill is a full block; a rare floating fill approximates with the far ramp.
                string glyph = fillEighths >= 8 ? BlockGlyphs.Glyph(8, blockAxis)
                             : fillNear == cellNear ? BlockGlyphs.Glyph(fillEighths, blockAxis)
                             : BlockGlyphs.FarGlyph(fillEighths, blockAxis);

                // Map (lane, depth) back to cell coordinates; d grows from the anchored (near) edge.
                int col, row;
                if (vertical)
                {
                    col = plot.Column + laneStart + t;
                    row = plot.Row + plot.Rows - 1 - d;     // up from the bottom
                }
                else
                {
                    col = plot.Column + d;                  // right from the left
                    row = plot.Row + laneStart + t;
                }

                Plot(context, col, row, glyph, plot);
            }
        }

        if (showLabels) DrawCategoryLabels(context, area, n, barThickness, gap);
    }

    // Draw category labels centered under each bar lane in the area's bottom row (vertical bars only). Labels
    // are truncated and centered by DISPLAY WIDTH (grapheme clusters) and written via the grapheme-aware
    // DrawText, so a wide glyph or surrogate pair is neither split mid-cluster nor mis-measured.
    private void DrawCategoryLabels(DrawingContext context, in Rect area, int n, int barThickness, int gap)
    {
        int labelRow = area.Row + area.Rows - 1;
        if ((uint) labelRow >= (uint) context.Bounds.Rows) return;
        int count = Math.Min(n, Categories!.Count);
        for (int b = 0; b < count; b++)
        {
            int laneStart = b * (barThickness + gap);
            if (laneStart >= area.Columns) break;

            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            string text = TruncateToWidth(Categories[b] ?? "", barThickness);                  // truncate to the lane
            int offset = Math.Max(0, (barThickness - GraphemeWidth.StringWidth(text)) / 2);     // center within the lane
            int col = area.Column + laneStart + offset;
            if ((uint) col < (uint) context.Bounds.Columns)
                context.DrawText(col, labelRow, text, LabelColor, Colors.Transparent);
        }
    }

    // Truncate to at most maxWidth display columns on grapheme-cluster boundaries (never splitting a cluster).
    private static string TruncateToWidth(string text, int maxWidth)
    {
        int width = 0;
        var builder = new StringBuilder();
        var graphemes = text.GetGraphemeEnumerator();
        while (graphemes.MoveNext())
        {
            int w = GraphemeWidth.ClusterWidth(graphemes.Current);
            if (width + w > maxWidth) break;
            builder.Append(graphemes.Current);
            width += w;
        }
        return builder.ToString();
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
