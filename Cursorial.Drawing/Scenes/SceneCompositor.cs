using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.Rendering.Fragments;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Composites an ordered z-stack of <see cref="SceneLayer"/>s onto a target buffer, maintaining the
/// <b>compositing invariant</b>: a scene cell is never composited onto a previously-composited cell;
/// it is always composited onto the base layer (or a lower scene freshly composited onto base, in
/// z-order). That is what makes retained scenes correct — compositing a translucent scene onto its
/// own prior output each frame would saturate (drift); compositing onto base each frame is stable.
/// </summary>
/// <remarks>
/// <para>
/// Each call: re-rasters happen via <see cref="Scene.Draw"/> beforehand (owner-driven); the
/// compositor computes the <b>dirty-region union</b> from any scene that re-rastered (a bumped
/// <see cref="Scene.RasterVersion"/>) or any layer whose <see cref="CompositeParameters"/> changed
/// (unioning the vacated and new footprints), <b>resets just that union to base</b>, composites
/// every layer that intersects the union (bottom-up), and <see cref="CellBufferView.MarkDirty(in Rect)"/>s
/// the union so a <c>RestrictToDirtyRegions</c> renderer gets a correct bounded repaint. When
/// nothing changed it does no work and returns <see langword="false"/> — leaving the target
/// untouched so the frame renderer's own diff emits nothing.
/// </para>
/// <para>
/// The target is treated as a <b>retained</b> buffer that the compositor maintains incrementally —
/// do not clear it between frames. The base is region-reconstructable: either a uniform
/// <see cref="Style"/> or a stored backdrop <see cref="CellBuffer"/> (target-buffer coordinates).
/// </para>
/// </remarks>
public sealed class SceneCompositor
{
    private readonly Style _baseStyle;
    private readonly CellBuffer? _baseLayer;

    private Scene?[] _lastScenes = [];
    private long[] _lastVersions = [];
    private CompositeParameters[] _lastParams = [];
    // The scene dimensions at the last composite — the vacated footprint must be computed from the OLD
    // size, not the current one: a scene that SHRANK (e.g. a window resized smaller) would otherwise leave
    // the cells beyond its new bounds un-reset (stale artifacts), since both footprints would read the new
    // smaller size. (A move keeps the size, so this matches the old behavior there.)
    private int[] _lastColumns = [];
    private int[] _lastRows = [];

    // Target anchors of the fragments this compositor registered last work-frame — removed and rebuilt each
    // work-frame so a scene's fragments (images, sized text) ride the cell pass and move/disappear correctly.
    private readonly List<(int Column, int Row)> _fragmentAnchors = [];

    // Footprints of Cells-layer images this compositor has committed to the terminal. These protocols
    // (iTerm2/Sixel) have NO protocol erase — their pixels linger until a cell paints over them. Updated each
    // work-frame to the set of such images still alive in SOME scene (an occluded-but-alive image stays — its
    // pixels are still physically on the terminal even though it's suppressed from the target as a registered
    // fragment). An entry that drops out (its scene unloaded) is genuinely gone, so we force-repaint its footprint
    // to overwrite the lingering pixels. Persisted across WindowManager.ResetCompositor via AdoptGhostFootprints —
    // without that hand-off, a tab switch performed WHILE the image is occluded by a popup couldn't erase it,
    // because the occluded image is absent from target.Fragments and a fresh compositor would never have seen it.
    private List<Rect> _ghostFootprints = [];
    // Per-work-frame scratch: the (uncropped, offset-translated) footprints of every Cells-layer fragment present
    // in a scene this frame — the "still alive" set the ghost footprints are tested against, and the next ghost set.
    private List<Rect> _liveCellsFootprints = [];
    // Scratch worklists for the per-ghost rectangle subtraction (ghost minus the live footprints). Reused +
    // swapped each ghost to keep the reconciliation allocation-free.
    private List<Rect> _ghostRemainder = [];
    private List<Rect> _ghostRemainderNext = [];

    /// <summary>Composite over a uniform base fill (default: <see cref="Style.Default"/>).</summary>
    public SceneCompositor(Style baseStyle = default) => _baseStyle = baseStyle;

    /// <summary>Composite over a stored backdrop buffer (copied per-region on reset-to-base).</summary>
    public SceneCompositor(CellBuffer baseLayer) => _baseLayer = baseLayer ?? throw new ArgumentNullException(nameof(baseLayer));

    /// <summary>
    /// Carry the <b>ghost-footprint set</b> — Cells-layer images (iTerm2/Sixel) committed to the terminal with no
    /// protocol erase — from a retiring compositor into this fresh one. A host that replaces its compositor on a
    /// layer-stack change (e.g. <c>WindowManager.ResetCompositor</c>) must call this <i>before</i> the first
    /// composite, so an image removed across the reset is still force-repainted: an occluded image isn't a
    /// registered target fragment, so this hand-off is the only record of its lingering pixels. No-op when the
    /// previous compositor tracked none.
    /// </summary>
    public void AdoptGhostFootprints(SceneCompositor previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        _ghostFootprints.Clear();
        _ghostFootprints.AddRange(previous._ghostFootprints);
    }

    /// <summary>
    /// Composite the ordered z-stack onto <paramref name="target"/>. Returns <see langword="true"/>
    /// when any region was rewritten, <see langword="false"/> when nothing changed (no work done).
    /// </summary>
    public bool Composite(ReadOnlySpan<SceneLayer> layers, in CellBufferView target)
    {
        int n = layers.Length;
        bool layerSetChanged = _lastVersions.Length != n;

        int colStart = int.MaxValue, rowStart = int.MaxValue, colEnd = int.MinValue, rowEnd = int.MinValue;
        bool any = false;

        void Union(in Rect r)
        {
            if (r.IsEmpty) return;
            any = true;
            colStart = Math.Min(colStart, r.Column);
            rowStart = Math.Min(rowStart, r.Row);
            colEnd = Math.Max(colEnd, r.ColumnEnd);
            rowEnd = Math.Max(rowEnd, r.RowEnd);
        }

        if (layerSetChanged)
        {
            Union(new Rect(0, 0, target.Columns, target.Rows));   // first frame / changed stack → full
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                // Identity check first: a different Scene swapped into this slot (e.g. a fresh pooled
                // scene that happens to share RasterVersion) must recomposite, or we'd silently keep
                // the previous scene's pixels.
                bool changed = !ReferenceEquals(layers[i].Scene, _lastScenes[i]) ||
                               layers[i].Scene.RasterVersion != _lastVersions[i] ||
                               layers[i].Parameters != _lastParams[i];
                if (!changed) continue;

                if (TryFootprint(layers[i].Scene.Columns, layers[i].Scene.Rows, layers[i].Parameters, target.Columns, target.Rows, out var nf)) Union(nf);
                // Vacated footprint at the PRIOR scene size + prior params — covers a shrink (the new size
                // would miss the cells the larger scene used to occupy) and a move (size unchanged).
                if (TryFootprint(_lastColumns[i], _lastRows[i], _lastParams[i], target.Columns, target.Rows, out var of)) Union(of);
            }
        }

        if (!any)
        {
            StashState(layers);
            return false;
        }

        colStart = Math.Max(0, colStart);
        rowStart = Math.Max(0, rowStart);
        colEnd = Math.Min(target.Columns, colEnd);
        rowEnd = Math.Min(target.Rows, rowEnd);

        if (colStart >= colEnd || rowStart >= rowEnd)
        {
            StashState(layers);
            return false;
        }

        // Pass 1: reset the whole union to base (before any compositing, so a wide glyph written by
        // pass 2 isn't clobbered when its continuation column is reset).
        for (int r = rowStart; r < rowEnd; r++)
        for (int c = colStart; c < colEnd; c++)
            ResetCellToBase(target, c, r);

        // Pass 2: composite the full z-stack over the union, bottom-up. Every cell is composited
        // onto base (just reset) or onto a lower layer freshly composited this pass — the invariant.
        for (int li = 0; li < n; li++)
        {
            var p = layers[li].Parameters;
            if (!TryFootprint(layers[li].Scene.Columns, layers[li].Scene.Rows, p, target.Columns, target.Rows, out var fp)) continue;

            int fcS = Math.Max(fp.Column, colStart), frS = Math.Max(fp.Row, rowStart);
            int fcE = Math.Min(fp.ColumnEnd, colEnd), frE = Math.Min(fp.RowEnd, rowEnd);
            if (fcS >= fcE || frS >= frE) continue;

            var mode = p.Mode ?? BlendingModes.Default;
            var buffer = layers[li].Scene.Buffer;

            // A WideLeft's continuation may not extend past EITHER edge: the union's (a cell outside
            // the reset+dirty range) or the layer's own visible footprint's (the clip — writing there
            // would bleed half a glyph outside a scroll viewport / window clip onto a neighbor's cells).
            int wideColumnEnd = Math.Min(colEnd, fp.ColumnEnd);

            for (int tr = frS; tr < frE; tr++)
            for (int tc = fcS; tc < fcE; tc++)
                CompositeCell(target, tc, tr, buffer[tc - p.OffsetColumn, tr - p.OffsetRow], p.Opacity, mode, wideColumnEnd);
        }

        PassThroughFragments(layers, target, layerSetChanged);

        target.MarkDirty(new Rect(colStart, rowStart, colEnd - colStart, rowEnd - rowStart));
        StashState(layers);
        return true;
    }

    /// <summary>
    /// Carry each scene's out-of-band fragments (images, sized text) onto the target so the frame renderer
    /// emits them. Removes the fragments we registered last work-frame, then re-registers each layer's
    /// fragments at the offset-translated anchor (skipping any outside the layer's clip). Fragments are sparse
    /// and the renderer diffs them by Key + anchor, so a stable image doesn't re-emit; a moved one (offset
    /// change) erases at the old anchor and emits at the new. Layer semantics (cell-cover vs overlay) are the
    /// renderer's concern, driven off the target buffer — the compositor just relocates the anchor. Under a
    /// layer clip a fragment straddling the edge is cropped via <c>IBufferFragment.Clip</c> (Sixel pixel-crop)
    /// or, when the protocol can't crop (iTerm2 / Kitty today), suppressed rather than overdrawn.
    /// </summary>
    private void PassThroughFragments(ReadOnlySpan<SceneLayer> layers, in CellBufferView target, bool fullReset)
    {
        // Drop the prior work-frame's fragments. On a FULL recomposite (a wholesale layer-set change, or — the
        // important case — a fresh compositor after WindowManager.ResetCompositor on a popup/window topology
        // change) clear EVERY target fragment: a discarded compositor's _fragmentAnchors are gone with it, so its
        // registered fragments would otherwise be orphaned on the persistent target. That stranded a graphics-
        // protocol image on screen when a popup open/close reset the compositor during a tab switch (the image
        // was never erased because the new compositor didn't know to remove it).
        if (fullReset)
        {
            target.ClearFragments();
        }
        else
        {
            foreach (var (column, row) in _fragmentAnchors)
                target.RemoveFragment(column, row);
        }

        _fragmentAnchors.Clear();

        // Collect the (uncropped) target footprint of every Cells-layer fragment a scene wants to draw this
        // work-frame — the "still alive" set for the genuine-removal test below. An occluded image keeps its
        // entry here (its scene still has the fragment); only an unloaded scene drops out.
        _liveCellsFootprints.Clear();
        for (int li = 0; li < layers.Length; li++)
        {
            var lp = layers[li].Parameters;
            var sb = layers[li].Scene.Buffer;
            if (sb.Fragments.Count == 0) continue;
            foreach (var (anchor, entry) in sb.Fragments)
            {
                if (entry.Fragment.Layer != FragmentLayer.Cells) continue;
                var s = entry.Fragment.GetSize();
                // Clamp the origin to >= 0 and shrink the extent accordingly (Rect can't carry a negative
                // origin): a negative composite offset (scrolled content / a window dragged off the top-left)
                // makes anchor+offset < 0. The off-screen prefix never had pixels on the terminal anyway, and
                // the surviving on-screen portion still overlaps any ghost the full footprint would. Mirrors
                // TryFootprint's clamp; a footprint wholly off the top-left collapses to empty and is skipped.
                int colStart = anchor.Column + lp.OffsetColumn;
                int rowStart = anchor.Row + lp.OffsetRow;
                int width = Math.Max(1, s.Columns) + Math.Min(0, colStart);
                int height = Math.Max(1, s.Rows) + Math.Min(0, rowStart);
                if (width <= 0 || height <= 0) continue;
                _liveCellsFootprints.Add(new Rect(Math.Max(0, colStart), Math.Max(0, rowStart), width, height));
            }
        }

        for (int li = 0; li < layers.Length; li++)
        {
            var p = layers[li].Parameters;
            var sceneBuffer = layers[li].Scene.Buffer;
            if (sceneBuffer.Fragments.Count == 0) continue;
            int surfaceZ = layers[li].SurfaceZ;

            foreach (var (anchor, entry) in sceneBuffer.Fragments)
            {
                int tc = anchor.Column + p.OffsetColumn;
                int tr = anchor.Row + p.OffsetRow;
                var fragment = entry.Fragment;
                var size = fragment.GetSize();
                int fw = Math.Max(1, size.Columns), fh = Math.Max(1, size.Rows);

                // The visible target rect: the fragment footprint, intersected with this layer's clip ...
                int vCol = tc, vRow = tr, vColEnd = tc + fw, vRowEnd = tr + fh;
                if (p.Clip is { } clip)
                {
                    vCol = Math.Max(vCol, clip.Column); vRow = Math.Max(vRow, clip.Row);
                    vColEnd = Math.Min(vColEnd, clip.ColumnEnd); vRowEnd = Math.Min(vRowEnd, clip.RowEnd);
                    if (vCol >= vColEnd || vRow >= vRowEnd) continue;   // fully outside the clip → drop
                }

                // ... minus every HIGHER OPAQUE SURFACE's footprint. A graphics-protocol image is drawn by the
                // terminal above the cell grid, so a popup/window stacked over it must crop the image (a clean
                // edge overlap) or suppress it (a middle/corner overlap that can't be one source-crop) — else it
                // shows through the popup. Same-surface zones (SurfaceZ ==) never occlude their own image.
                bool suppressed = false;
                for (int lj = li + 1; lj < layers.Length && !suppressed; lj++)
                {
                    if (!layers[lj].IsOccluder || layers[lj].SurfaceZ <= surfaceZ) continue;
                    if (!TryFootprint(layers[lj].Scene.Columns, layers[lj].Scene.Rows, layers[lj].Parameters, target.Columns, target.Rows, out var occ))
                        continue;
                    suppressed = !SubtractOccluder(ref vCol, ref vRow, ref vColEnd, ref vRowEnd, occ);
                }
                if (suppressed || vCol >= vColEnd || vRow >= vRowEnd) continue;

                // Crop the fragment to the final visible sub-rect (fragment-local), or suppress if it can't crop.
                if (vCol != tc || vRow != tr || vColEnd != tc + fw || vRowEnd != tr + fh)
                {
                    var cropped = fragment.Clip(new Rect(vCol - tc, vRow - tr, vColEnd - vCol, vRowEnd - vRow));
                    if (cropped is null) continue;
                    fragment = cropped;
                    tc = vCol;
                    tr = vRow;
                }

                if (target.AddFragment(tc, tr, fragment, entry.AnchorStyle))
                    _fragmentAnchors.Add((tc, tr));
            }
        }

        // Force-repaint the part of each GHOST (a Cells-layer image committed to the terminal) NOT covered by a
        // live footprint. SUBTRACTION, not an all-or-nothing overlap test, is required so a removed image is
        // erased exactly where no surviving image now sits: a removed image partially overlapped by a different
        // live image, or a single image that SHRINKS in place (window resize), leaves an uncovered remainder
        // whose pixels would otherwise linger (Cells-layer images have no protocol erase). Subtraction also
        // preserves the occlusion non-goal — an occluded-but-alive image keeps its full uncropped footprint in
        // the live set, which covers its ghost exactly, leaving an empty remainder (nothing force-repainted) —
        // and it never force-repaints a cell under a surviving image (which would partially erase it, since a
        // stable live fragment is not re-transmitted). Matching by geometry, not fragment Key, is deliberate: an
        // occlusion crop re-encodes the fragment into a fresh-identity instance (Sixel), so a Key check would
        // mis-read a merely-occluded image as removed.
        foreach (var ghost in _ghostFootprints)
        {
            _ghostRemainder.Clear();
            _ghostRemainder.Add(ghost);
            foreach (var live in _liveCellsFootprints)
            {
                _ghostRemainderNext.Clear();
                foreach (var part in _ghostRemainder)
                    SubtractRect(part, live, _ghostRemainderNext);
                (_ghostRemainder, _ghostRemainderNext) = (_ghostRemainderNext, _ghostRemainder);
                if (_ghostRemainder.Count == 0) break;
            }
            foreach (var rem in _ghostRemainder)
                target.ForceRepaint(rem);
        }

        // The new ghost set IS this frame's live footprints — every Cells-layer image alive in a scene now (its
        // pixels are, or imminently will be, on the terminal). Swap the lists (zero-alloc); _liveCellsFootprints
        // becomes scratch that's cleared before reuse next frame.
        (_ghostFootprints, _liveCellsFootprints) = (_liveCellsFootprints, _ghostFootprints);
    }

    // Append the parts of <paramref name="a"/> not covered by <paramref name="b"/> to <paramref name="output"/>,
    // decomposing the (possibly L-/U-shaped) remainder into up to four non-overlapping axis-aligned bands. When
    // the two are disjoint, <paramref name="a"/> passes through unchanged; when b fully covers a, nothing is added.
    private static void SubtractRect(in Rect a, in Rect b, List<Rect> output)
    {
        if (!a.Intersects(b)) { output.Add(a); return; }

        int ic0 = Math.Max(a.Column, b.Column), ir0 = Math.Max(a.Row, b.Row);
        int ic1 = Math.Min(a.ColumnEnd, b.ColumnEnd), ir1 = Math.Min(a.RowEnd, b.RowEnd);

        if (a.Row < ir0)       output.Add(new Rect(a.Column, a.Row, a.Columns, ir0 - a.Row));       // top band
        if (ir1 < a.RowEnd)    output.Add(new Rect(a.Column, ir1, a.Columns, a.RowEnd - ir1));      // bottom band
        if (a.Column < ic0)    output.Add(new Rect(a.Column, ir0, ic0 - a.Column, ir1 - ir0));      // left band
        if (ic1 < a.ColumnEnd) output.Add(new Rect(ic1, ir0, a.ColumnEnd - ic1, ir1 - ir0));       // right band
    }

    // Subtract an occluder rect from the visible rect [c0,r0 .. c1,r1). A graphics-protocol fragment can only be
    // re-expressed as ONE source-crop, so the remainder must be a single rectangle: an occluder covering a clean
    // edge band narrows the rect (returns true); a full cover, a middle band, or a corner leaves a non-rectangular
    // remainder and returns false (the caller suppresses the fragment → the popup's cells / placeholder show).
    private static bool SubtractOccluder(ref int c0, ref int r0, ref int c1, ref int r1, in Rect o)
    {
        int ic0 = Math.Max(c0, o.Column), ir0 = Math.Max(r0, o.Row);
        int ic1 = Math.Min(c1, o.ColumnEnd), ir1 = Math.Min(r1, o.RowEnd);
        if (ic0 >= ic1 || ir0 >= ir1) return true;   // no overlap — unchanged

        bool fullWidth = ic0 <= c0 && ic1 >= c1;
        bool fullHeight = ir0 <= r0 && ir1 >= r1;

        if (fullWidth && fullHeight) return false;    // fully covered → suppress

        if (fullWidth)                                // a horizontal band across the whole width
        {
            if (ir0 <= r0) { r0 = ir1; return r0 < r1; }   // covers the top → keep the band below
            if (ir1 >= r1) { r1 = ir0; return r0 < r1; }   // covers the bottom → keep the band above
            return false;                             // a middle band → non-rectangular remainder
        }

        if (fullHeight)                               // a vertical band across the whole height
        {
            if (ic0 <= c0) { c0 = ic1; return c0 < c1; }   // covers the left → keep the band to the right
            if (ic1 >= c1) { c1 = ic0; return c0 < c1; }   // covers the right → keep the band to the left
            return false;                             // a middle band → non-rectangular remainder
        }

        return false;                                 // a corner / partial overlap → non-rectangular remainder
    }

    private void ResetCellToBase(in CellBufferView target, int column, int row) =>
        // A stored backdrop may be smaller than the target (e.g. the target was resized larger after
        // construction). Read the backdrop only where it covers; fall back to the uniform base style
        // beyond its bounds, so reset stays in-bounds rather than throwing.
        target[column, row] = _baseLayer is { } baseLayer && column < baseLayer.Columns && row < baseLayer.Rows
                                  ? baseLayer[column, row]
                                  : new Cell(null, CellKind.Single, _baseStyle);

    private static void CompositeCell(in CellBufferView target, int column, int row,
                                      in Cell source, byte opacity, IBlendingMode mode, int wideColumnEnd)
    {
        if (source.Kind == CellKind.WideContinuation) return;   // the WideLeft paints both columns

        var dst = target[column, row];
        var sourceStyle = opacity == 255 ? source.Style : ScaleSourceAlpha(source.Style, opacity);
        var targetStyle = dst.Style;
        var mergedBackground = Color.Composite(sourceStyle.Background, targetStyle.Background, mode);

        if (string.IsNullOrEmpty(source.Grapheme))
        {
            var blendedForeground = Color.Composite(sourceStyle.Background, targetStyle.Foreground, mode);
            var tinted = dst.Style with { Foreground = blendedForeground, Background = mergedBackground };

            // Background-only contribution: keep the target's glyph, fg, and hyperlink; merge
            // bg — the cross-layer tint contract that lets TEXT ghost through translucent chrome
            // (menus/popups) dimmed, or hide under an opaque cover (fg == bg). COLOR EMOJI break
            // both: the terminal draws the bitmap regardless of the SGR foreground — full-bright
            // through an opaque dialog AND through a translucent menu's dimming veil (both
            // observed live on macOS; a menu row reverting from its opaque selection highlight
            // to the translucent normal background resurfaced the emoji beneath it). A bitmap
            // cannot be tinted — only removed — so emoji are STOMPED under ANY cover whose
            // background contributes at all (not fully transparent; Palette/Default replace
            // outright, and a layer-opacity fade scales to transparent and correctly keeps).
            // Text-selection highlights are unaffected: they draw within their own scene at
            // raster time (background first, glyphs after) and never reach this cross-layer path.
            if (!sourceStyle.Background.IsTransparent)
            {
                if (dst.Grapheme is { Length: > 0 } glyph && GraphemeWidth.IsEmojiPresentation(glyph))
                {
                    // Stomp, then repair the pair partner EXPLICITLY: the maintaining indexer's
                    // hygiene blanks it with default(Style), which would punch a terminal-default
                    // hole where a cover edge lands mid-pair (the partner may lie OUTSIDE this
                    // layer's footprint and never be recomposited this pass).
                    var partnerStyle = dst.Kind == CellKind.WideLeft && column + 1 < target.Columns
                                           ? target[column + 1, row].Style
                                           : default;

                    target[column, row] = new Cell(null, CellKind.Single, tinted);

                    if (dst.Kind == CellKind.WideLeft && column + 1 < target.Columns)
                        target[column + 1, row] = new Cell(null, CellKind.Single, partnerStyle);

                    return;
                }

                // Covering only the RIGHT half of an emoji (a layer edge landing mid-pair) must
                // stomp the whole glyph too — the terminal cannot render half a bitmap, and the
                // surviving WideLeft would paint it over this layer's first column.
                if (dst.Kind == CellKind.WideContinuation && column > 0 &&
                    target[column - 1, row] is { Kind: CellKind.WideLeft, Grapheme: { Length: > 0 } leftGlyph } &&
                    GraphemeWidth.IsEmojiPresentation(leftGlyph))
                {
                    var leftStyle = target[column - 1, row].Style;
                    target[column, row] = new Cell(null, CellKind.Single, tinted);
                    // The uncovered left half keeps its own composited style — a blank in the
                    // emoji's colors, not a default-styled hole at the cover's edge.
                    target[column - 1, row] = new Cell(null, CellKind.Single, leftStyle);
                    return;
                }
            }

            // Raw indexer — the compositor already ran Color.Composite, so routing through Set
            // (which composites again) would double-composite.
            target[column, row] = dst with { Style = tinted };
            return;
        }

        // Glyph-bearing: the scene owns the glyph (and its hyperlink); composite fg over merged bg.
        var mergedForeground = Color.Composite(sourceStyle.Foreground, mergedBackground, mode);
        var style = sourceStyle with { Foreground = mergedForeground, Background = mergedBackground };

        if (source.Kind == CellKind.WideLeft)
        {
            // A WideLeft at the composite union's right edge would write its WideContinuation one column
            // past the reset + MarkDirty range, stranding a stale continuation a RestrictToDirtyRegions
            // renderer never revisits (a dirty-region hole, not just a glitch); one at the layer's own
            // clip edge would bleed the continuation outside the clip onto a neighbor's cells. Degrade to
            // a blank single cell at either edge — the same trick CellBuffer.Set uses at the buffer edge.
            // (The maintaining indexer blanks a previously-paired continuation next door, so the degrade
            // never strands the old right half.)
            if (column + 1 >= wideColumnEnd)
                target[column, row] = new Cell(null, CellKind.Single, style);
            else
                target.Set(column, row, source.Grapheme, in style);   // Set cleans up the orphaned neighbor
        }
        else
        {
            target[column, row] = new Cell(source.Grapheme, source.Kind, style);
        }
    }

    private static Style ScaleSourceAlpha(Style style, byte opacity) =>
        style with
        {
            Foreground = ScaleAlpha(style.Foreground, opacity),
            Background = ScaleAlpha(style.Background, opacity),
            UnderlineColor = ScaleAlpha(style.UnderlineColor, opacity)
        };

    private static Color ScaleAlpha(Color color, byte opacity) =>
        color.Kind == ColorKind.Rgb ? color.WithAlpha((byte) (color.Alpha * opacity / 255)) : color;

    private static bool TryFootprint(int sceneColumns, int sceneRows, in CompositeParameters p, int targetColumns, int targetRows, out Rect rect)
    {
        int colStart = Math.Max(0, p.OffsetColumn);
        int rowStart = Math.Max(0, p.OffsetRow);
        int colEnd = Math.Min(targetColumns, p.OffsetColumn + sceneColumns);
        int rowEnd = Math.Min(targetRows, p.OffsetRow + sceneRows);

        if (p.Clip is { } clip)
        {
            colStart = Math.Max(colStart, clip.Column);
            rowStart = Math.Max(rowStart, clip.Row);
            colEnd = Math.Min(colEnd, clip.ColumnEnd);
            rowEnd = Math.Min(rowEnd, clip.RowEnd);
        }

        if (colStart >= colEnd || rowStart >= rowEnd)
        {
            rect = Rect.Empty;
            return false;
        }

        rect = new Rect(colStart, rowStart, colEnd - colStart, rowEnd - rowStart);
        return true;
    }

    private void StashState(ReadOnlySpan<SceneLayer> layers)
    {
        int n = layers.Length;
        if (_lastVersions.Length != n)
        {
            _lastScenes = new Scene?[n];
            _lastVersions = new long[n];
            _lastParams = new CompositeParameters[n];
            _lastColumns = new int[n];
            _lastRows = new int[n];
        }

        for (int i = 0; i < n; i++)
        {
            _lastScenes[i] = layers[i].Scene;
            _lastVersions[i] = layers[i].Scene.RasterVersion;
            _lastParams[i] = layers[i].Parameters;
            _lastColumns[i] = layers[i].Scene.Columns;
            _lastRows[i] = layers[i].Scene.Rows;
        }
    }
}
