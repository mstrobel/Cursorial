using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// The authoring surface handed to <see cref="Scene.Draw"/>. It draws into the scene's backing
/// buffer — the one place an <see cref="IBrush"/> is resolved to a scalar <see cref="CellStyle"/> before
/// reaching a cell. It exposes a scalar <see cref="Set"/>, a brush
/// <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> (solid or gradient), single-line brush
/// <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>, and
/// <see cref="Pen"/>-based <see cref="DrawLine(int, int, int, int, in Pen, bool, Arm?)"/> /
/// <see cref="DrawBox(in Rect, in Pen, bool)"/> / <see cref="DrawRectangle(in Rect, in Pen, IBrush?, bool)"/>.
/// <see cref="Color"/> overloads wrap a <see cref="SolidColorBrush"/> / <see cref="Pen"/> for the
/// common solid case.
/// </summary>
/// <remarks>
/// <see cref="Set"/> / <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> /
/// <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>
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
    private BrailleRaster? _braille;            // lazy — only allocated when a diagonal/braille line is drawn
    private int _openFigureId = -1;             // -1 = no explicit figure open

    // Clip + translate state stack (empty = identity: no translate, clip = the whole scene). Each push
    // composes onto the current top; coordinates the per-cell write paths receive are mapped through it.
    private readonly List<DrawState> _stateStack = [];

    private readonly record struct DrawState(int Dx, int Dy, Rect Clip);

    private DrawState CurrentState => _stateStack.Count > 0 ? _stateStack[^1] : new DrawState(0, 0, Bounds);

    internal DrawingContext(Scene scene)
    {
        _surface = scene.Buffer.AsView();
        Bounds = scene.Bounds;
        _strokes = new StrokeAccumulator(_surface.Columns, _surface.Rows);
    }

    /// <summary>The scene's bounds, in scene-local coordinates.</summary>
    public Rect Bounds { get; }

    // ---- Clip + translate state stack ----------------------------------------------------------

    /// <summary>
    /// Push a clip rectangle, given in <b>current-local</b> coordinates (the space draw calls use right
    /// now — i.e., after any active translate). It is mapped into scene coordinates and intersected with
    /// the current clip; subsequent draws are bounded to the result until the returned scope is disposed.
    /// Nests. An empty intersection means subsequent draws paint nothing.
    /// </summary>
    /// <remarks>
    /// Honored by <b>every</b> draw path: the per-cell writes (<see cref="Set"/>,
    /// <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>, <see cref="FillOpaque(in Rect, IBrush, TextAttributes, bool)"/>,
    /// <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>), the document/content
    /// paths (<see cref="DrawFormattedText(FormattedText, in Rect, IBrush, OutputCapabilities, TextAttributes, UnderlineStyle)"/>,
    /// <see cref="DrawContent(in Rect, IContent, OutputCapabilities)"/>), the shadows, the titled boxes / panels, and the <b>deferred</b>
    /// <see cref="Pen"/> strokes and chart braille — deferred records capture the ambient translate + clip at
    /// <em>record</em> time (the draw call), not at flush, so junctions still form in final scene coordinates.
    /// See <see cref="DrawContent(in Rect, IContent, OutputCapabilities)"/> for the one residual limitation around protocol <em>fragments</em>.
    /// </remarks>
    public DrawingStateScope PushClip(in Rect clip)
    {
        var s = CurrentState;
        _stateStack.Add(s with { Clip = IntersectClip(s.Clip, clip.Column + s.Dx, clip.Row + s.Dy, clip.Columns, clip.Rows) });
        return new DrawingStateScope(this, _stateStack.Count);
    }

    /// <summary>
    /// Push an integer cell translation: subsequent draw coordinates are shifted by
    /// (<paramref name="columns"/>, <paramref name="rows"/>) when written to the scene. Offsets may be
    /// <b>negative</b> (content scrolled above / left of its viewport). Composes additively with any active
    /// translate. Nests. See <see cref="PushClip"/> for which draw calls honor it.
    /// </summary>
    public DrawingStateScope PushTranslate(int columns, int rows)
    {
        var s = CurrentState;
        _stateStack.Add(s with { Dx = s.Dx + columns, Dy = s.Dy + rows });
        return new DrawingStateScope(this, _stateStack.Count);
    }

    /// <summary>
    /// Push a clip and a translate together — the common "give this widget a viewport" call. The clip is in
    /// current-local coordinates (before the new translate); the translate then positions content within it.
    /// Either argument at its identity (null clip / zero offset) is a no-op for that axis. Nests.
    /// </summary>
    public DrawingStateScope Push(in Rect? clip = null, int translateColumns = 0, int translateRows = 0)
    {
        var s = CurrentState;
        var next = s with { Dx = s.Dx + translateColumns, Dy = s.Dy + translateRows };
        if (clip is { } c)
            next = next with { Clip = IntersectClip(s.Clip, c.Column + s.Dx, c.Row + s.Dy, c.Columns, c.Rows) };
        _stateStack.Add(next);
        return new DrawingStateScope(this, _stateStack.Count);
    }

    /// <summary>The active clip in scene coordinates (the scene bounds when nothing is pushed).</summary>
    public Rect CurrentClip => CurrentState.Clip;

    /// <summary>The active cumulative translate in cells (component-wise; may be negative).</summary>
    public (int Columns, int Rows) CurrentTranslate => (CurrentState.Dx, CurrentState.Dy);

    // Pop the state stack back to (and including) the scope created at this depth. Idempotent: a no-op if
    // the stack is already at or below that depth (double dispose, out-of-order dispose).
    internal void PopTo(int depth)
    {
        while (_stateStack.Count >= depth && depth > 0)
            _stateStack.RemoveAt(_stateStack.Count - 1);
    }

    // Intersect the current scene clip with a scene-coordinate rect, clamping to non-negative before
    // constructing the result (Rect is ushort-backed and throws on negative coordinates).
    private static Rect IntersectClip(in Rect current, int col, int row, int cols, int rows)
    {
        int c0 = Math.Max(current.Column, col);
        int r0 = Math.Max(current.Row, row);
        int c1 = Math.Min(current.ColumnEnd, col + Math.Max(0, cols));
        int r1 = Math.Min(current.RowEnd, row + Math.Max(0, rows));
        return c1 > c0 && r1 > r0 ? new Rect(c0, r0, c1 - c0, r1 - r0) : Rect.Empty;
    }

    // Map a current-local coordinate to a scene coordinate, returning false when it falls outside the active
    // clip (or onto a negative axis a ushort Rect can't address). The active clip is always within the scene
    // bounds, so a true result is safe for the raw indexer.
    private bool TryMap(int localCol, int localRow, out int sceneCol, out int sceneRow)
    {
        var s = CurrentState;
        sceneCol = localCol + s.Dx;
        sceneRow = localRow + s.Dy;
        if (sceneCol < 0 || sceneRow < 0) return false;
        var clip = s.Clip;
        return sceneCol >= clip.Column && sceneCol < clip.ColumnEnd
            && sceneRow >= clip.Row && sceneRow < clip.RowEnd;
    }

    /// <summary>
    /// True when a draw at the current-local (<paramref name="column"/>, <paramref name="row"/>) would land
    /// inside the active clip (the scene bounds when nothing is pushed) after the active translate — the
    /// cheap pre-test a painter can use before sampling a brush for a cell it may not paint.
    /// </summary>
    public bool IsVisible(int column, int row) => TryMap(column, row, out _, out _);

    // The paint surface for the document / content paths: under an active push, a window over the scene
    // restricted to the active clip with the local origin re-based to the active translate, so the painter
    // keeps working in current-local coordinates while the view clips per cell (including the wide-glyph
    // degrade at the window's right edge). The plain scene surface when nothing is pushed.
    private CellBufferView MappedSurface()
    {
        if (_stateStack.Count == 0) return _surface;
        var s = CurrentState;
        return _surface.View(s.Clip).WithOrigin(s.Dx, s.Dy);
    }

    /// <summary>
    /// Scalar write: place <paramref name="grapheme"/> at <paramref name="column"/>,
    /// <paramref name="row"/> with the given <paramref name="style"/>. The style's colors are
    /// stored as-is (intra-scene composition follows <see cref="CellBuffer.Set"/>'s rules); the
    /// scene's source colors are later composited onto a target by <see cref="SceneCompositor"/>.
    /// </summary>
    public void Set(int column, int row, string? grapheme, in CellStyle style)
    {
        if (_stateStack.Count == 0) { _surface.Set(column, row, grapheme, in style); return; }
        EmitMapped(column, row, grapheme, in style);
    }

    // Translate + clip a single glyph write under an active push. A wide glyph whose right half would fall
    // outside the active clip degrades to a blank single cell (mirroring the surface-edge degrade in
    // CellBufferView.Set), so a clip can never strand a continuation past its edge.
    private void EmitMapped(int localCol, int localRow, string? grapheme, in CellStyle style)
    {
        if (!TryMap(localCol, localRow, out int sc, out int sr)) return;

        if (!string.IsNullOrEmpty(grapheme) && GraphemeWidth.ClusterWidth(grapheme.AsSpan()) == 2 &&
            sc + 1 >= CurrentState.Clip.ColumnEnd)
        {
            _surface.Set(sc, sr, null, in style);
            return;
        }

        _surface.Set(sc, sr, grapheme, in style);
    }

    /// <summary>Fill <paramref name="region"/>'s backgrounds with a solid <paramref name="color"/>.</summary>
    public void FillRectangle(in Rect region, Color color, TextAttributes attributes = default)
        => FillRectangleCore(region, brush: null, color, brushBounds: region, durable: false, attributes,
                             overwrite: true);

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
    public void FillRectangle(in Rect region, IBrush brush, TextAttributes attributes = default) 
    {
        ArgumentNullException.ThrowIfNull(brush);

        FillRectangleCore(region, brush, color: default, brushBounds: region, durable: false, overwrite: true,
                          attributes: attributes);
    }

    /// <summary>
    /// As <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>, but the brush is sampled against
    /// <paramref name="brushBounds"/> — which may be larger than the painted <paramref name="region"/> — so a
    /// gradient spans the full bounds while only the region's cells are painted. Used by area-fill charts that
    /// paint one column at a time yet want the gradient to flow across the whole chart, not restart per column.
    /// </summary>
    public void FillRectangle(in Rect region, IBrush brush, in Rect brushBounds)
    {
        ArgumentNullException.ThrowIfNull(brush);
        FillRectangleCore(region, brush, color: default, brushBounds: brushBounds, durable: false, overwrite: true);
    }

    /// <summary>
    /// Fill <paramref name="region"/> with <paramref name="color"/> as <b>space-bearing</b> cells, so the fill
    /// <em>hides</em> (occludes) any glyph beneath it on a lower layer <em>and</em> prevents it from being
    /// overwritten by background-only methods like <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> or
    /// <see cref="PaintRectangle(in Rect, IBrush, TextAttributes, bool)"/>, which allow lower glyphs show through,
    /// either at the compositor level or within the same scene, respectively. Use it for opaque panels, modals,
    /// and menus drawn over content. A translucent brush sample is preserved (the alpha rides to the compositor
    /// for a frosted-panel effect), but the glyph beneath is still replaced.
    /// </summary>
    /// <remarks>
    /// To draw a <b>bordered</b> opaque panel, fill the box then draw the border with <c>overwrite: true</c>
    /// (<c>ctx.FillOpaque(rect, color); ctx.DrawBox(rect, pen, overwrite: true);</c>): a non-overwriting stroke
    /// yields to the fill's space cells, and an overwriting stroke over an opaque fill keeps the fill's
    /// background so the border sits on the panel rather than punching a transparent hole.
    /// </remarks>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="color">The fill color.</param>
    /// <param name="attributes">
    /// Text attributes applied to every occluder cell (default none). Because <c>FillOpaque</c> writes
    /// space-bearing (opaque) cells, the attribute composites onto the screen — e.g.,
    /// <see cref="TextAttributes.Inverse"/> reverse-videos the whole filled region even when the brush color
    /// is <c>Default</c> (the NoColor reverse-video face). <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>'s
    /// transparent tint cannot do this — its background-only cells drop the attribute on composite.
    /// </param>
    /// <param name="overwrite">Whether to overwrite existing non-whitespace content. Default is <c>true</c>.</param>
    public void FillOpaque(in Rect region, Color color, TextAttributes attributes = default, bool overwrite = true)
        => FillRectangleCore(region, brush: null, color: color, brushBounds: region, durable: true, attributes, overwrite);

    /// <summary>
    /// Fill <paramref name="region"/> with <paramref name="brush"/> as <b>space-bearing</b> cells, so the fill
    /// <em>hides</em> (occludes) any glyph beneath it on a lower layer <em>and</em> prevents it from being
    /// overwritten by background-only methods like <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> or
    /// <see cref="PaintRectangle(in Rect, IBrush, TextAttributes, bool)"/>, which allow lower glyphs show through,
    /// either at the compositor level or within the same scene, respectively. Use it for opaque panels, modals,
    /// and menus drawn over content. A translucent brush sample is preserved (the alpha rides to the compositor
    /// for a frosted-panel effect), but the glyph beneath is still replaced.
    /// </summary>
    /// <remarks>
    /// To draw a <b>bordered</b> opaque panel, fill the box then draw the border with <c>overwrite: true</c>
    /// (<c>ctx.FillOpaque(rect, color); ctx.DrawBox(rect, pen, overwrite: true);</c>): a non-overwriting stroke
    /// yields to the fill's space cells, and an overwriting stroke over an opaque fill keeps the fill's
    /// background so the border sits on the panel rather than punching a transparent hole.
    /// </remarks>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="brush">The brush sampled per cell for the fill color (a solid brush returns one color).</param>
    /// <param name="attributes">
    /// Text attributes applied to every occluder cell (default none). Because <c>FillOpaque</c> writes
    /// space-bearing (opaque) cells, the attribute composites onto the screen — e.g.,
    /// <see cref="TextAttributes.Inverse"/> reverse-videos the whole filled region even when the brush color
    /// is <c>Default</c> (the NoColor reverse-video face). <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>'s
    /// transparent tint cannot do this — its background-only cells drop the attribute on composite.
    /// </param>
    /// <param name="overwrite">Whether to overwrite existing non-whitespace content. Default is <c>true</c>.</param>
    public void FillOpaque(in Rect region, IBrush brush, TextAttributes attributes = default, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(brush);
        FillRectangleCore(region, brush, color: default, brushBounds: region, durable: true, attributes, overwrite);
    }

    /// <summary>
    /// Fill <paramref name="region"/> with <paramref name="brush"/> as <b>space-bearing</b> cells, so the fill
    /// <em>hides</em> (occludes) any glyph beneath it on a lower layer <em>and</em> prevents it from being
    /// overwritten by background-only methods like <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> or
    /// <see cref="PaintRectangle(in Rect, IBrush, TextAttributes, bool)"/>, which allow lower glyphs show through,
    /// either at the compositor level or within the same scene, respectively. Use it for opaque panels, modals,
    /// and menus drawn over content. A translucent brush sample is preserved (the alpha rides to the compositor
    /// for a frosted-panel effect), but the glyph beneath is still replaced.
    /// </summary>
    /// <remarks>
    /// To draw a <b>bordered</b> opaque panel, fill the box then draw the border with <c>overwrite: true</c>
    /// (<c>ctx.FillOpaque(rect, color); ctx.DrawBox(rect, pen, overwrite: true);</c>): a non-overwriting stroke
    /// yields to the fill's space cells, and an overwriting stroke over an opaque fill keeps the fill's
    /// background so the border sits on the panel rather than punching a transparent hole.
    /// </remarks>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="brush">The brush sampled per cell for the fill color (a solid brush returns one color).</param>
    /// <param name="brushBounds">The sampling region for the <paramref name="brush"/>; may be larger or smaller than <paramref name="region"/>.</param>
    /// <param name="attributes">
    /// Text attributes applied to every occluder cell (default none). Because <c>FillOpaque</c> writes
    /// space-bearing (opaque) cells, the attribute composites onto the screen — e.g.,
    /// <see cref="TextAttributes.Inverse"/> reverse-videos the whole filled region even when the brush color
    /// is <c>Default</c> (the NoColor reverse-video face). <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>'s
    /// transparent tint cannot do this — its background-only cells drop the attribute on composite.
    /// </param>
    /// <param name="overwrite">Whether to overwrite existing non-whitespace content. Default is <c>true</c>.</param>
    public void FillOpaque(in Rect region, IBrush brush, in Rect brushBounds, TextAttributes attributes = default, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(brush);
        FillRectangleCore(region, brush, color: default, brushBounds: brushBounds, durable: true, attributes, overwrite);
    }

    /// <summary>
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="color"/>. Each cell is painted background-only
    /// (no glyph). The fill tints the target background <em>within the scene</em>, and the <paramref name="overwrite"/>
    /// argument governs whether the existing glyph is left showing through when painting with less than full opacity.
    /// </summary>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="color">The fill color.</param>
    /// <param name="attributes">Text attributes applied to every cell (default none).</param>
    /// <param name="overwrite">Whether an existing glyph is left showing through when painting with less than full opacity.</param>
    /// <remarks>
    /// Unlike <see cref="FillRectangle(in Rect, Color, TextAttributes)"/>, which intends for blending to be applied by the compositor
    /// across scenes, this method performs blending <em>intra-scene</em>. 
    /// </remarks>
    public void PaintRectangle(in Rect region, Color color, TextAttributes attributes = default, bool overwrite = false)
        => FillRectangleCore(region, brush: null, color: color, brushBounds: region, durable: false, attributes, overwrite);

    /// <summary>
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="brush"/> (solid or gradient), sampled per cell
    /// with <paramref name="region"/> as the brush bounds. Each cell is painted background-only (no glyph). The fill
    /// tints the target background <em>within the scene</em>, and the <paramref name="overwrite"/> argument governs whether
    /// the existing glyph is left showing through when painting with less than full opacity.
    /// </summary>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="brush">The brush sampled per cell for the fill color (a solid brush returns one color).</param>
    /// <param name="attributes">Text attributes applied to every cell (default none).</param>
    /// <param name="overwrite">Whether an existing glyph is left showing through when painting with less than full opacity.</param>
    /// <remarks>
    /// Unlike <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/>, which intends for blending to be applied by the compositor
    /// across scenes, this method performs blending <em>intra-scene</em>. 
    /// </remarks>
    public void PaintRectangle(in Rect region, IBrush brush, TextAttributes attributes = default, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(brush);
        FillRectangleCore(region, brush, default, region, durable: false, attributes, overwrite);
    }

    /// <summary>
    /// Fill <paramref name="region"/>'s backgrounds with <paramref name="brush"/> (solid or gradient), sampled per cell
    /// with <paramref name="region"/> as the brush bounds. Each cell is painted background-only (no glyph). The fill
    /// tints the target background <em>within the scene</em>, and the <paramref name="overwrite"/> argument governs whether
    /// the existing glyph is left showing through when painting with less than full opacity.
    /// </summary>
    /// <param name="region">The rectangle to fill, in current-local coordinates (mapped through any active translate).</param>
    /// <param name="brush">The brush sampled per cell for the fill color (a solid brush returns one color).</param>
    /// <param name="brushBounds">The sampling rectangle to be used for the brush, when distinct from <paramref name="region"/>.</param>
    /// <param name="attributes">Text attributes applied to every cell (default none).</param>
    /// <param name="overwrite">Whether an existing glyph is left showing through when painting with less than full opacity.</param>
    /// <remarks>
    /// Unlike <see cref="FillRectangle(in Rect, IBrush, in Rect)"/>, which intends for blending to be applied
    /// by the compositor across scenes, this method performs blending <em>intra-scene</em>. 
    /// </remarks>
    public void PaintRectangle(in Rect region, IBrush brush, in Rect brushBounds, TextAttributes attributes = default, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(brush);
        FillRectangleCore(region, brush, default, brushBounds, durable: false, attributes, overwrite);
    }

    private void FillRectangleCore(in Rect region, IBrush? brush, Color color, in Rect brushBounds, bool durable,
                                   TextAttributes attributes = default, bool overwrite = false)
    {
        if (_stateStack.Count != 0)
        {
            // Transformed path: sample the brush in local coordinates, write at the translated scene cell.
            // The iteration range is pre-clamped to the active clip mapped back into local space — an
            // oversized region must cost O(visible), not O(requested) (Rect permits 65535×65535) — so every
            // remaining cell lands inside the clip, which is within the scene: a safe raw write.
            var s = CurrentState;
            int lColStart = Math.Max(region.Column, s.Clip.Column - s.Dx);
            int lRowStart = Math.Max(region.Row, s.Clip.Row - s.Dy);
            int lColEnd = Math.Min(region.ColumnEnd, s.Clip.ColumnEnd - s.Dx);
            int lRowEnd = Math.Min(region.RowEnd, s.Clip.RowEnd - s.Dy);

            for (int row = lRowStart; row < lRowEnd; row++)
            for (int col = lColStart; col < lColEnd; col++)
            {
                var c = brush?.ColorAt(col, row, brushBounds) ?? color;
                var cell = durable ? DurableCell(c, attributes) : WeakCell(c, attributes);

                if (overwrite)
                {
                    int col1 = col + s.Dx;
                    int row1 = row + s.Dy;
                    _surface[col1, row1] = cell;
                }
                else
                    _surface.Set(col + s.Dx, row + s.Dy, cell.Grapheme, cell.Style);
            }
            return;
        }

        int colStart = Math.Max(0, region.Column);
        int rowStart = Math.Max(0, region.Row);
        int colEnd = Math.Min(region.ColumnEnd, _surface.Columns);
        int rowEnd = Math.Min(region.RowEnd, _surface.Rows);
        if (colStart >= colEnd || rowStart >= rowEnd) return;

        for (int row = rowStart; row < rowEnd; row++)
        for (int col = colStart; col < colEnd; col++)
        {
            var c = brush?.ColorAt(col, row, brushBounds) ?? color;
            var cell = durable ? DurableCell(c, attributes) : WeakCell(c, attributes);

            if (overwrite)
                _surface[col, row] = cell;
            else
                _surface.Set(col, row, cell.Grapheme, cell.Style);
        }
    }

    private static Cell DurableCell(Color color, TextAttributes attributes = default)
        => new(CellBuffer.DurableEmptyGrapheme, CellKind.Single, CellStyle.Default.WithBackground(color).WithAttributes(attributes));

    private static Cell WeakCell(Color color, TextAttributes attributes = default)
        => new(null, CellKind.Single, CellStyle.Default.WithBackground(color).WithAttributes(attributes));

    /// <summary>
    /// Paint a soft <b>drop</b> shadow cast by <paramref name="element"/> per <paramref name="geometry"/>, tinted
    /// by <paramref name="shadowColor"/> (its alpha scaled per cell by the falloff). Shadow cells lie outside the
    /// element (which occludes its own footprint); each gets a translucent <em>background</em> — the glyph it
    /// covers (if any) is preserved — so <see cref="SceneCompositor"/> darkens whatever is on the target beneath
    /// at composite time. Draw it before the element. <see cref="ShadowGeometry.Edges"/> selects which sides cast.
    /// </summary>
    /// <remarks>
    /// The shadow is the element's silhouette grown by a <see cref="ShadowGeometry.Radius"/>-cell soft fringe on
    /// the casting edges (the element occludes its own footprint, and only the casting edges show). The fringe is
    /// aspect-corrected (about half as deep vertically as it is wide) and rounds toward a non-casting corner; see
    /// <see cref="ShadowDistance"/>. The translucent background rides to the compositor (so the shadow darkens the
    /// layer beneath), while the foreground is blended at draw time so a <em>same-scene</em> glyph the shadow
    /// falls on dims too. (A glyph on a <em>lower layer</em> is not dimmed — the compositor's background-only path
    /// leaves a lower foreground untouched.)
    /// </remarks>
    public void DrawDropShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        if (element.Columns <= 0 || element.Rows <= 0) return;
        if (!TryShadow(geometry, shadowColor, out int radius, out double strength)) return;

        // The whole composite (element silhouette + fringe) translates as a unit by the ambient push translate,
        // and the painted band is bounded by the ambient clip (the scene bounds when nothing is pushed).
        var state = CurrentState;
        int eCol = element.Column + state.Dx, eRow = element.Row + state.Dy;
        int eColEnd = eCol + element.Columns, eRowEnd = eRow + element.Rows;

        int c0 = Math.Max(state.Clip.Column, eCol - radius), r0 = Math.Max(state.Clip.Row, eRow - radius);
        int c1 = Math.Min(state.Clip.ColumnEnd, eColEnd + radius), r1 = Math.Min(state.Clip.RowEnd, eRowEnd + radius);

        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            if (c >= eCol && c < eColEnd && r >= eRow && r < eRowEnd) continue;    // the element occludes itself
            if (!OnCastingSide(eCol, eRow, eColEnd, eRowEnd, geometry.Edges, c, r)) continue;
            int d = ShadowDistance(eCol, eRow, eColEnd, eRowEnd, c, r, radius, geometry.Edges);
            if (d > radius) continue;

            byte alpha = ShadowAlpha(shadowColor.Alpha, strength, d, radius);
            if (alpha == 0) continue;

            var existing = _surface[c, r];
            if (existing.Style.Background is { Kind: ColorKind.Rgb } prior && prior.Alpha >= alpha) continue;

            // Background stays translucent for the compositor; foreground blends now so a same-scene glyph dims.
            var shadow = shadowColor.WithAlpha(alpha);
            var fg = Color.Composite(shadow, existing.Style.Foreground, BlendingModes.Default);
            _surface[c, r] = existing with { Style = existing.Style.WithForeground(fg).WithBackground(shadow) };
        }
    }

    /// <summary>Drop shadow with the default soft black shadow (radius 1, strength 0.5, all edges).</summary>
    public void DrawDropShadow(in Rect element) => DrawDropShadow(element, ShadowGeometry.Drop(), Color.FromRgb(0, 0, 0));

    /// <summary>
    /// Paint a soft <b>inner</b> shadow inside <paramref name="element"/>'s edges per <paramref name="geometry"/>,
    /// tinted by <paramref name="shadowColor"/>. Unlike a drop shadow this is a read-modify-write that darkens each
    /// cell's existing background <em>at draw time</em> (compositing the shadow over the cell's own fill and storing
    /// the opaque result), preserving any glyph. Alpha falls off toward the interior; the offset fields are ignored.
    /// Draw it after the fill it insets. <see cref="ShadowGeometry.Edges"/> selects which sides cast.
    /// </summary>
    public void DrawInnerShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        if (element.Columns <= 0 || element.Rows <= 0) return;
        if (!TryShadow(geometry, shadowColor, out int radius, out double strength)) return;

        // Translated as a unit by the ambient push translate; bounded by the ambient clip.
        var state = CurrentState;
        int eCol = element.Column + state.Dx, eRow = element.Row + state.Dy;
        int eColEnd = eCol + element.Columns, eRowEnd = eRow + element.Rows;

        int c0 = Math.Max(state.Clip.Column, eCol), r0 = Math.Max(state.Clip.Row, eRow);
        int c1 = Math.Min(state.Clip.ColumnEnd, eColEnd), r1 = Math.Min(state.Clip.RowEnd, eRowEnd);

        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            int d = InnerEdgeDistance(eCol, eRow, eColEnd, eRowEnd, geometry.Edges, c, r);
            if (d < 0 || d > radius) continue;

            byte alpha = ShadowAlpha(shadowColor.Alpha, strength, d, radius);
            if (alpha == 0) continue;

            // Darken the cell's own fill (and any glyph on it) at draw time, storing the opaque result.
            var existing = _surface[c, r];
            var sh = shadowColor.WithAlpha(alpha);
            var bg = Color.Composite(sh, existing.Style.Background, BlendingModes.Default);
            var fg = Color.Composite(sh, existing.Style.Foreground, BlendingModes.Default);
            _surface[c, r] = existing with { Style = existing.Style.WithForeground(fg).WithBackground(bg) };
        }
    }

    /// <summary>Inner shadow with the default soft black inner shadow (radius 1, strength 0.5, all edges).</summary>
    public void DrawInnerShadow(in Rect element) => DrawInnerShadow(element, ShadowGeometry.Inner(), Color.FromRgb(0, 0, 0));

    private static bool TryShadow(in ShadowGeometry geometry, Color shadowColor, out int radius, out double strength)
    {
        radius = Math.Max(0, geometry.Radius);
        strength = Math.Clamp(geometry.Strength, 0.0, 1.0);
        // Only an RGB shadow color carries an alpha to scale; default / palette have none to fade.
        return geometry.Edges != ShadowEdges.None && strength > 0.0 && shadowColor.Kind == ColorKind.Rgb;
    }

    // Per-cell shadow alpha: peak strength at the casting edge (d = 0), linear falloff to 0 across the radius.
    private static byte ShadowAlpha(byte sourceAlpha, double strength, int distance, int radius)
    {
        double falloff = radius == 0 ? 1.0 : 1.0 - (double) distance / (radius + 1);
        return (byte) Math.Clamp(Math.Round(sourceAlpha * strength * falloff), 0, 255);
    }

    // Whether a cell casts, classified against the element [col,colEnd)×[row,rowEnd):
    //  • an edge cell (outside in one axis, within the other) casts when that single edge is set;
    //  • a corner cell (outside in both axes) casts only when BOTH its edges are set — so the soft fringe
    //    spills only into a casting corner, never past an unlit one.
    private static bool OnCastingSide(int col, int row, int colEnd, int rowEnd, ShadowEdges edges, int c, int r)
    {
        bool left = c < col, right = c >= colEnd;
        bool above = r < row, below = r >= rowEnd;
        bool hOut = left || right, vOut = above || below;

        bool hSet = (left && edges.HasFlag(ShadowEdges.Left)) || (right && edges.HasFlag(ShadowEdges.Right));
        bool vSet = (above && edges.HasFlag(ShadowEdges.Top)) || (below && edges.HasFlag(ShadowEdges.Bottom));

        if (hOut && vOut) return hSet && vSet;   // corner
        if (hOut) return hSet;                   // left / right band
        if (vOut) return vSet;                   // top / bottom band
        return true;                             // within the element (occluded before this call) — defensive
    }

    // The soft drop-shadow falloff distance for a casting cell (c, r), measured from the silhouette
    // [col,colEnd)×[row,rowEnd). DrawDropShadow culls d > radius; ShadowAlpha fades d = 0 (peak) → radius (faint).
    //
    // Two shaping rules give the look in the diagrams below:
    //
    //  1. ASPECT. Terminal cells are ~2× taller than wide, so the shadow reaches `radius` cells HORIZONTALLY but
    //     only rv = (radius + 1) / 2 rows VERTICALLY — a vertical step costs ~2 horizontal units — so it reads as
    //     visually round, not a tall smear. (Hence the bottom band is about half as deep as the side band is wide.)
    //
    //  2. CORNER ROUNDING. A band rounds toward a corner whose perpendicular edge does NOT cast (a soft tip), and
    //     runs full into a corner where BOTH edges cast. So with bottom+right set, the right band tapers up to a
    //     1-cell tip at the top (no top edge) and the bottom band insets on the left (no left edge) but stays full
    //     into the bottom-right corner. With bottom only, both ends of the bottom band inset symmetrically:
    //
    // ReSharper disable CommentTypo
    //
    //       +------------------+x        +------------------+        radius = 4 (→ rv = 2):
    //       |                  |xx       |                  |          • right band tapers 1,2,3,4 down to the
    //       |                  |xxx      |                  |            casting bottom-right corner;
    //       +------------------+xxxx     +------------------+          • bottom band is 2 rows deep (≈ rv) and
    //        xxxxxxxxxxxxxxxxxxxxxxx      xxxxxxxxxxxxxxxxxx             insets 1 cell per row toward a non-
    //         xxxxxxxxxxxxxxxxxxxx         xxxxxxxxxxxxxxxx              casting corner.
    //          (bottom | right)             (bottom only)
    //
    // ReSharper restore CommentTypo
    //
    // (The diagrams are approximate — the off-by-one at the tips is not load-bearing. Tune `radius`/`rv` and the
    // per-band `reach` to adjust the look; OnCastingSide gates WHICH cells cast, this gates HOW FAR / HOW SOFT.)
    private static int ShadowDistance(int col, int row, int colEnd, int rowEnd, int c, int r, int radius, ShadowEdges edges)
    {
        const int culled = int.MaxValue;

        int rv = (radius + 1) / 2; // vertical reach in rows (≈ half the horizontal reach — cells are ~2× tall)

        bool hasLeft = edges.HasFlag(ShadowEdges.Left), hasRight = edges.HasFlag(ShadowEdges.Right);
        bool hasTop = edges.HasFlag(ShadowEdges.Top), hasBottom = edges.HasFlag(ShadowEdges.Bottom);

        int hx = c < col ? col - c : c >= colEnd ? c - (colEnd - 1) : 0;  // cells past the L/R edge (1 = adjacent); 0 = within
        int vx = r < row ? row - r : r >= rowEnd ? r - (rowEnd - 1) : 0;  // cells past the T/B edge (1 = adjacent); 0 = within

        if (vx == 0) // a left/right edge band — full reach into a casting top/bottom corner, tapering to a tip otherwise
        {
            int topRoom = hasTop ? radius : r - row + 1;     // rows from the top edge (a casting top → no rounding)
            int botRoom = hasBottom ? radius : rowEnd - r;   // rows from the bottom edge
            int reach = Math.Min(radius, Math.Min(topRoom, botRoom));
            return hx <= reach ? (hx - 1) + (radius - reach) : culled;
        }

        if (hx == 0) // a top/bottom edge band — half-scale vertical depth, insetting toward a non-casting side
        {
            int leftRoom = hasLeft ? rv : c - col;           // cells from the left edge (a casting left → no inset)
            int rightRoom = hasRight ? rv : colEnd - 1 - c;  // cells from the right edge
            int reach = Math.Min(rv, Math.Min(leftRoom, rightRoom));
            return vx <= reach ? 2 * (vx - 1) + 2 * (rv - reach) : culled;
        }

        // A corner cell (outside on both axes; OnCastingSide already required both edges set): a rounded quarter in
        // the anisotropic metric — horizontal cost hx, vertical cost ~2× — clipped to the vertical reach.
        return vx <= rv ? (hx - 1) + 2 * (vx - 1) : culled;
    }
    private static int InnerEdgeDistance(int eCol, int eRow, int eColEnd, int eRowEnd, ShadowEdges edges, int c, int r)
    {
        int best = int.MaxValue;
        if (edges.HasFlag(ShadowEdges.Left)) best = Math.Min(best, c - eCol);
        if (edges.HasFlag(ShadowEdges.Right)) best = Math.Min(best, eColEnd - 1 - c);
        if (edges.HasFlag(ShadowEdges.Top)) best = Math.Min(best, r - eRow);
        if (edges.HasFlag(ShadowEdges.Bottom)) best = Math.Min(best, eRowEnd - 1 - r);
        return best == int.MaxValue ? -1 : best;
    }

    /// <summary>Draw text (multi-line capable, see the brush overload) with a solid foreground (and optional background) color.</summary>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         Color foreground, Color? background = null, in CellStyle baseStyle = default)
        => DrawText(column, row, text, new SolidColorBrush(foreground),
                    background is { } bg ? new SolidColorBrush(bg) : null, baseStyle);

    /// <summary>
    /// Draw <paramref name="text"/> starting at <paramref name="column"/>, <paramref name="row"/>,
    /// sampling <paramref name="foreground"/> (and optional <paramref name="background"/>) per cell —
    /// so a gradient brush colors the text continuously, glyph by glyph.
    /// <paramref name="background"/> defaults to transparent (glyph only). Grapheme-aware (wide
    /// clusters occupy two cells). <c>\r\n</c>, <c>\n</c>, and <c>\r</c> are line breaks: each
    /// subsequent line continues at the original start <paramref name="column"/> one row down; empty
    /// lines consume a row; the active clip/translate applies per line; the brush samples against
    /// the <b>full multi-line extent</b> (widest line × line count), so a gradient flows down the
    /// lines. A tab is substituted with one space and any other C0/C1 control is skipped (zero
    /// columns) — each with a DEBUG diagnostic (<see cref="DrawingDiagnostics"/>). Returns the
    /// text's bounding box: the widest line's column <b>advance</b> × the line count (a trailing
    /// newline yields a final empty line that counts). Per line the advance keeps the single-line
    /// contract: under an active push the run advances through clusters an active clip suppresses
    /// (the full local line width); with no push it stops at the surface's right edge and yields
    /// the clamped width (0 for an off-surface row). Negative starts never throw (design doc
    /// §13.1): with no push, rows off the surface on either side draw nothing (advance 0) and
    /// clusters starting left of column 0 are skipped while the run still advances through them;
    /// under a push, negative local coordinates flow through the translate/clip map. A negative
    /// brush-bounds anchor samples contract-equivalently (shifted to a zero origin).
    /// </summary>
    /// <remarks>
    /// Glyphs are written through <see cref="CellBuffer.Set"/>, which composites against the
    /// transparent scene backdrop and stores opaque — so per-cell <em>translucent</em> foreground /
    /// background alpha is consumed here, not preserved for the compositor. For scene-level
    /// translucency use a composite opacity instead. A transparent background correctly lets a prior
    /// fill (or the composite target) show through under the glyph.
    /// </remarks>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         IBrush foreground, IBrush? background = null, in CellStyle baseStyle = default)
    {
        ArgumentNullException.ThrowIfNull(foreground);
        if (text.IsEmpty) return Size.Empty;
        bool transformed = _stateStack.Count != 0;

        var bg = background ?? Brushes.Transparent;

        // Measure pass: the brush bounds are the full multi-line extent (widest sanitized line ×
        // line count) anchored at the start cell, sampled in local coordinates — a gradient flows
        // down the lines instead of restarting per line. The signed SampleBounds carrier (not the
        // ushort-backed Rect) keeps a negative anchor — or an extent past the Rect cap — from
        // throwing: it shifts/clamps contract-equivalently at sample time (design doc §13.1).
        int widest = 0, lineCount = 0;
        var measure = text;
        bool moreLines = true;
        while (moreLines)
        {
            var line = NextLine(ref measure, out moreLines);
            widest = Math.Max(widest, SanitizedLineWidth(line));
            lineCount++;
        }
        var bounds = new SampleBounds(column, row, widest, lineCount);

        int maxAdvance = 0;
        int currentRow = row;
        var remaining = text;
        moreLines = true;
        while (moreLines)
        {
            var line = NextLine(ref remaining, out moreLines);
            maxAdvance = Math.Max(maxAdvance, DrawTextLine(column, currentRow, line, foreground, bg, in baseStyle, in bounds, transformed));
            currentRow++;
        }

        return new Size(maxAdvance, lineCount);
    }

    // Draw one (break-free) line of text; returns the columns advanced under the single-line contract.
    private int DrawTextLine(int column, int row, ReadOnlySpan<char> line, IBrush foreground, IBrush background,
                             in CellStyle baseStyle, in SampleBounds bounds, bool transformed)
    {
        if (line.IsEmpty) return 0;
        if (!transformed && (uint) row >= (uint) _surface.Rows) return 0;   // surface-row guard (no transform; covers negative rows)

        int start = column;
        var clusters = line.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            string? substitute = null;
            int width;
            if (IsC0OrC1Control(cluster[0]))   // controls are single-char clusters; breaks never reach here
            {
                if (cluster[0] != '\t')
                {
                    DrawingDiagnostics.Emit(DrawingDiagnosticKind.ControlCharacterInText,
                                            $"DrawText skipped control character U+{(int) cluster[0]:X4} at local ({column}, {row}).");
                    continue;
                }

                DrawingDiagnostics.Emit(DrawingDiagnosticKind.TabInText,
                                        $"DrawText substituted one space for a tab at local ({column}, {row}); expand tabs upstream.");
                substitute = " ";
                width = 1;
            }
            else
            {
                width = GraphemeWidth.ClusterWidth(cluster);
                if (width < 1) width = 1;
            }

            if (transformed)
            {
                // Translate + clip per cluster (the run advances in local columns regardless of clipping).
                var style = baseStyle.WithForeground(bounds.Sample(foreground, column, row))
                                     .WithBackground(bounds.Sample(background, column, row));
                EmitMapped(column, row, substitute ?? cluster.ToString(), in style);
                column += width;
            }
            else
            {
                if (column < 0) { column += width; continue; }  // left-edge clip (negative start; the run still advances)
                if (column + width > _surface.Columns) break;   // right-edge clip (stops the line)

                var style = baseStyle.WithForeground(bounds.Sample(foreground, column, row))
                                     .WithBackground(bounds.Sample(background, column, row));
                column += _surface.Set(column, row, substitute ?? cluster.ToString(), style);
            }
        }

        return column - start;
    }

    // Slice the next line off `remaining` at the first \r\n, \n, or \r. `hasMore` reports whether a
    // break was consumed — i.e., at least one more line (possibly empty) follows.
    private static ReadOnlySpan<char> NextLine(ref ReadOnlySpan<char> remaining, out bool hasMore)
    {
        int i = remaining.IndexOfAny('\r', '\n');
        if (i < 0)
        {
            var last = remaining;
            remaining = default;
            hasMore = false;
            return last;
        }

        var line = remaining[..i];
        int skip = remaining[i] == '\r' && i + 1 < remaining.Length && remaining[i + 1] == '\n' ? 2 : 1;
        remaining = remaining[(i + skip)..];
        hasMore = true;
        return line;
    }

    // The width one line of text will advance after sanitization (tab → 1 column, other controls → 0,
    // zero-width clusters coerced to 1 as the draw loop does). Diagnostics are the draw loop's job.
    private static int SanitizedLineWidth(ReadOnlySpan<char> line)
    {
        int width = 0;
        var clusters = line.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            if (IsC0OrC1Control(cluster[0]))
            {
                if (cluster[0] == '\t') width++;
                continue;
            }

            int w = GraphemeWidth.ClusterWidth(cluster);
            width += w < 1 ? 1 : w;
        }

        return width;
    }

    // C0 (U+0000–U+001F), DEL (U+007F), and C1 (U+0080–U+009F). Controls are grapheme-cluster
    // boundaries on both sides (UAX #29), so checking a cluster's first char classifies the cluster.
    private static bool IsC0OrC1Control(char c) => c < 0x20 || (c >= 0x7F && c <= 0x9F);

    /// <summary>
    /// Paint a laid-out <paramref name="text"/> document at <paramref name="bounds"/>, coloring it with
    /// <paramref name="brush"/> sampled against <b>each block's rect</b> (block-scoped, 2-D — a gradient spans
    /// each block and resets between them). Text and horizontal rules are colored per cell; FIGlet, sized text,
    /// and inline content take one sampled color at their center (their painters take a single style) — so an
    /// image / icon that <em>degrades to a glyph</em> picks up the gradient too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brush colors cells that <b>inherited</b> the document foreground — i.e., whose foreground is unset
    /// (<see cref="Color.Default"/>) or equals the document's <see cref="FormattedText.DefaultStyle"/>
    /// foreground. A run's <em>own</em> explicit foreground (a markup color, a content's fallback color — one
    /// that differs from the document default) <b>wins</b> over the brush. So a document that sets a default
    /// text color still receives the gradient, while individually-colored runs keep their color.
    /// <paramref name="capabilities"/> drives protocol selection for embedded content; pass the session's
    /// negotiated capabilities. (Per-run <c>BrushedStyle</c> and inline 1-D wrap-invariant sampling arrive in a
    /// later slice; this is the single document/block brush.)
    /// </para>
    /// <para>
    /// <paramref name="baseAttributes"/> (default none) is the inherited-attribute leg — an ancestor's
    /// <c>TextElement.TextAttributes</c> — UNION-merged onto every painted cell's style at paint time. A run's own
    /// attributes (e.g. markup <c>[b]</c>) compose ON TOP via OR, so an inherited <see cref="TextAttributes.Inverse"/>
    /// or <see cref="TextAttributes.Faint"/> reaches the glyphs without re-laying-out the cached document — the flip
    /// is honored by a re-paint, not a re-format, so it never goes stale on an attribute-only change.
    /// </para>
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, IBrush brush, OutputCapabilities capabilities, TextAttributes baseAttributes = default, UnderlineStyle baseUnderlineShape = UnderlineStyle.Single)
    {
        ArgumentNullException.ThrowIfNull(brush);
        DrawFormattedCore(text, bounds, capabilities, brush, baseAttributes, baseUnderlineShape);
    }

    /// <summary>
    /// Paint <paramref name="text"/> coloring only its <b>per-run</b> brushes (declared via
    /// <c>BrushedRun</c>) — runs without a brush keep their formatted style. Use the
    /// <see cref="DrawFormattedText(FormattedText, in Rect, IBrush, OutputCapabilities, TextAttributes, UnderlineStyle)"/>
    /// overload to add a document-wide brush underneath the per-run ones. <paramref name="baseAttributes"/>
    /// (default none) union-merges an inherited <see cref="TextAttributes"/> onto every painted cell at paint
    /// time — see the brushed overload.
    /// </summary>
    public void DrawFormattedText(FormattedText text, in Rect bounds, OutputCapabilities capabilities, TextAttributes baseAttributes = default, UnderlineStyle baseUnderlineShape = UnderlineStyle.Single)
        => DrawFormattedCore(text, bounds, capabilities, documentBrush: null, baseAttributes, baseUnderlineShape);

    private void DrawFormattedCore(FormattedText text, in Rect bounds, OutputCapabilities capabilities, IBrush? documentBrush, TextAttributes baseAttributes = default, UnderlineStyle baseUnderlineShape = UnderlineStyle.Single)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(capabilities);

        var documentForeground = text.DefaultStyle.Foreground;
        Rect docBounds = bounds;   // can't capture an `in` parameter in the resolver closure

        // Under an active push the document paints through a clip-windowed, origin-re-based view: every
        // cell write is translated + clipped (a negative translate clips the scrolled-off top/left), while
        // the painter — and the brush resolver below — keeps working in current-local coordinates, so
        // brush sampling matches the immediate paths' local-frame convention. Embedded content fragments
        // ride the same view; bodies escaping the clip are cropped after the paint (see DrawContent).
        bool transformed = _stateStack.Count != 0;
        var clip = CurrentState.Clip;
        var fragmentsBefore = transformed ? SnapshotFragments() : null;

        text.Paint(
            MappedSurface(),
            bounds,
            capabilities,
            resolver: CreateBrushResolver(documentBrush, documentForeground, docBounds, baseAttributes, baseUnderlineShape));

        if (transformed)
            CropNewFragmentsToClip(clip, fragmentsBefore);
    }

    /// <summary>
    /// Creates a brush resolver that generates text styles based on the provided brush, foreground color,
    /// document bounds, and text attributes. The resolver determines the final style of text rendering
    /// for use in drawing operations, accommodating document-specific configurations and underline shapes.
    /// </summary>
    /// <param name="documentBrush">The brush applied to the document text. Can be null to use default behavior.</param>
    /// <param name="documentForeground">The default foreground color used for rendering text within the document bounds.</param>
    /// <param name="docBounds">Defines the rectangular boundary within which the text style applies.</param>
    /// <param name="baseAttributes">The foundational set of text attributes that augment text styling. These include alignment, weight, and other styling parameters.</param>
    /// <param name="baseUnderlineShape">Defines the default underline style to apply to the document text (e.g., single, double).</param>
    /// <returns>
    /// A <see cref="BrushedTextResolver"/> delegate that accepts a text context and resolves the appropriate
    /// text style based on the brush and other styling parameters supplied to this method.
    /// </returns>
    public static BrushedTextResolver CreateBrushResolver(IBrush? documentBrush,
                                                          Color documentForeground,
                                                          Rect docBounds,
                                                          TextAttributes baseAttributes,
                                                          UnderlineStyle baseUnderlineShape)
    {
        // ReSharper disable once RedundantLambdaParameterType
        return (in BrushedTextContext ctx) =>
               {
                   CellStyle style;

                   // A run that declares its own brush wins, sampled at its declaration scope.
                   if (ctx.Tag is BrushedStyle bs)
                   {
                       // Inline → wrap-invariant 1-D reading-order strip: sample at the cell's cumulative
                       // logical offset within the source run, over the run's total width, so the gradient
                       // flows continuously across a wrap instead of restarting per line-piece. Block /
                       // Document → the 2-D laid-out box.
                       Color foreground = bs.Scope == DeclarationScope.Inline
                                              ? bs.Foreground.ColorAt(ctx.LogicalColumn, 0, new Rect(0, 0, Math.Max(1, ctx.ScopeWidth), 1))
                                              : bs.Foreground.ColorAt(ctx.Column, ctx.Row,
                                                                      bs.Scope == DeclarationScope.Document ? docBounds : ctx.Block);

                       style = ctx.BaseStyle.WithForeground(foreground);
                   }
                   else if (documentBrush is null)
                   {
                       style = ctx.BaseStyle;
                   }
                   else
                   {
                       // Otherwise the document brush colors cells that inherited the document
                       // foreground; an explicit run color (differing from the default) wins.
                       var fg = ctx.BaseStyle.Foreground;
                       bool inherited = fg.IsDefault || fg == documentForeground;
                       style = inherited
                                   ? ctx.BaseStyle.WithForeground(documentBrush.ColorAt(ctx.Column, ctx.Row, ctx.Block))
                                   : ctx.BaseStyle;
                   }

                   // The base-attribute leg: OR the element-effective attributes onto the run's own
                   // (default none = a no-op for every pre-existing caller). When the base carries the
                   // Underline presence bit with a non-Single shape, the shape rides along (the widened
                   // seam — proposal-TextAttributes-decomposition §3.1/Q2); a run cannot author shapes
                   // today, so the base shape never overwrites authored run state.
                   if (baseAttributes == default)
                       return style;

                   var merged = style.AddAttributes(baseAttributes);
                   if (baseUnderlineShape != UnderlineStyle.Single && (baseAttributes & TextAttributes.Underline) != 0)
                       merged = merged.WithUnderlineStyle(baseUnderlineShape);

                   return merged;
               };
    }

    /// <summary>
    /// Restyles the cells in <paramref name="bounds"/> IN PLACE — graphemes are preserved, the
    /// style's background replaces each cell's (when non-default) and its attributes OR in. The
    /// selection-highlight primitive for glyph-rendered text (FIGlet editors): painting is left
    /// untouched, so glyph geometry never shifts with the selection; the highlight is a clean
    /// cell-space rectangle over whatever ink is there.
    /// </summary>
    public void TintCells(in Rect bounds, in CellStyle style)
    {
        // Per-cell translate + clip via TryMap, like the scalar write path — bounds is in
        // current-local coordinates while the active clip is in scene coordinates, so a
        // rectangle-level intersection here would mix spaces whenever a translate is active.
        for (int row = bounds.Row; row < bounds.RowEnd; row++)
        {
            for (int column = bounds.Column; column < bounds.ColumnEnd; column++)
            {
                if (!TryMap(column, row, out int sceneColumn, out int sceneRow))
                    continue;

                var cell = _surface[sceneColumn, sceneRow];
                var tinted = cell.Style with { Attributes = (cell.Style.Attributes & ~TextAttributes.Inverse) | style.Attributes };

                if (!style.Background.IsDefault)
                    tinted = tinted with { Background = style.Background };

                _surface[sceneColumn, sceneRow] = cell with { Style = tinted };
            }
        }
    }

    /// <summary>
    /// Paints <paramref name="text"/> through <paramref name="face"/> anchored at the element-local
    /// (<paramref name="column"/>, <paramref name="row"/>) — which may be NEGATIVE (a horizontally
    /// scrolled editor): the font clips to the surface, exactly like a cell write. The direct
    /// glyph-text primitive FIGlet-rendered editors paint words with.
    /// </summary>
    public void DrawGlyphText(IGlyphFont face, int column, int row, string text, in CellStyle style)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(text);

        var surface = _stateStack.Count == 0 ? _surface : MappedSurface();
        face.Paint(surface, column, row, text, style);
    }

    /// <inheritdoc cref="DrawGlyphText(IGlyphFont, int, int, string, in CellStyle)"/>
    public void DrawGlyphText(IGlyphFont face, int column, int row, string text, GlyphStyleProvider styleProvider)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(styleProvider);

        var surface = _stateStack.Count == 0 ? _surface : MappedSurface();
        face.Paint(surface, column, row, text, styleProvider);
    }

    /// <summary>
    /// Paint <paramref name="content"/> (an image, icon, sized text, or any <see cref="IContent"/>) into the
    /// scene at <paramref name="bounds"/>. Content that renders via a graphics protocol registers an
    /// out-of-band fragment on the scene buffer; <see cref="SceneCompositor"/> carries that fragment onto the
    /// composite target (offset-translated) so it renders. Content that falls back to glyphs (no protocol)
    /// paints cells like any other draw. <paramref name="capabilities"/> selects the protocol — pass the
    /// session's negotiated capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fragments are positioned in cell units, so an integer composite offset slides them with the scene.
    /// A composite <c>Clip</c> crops fragments per protocol (Kitty via a source rectangle, Sixel / iTerm2 via
    /// pixel cropping). Opacity remains a hard terminal limit (see design doc §8): cell-layer images
    /// (Sixel / iTerm2) can't be made translucent.
    /// </para>
    /// <para>
    /// Under an active <see cref="Push"/>, <paramref name="bounds"/> is translated and the content's
    /// <b>cell</b> output is clipped per cell. Fragments mirror the compositor's clip rules at draw time:
    /// a fragment whose body straddles the active clip is cropped via <c>IBufferFragment.Clip</c> (or
    /// suppressed when the protocol can't crop), and one whose anchor (its translated bounds origin) maps
    /// outside the clip — or off the scene — is dropped whole (fragment registration is anchor-keyed; the
    /// partially-visible-from-above case the compositor can express is out of reach here). A cached
    /// fragment a content reuses across re-rasters is <em>not</em> re-cropped when only the clip changed —
    /// for a moving viewport over protocol images, prefer the compositor-level
    /// <c>CompositeParameters.Clip</c>, which re-crops every frame.
    /// </para>
    /// </remarks>
    public void DrawContent(in Rect bounds, IContent content, OutputCapabilities capabilities)
        => DrawContent(bounds, content, capabilities, style: default);

    /// <summary>
    /// <inheritdoc cref="DrawContent(in Rect, IContent, OutputCapabilities)"/> The
    /// <paramref name="style"/> flows to <see cref="IContent.Paint"/> — for protocol-backed
    /// content (sized text) it is the SGR backdrop, which is how a selection background rides an
    /// OSC 66 emission (proposal-glyph-runs Phase 2).
    /// </summary>
    public void DrawContent(in Rect bounds, IContent content, OutputCapabilities capabilities, in CellStyle style)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (_stateStack.Count == 0)
        {
            content.Paint(_surface, bounds, style, capabilities);
            return;
        }

        var clip = CurrentState.Clip;
        var fragmentsBefore = SnapshotFragments();
        content.Paint(MappedSurface(), bounds, style, capabilities);
        CropNewFragmentsToClip(clip, fragmentsBefore);
    }

    // ---- Fragment clipping under an active push --------------------------------------------------

    // The scene buffer's registered fragments before a content paint (anchor + fragment identity), so the
    // paint's own additions can be told apart from pre-existing registrations. Fragment counts are tiny.
    private List<((int Column, int Row) Anchor, IBufferFragment Fragment)>? SnapshotFragments()
    {
        var fragments = _surface.Fragments;
        if (fragments.Count == 0) return null;

        var snapshot = new List<((int, int), IBufferFragment)>(fragments.Count);
        foreach (var (anchor, entry) in fragments)
            snapshot.Add((anchor, entry.Fragment));
        return snapshot;
    }

    // Mirror SceneCompositor.PassThroughFragments' clip rules at draw time for the fragments a content
    // paint just registered: a body escaping the active clip is cropped via IBufferFragment.Clip, or
    // removed when the protocol can't crop (suppression beats overdrawing past the clip). Anchors are
    // inside the clip by construction — the windowed view gated registration — so the crop never moves one.
    private void CropNewFragmentsToClip(in Rect clip, List<((int Column, int Row) Anchor, IBufferFragment Fragment)>? before)
    {
        var fragments = _surface.Fragments;
        if (fragments.Count == 0) return;

        List<((int Column, int Row) Anchor, CellBuffer.FragmentEntry Entry)>? straddling = null;
        foreach (var (anchor, entry) in fragments)
        {
            if (IsInSnapshot(before, anchor, entry.Fragment)) continue;   // pre-existing — not this paint's

            var size = entry.Fragment.GetSize();
            int columns = Math.Max(1, size.Columns), rows = Math.Max(1, size.Rows);
            if (anchor.Column + columns <= clip.ColumnEnd && anchor.Row + rows <= clip.RowEnd) continue;

            (straddling ??= []).Add((anchor, entry));
        }

        if (straddling is null) return;

        foreach (var (anchor, entry) in straddling)
        {
            var size = entry.Fragment.GetSize();
            int visibleColumns = Math.Min(Math.Max(1, size.Columns), clip.ColumnEnd - anchor.Column);
            int visibleRows = Math.Min(Math.Max(1, size.Rows), clip.RowEnd - anchor.Row);

            _surface.RemoveFragment(anchor.Column, anchor.Row);
            var cropped = entry.Fragment.Clip(new Rect(0, 0, visibleColumns, visibleRows));
            if (cropped is not null)
                _surface.AddFragment(anchor.Column, anchor.Row, cropped, entry.AnchorStyle);
        }
    }

    private static bool IsInSnapshot(List<((int Column, int Row) Anchor, IBufferFragment Fragment)>? snapshot,
                                     (int Column, int Row) anchor, IBufferFragment fragment)
    {
        if (snapshot is null) return false;
        foreach (var (snapAnchor, snapFragment) in snapshot)
            if (snapAnchor == anchor && ReferenceEquals(snapFragment, fragment))
                return true;
        return false;
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
    /// sample against <paramref name="bounds"/> instead of the figure's stroke union (e.g., to color-match
    /// a partial border to a full box drawn elsewhere). <paramref name="bounds"/> is in current-local
    /// coordinates — the ambient push translate at this call maps it into scene coordinates.
    /// </summary>
    /// <exception cref="InvalidOperationException">A figure is already open (figures do not nest).</exception>
    public FigureScope BeginFigure(in Rect bounds)
    {
        var s = CurrentState;
        return BeginFigureCore(SampleBounds.From(bounds, s.Dx, s.Dy));
    }

    private FigureScope BeginFigureCore(SampleBounds? bounds)
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

    /// <summary>
    /// Stroke a line from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), inclusive, with <paramref name="pen"/>. An <b>axis-aligned</b> line uses
    /// box-drawing glyphs (and junctions with other strokes); a <b>diagonal</b> line rasterizes into
    /// braille dots (sub-cell resolution), for which the pen's weight / corners / dash / cap don't apply
    /// (only its brush, attributes, and glyph set do).
    /// </summary>
    /// <remarks>
    /// In the event a line may have single-cell length, the framework cannot infer its direction. If you
    /// need to cover that case, simply specify <paramref name="armHint"/> with a value of <see cref="Arm.Left"/>
    /// or <see cref="Arm.Right"/> for horizontal; or <see cref="Arm.Up"/> or <see cref="Arm.Down"/> for vertical.
    /// The framework will only consult the hint when <c>x0 == x1</c> and <c>y0 == y1</c>.
    /// </remarks>
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false, Arm? armHint = null)
    {
        // EDIT: This check didn't actually exclude zero-length; it excluded single-length.
        //       We want to support that case, but can't infer direction, hence the addition of 'armHint'.
        // if (x0 == x1 && y0 == y1)
        //     return;   // zero-length — nothing to draw

        if (x0 == x1 || y0 == y1)   // axis-aligned → box accumulator (exact, with junctions)
        {
            int recordId = AddStrokeRecord(pen, LineBounds(x0, y0, x1, y1), overwrite);
            DepositSegment(x0, y0, x1, y1, pen.Weight, recordId, pen.Junction, armHint);
        }
        else                        // diagonal → braille raster (Bresenham at sub-cell resolution)
        {
            DepositBrailleLine(x0, y0, x1, y1, pen, overwrite);
        }
    }

    /// <summary>Stroke a line (axis-aligned box, or diagonal braille) with a solid <paramref name="color"/>.</summary>
    /// <remarks>
    /// In the event a line may have single-cell length, the framework cannot infer its direction. If you
    /// need to cover that case, simply specify <paramref name="armHint"/> with a value of <see cref="Arm.Left"/>
    /// or <see cref="Arm.Right"/> for horizontal; or <see cref="Arm.Up"/> or <see cref="Arm.Down"/> for vertical.
    /// The framework will only consult the hint when <c>x0 == x1</c> and <c>y0 == y1</c>.
    /// </remarks>
    public void DrawLine(int x0, int y0, int x1, int y1, Color color, bool overwrite = false, Arm? armHint = null) =>
        DrawLine(x0, y0, x1, y1, new Pen(color), overwrite, armHint);

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

    /// <summary>
    /// Draw a titled border around <paramref name="rect"/> with <paramref name="pen"/>: the box outline plus
    /// <paramref name="title"/> laid onto the top edge, the rule split around the label (a pad cell each side)
    /// so the line never overstrikes the text. A null/empty title — or a box too narrow to seat one — degrades
    /// to a plain <see cref="DrawBox(in Rect, in Pen, bool)"/>. The title is colored by its own brush, or the
    /// pen's color when the title brush is null, and is clipped to the interior span between the corners.
    /// </summary>
    public void DrawTitledBox(in Rect rect, in PanelTitle title, in Pen pen, bool overwrite = false)
    {
        if (string.IsNullOrEmpty(title.Text)) { DrawBox(rect, pen, overwrite); return; }
        DrawTitledBoxCore(rect, title, pen, overwrite);
    }

    /// <summary>Titled border with a solid <paramref name="color"/> pen.</summary>
    public void DrawTitledBox(in Rect rect, in PanelTitle title, Color color, bool overwrite = false) =>
        DrawTitledBox(rect, title, new Pen(color), overwrite);

    /// <summary>
    /// Draw a complete panel — optional <paramref name="fill"/> interior, a <paramref name="pen"/> border, and
    /// an optional <paramref name="title"/> on the top edge — the one-call "group box". Equivalent to a
    /// <see cref="FillRectangle(in Rect, IBrush, TextAttributes)"/> (background-only; lower glyphs show through on composite)
    /// followed by <see cref="DrawTitledBox(in Rect, in PanelTitle, in Pen, bool)"/>. For an <em>opaque</em>
    /// panel that hides content beneath, use <see cref="FillOpaque(in Rect, IBrush, TextAttributes, bool)"/> +
    /// <c>DrawTitledBox</c> (overwrite: true) instead.
    /// </summary>
    public void DrawPanel(in Rect rect, in Pen pen, IBrush? fill = null, PanelTitle title = default, bool overwrite = false)
    {
        if (rect.Columns <= 0 || rect.Rows <= 0) return;
        if (fill is not null) FillRectangle(rect, fill);
        DrawTitledBox(rect, title, pen, overwrite);
    }

    /// <summary>Panel with a solid border <paramref name="color"/> and optional <paramref name="fill"/> brush.</summary>
    public void DrawPanel(in Rect rect, Color color, IBrush? fill = null, PanelTitle title = default, bool overwrite = false) =>
        DrawPanel(rect, new Pen(color), fill, title, overwrite);

    /// <summary>Panel with solid border and solid fill colors plus an optional title.</summary>
    public void DrawPanel(in Rect rect, Color color, Color fill, PanelTitle title = default, bool overwrite = false) =>
        DrawPanel(rect, new Pen(color), new SolidColorBrush(fill), title, overwrite);

    // Draw a titled box. All four edges deposit under ONE stroke record (like DrawBox), so the corners form via
    // same-record self-merge — independent of the pen's JunctionMode — and the gradient samples the full rect.
    // The top edge is two runs around the title gap; the title text writes immediately (beating the deferred
    // outline) clipped to the interior, so the line and label never collide.
    private void DrawTitledBoxCore(in Rect rect, in PanelTitle title, in Pen pen, bool overwrite)
    {
        if (rect.Columns <= 0 || rect.Rows <= 0) return;

        var drawBox = pen != Pens.None;

        int left = rect.Column, top = rect.Row, right = rect.ColumnEnd - 1, bottom = rect.RowEnd - 1;
        var weight = pen.Weight;
        var mode = pen.Junction;
        int recordId = -1;
        
        if (drawBox)
        {
            recordId = AddStrokeRecord(pen, rect, overwrite);

            DepositSegment(left, bottom, right, bottom, weight, recordId, mode); // bottom
            DepositSegment(left, top, left, bottom, weight, recordId, mode);     // left
            DepositSegment(right, top, right, bottom, weight, recordId, mode);   // right
        }

        // A title is a single-line slot: sanitize to the first line before truncation/gap math
        // (design doc §13.2). An empty first line degrades to a plain box, like an empty title.
        string titleText = title.Text!;
        int breakAt = titleText.AsSpan().IndexOfAny('\r', '\n');
        if (breakAt >= 0)
        {
            DrawingDiagnostics.Emit(DrawingDiagnosticKind.MultiLineTextInSingleLineSlot,
                                    "DrawTitledBox: a PanelTitle is a single-line slot; only the first line of a multi-line title is used.");
            titleText = titleText[..breakAt];
        }

        // A title needs a pad cell each side plus a ≥2-cell line run to each corner (so the corner keeps its
        // horizontal arm): the text fits only when its width ≤ Columns − 6. Narrower → plain box, no title.
        int maxText = rect.Columns - 6;
        int textWidth = 0;
        string text = maxText >= 1 && titleText.Length > 0 ? TruncateToWidth(titleText, maxText, out textWidth) : string.Empty;
        if (text.Length == 0 && recordId >= 0)
        {
            DepositSegment(left, top, right, top, weight, recordId, mode);     // full top — plain box
            return;
        }

        int titleOffset = drawBox ? 2 : 0;
        int gapWidth = textWidth + titleOffset;   // 1 pad cell each side of the label
        int gapStartMin = left + titleOffset;
        int gapStartMax = right - 1 - gapWidth;

        int gapStart = title.Position switch
                       {
                           TitlePosition.Center => left + (rect.Columns - gapWidth) / 2,
                           TitlePosition.Right  => gapStartMax,
                           _                    => gapStartMin,
                       };

        gapStart = Math.Clamp(gapStart, gapStartMin, gapStartMax);
        int gapEnd = gapStart + gapWidth - 1;

        if (drawBox)
        {
            DepositSegment(left, top, gapStart - 1, top, weight, recordId, mode); // corner → title
            DepositSegment(gapEnd + 1, top, right, top, weight, recordId, mode);  // title → corner
        }

        var titleBrush = title.Brush ?? pen.ResolveBrush();
        DrawText(gapStart + 1, top, text, titleBrush, background: null, CellStyle.Default.WithAttributes(title.Attributes));
    }

    // Grapheme-aware truncation to at most maxWidth display columns; returns the kept prefix and
    // its width. Width accounting matches SanitizedLineWidth / the DrawText draw loop (tab → 1
    // column, other controls → 0) so the title-gap math and the painted label agree — a control
    // character in a title must not leave a hole in the top rule.
    private static string TruncateToWidth(string text, int maxWidth, out int width)
    {
        width = 0;
        int end = 0;
        var clusters = text.AsSpan().GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int w;
            if (IsC0OrC1Control(cluster[0]))
            {
                w = cluster[0] == '\t' ? 1 : 0;   // DrawText substitutes / skips — count the same
            }
            else
            {
                w = GraphemeWidth.ClusterWidth(cluster);
                if (w < 1) w = 1;
            }

            if (width + w > maxWidth) break;
            width += w;
            end += cluster.Length;
        }
        return text[..end];
    }

    // Resolve all deferred sub-cell layers to cells. Called by Scene.Draw after the draw delegate
    // returns. Order is priority high→low (each later layer yields to a glyph already in the buffer):
    // immediate writes (text/fills/bars) → braille data → box strokes/axes.
    internal void FlushDeferred()
    {
        _stateStack.Clear();   // reconcile any leaked clip/translate scope before the deferred pass

        if (_openFigureId >= 0)
            EndFigure();   // close a leaked figure (back-patch bounds) before sampling

        if (_braille is { IsEmpty: false })
            _braille.Flush(EmitBrailleCell);
        if (!_strokes.IsEmpty)
            _strokes.Flush(EmitStrokeCell);
    }

    private void EmitStrokeCell(int column, int row, byte arms, StrokeRecord record, IReadOnlyList<StrokeRecord>? merged)
    {
        // A JunctionMode.Blend crossing averages the crossing strokes' colors at this cell; otherwise the
        // owning record's brush colors it.
        Color color = merged is { Count: > 1 }
                          ? BlendStrokeColors(merged, column, row)
                          : record.Bounds.Sample(record.Brush, column, row);
        EmitDecorationCell(column, row, BoxGlyphs.Resolve(arms, record.Decoration, record.GlyphSet),
                           color, record.Attributes, record.Overwrite);
    }

    // Average the crossing records' colors (each sampled at this cell against its own bounds) in premultiplied
    // sRGB via the running mean Color.Lerp gives.
    private static Color BlendStrokeColors(IReadOnlyList<StrokeRecord> merged, int column, int row)
    {
        var color = merged[0].Bounds.Sample(merged[0].Brush, column, row);
        for (int i = 1; i < merged.Count; i++)
            color = Color.Lerp(color, merged[i].Bounds.Sample(merged[i].Brush, column, row), 1.0 / (i + 1));
        return color;
    }

    private void EmitBrailleCell(int column, int row, byte dots, BrailleRecord record) =>
        EmitDecorationCell(column, row, BrailleGlyphs.Glyph(dots, record.GlyphSet),
                           record.Bounds.Sample(record.Brush, column, row), record.Attributes, record.Overwrite);

    // The shared emit tail for every deferred layer: text-beats-decoration eviction, then write the
    // (already-sampled) color through Set with a transparent background.
    private void EmitDecorationCell(int column, int row, string glyph, Color color,
                                    TextAttributes attributes, bool overwrite)
    {
        var current = _surface[column, row];

        // A glyph (or a wide glyph's continuation) already here survives unless this layer overwrites.
        var isNullOrWhiteSpace = current.Grapheme.IsWhiteSpace() && current.Grapheme != CellBuffer.DurableEmptyGrapheme;

        if (!overwrite && (current.Kind == CellKind.WideContinuation || !isNullOrWhiteSpace))
            return;

        // Normally the stroke keeps a transparent background so a fill / the composite target shows under the
        // glyph. But when overwriting an opaque fill (a FillOpaque panel body), keep that background so the
        // border sits ON the panel instead of punching a transparent hole that lets a lower layer bleed
        // through behind the glyph — the one case that makes a bordered opaque panel expressible.
        var underBg = current.Style.Background;
        Color background = overwrite && underBg.Kind != ColorKind.Default && underBg.IsOpaque
                               ? underBg
                               : Colors.Transparent;
        var style = CellStyle.Default
            .WithForeground(color)
            .WithBackground(background)
            .WithAttributes(attributes);

        _surface.Set(column, row, glyph, in style);
    }

    // The record's sampling bounds capture the ambient translate at record time, so flush-time sampling at
    // scene cells is equivalent to local-frame sampling — matching the immediate paths' convention.
    private int AddStrokeRecord(in Pen pen, in Rect bounds, bool overwrite)
    {
        var s = CurrentState;
        return _strokes.AddRecord(new StrokeRecord
        {
            Brush = pen.ResolveBrush(),
            Bounds = SampleBounds.From(bounds, s.Dx, s.Dy),
            Decoration = new StrokeDecoration(pen.Corners, pen.Dash, pen.EndCap),
            GlyphSet = pen.GlyphSet,
            Attributes = pen.Attributes,
            Overwrite = overwrite,
        });
    }

    // Deposit one axis-aligned segment's per-cell arms (no validation — callers guarantee axis-aligned).
    // Cells are mapped through the ambient translate and clipped to the ambient clip AT DEPOSIT TIME, so
    // junctions form in final scene coordinates (strokes of one figure recorded under different translates
    // still merge where they actually cross) and a clipped-away cell deposits nothing. A cell just inside
    // the clip keeps its arm toward the clipped neighbor — the line visually runs to the viewport edge,
    // matching how a scene-edge-clipped stroke has always rendered.
    private void DepositSegment(int x0, int y0, int x1, int y1, StrokeWeight weight, int recordId, JunctionMode mode,
                                Arm? armHint = null)
    {
        var s = CurrentState;
        var horizontal = y0 == y1 && (x0 != x1 || armHint is not (Arm.Up or Arm.Down));

        if (horizontal)   // horizontal (also the degenerate single-cell case)
        {
            int lo = Math.Min(x0, x1), hi = Math.Max(x0, x1);
            for (int x = lo; x <= hi; x++)
            {
                byte arm = 0;
                if (x > lo || lo == hi) arm |= StrokeAccumulator.ArmBits(Arm.Left, weight);
                if (x < hi) arm |= StrokeAccumulator.ArmBits(Arm.Right, weight);
                DepositMapped(x, y0, arm, recordId, mode, in s);
            }
        }
        else            // vertical
        {
            int lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
            for (int y = lo; y <= hi; y++)
            {
                byte arm = 0;
                if (y > lo) arm |= StrokeAccumulator.ArmBits(Arm.Up, weight);
                if (y < hi || hi == lo) arm |= StrokeAccumulator.ArmBits(Arm.Down, weight);
                DepositMapped(x0, y, arm, recordId, mode, in s);
            }
        }
    }

    // Translate a local stroke cell into scene coordinates and deposit it when inside the captured clip.
    private void DepositMapped(int x, int y, byte arm, int recordId, JunctionMode mode, in DrawState s)
    {
        int sceneX = x + s.Dx, sceneY = y + s.Dy;
        if (sceneX < s.Clip.Column || sceneX >= s.Clip.ColumnEnd || sceneY < s.Clip.Row || sceneY >= s.Clip.RowEnd)
            return;
        _strokes.Deposit(sceneX, sceneY, arm, recordId, mode);
    }

    private static Rect LineBounds(int x0, int y0, int x1, int y1)
    {
        int left = Math.Min(x0, x1), top = Math.Min(y0, y1);
        return new Rect(left, top, Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1);
    }

    // Rasterize a diagonal line into braille dots. Endpoints map to the cell's top-left dot (x·2, y·4).
    private void DepositBrailleLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite)
    {
        int recordId = AddBrailleRecord(pen, LineBounds(x0, y0, x1, y1), overwrite);
        PlotBrailleSegment(x0 * 2, y0 * 4, x1 * 2, y1 * 4, recordId);
    }

    // Braille sub-cell seam (used by DrawLine's diagonal path and by the chart layer). The sub-cell grid
    // is 2 sub-columns × 4 sub-rows per cell; coordinates are current-local sub-cell units — each plot is
    // mapped through the ambient translate (×2 / ×4 in sub-cell units) and clipped to the ambient clip at
    // plot time, so the deferred raster accumulates in final scene coordinates.

    /// <summary>Begin a braille stroke (its brush is sampled at flush against <paramref name="bounds"/>,
    /// translated into scene coordinates by the ambient push translate); returns its record id.</summary>
    internal int AddBrailleRecord(in Pen pen, in Rect bounds, bool overwrite)
    {
        var s = CurrentState;
        _braille ??= new BrailleRaster(_surface.Columns, _surface.Rows);
        return _braille.AddRecord(new BrailleRecord
        {
            Brush = pen.ResolveBrush(),
            Bounds = SampleBounds.From(bounds, s.Dx, s.Dy),
            Attributes = pen.Attributes,
            GlyphSet = pen.GlyphSet,
            Overwrite = overwrite,
        });
    }

    /// <summary>Plot a single braille dot at a current-local sub-cell coordinate for <paramref name="recordId"/>.</summary>
    internal void PlotBrailleDot(int subColumn, int subRow, int recordId)
    {
        var s = CurrentState;
        PlotMappedBrailleDot(subColumn, subRow, recordId, in s);
    }

    /// <summary>Bresenham a braille segment between two current-local sub-cell coordinates for <paramref name="recordId"/>.</summary>
    internal void PlotBrailleSegment(int subX0, int subY0, int subX1, int subY1, int recordId)
    {
        var s = CurrentState;
        int dx = Math.Abs(subX1 - subX0), dy = -Math.Abs(subY1 - subY0);
        int stepX = subX0 < subX1 ? 1 : -1, stepY = subY0 < subY1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            PlotMappedBrailleDot(subX0, subY0, recordId, in s);
            if (subX0 == subX1 && subY0 == subY1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; subX0 += stepX; }
            if (e2 <= dx) { err += dx; subY0 += stepY; }
        }
    }

    // Map a local sub-cell dot through the ambient translate, clip its CELL against the ambient clip (the
    // clip is cell-grained, so cell-level rejection is exact), and plot. Negative mapped sub-coordinates
    // are dropped here; the raster's own range check drops the off-scene remainder.
    private void PlotMappedBrailleDot(int subX, int subY, int recordId, in DrawState s)
    {
        int sceneSubX = subX + s.Dx * 2, sceneSubY = subY + s.Dy * 4;
        if (sceneSubX < 0 || sceneSubY < 0) return;

        int column = sceneSubX >> 1, row = sceneSubY >> 2;
        if (column < s.Clip.Column || column >= s.Clip.ColumnEnd || row < s.Clip.Row || row >= s.Clip.RowEnd)
            return;

        _braille!.Plot(sceneSubX, sceneSubY, recordId);
    }

    /// <summary>
    /// Copies <paramref name="view"/>'s cells into the scene with <paramref name="region"/>'s top-left
    /// (in current-local coordinates) as the anchor — the bulk-copy counterpart to <see cref="Set"/>,
    /// used to land a separately composited surface (a layered chart) in one step.
    /// </summary>
    /// <remarks>
    /// Honors the ambient translate and clip like every other draw path: the destination is mapped
    /// through the active translate and trimmed to the active clip, with the source trimmed in step
    /// so the copy stays aligned. Without that, a caller which is not a render-boundary element (the
    /// only ones that paint at a zero translate) silently blitted onto the zone's origin.
    /// </remarks>
    public void Blit(CellBufferView view, in Rect region)
    {
        if (view.IsEmpty || region.IsEffectivelyEmpty)
            return;

        var state = CurrentState;

        int columns = Math.Min(region.Columns, view.Columns);
        int rows = Math.Min(region.Rows, view.Rows);

        // The destination in scene coordinates, clipped to the active clip.
        int sceneColumn = region.Column + state.Dx;
        int sceneRow = region.Row + state.Dy;

        var clip = _stateStack.Count == 0 ? Bounds : state.Clip;

        int column0 = Math.Max(sceneColumn, clip.Column);
        int row0 = Math.Max(sceneRow, clip.Row);
        int column1 = Math.Min(sceneColumn + columns, clip.ColumnEnd);
        int row1 = Math.Min(sceneRow + rows, clip.RowEnd);

        if (column1 <= column0 || row1 <= row0)
            return;

        // Trim the SOURCE by whatever the clip took off the leading edges, so the two stay aligned.
        var trimmed = view.View(column0 - sceneColumn, row0 - sceneRow, column1 - column0, row1 - row0);

        _surface.Blit(trimmed, new Rect(column0, row0, column1 - column0, row1 - row0));
    }
}
