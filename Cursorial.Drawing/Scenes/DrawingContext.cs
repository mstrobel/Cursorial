using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Text;
using Cursorial.Text;

// ReSharper disable CheckNamespace

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
    /// now — i.e. after any active translate). It is mapped into scene coordinates and intersected with
    /// the current clip; subsequent draws are bounded to the result until the returned scope is disposed.
    /// Nests. An empty intersection means subsequent draws paint nothing.
    /// </summary>
    /// <remarks>
    /// Honored by the per-cell write paths — <see cref="Set"/>, <see cref="FillRectangle(in Rect, IBrush)"/>,
    /// and <see cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in Style)"/>. In this version it
    /// does <b>not</b> bound <see cref="DrawFormattedText(FormattedText, in Rect, IBrush, OutputCapabilities)"/>,
    /// <see cref="DrawContent"/>, or deferred <see cref="Pen"/> strokes / chart braille (draw those in absolute
    /// scene coordinates, or isolate them in a sub-scene composited at an offset).
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
    /// Scalar write: place <paramref name="grapheme"/> at <paramref name="column"/>,
    /// <paramref name="row"/> with the given <paramref name="style"/>. The style's colors are
    /// stored as-is (intra-scene composition follows <see cref="CellBuffer.Set"/>'s rules); the
    /// scene's source colors are later composited onto a target by <see cref="SceneCompositor"/>.
    /// </summary>
    public void Set(int column, int row, string? grapheme, in Style style)
    {
        if (_stateStack.Count == 0) { _surface.Set(column, row, grapheme, in style); return; }
        EmitMapped(column, row, grapheme, in style);
    }

    // Translate + clip a single glyph write under an active push. A wide glyph whose right half would fall
    // outside the active clip degrades to a blank single cell (mirroring the surface-edge degrade in
    // CellBufferView.Set), so a clip can never strand a continuation past its edge.
    private void EmitMapped(int localCol, int localRow, string? grapheme, in Style style)
    {
        if (!TryMap(localCol, localRow, out int sc, out int sr)) return;
        if (!string.IsNullOrEmpty(grapheme) && GraphemeWidth.ClusterWidth(grapheme.AsSpan()) == 2
            && sc + 1 >= CurrentState.Clip.ColumnEnd)
        {
            _surface.Set(sc, sr, null, in style);
            return;
        }
        _surface.Set(sc, sr, grapheme, in style);
    }

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
    public void FillRectangle(in Rect region, IBrush brush) => FillRectangle(region, brush, region);

    /// <summary>
    /// As <see cref="FillRectangle(in Rect, IBrush)"/>, but the brush is sampled against
    /// <paramref name="brushBounds"/> — which may be larger than the painted <paramref name="region"/> — so a
    /// gradient spans the full bounds while only the region's cells are painted. Used by area-fill charts that
    /// paint one column at a time yet want the gradient to flow across the whole chart, not restart per column.
    /// </summary>
    public void FillRectangle(in Rect region, IBrush brush, in Rect brushBounds)
    {
        ArgumentNullException.ThrowIfNull(brush);

        if (_stateStack.Count != 0)
        {
            // Transformed path: sample the brush in local coordinates, write at the translated+clipped scene
            // cell. The active clip is within the scene, so a mapped cell is always a safe raw write.
            for (int row = region.Row; row < region.RowEnd; row++)
            for (int col = region.Column; col < region.ColumnEnd; col++)
            {
                if (!TryMap(col, row, out int sc, out int sr)) continue;
                var c = brush.ColorAt(col, row, brushBounds);
                _surface[sc, sr] = new Cell(null, CellKind.Single, Style.Default.WithBackground(c));
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
            var color = brush.ColorAt(col, row, brushBounds);
            _surface[col, row] = new Cell(null, CellKind.Single, Style.Default.WithBackground(color));
        }
    }

    /// <summary>Fill <paramref name="region"/> with an <b>opaque, occluding</b> solid <paramref name="color"/>.</summary>
    public void FillOpaque(in Rect region, Color color) => FillOpaque(region, new SolidColorBrush(color));

    /// <summary>
    /// Fill <paramref name="region"/> with <paramref name="brush"/> as <b>space-bearing</b> cells, so the fill
    /// <em>hides</em> (occludes) any glyph beneath it on a lower layer — unlike
    /// <see cref="FillRectangle(in Rect, IBrush)"/>, which is background-only and lets lower glyphs show through.
    /// Use it for opaque panels, modals, and menus drawn over content. A translucent brush sample is preserved
    /// (the alpha rides to the compositor for a frosted-panel effect), but the glyph beneath is still replaced.
    /// </summary>
    /// <remarks>
    /// To draw a <b>bordered</b> opaque panel, fill the box then draw the border with <c>overwrite: true</c>
    /// (<c>ctx.FillOpaque(rect, color); ctx.DrawBox(rect, pen, overwrite: true);</c>): a non-overwriting stroke
    /// yields to the fill's space cells, and an overwriting stroke over an opaque fill keeps the fill's
    /// background so the border sits on the panel rather than punching a transparent hole.
    /// </remarks>
    public void FillOpaque(in Rect region, IBrush brush)
    {
        ArgumentNullException.ThrowIfNull(brush);

        if (_stateStack.Count != 0)
        {
            for (int row = region.Row; row < region.RowEnd; row++)
            for (int col = region.Column; col < region.ColumnEnd; col++)
                if (TryMap(col, row, out int sc, out int sr))
                    RawWriteWithCleanup(sc, sr, OccluderCell(brush.ColorAt(col, row, region)));
            return;
        }

        int colStart = Math.Max(0, region.Column);
        int rowStart = Math.Max(0, region.Row);
        int colEnd = Math.Min(region.ColumnEnd, _surface.Columns);
        int rowEnd = Math.Min(region.RowEnd, _surface.Rows);

        for (int row = rowStart; row < rowEnd; row++)
        for (int col = colStart; col < colEnd; col++)
            RawWriteWithCleanup(col, row, OccluderCell(brush.ColorAt(col, row, region)));
    }

    private static Cell OccluderCell(Color color) => new(" ", CellKind.Single, Style.Default.WithBackground(color));

    // Raw-write a cell (preserving its color alpha for the compositor), first blanking any wide-glyph partner
    // the write would orphan — overwriting a WideContinuation blanks its left half (col−1); overwriting a
    // WideLeft blanks its now-dangling continuation (col+1). This is the cleanup CellBuffer.Set does, replicated
    // for the alpha-preserving raw path so a fill straddling a wide glyph can't strand a half-glyph.
    private void RawWriteWithCleanup(int col, int row, in Cell cell)
    {
        var existing = _surface[col, row];
        if (existing.Kind == CellKind.WideContinuation && col > 0)
            _surface[col - 1, row] = Cell.Blank;
        else if (existing.Kind == CellKind.WideLeft && col + 1 < _surface.Columns)
            _surface[col + 1, row] = Cell.Blank;
        _surface[col, row] = cell;
    }

    /// <summary>
    /// Paint a soft <b>drop</b> shadow cast by <paramref name="element"/> per <paramref name="geometry"/>, tinted
    /// by <paramref name="shadowColor"/> (its alpha scaled per cell by the falloff). Shadow cells lie outside the
    /// element (which occludes its own footprint); each gets a translucent <em>background</em> — the glyph it
    /// covers (if any) is preserved — so <see cref="SceneCompositor"/> darkens whatever is on the target beneath
    /// at composite time. Draw it before the element. <see cref="ShadowGeometry.Edges"/> selects which sides cast.
    /// </summary>
    /// <remarks>
    /// The shadow is the element's silhouette displaced by the offset and softened by a
    /// <see cref="ShadowGeometry.Radius"/>-cell fringe (the element occludes its own footprint, and only the
    /// casting edges show). The <b>offset</b> trims the near, lit corners (an offset-1 / radius-0 shadow is a
    /// crisp one-cell sliver, no overhang); the soft fringe spills only into a <b>casting</b> corner — where two
    /// set edges meet — never past an unlit one. The translucent background rides to the compositor (so the
    /// shadow darkens the layer beneath), while the foreground is blended at draw time so a <em>same-scene</em>
    /// glyph the shadow falls on dims too. (A glyph on a <em>lower layer</em> is not dimmed — the compositor's
    /// background-only path leaves a lower foreground untouched.)
    /// </remarks>
    public void DrawDropShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        if (element.Columns <= 0 || element.Rows <= 0) return;
        if (!TryShadow(geometry, shadowColor, out int radius, out double strength)) return;

        int dx = geometry.OffsetColumn, dy = geometry.OffsetRow;
        int sCol = element.Column + dx, sRow = element.Row + dy;
        int sColEnd = sCol + element.Columns, sRowEnd = sRow + element.Rows;

        int c0 = Math.Max(0, sCol - radius), r0 = Math.Max(0, sRow - radius);
        int c1 = Math.Min(_surface.Columns, sColEnd + radius), r1 = Math.Min(_surface.Rows, sRowEnd + radius);

        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            if (Contains(element, c, r)) continue;                                 // the element occludes itself
            if (!OnCastingSide(sCol, sRow, sColEnd, sRowEnd, geometry.Edges, c, r)) continue;
            int d = ChebyshevOutside(sCol, sRow, sColEnd, sRowEnd, c, r);
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

    /// <summary>Drop shadow with the default soft black shadow (radius 1, offset 1, strength 0.5, all edges).</summary>
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

        int c0 = Math.Max(0, element.Column), r0 = Math.Max(0, element.Row);
        int c1 = Math.Min(_surface.Columns, element.ColumnEnd), r1 = Math.Min(_surface.Rows, element.RowEnd);

        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            int d = InnerEdgeDistance(element, geometry.Edges, c, r);
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

    private static bool Contains(in Rect rect, int c, int r) =>
        c >= rect.Column && c < rect.ColumnEnd && r >= rect.Row && r < rect.RowEnd;

    // Whether a cell casts, classified against the offset silhouette [sCol,sColEnd)×[sRow,sRowEnd):
    //  • an edge cell (outside in one axis, within the other) casts when that single edge is set;
    //  • a corner cell (outside in both axes) casts only when BOTH its edges are set — so the soft fringe
    //    spills only into a casting corner, never past an unlit one;
    //  • a cell inside the silhouette but outside the element (the offset sliver) casts at full strength.
    private static bool OnCastingSide(int sCol, int sRow, int sColEnd, int sRowEnd, ShadowEdges edges, int c, int r)
    {
        bool left = c < sCol, right = c >= sColEnd;
        bool above = r < sRow, below = r >= sRowEnd;
        bool hOut = left || right, vOut = above || below;

        bool hSet = (left && edges.HasFlag(ShadowEdges.Left)) || (right && edges.HasFlag(ShadowEdges.Right));
        bool vSet = (above && edges.HasFlag(ShadowEdges.Top)) || (below && edges.HasFlag(ShadowEdges.Bottom));

        if (hOut && vOut) return hSet && vSet;   // corner
        if (hOut) return hSet;                    // left / right band
        if (vOut) return vSet;                    // top / bottom band
        return true;                              // inside the silhouette (the offset sliver) → full shadow
    }

    // Chebyshev distance from (c,r) to the rectangle [col,colEnd)×[row,rowEnd); 0 when inside.
    private static int ChebyshevOutside(int col, int row, int colEnd, int rowEnd, int c, int r) =>
        Math.Max(0, Math.Max(Math.Max(col - c, c - (colEnd - 1)), Math.Max(row - r, r - (rowEnd - 1))));

    // Distance from an interior cell to the nearest casting edge of the element; −1 when no set edge applies.
    private static int InnerEdgeDistance(in Rect element, ShadowEdges edges, int c, int r)
    {
        int best = int.MaxValue;
        if (edges.HasFlag(ShadowEdges.Left)) best = Math.Min(best, c - element.Column);
        if (edges.HasFlag(ShadowEdges.Right)) best = Math.Min(best, element.ColumnEnd - 1 - c);
        if (edges.HasFlag(ShadowEdges.Top)) best = Math.Min(best, r - element.Row);
        if (edges.HasFlag(ShadowEdges.Bottom)) best = Math.Min(best, element.RowEnd - 1 - r);
        return best == int.MaxValue ? -1 : best;
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
        if (text.IsEmpty) return 0;
        bool transformed = _stateStack.Count != 0;
        if (!transformed && (uint) row >= (uint) _surface.Rows) return 0;   // surface-row guard (no transform)

        var bg = background ?? Brushes.Transparent;

        // The run's extent (its cells on this row) is the brush bounds — sampled in local coordinates.
        int runWidth = GraphemeWidth.StringWidth(text);
        var bounds = new Rect(column, row, runWidth, 1);

        int start = column;
        var clusters = text.GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;

            var style = baseStyle.WithForeground(foreground.ColorAt(column, row, bounds))
                                 .WithBackground(bg.ColorAt(column, row, bounds));

            if (transformed)
            {
                // Translate + clip per cluster (the run advances in local columns regardless of clipping).
                EmitMapped(column, row, cluster.ToString(), in style);
                column += width;
            }
            else
            {
                if (column + width > _surface.Columns) break;   // surface-edge clip
                column += _surface.Set(column, row, cluster.ToString(), style);
            }
        }

        return column - start;
    }

    /// <summary>
    /// Paint a laid-out <paramref name="text"/> document at <paramref name="bounds"/>, coloring it with
    /// <paramref name="brush"/> sampled against <b>each block's rect</b> (block-scoped, 2-D — a gradient spans
    /// each block and resets between them). Text and horizontal rules are colored per cell; FIGlet, sized text,
    /// and inline content take one sampled color at their center (their painters take a single style) — so an
    /// image / icon that <em>degrades to a glyph</em> picks up the gradient too.
    /// </summary>
    /// <remarks>
    /// The brush colors cells that <b>inherited</b> the document foreground — i.e. whose foreground is unset
    /// (<see cref="Color.Default"/>) or equals the document's <see cref="FormattedText.DefaultStyle"/>
    /// foreground. A run's <em>own</em> explicit foreground (a markup color, a content's fallback color — one
    /// that differs from the document default) <b>wins</b> over the brush. So a document that sets a default
    /// text color still receives the gradient, while individually-colored runs keep their color.
    /// <paramref name="capabilities"/> drives protocol selection for embedded content; pass the session's
    /// negotiated capabilities. (Per-run <c>BrushedStyle</c> and inline 1-D wrap-invariant sampling arrive in a
    /// later slice; this is the single document/block brush.)
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, IBrush brush, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(brush);
        DrawFormattedCore(text, bounds, capabilities, brush);
    }

    /// <summary>
    /// Paint <paramref name="text"/> coloring only its <b>per-run</b> brushes (declared via
    /// <c>BrushedRun</c>) — runs without a brush keep their formatted style. Use the
    /// <see cref="DrawFormattedText(FormattedText, in Rect, IBrush, OutputCapabilities)"/> overload to add a
    /// document-wide brush underneath the per-run ones.
    /// </summary>
    public void DrawFormattedText(FormattedText text, in Rect bounds, OutputCapabilities capabilities)
        => DrawFormattedCore(text, bounds, capabilities, documentBrush: null);

    private void DrawFormattedCore(FormattedText text, in Rect bounds, OutputCapabilities capabilities, IBrush? documentBrush)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(capabilities);

        var documentForeground = text.DefaultStyle.Foreground;
        Rect docBounds = bounds;   // can't capture an `in` parameter in the resolver closure

        text.Paint(
            _surface,
            bounds,
            capabilities,
            // ReSharper disable once RedundantLambdaParameterType
            resolver: (in BrushedTextContext ctx) =>
                      {
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

                              return ctx.BaseStyle.WithForeground(foreground);
                          }

                          // Otherwise the document brush (if any) colors cells that inherited the document
                          // foreground; an explicit run color (differing from the default) wins.
                          if (documentBrush is null) return ctx.BaseStyle;
                          var fg = ctx.BaseStyle.Foreground;
                          bool inherited = fg.IsDefault || fg == documentForeground;

                          return inherited
                                     ? ctx.BaseStyle.WithForeground(documentBrush.ColorAt(ctx.Column, ctx.Row, ctx.Block))
                                     : ctx.BaseStyle;
                      });
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
    /// Fragments are positioned in cell units, so an integer composite offset slides them with the scene.
    /// A composite <c>Clip</c> crops fragments per protocol (Kitty via a source rectangle, Sixel / iTerm2 via
    /// pixel cropping). Opacity remains a hard terminal limit (see design doc §8): cell-layer images
    /// (Sixel / iTerm2) can't be made translucent.
    /// </remarks>
    public void DrawContent(in Rect bounds, IContent content, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(capabilities);
        content.Paint(_surface, bounds, style: default, capabilities);
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

    /// <summary>
    /// Stroke a line from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>,
    /// <paramref name="y1"/>), inclusive, with <paramref name="pen"/>. An <b>axis-aligned</b> line uses
    /// box-drawing glyphs (and junctions with other strokes); a <b>diagonal</b> line rasterizes into
    /// braille dots (sub-cell resolution), for which the pen's weight / corners / dash / cap don't apply
    /// (only its brush, attributes, and glyph set do).
    /// </summary>
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false)
    {
        if (x0 == x1 && y0 == y1)
            return;   // zero-length — nothing to draw

        if (x0 == x1 || y0 == y1)   // axis-aligned → box accumulator (exact, with junctions)
        {
            int recordId = AddStrokeRecord(pen, LineBounds(x0, y0, x1, y1), overwrite);
            DepositSegment(x0, y0, x1, y1, pen.Weight, recordId, pen.Junction);
        }
        else                        // diagonal → braille raster (Bresenham at sub-cell resolution)
        {
            DepositBrailleLine(x0, y0, x1, y1, pen, overwrite);
        }
    }

    /// <summary>Stroke a line (axis-aligned box, or diagonal braille) with a solid <paramref name="color"/>.</summary>
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
    /// <see cref="FillRectangle(in Rect, IBrush)"/> (background-only; lower glyphs show through on composite)
    /// followed by <see cref="DrawTitledBox(in Rect, in PanelTitle, in Pen, bool)"/>. For an <em>opaque</em>
    /// panel that hides content beneath, use <see cref="FillOpaque(in Rect, IBrush)"/> + <c>DrawTitledBox</c>
    /// (overwrite: true) instead.
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

        int recordId = AddStrokeRecord(pen, rect, overwrite);
        int left = rect.Column, top = rect.Row, right = rect.ColumnEnd - 1, bottom = rect.RowEnd - 1;
        var weight = pen.Weight;
        var mode = pen.Junction;

        DepositSegment(left, bottom, right, bottom, weight, recordId, mode);   // bottom
        DepositSegment(left, top, left, bottom, weight, recordId, mode);       // left
        DepositSegment(right, top, right, bottom, weight, recordId, mode);     // right

        // A title needs a pad cell each side plus a ≥2-cell line run to each corner (so the corner keeps its
        // horizontal arm): the text fits only when its width ≤ Columns − 6. Narrower → plain box, no title.
        int maxText = rect.Columns - 6;
        int textWidth = 0;
        string text = maxText >= 1 ? TruncateToWidth(title.Text!, maxText, out textWidth) : string.Empty;
        if (text.Length == 0)
        {
            DepositSegment(left, top, right, top, weight, recordId, mode);     // full top — plain box
            return;
        }

        int gapWidth = textWidth + 2;   // 1 pad cell each side of the label
        int gapStartMin = left + 2;
        int gapStartMax = right - 1 - gapWidth;
        int gapStart = title.Position switch
        {
            TitlePosition.Center => left + (rect.Columns - gapWidth) / 2,
            TitlePosition.Right => gapStartMax,
            _ => gapStartMin,
        };
        gapStart = Math.Clamp(gapStart, gapStartMin, gapStartMax);
        int gapEnd = gapStart + gapWidth - 1;

        DepositSegment(left, top, gapStart - 1, top, weight, recordId, mode);   // corner → title
        DepositSegment(gapEnd + 1, top, right, top, weight, recordId, mode);    // title → corner

        var titleBrush = title.Brush ?? pen.ResolveBrush();
        DrawText(gapStart + 1, top, text, titleBrush, background: null, Style.Default.WithAttributes(title.Attributes));
    }

    // Grapheme-aware truncation to at most maxWidth display columns; returns the kept prefix and its width.
    private static string TruncateToWidth(string text, int maxWidth, out int width)
    {
        width = 0;
        int end = 0;
        var clusters = text.AsSpan().GetGraphemeEnumerator();
        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;
            int w = GraphemeWidth.ClusterWidth(cluster);
            if (w < 1) w = 1;
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
                          : record.Brush.ColorAt(column, row, record.Bounds);
        EmitDecorationCell(column, row, BoxGlyphs.Resolve(arms, record.Decoration, record.GlyphSet),
                           color, record.Attributes, record.Overwrite);
    }

    // Average the crossing records' colors (each sampled at this cell against its own bounds) in premultiplied
    // sRGB via the running mean Color.Lerp gives.
    private static Color BlendStrokeColors(IReadOnlyList<StrokeRecord> merged, int column, int row)
    {
        var color = merged[0].Brush.ColorAt(column, row, merged[0].Bounds);
        for (int i = 1; i < merged.Count; i++)
            color = Color.Lerp(color, merged[i].Brush.ColorAt(column, row, merged[i].Bounds), 1.0 / (i + 1));
        return color;
    }

    private void EmitBrailleCell(int column, int row, byte dots, BrailleRecord record) =>
        EmitDecorationCell(column, row, BrailleGlyphs.Glyph(dots, record.GlyphSet),
                           record.Brush.ColorAt(column, row, record.Bounds), record.Attributes, record.Overwrite);

    // The shared emit tail for every deferred layer: text-beats-decoration eviction, then write the
    // (already-sampled) color through Set with a transparent background.
    private void EmitDecorationCell(int column, int row, string glyph, Color color,
                                    TextAttributes attributes, bool overwrite)
    {
        var current = _surface[column, row];

        // A glyph (or a wide glyph's continuation) already here survives unless this layer overwrites.
        if (!overwrite && (current.Kind == CellKind.WideContinuation || !string.IsNullOrEmpty(current.Grapheme)))
            return;

        // Normally the stroke keeps a transparent background so a fill / the composite target shows under the
        // glyph. But when overwriting an opaque fill (a FillOpaque panel body), keep that background so the
        // border sits ON the panel instead of punching a transparent hole that lets a lower layer bleed
        // through behind the glyph — the one case that makes a bordered opaque panel expressible.
        var underBg = current.Style.Background;
        Color background = overwrite && underBg.Kind != ColorKind.Default && underBg.IsOpaque
                               ? underBg
                               : Colors.Transparent;
        var style = Style.Default
            .WithForeground(color)
            .WithBackground(background)
            .WithAttributes(attributes);

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

    // Rasterize a diagonal line into braille dots. Endpoints map to the cell's top-left dot (x·2, y·4).
    private void DepositBrailleLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite)
    {
        int recordId = AddBrailleRecord(pen, LineBounds(x0, y0, x1, y1), overwrite);
        PlotBrailleSegment(x0 * 2, y0 * 4, x1 * 2, y1 * 4, recordId);
    }

    // Braille sub-cell seam (used by DrawLine's diagonal path and by the chart layer). The sub-cell grid
    // is 2 sub-columns × 4 sub-rows per cell; coordinates are absolute (scene) sub-cell units.

    /// <summary>Begin a braille stroke (its brush is sampled at flush against <paramref name="bounds"/>); returns its record id.</summary>
    internal int AddBrailleRecord(in Pen pen, in Rect bounds, bool overwrite)
    {
        _braille ??= new BrailleRaster(_surface.Columns, _surface.Rows);
        return _braille.AddRecord(new BrailleRecord
        {
            Brush = pen.ResolveBrush(),
            Bounds = bounds,
            Attributes = pen.Attributes,
            GlyphSet = pen.GlyphSet,
            Overwrite = overwrite,
        });
    }

    /// <summary>Plot a single braille dot at an absolute sub-cell coordinate for <paramref name="recordId"/>.</summary>
    internal void PlotBrailleDot(int subColumn, int subRow, int recordId) =>
        _braille!.Plot(subColumn, subRow, recordId);

    /// <summary>Bresenham a braille segment between two absolute sub-cell coordinates for <paramref name="recordId"/>.</summary>
    internal void PlotBrailleSegment(int subX0, int subY0, int subX1, int subY1, int recordId)
    {
        int dx = Math.Abs(subX1 - subX0), dy = -Math.Abs(subY1 - subY0);
        int stepX = subX0 < subX1 ? 1 : -1, stepY = subY0 < subY1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            _braille!.Plot(subX0, subY0, recordId);
            if (subX0 == subX1 && subY0 == subY1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; subX0 += stepX; }
            if (e2 <= dx) { err += dx; subY0 += stepY; }
        }
    }
}
