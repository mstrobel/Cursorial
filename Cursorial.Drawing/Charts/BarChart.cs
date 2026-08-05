using System.Text;

using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Drawing.Charts;

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
    // Each rendered bar's cell extent (partial edge cells included) and what it stands for, recorded by
    // Render for HitTest — the tooltip reports the bar under the pointer, category-prefixed when labeled.
    private readonly Dictionary<Rect, (double Value, string Category)> _renderedBars = new();
    private AxisRange _renderedRange;

    /// <summary>Create a bar chart over <paramref name="values"/> painted with <paramref name="brush"/>.</summary>
    public BarChart(ReadOnlySpan<double> values, IBrush? brush = null)
    {
        Values = values.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a bar chart over <paramref name="values"/> painted with <paramref name="brush"/>.</summary>
    public BarChart(IReadOnlyList<double> values, IBrush? brush = null)
    {
        Values = values.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a bar chart over <paramref name="values"/> in a solid <paramref name="color"/>.</summary>
    public BarChart(ReadOnlySpan<double> values, Color color) : this(values, new SolidColorBrush(color)) { }

    /// <summary>The bar values (defensively copied).</summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>The bar fill brush (never null; null was replaced by <see cref="Brushes.Default"/>).</summary>
    public IBrush Brush { get; }

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

        _renderedBars.Clear();

        var values = Values;
        int n = values.Count;
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
            foreach (double v in values)
                if (double.IsFinite(v)) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        }
        double span = hi - lo;
        if (span <= 0.0) span = 1.0;   // all-zero → flat baseline, no divide-by-zero

        _renderedRange = new AxisRange(lo, lo + span);

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

            double v = values[b];
            if (!double.IsFinite(v)) continue;

            // The bar fills the eighth-range between the baseline and the value — above it for positives,
            // below for negatives.
            int valueEighths = (int) Math.Round((v - lo) / span * depth * 8);
            int loEighth = Math.Min(baselineEighths, valueEighths);
            int hiEighth = Math.Max(baselineEighths, valueEighths);
            if (hiEighth <= loEighth) continue;   // value sits on the baseline → nothing to draw

            // The bar's whole-cell extent (partial edge cells count — a half-filled cell is still the
            // bar's), recorded for HitTest. Clamped to the plot's eighth-range first: with an explicit
            // Range, an out-of-range value projects outside the plot, and the unclamped extent recorded
            // phantom bars over cells the paint below never touches (it intersects per cell).
            int recordLo = Math.Max(0, loEighth);
            int recordHi = Math.Min(depth * 8, hiEighth);
            int cellLo = recordLo / 8;
            int cellHi = Math.Min(depth, (recordHi + 7) / 8);
            int thickness = Math.Min(barThickness, lane - laneStart);
            if (recordHi > recordLo && cellHi > cellLo && thickness > 0)
            {
                var barRect = vertical
                    ? new Rect(plot.Column + laneStart, plot.Row + plot.Rows - cellHi, thickness, cellHi - cellLo)
                    : new Rect(plot.Column + cellLo, plot.Row + laneStart, cellHi - cellLo, thickness);
                string category = Categories is {} c && b < c.Count ? c[b] ?? "" : "";
                _renderedBars[barRect] = (v, category);
            }

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
        int count = Math.Min(n, Categories!.Count);
        for (int b = 0; b < count; b++)
        {
            int laneStart = b * (barThickness + gap);
            if (laneStart >= area.Columns) break;

            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            string text = TruncateToWidth(Categories[b] ?? "", barThickness);                  // truncate to the lane
            int offset = Math.Max(0, (barThickness - GraphemeWidth.StringWidth(text)) / 2);     // center within the lane
            int col = area.Column + laneStart + offset;
            context.DrawText(col, labelRow, text, LabelColor, Colors.Transparent);   // per-cell clipped by the context
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

    /// <inheritdoc/>
    public bool HitTest(CellPosition position, out object? hitObject)
    {
        hitObject = null;

        foreach (var (rect, bar) in _renderedBars)
        {
            if (!rect.Contains(position.Column, position.Row))
                continue;

            var value = ChartMath.Format(bar.Value, Math.Abs(_renderedRange.Span));
            hitObject = bar.Category.Length > 0 ? $"{bar.Category}: {value}" : value;
            return true;
        }

        return false;
    }

    // Write one block cell (foreground = sampled brush, transparent background) if it would be visible
    // (inside the active clip after the active translate — the scene bounds when nothing is pushed).
    private void Plot(DrawingContext context, int column, int row, string glyph, in Rect bounds)
    {
        if (!context.IsVisible(column, row))
            return;

        var color = Brush.ColorAt(column, row, bounds);
        context.Set(column, row, glyph, Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
    }
}
