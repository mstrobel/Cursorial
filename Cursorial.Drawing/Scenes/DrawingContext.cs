using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Drawing;

/// <summary>
/// The authoring surface handed to <see cref="Scene.Draw"/>. It draws into the scene's backing
/// buffer — the one place an <see cref="IBrush"/> is resolved to a scalar <see cref="Style"/> before
/// reaching a cell. It exposes a scalar <see cref="Set"/>, a brush
/// <see cref="FillRectangle(in Rect, IBrush)"/> (solid or gradient), and a single-line brush
/// <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in Style)"/>; pen/box drawing
/// and charts extend this in later phases. <see cref="Color"/> overloads wrap a
/// <see cref="SolidColorBrush"/> for the common solid case.
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

    /// <summary>Fill <paramref name="region"/>'s backgrounds with a solid <paramref name="color"/>.</summary>
    public void FillRectangle(in Rect region, Color color) => FillRectangle(region, new SolidColorBrush(color));

    /// <summary>
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="brush"/> (solid or gradient),
    /// sampled per cell with <paramref name="region"/> as the brush bounds. Each cell is painted
    /// background-only (no glyph), so on composite the fill tints the target background and leaves any
    /// glyph beneath showing through — the scene's transparency model.
    /// </summary>
    /// <remarks>
    /// Writes via the raw indexer rather than <see cref="CellBuffer.Set"/> so a translucent sampled
    /// color is stored <em>verbatim</em> (its alpha preserved for the compositor to blend). Going
    /// through <c>Set</c> would consume the alpha by pre-compositing over the transparent backdrop.
    /// </remarks>
    public void FillRectangle(in Rect region, IBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        int colStart = Math.Max(0, region.Column);
        int rowStart = Math.Max(0, region.Row);
        int colEnd = Math.Min(region.ColumnEnd, _surface.Columns);
        int rowEnd = Math.Min(region.RowEnd, _surface.Rows);
        if (colStart >= colEnd || rowStart >= rowEnd) return;

        for (int row = rowStart; row < rowEnd; row++)
        for (int col = colStart; col < colEnd; col++)
        {
            var color = brush.ColorAt(col, row, region);
            _surface[col, row] = new Cell(null, CellKind.Single, Style.Default.WithBackground(color));
        }
    }

    /// <summary>Draw a single line of text with a solid foreground (and optional background) color.</summary>
    public int DrawText(int column, int row, ReadOnlySpan<char> text,
                        Color foreground, Color? background = null, in Style baseStyle = default)
        => DrawText(column, row, text, new SolidColorBrush(foreground),
                    background is { } bg ? new SolidColorBrush(bg) : null, baseStyle);

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
                        IBrush foreground, IBrush? background = null, in Style baseStyle = default)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (text.IsEmpty || (uint) row >= (uint) _surface.Rows) return 0;

        var bg = background ?? Brushes.Transparent;

        // The run's extent (its cells on this row) is the brush bounds.
        int runWidth = GraphemeWidth.StringWidth(text);
        var bounds = new Rect(column, row, runWidth, 1);

        int start = column;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;
            if (column + width > _surface.Columns) break;

            var style = baseStyle.WithForeground(foreground.ColorAt(column, row, bounds))
                                 .WithBackground(bg.ColorAt(column, row, bounds));

            column += _surface.Set(column, row, cluster.ToString(), style);
        }

        return column - start;
    }
}
