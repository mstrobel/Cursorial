using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;

namespace Cursorial.Drawing;

/// <summary>
/// The authoring surface handed to <see cref="Scene.Draw"/>. It draws into the scene's backing
/// buffer — the one place an <see cref="IBrush"/> is resolved to a scalar <see cref="Style"/> before
/// reaching a cell. It exposes a scalar <see cref="Set"/>, a brush
/// <see cref="FillRectangle(in Rect, IBrush)"/> (solid or gradient), single-line brush
/// <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in Style)"/>, and
/// <see cref="Pen"/>-based <see cref="DrawLine(int, int, int, int, in Pen, bool)"/> /
/// <see cref="DrawBox(in Rect, in Pen, bool)"/> / <see cref="DrawRectangle(in Rect, in Pen, IBrush?, bool)"/>.
/// <see cref="Color"/> overloads wrap a <see cref="SolidColorBrush"/> / <see cref="Pen"/> for the
/// common solid case.
/// </summary>
/// <remarks>
/// <see cref="Set"/> / <see cref="FillRectangle(in Rect, IBrush)"/> / <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in Style)"/>
/// write cells <em>immediately</em>. <see cref="Pen"/> strokes are <em>deferred</em>: they accumulate
/// so junctions form across separate calls (within a <see cref="BeginFigure()">figure</see>), then
/// flush once after the draw delegate returns — last, so existing glyphs (text) survive a box edge
/// (text beats decoration). Per-figure brush bounds apply to strokes only; immediate writes keep
/// per-call bounds.
/// </remarks>
public sealed class DrawingContext
{
    private readonly CellBufferView _surface;   // the scene buffer's view — the public seam
    private readonly StrokeAccumulator _strokes;
    private int _openFigureId = -1;             // -1 = no explicit figure open

    internal DrawingContext(Scene scene)
    {
        _surface = scene.Buffer.AsView();
        Bounds = scene.Bounds;
        _strokes = new StrokeAccumulator(_surface.Columns, _surface.Rows);
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

    // ---- Figures -------------------------------------------------------------------------------

    /// <summary>
    /// Begin a figure: a discrete group of strokes whose junctions form among themselves but not with
    /// strokes outside it, and whose pen brushes sample against the <b>union</b> bounds of the figure's
    /// own strokes (resolved when the scope closes). Affects <see cref="Pen"/> strokes only.
    /// </summary>
    /// <returns>A scope to dispose (normally via <c>using</c>) to close the figure.</returns>
    /// <exception cref="InvalidOperationException">A figure is already open (figures do not nest).</exception>
    public FigureScope BeginFigure() => BeginFigureCore(null);

    /// <summary>
    /// Begin a figure with explicit, eager brush bounds — the same junction grouping, but pen brushes
    /// sample against <paramref name="bounds"/> instead of the figure's stroke union (e.g. to color-match
    /// a partial border to a full box drawn elsewhere).
    /// </summary>
    /// <exception cref="InvalidOperationException">A figure is already open (figures do not nest).</exception>
    public FigureScope BeginFigure(in Rect bounds) => BeginFigureCore(bounds);

    private FigureScope BeginFigureCore(Rect? bounds)
    {
        if (_openFigureId >= 0)
            throw new InvalidOperationException("Figures do not nest; end the current figure before beginning another.");

        _openFigureId = _strokes.BeginFigure(bounds);
        return new FigureScope(this, _openFigureId);
    }

    /// <summary>Close the currently open figure, if any (a no-op otherwise).</summary>
    public void EndFigure()
    {
        if (_openFigureId < 0) return;
        _strokes.EndFigure(_openFigureId);
        _openFigureId = -1;
    }

    // Called by FigureScope.Dispose; no-op unless this token is the open figure (handles double-dispose).
    internal void EndFigure(int figureId)
    {
        if (figureId != _openFigureId) return;
        EndFigure();
    }

    // ---- Pen strokes ---------------------------------------------------------------------------

    /// <summary>Stroke an axis-aligned line from (<paramref name="x0"/>, <paramref name="y0"/>) to
    /// (<paramref name="x1"/>, <paramref name="y1"/>), inclusive, with <paramref name="pen"/>.</summary>
    /// <exception cref="ArgumentException">The endpoints are not axis-aligned (diagonals are not supported).</exception>
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false)
    {
        if (x0 != x1 && y0 != y1)
            throw new ArgumentException("DrawLine requires an axis-aligned segment; diagonal lines are not supported.");
        if (x0 == x1 && y0 == y1)
            return;   // zero-length — nothing to draw

        int recordId = AddStrokeRecord(pen, LineBounds(x0, y0, x1, y1), overwrite);
        DepositSegment(x0, y0, x1, y1, pen.Weight, recordId, pen.Junction);
    }

    /// <summary>Stroke an axis-aligned line with a solid <paramref name="color"/>.</summary>
    public void DrawLine(int x0, int y0, int x1, int y1, Color color, bool overwrite = false) =>
        DrawLine(x0, y0, x1, y1, new Pen(color), overwrite);

    /// <summary>Stroke the outline of <paramref name="rect"/> with <paramref name="pen"/> (corners close).</summary>
    public void DrawBox(in Rect rect, in Pen pen, bool overwrite = false)
    {
        if (rect.Columns <= 0 || rect.Rows <= 0)
            return;

        int recordId = AddStrokeRecord(pen, rect, overwrite);
        int left = rect.Column, top = rect.Row, right = rect.ColumnEnd - 1, bottom = rect.RowEnd - 1;
        var weight = pen.Weight;
        var mode = pen.Junction;

        DepositSegment(left, top, right, top, weight, recordId, mode);          // top
        DepositSegment(left, bottom, right, bottom, weight, recordId, mode);    // bottom
        DepositSegment(left, top, left, bottom, weight, recordId, mode);        // left
        DepositSegment(right, top, right, bottom, weight, recordId, mode);      // right
    }

    /// <summary>Stroke the outline of <paramref name="rect"/> with a solid <paramref name="color"/>.</summary>
    public void DrawBox(in Rect rect, Color color, bool overwrite = false) =>
        DrawBox(rect, new Pen(color), overwrite);

    /// <summary>
    /// Stroke the outline of <paramref name="rect"/> with <paramref name="pen"/> and, when
    /// <paramref name="fill"/> is non-null, fill its interior first (an immediate background fill the
    /// outline then strokes over). Distinct from <see cref="DrawBox(in Rect, in Pen, bool)"/>, which
    /// never fills.
    /// </summary>
    public void DrawRectangle(in Rect rect, in Pen pen, IBrush? fill = null, bool overwrite = false)
    {
        if (fill is not null)
            FillRectangle(rect, fill);
        DrawBox(rect, pen, overwrite);
    }

    /// <summary>Stroke a rectangle outline (solid <paramref name="color"/>) with an optional <paramref name="fill"/> brush.</summary>
    public void DrawRectangle(in Rect rect, Color color, IBrush? fill = null, bool overwrite = false) =>
        DrawRectangle(rect, new Pen(color), fill, overwrite);

    /// <summary>Stroke a rectangle outline and fill its interior, both solid colors.</summary>
    public void DrawRectangle(in Rect rect, Color color, Color fill, bool overwrite = false) =>
        DrawRectangle(rect, new Pen(color), new SolidColorBrush(fill), overwrite);

    // Resolve all deferred strokes to cells. Called by Scene.Draw after the draw delegate returns.
    internal void FlushDeferredStrokes()
    {
        if (_openFigureId >= 0)
            EndFigure();   // close a leaked figure (back-patch bounds) before sampling
        if (_strokes.IsEmpty)
            return;

        _strokes.Flush(EmitStrokeCell);
    }

    private void EmitStrokeCell(int column, int row, byte arms, StrokeRecord record)
    {
        var current = _surface[column, row];

        // text-beats-decoration: a glyph (or a wide glyph's continuation) already here survives.
        if (!record.Overwrite && (current.Kind == CellKind.WideContinuation || !string.IsNullOrEmpty(current.Grapheme)))
            return;

        string glyph = BoxGlyphs.Resolve(arms, record.Decoration, record.GlyphSet);
        var color = record.Brush.ColorAt(column, row, record.Bounds);
        var style = Style.Default
            .WithForeground(color)
            .WithBackground(Colors.Transparent)   // let any fill / target under the glyph show through
            .WithAttributes(record.Attributes);

        _surface.Set(column, row, glyph, in style);
    }

    private int AddStrokeRecord(in Pen pen, in Rect bounds, bool overwrite) =>
        _strokes.AddRecord(new StrokeRecord
        {
            Brush = pen.ResolveBrush(),
            Bounds = bounds,
            Decoration = new StrokeDecoration(pen.Corners, pen.Dash, pen.EndCap),
            GlyphSet = pen.GlyphSet,
            Attributes = pen.Attributes,
            Overwrite = overwrite,
        });

    // Deposit one axis-aligned segment's per-cell arms (no validation — callers guarantee axis-aligned).
    private void DepositSegment(int x0, int y0, int x1, int y1, StrokeWeight weight, int recordId, JunctionMode mode)
    {
        if (y0 == y1)   // horizontal (also the degenerate single-cell case)
        {
            int lo = Math.Min(x0, x1), hi = Math.Max(x0, x1);
            for (int x = lo; x <= hi; x++)
            {
                byte arm = 0;
                if (x > lo) arm |= StrokeAccumulator.ArmBits(Arm.Left, weight);
                if (x < hi) arm |= StrokeAccumulator.ArmBits(Arm.Right, weight);
                _strokes.Deposit(x, y0, arm, recordId, mode);
            }
        }
        else            // vertical
        {
            int lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
            for (int y = lo; y <= hi; y++)
            {
                byte arm = 0;
                if (y > lo) arm |= StrokeAccumulator.ArmBits(Arm.Up, weight);
                if (y < hi) arm |= StrokeAccumulator.ArmBits(Arm.Down, weight);
                _strokes.Deposit(x0, y, arm, recordId, mode);
            }
        }
    }

    private static Rect LineBounds(int x0, int y0, int x1, int y1)
    {
        int left = Math.Min(x0, x1), top = Math.Min(y0, y1);
        return new Rect(left, top, Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1);
    }
}
