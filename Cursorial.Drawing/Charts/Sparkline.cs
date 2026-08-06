using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Charts;

/// <summary>
/// A single-row sparkline: each cell shows one value as a vertical eighth-block, normalized across the
/// data's own min…max so the smallest value reads as ▁ and the largest as █. The cheapest chart — no
/// axes, no plot area, just <c>Render(ctx, column, row, width)</c>. If there are more values than cells,
/// they are resampled to the width. The <see cref="Brush"/> samples per cell across the row.
/// </summary>
public sealed class Sparkline
{
    private readonly double[] _values;

    /// <summary>Create a sparkline over <paramref name="values"/> painted with <paramref name="brush"/>.</summary>
    public Sparkline(ReadOnlySpan<double> values, IBrush? brush = null)
    {
        _values = values.ToArray();
        Brush = brush ?? Brushes.Default;
    }

    /// <summary>Create a sparkline over <paramref name="values"/> in a solid <paramref name="color"/>.</summary>
    public Sparkline(ReadOnlySpan<double> values, Color color) : this(values, new SolidColorBrush(color)) { }

    /// <summary>The values (defensively copied).</summary>
    public IReadOnlyList<double> Values => _values;

    /// <summary>The fill brush (never null).</summary>
    public IBrush Brush { get; init; }

    /// <summary>Paint the sparkline across <paramref name="width"/> cells starting at (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public void Render(DrawingContext context, int column, int row, int width)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_values.Length == 0 || width <= 0) return;

        var range = AxisRange.FromValues(_values);
        var bounds = new Rect(column, row, width, 1);

        for (int i = 0; i < width; i++)
        {
            int col = column + i;
            if (!context.IsVisible(col, row)) continue;

            // Resample value index to the cell (nearest), so width need not equal the value count.
            int index = (width == 1 || _values.Length == 1)
                ? 0
                : (int) Math.Round((double) i * (_values.Length - 1) / (width - 1));
            double v = _values[index];
            if (!double.IsFinite(v)) continue;   // gap

            int level = 1 + (int) Math.Round(Math.Clamp(range.Normalize(v), 0.0, 1.0) * 7.0);   // 1..8 (min visible)
            var color = Brush.ColorAt(col, row, bounds);
            context.Set(col, row, BlockGlyphs.Glyph(level, BlockAxis.Vertical),
                        Style.Default.WithForeground(color).WithBackground(Colors.Transparent));
        }
    }
}
