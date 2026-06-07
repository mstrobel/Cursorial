using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Drawing;

/// <summary>
/// The authoring surface handed to <see cref="Scene.Draw"/>. It draws into the scene's backing
/// buffer — the one place a <see cref="Brush"/> is resolved to a scalar <see cref="Style"/> before
/// reaching a cell. It exposes a scalar <see cref="Set"/>, a brush <see cref="FillRectangle(in Rect, in Brush)"/>
/// (solid or gradient), and a single-line brush <see cref="DrawText"/>; pen/box drawing and charts
/// extend this in later phases.
/// </summary>
public sealed class DrawingContext
{
    private readonly CellBufferView _surface;   // the scene buffer's view — the public seam

    internal DrawingContext(Scene scene)
    {
        _surface = scene.Buffer.AsView();
        Bounds = scene.Bounds;
    }

    /// <summary>The scene's bounds, in scene-local coordinates.</summary>
    public Rect Bounds { get; }

    /// <summary>
    /// Scalar write: place <paramref name="grapheme"/> at <paramref name="column"/>,
    /// <paramref name="row"/> with the given <paramref name="style"/>. The style's colors are
    /// stored as-is (intra-scene composition follows <see cref="CellBuffer.Set"/>'s rules); the
    /// scene's source colors are later composited onto a target by <see cref="SceneCompositor"/>.
    /// </summary>
    public void Set(int column, int row, string? grapheme, in Style style) =>
        _surface.Set(column, row, grapheme, in style);

    /// <summary>
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="fill"/> — solid or gradient.
    /// Each cell is painted background-only (no glyph), so on composite the fill tints the target
    /// background and leaves any glyph beneath showing through — the scene's transparency model. A
    /// gradient is sampled per cell against <paramref name="region"/> as its extent (relative-to-
    /// bounding-box); to span a gradient across a larger logical area, use the overload taking an
    /// explicit <see cref="BrushExtent"/>.
    /// </summary>
    /// <remarks>
    /// Writes via the raw indexer rather than <see cref="CellBuffer.Set"/> so a translucent source
    /// color is stored <em>verbatim</em> (its alpha preserved for the compositor to blend). Going
    /// through <c>Set</c> would consume the alpha by pre-compositing over the transparent backdrop.
    /// </remarks>
    public void FillRectangle(in Rect region, in Brush fill) => FillRectangle(region, fill, BrushExtent.FromRect(region));

    /// <summary>
    /// Fill <paramref name="region"/> with <paramref name="fill"/>, mapping a gradient onto the
    /// explicit <paramref name="extent"/> (which may extend beyond <paramref name="region"/>).
    /// </summary>
    public void FillRectangle(in Rect region, in Brush fill, in BrushExtent extent)
    {
        int colEnd = Math.Min(region.ColumnEnd, _surface.Columns);
        int rowEnd = Math.Min(region.RowEnd, _surface.Rows);
        int colStart = Math.Max(0, region.Column);
        int rowStart = Math.Max(0, region.Row);
        if (colStart >= colEnd || rowStart >= rowEnd) return;

        if (fill.IsSolid)
        {
            var color = fill.SolidColor;
            for (int row = rowStart; row < rowEnd; row++)
            for (int col = colStart; col < colEnd; col++)
                _surface[col, row] = new Cell(null, CellKind.Single, Style.Default.WithBackground(color));
            return;
        }

        // Gradient: build the sampler once for the whole fill, then sample per cell.
        var sampler = new GradientSampler(fill.Gradient!, extent);
        for (int row = rowStart; row < rowEnd; row++)
        for (int col = colStart; col < colEnd; col++)
            _surface[col, row] = new Cell(null, CellKind.Single, Style.Default.WithBackground(sampler.Sample(col, row)));
    }

    /// <summary>
    /// Draw a single line of <paramref name="text"/> starting at <paramref name="column"/>,
    /// <paramref name="row"/>, sampling <paramref name="foreground"/> (and optional
    /// <paramref name="background"/>) per cell across the run — so a gradient brush colors the text
    /// continuously, glyph by glyph. <paramref name="background"/> defaults to transparent (glyph
    /// only). Grapheme-aware (wide clusters occupy two cells); does not wrap or interpret newlines.
    /// Returns the number of columns written.
    /// </summary>
    /// <remarks>
    /// Glyphs are written through <see cref="CellBuffer.Set"/>, which composites against the
    /// transparent scene backdrop and stores opaque — so per-cell <em>translucent</em> foreground /
    /// background alpha is consumed here, not preserved for the compositor. For scene-level
    /// translucency use a composite opacity instead. A transparent background correctly lets a prior
    /// fill (or the composite target) show through under the glyph.
    /// </remarks>
    public int DrawText(int column, int row, ReadOnlySpan<char> text,
                        in Brush foreground, Brush? background = null, in Style baseStyle = default)
    {
        if (text.IsEmpty || (uint) row >= (uint) _surface.Rows) return 0;

        var fg = foreground;
        var bg = background ?? Brush.Solid(Color.Transparent);

        // The run's extent (its cells on this row) is the gradient's bounding box.
        int runWidth = GraphemeWidth.StringWidth(text);
        var extent = new BrushExtent(column, row, runWidth, 1);

        var fgSampler = fg.IsSolid ? null : new GradientSampler(fg.Gradient!, extent);
        var bgSampler = bg.IsSolid ? null : new GradientSampler(bg.Gradient!, extent);

        int start = column;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;
            if (column + width > _surface.Columns) break;

            var fgColor = fgSampler?.Sample(column, row) ?? fg.SolidColor;
            var bgColor = bgSampler?.Sample(column, row) ?? bg.SolidColor;
            var style = baseStyle.WithForeground(fgColor).WithBackground(bgColor);

            column += _surface.Set(column, row, cluster.ToString(), style);
        }

        return column - start;
    }
}
