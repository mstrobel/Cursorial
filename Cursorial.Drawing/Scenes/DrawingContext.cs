using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// The authoring surface handed to <see cref="Scene.Draw"/>. It draws into the scene's backing
/// buffer — the one place a <see cref="Brush"/> is resolved to a scalar <see cref="Style"/> before
/// reaching a cell. Phase 1 exposes a scalar <see cref="Set"/> and a solid-brush
/// <see cref="FillRectangle"/>; gradients, pen/box drawing, brush text, and charts extend this in
/// later phases.
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
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="fill"/>. Each cell is
    /// painted background-only (no glyph), so on composite the fill tints the target background and
    /// leaves any glyph beneath showing through — the scene's transparency model. (An opaque
    /// block-fill that also clears glyphs is a later addition.) Phase 1 supports solid brushes;
    /// the per-cell gradient sample arrives in Phase 2.
    /// </summary>
    /// <remarks>
    /// Writes via the raw indexer rather than <see cref="CellBuffer.Set"/> so a translucent source
    /// color is stored <em>verbatim</em> (its alpha preserved for the compositor to blend). Going
    /// through <c>Set</c> would consume the alpha by pre-compositing over the transparent backdrop.
    /// </remarks>
    public void FillRectangle(in Rect region, in Brush fill)
    {
        var extent = BrushExtent.FromRect(region);

        int colEnd = Math.Min(region.ColumnEnd, _surface.Columns);
        int rowEnd = Math.Min(region.RowEnd, _surface.Rows);

        for (int row = Math.Max(0, region.Row); row < rowEnd; row++)
        for (int col = Math.Max(0, region.Column); col < colEnd; col++)
        {
            var color = fill.Sample(col, row, extent);
            _surface[col, row] = new Cell(null, CellKind.Single, Style.Default.WithBackground(color));
        }
    }
}
