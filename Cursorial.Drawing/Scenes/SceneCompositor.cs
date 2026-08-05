using System.Diagnostics;

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
/// (unioning the vacated and new footprints) — narrowed to <see cref="SceneLayer.Damage"/> where a
/// layer declares one and nothing else about it moved — <b>resets just that union to base</b>, composites
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
    private const TextAttributes PassthroughAttributes = TextAttributes.Faint |
                                                         TextAttributes.Hidden |
                                                         TextAttributes.Inverse;

    // A blank that REPLACES the destination instead of merging into it — the one thing the cell model cannot
    // otherwise say, and the whole of the cell-level divergence between an identity group and the flat path.
    // CompositeCell classifies a source cell by whether it carries a grapheme: glyph-bearing replaces (glyph,
    // kind, foreground, attributes, underline, hyperlink), glyphless merges (background composited, the
    // destination's glyph and everything else kept). Several writes legitimately produce a BLANK that must
    // still replace — the emoji stomp, the WideLeft edge degrade, and the blank the buffer's pair hygiene
    // leaves next door — and on the final target that is fine, because the write IS the answer. On an
    // intermediate surface it is not: a surface can only hand its result onwards as a cell, so the pass that
    // blends it down re-reads that blank as an ordinary background-only contribution and merges it, and the
    // foreground, attributes and underline of the group's backdrop win over the source's.
    //
    // Intermediate mode therefore stores this marker where it would otherwise store a null grapheme, and the
    // final pass translates it back (EmittedGrapheme) so the target holds exactly the cell the flat path
    // wrote. Compared by VALUE (IsReplacingBlank), because the value is a Unicode NONCHARACTER: U+FFFF is
    // permanently reserved and guaranteed never to occur in valid interchanged text, so no scene, no font, no
    // clipboard paste and no user string can produce a cell that collides with it. That is what a value
    // comparison needs and what the previous value could not give — NBSP was chosen so a leak read as a
    // space, but NBSP is also CellBuffer.DurableEmptyGrapheme, real content every opaque brush fill produces,
    // so the marker had to be told apart from it by reference. Reference identity was the wrong thing to rest
    // on: the marker rides through nested groups as ordinary cell content (a string field copied from surface
    // to surface), and "a string instance is never silently replaced by an equal one" is an unwritten runtime
    // guarantee whose loss would be SILENT — the marker would degrade into content and the cell-level
    // divergence would quietly return. U+FFFF rather than U+FFFE, the other BMP noncharacter, because U+FFFE
    // is the byte-swapped BOM: a buffer that ever reached a byte stream would read as an encoding fault
    // rather than as this marker.
    //
    // The trade the value change makes: a leak no longer degrades gracefully into a space, it degrades into a
    // codepoint no font draws. That is deliberate — a silently wrong cell is worse than a loudly wrong one
    // — and VerifyMarkerDidNotReachTheTarget reports it in DEBUG at the pass that would leak it.
    private const string ReplacingBlankGrapheme = "\uFFFF";

    /// <summary>
    /// Whether <paramref name="grapheme"/> is the replacing-blank marker. Ordinal VALUE comparison: the
    /// marker is a noncharacter, so equality of value is equality of meaning.
    /// </summary>
    private static bool IsReplacingBlank(string? grapheme) =>
        string.Equals(grapheme, ReplacingBlankGrapheme, StringComparison.Ordinal);

    private readonly Style _baseStyle;
    private readonly CellBuffer? _baseLayer;

    // Whether the target is an INTERMEDIATE surface (a group buffer that will itself be composited
    // later) rather than the terminal's final target. See ForIntermediate for what that changes.
    private readonly bool _intermediate;

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

    private SceneCompositor(Style baseStyle, bool intermediate)
    {
        _baseStyle = baseStyle;
        _intermediate = intermediate;
    }

    /// <summary>
    /// A compositor that composites into an <b>intermediate</b> surface — a group buffer that will itself be
    /// composited onto the final target later, once, at the group's opacity. That is what makes a translucent
    /// subtree a true opacity group: its members blend against each other at full strength here, and the
    /// ancestor's translucency is applied exactly once when the surface is blended down, instead of once per
    /// member (which is what a flat sibling stack does where members overlap).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base is <see cref="Style.Transparent"/>, not <see cref="Style.Default"/>: reset-to-base runs over
    /// the whole rewritten union before any layer composites, so an opaque base would paint every cell the
    /// group's members do not cover and the surface would blend down as a solid rectangle.
    /// </para>
    /// <para>
    /// Cell blending uses <see cref="Color.CompositeOver"/> instead of <see cref="Color.Composite"/> so a
    /// translucent member stays translucent on the surface (<see cref="Color.Composite"/> reports every
    /// non-degenerate result as opaque, which is right for the terminal's fundamentally opaque final target
    /// and wrong for a surface with another composite still ahead of it), and a glyph-bearing source keeps its
    /// foreground <b>verbatim</b> rather than folding it over the merged background — the fold belongs to the
    /// final pass, where the group's real backdrop is known. Folding twice applies the backdrop twice.
    /// </para>
    /// <para>
    /// A blank cell that <b>replaces</b> its destination — what the emoji stomp, the WideLeft edge degrade and
    /// the buffer's wide-pair hygiene write — is stored with a private marker grapheme instead of a null one,
    /// because a surface can only hand its result onwards as a cell and a glyphless cell is read as a
    /// background-only contribution to merge. The pass that writes the final target translates the marker back
    /// to a null grapheme, so the terminal's buffer holds exactly what a flat composite would have written.
    /// Anything else that reads a surface cell — an inspector, a diagnostic dump — must go through
    /// <see cref="ResolveSurfaceCell"/> for the same reason.
    /// </para>
    /// <para>
    /// Screen-level bookkeeping is skipped: no dirty marking (see <see cref="Scene.CompositeInto"/>, which owns
    /// the surface's dirty lifecycle), and no ghost / force-repaint reconciliation, which is only meaningful
    /// against a buffer a <c>FrameRenderer</c> will emit. Fragments are still relocated onto the surface — the
    /// outer pass reads them from there and carries them the rest of the way.
    /// </para>
    /// </remarks>
    public static SceneCompositor ForIntermediate() => new(Style.Transparent, intermediate: true);

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
    public bool Composite(ReadOnlySpan<SceneLayer> layers, in CellBufferView target) =>
        CompositeCore(layers, target, out _);

    /// <summary>
    /// <see cref="Composite"/>, additionally reporting through <paramref name="rewritten"/> the region actually
    /// reset and recomposited (<see cref="Rect.Empty"/> when nothing changed) — the damage an intermediate
    /// surface must pass to whatever composites it in turn, which cannot read it back from the target's dirty
    /// regions because intermediate mode does not mark them.
    /// </summary>
    internal bool CompositeCore(ReadOnlySpan<SceneLayer> layers, in CellBufferView target, out Rect rewritten)
    {
        rewritten = Rect.Empty;

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
                bool sameScene = ReferenceEquals(layers[i].Scene, _lastScenes[i]);
                bool sameParams = layers[i].Parameters == _lastParams[i];
                long version = layers[i].Scene.RasterVersion;

                if (sameScene && sameParams && version == _lastVersions[i]) continue;

                // The layer is otherwise untouched and reported exactly which of its own cells the ONE
                // version bump we missed rewrote — so only those target cells can differ. Everything about
                // the layer that could invalidate that claim is excluded above: a swapped scene, changed
                // parameters (a move/fade/reclip rewrites the whole footprint AND vacates the old one), and
                // a version that moved by more than one (an intermediate surface composited twice while we
                // looked away, so the reported region only describes the last of them). Any of those falls
                // through to the full-footprint path below, which is also the only path a layer with no
                // damage to declare — every ordinary Scene.Draw, which re-rasters wholesale — ever takes.
                if (sameScene && sameParams && version == _lastVersions[i] + 1 && layers[i].Damage is { } damage)
                {
                    if (TryFootprint(layers[i].Scene.Columns, layers[i].Scene.Rows, layers[i].Parameters, target.Columns, target.Rows, out var df))
                    {
                        var p = layers[i].Parameters;
                        int dcS = Math.Max(df.Column, damage.Column + p.OffsetColumn);
                        int drS = Math.Max(df.Row, damage.Row + p.OffsetRow);
                        int dcE = Math.Min(df.ColumnEnd, damage.ColumnEnd + p.OffsetColumn);
                        int drE = Math.Min(df.RowEnd, damage.RowEnd + p.OffsetRow);
                        if (dcS < dcE && drS < drE) Union(new Rect(dcS, drS, dcE - dcS, drE - drS));
                    }

                    continue;
                }

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
                CompositeCell(target, tc, tr, buffer[tc - p.OffsetColumn, tr - p.OffsetRow], p.Opacity, mode, wideColumnEnd, _intermediate);
        }

        PassThroughFragments(layers, target, layerSetChanged);

        rewritten = new Rect(colStart, rowStart, colEnd - colStart, rowEnd - rowStart);

        VerifyMarkerDidNotReachTheTarget(target, rewritten);

        // An intermediate surface is never emitted, so its dirty regions have no consumer and no renderer to
        // clear them; the union travels out through `rewritten` instead.
        if (!_intermediate)
            target.MarkDirty(rewritten);

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
        // Intermediate mode does none of this: a ghost is a pixel the TERMINAL is still showing, so the live
        // set is only meaningful in final-target coordinates. The outer pass collects it from the group
        // surface's forwarded fragments — which is the same set, already in those coordinates.
        if (!_intermediate)
        {
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
        if (_intermediate) return;   // no live set was collected, and ForceRepaint on a surface nobody emits is noise

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
                                      in Cell source, byte opacity, IBlendingMode mode, int wideColumnEnd,
                                      bool intermediate)
    {
        if (source.Kind == CellKind.WideContinuation) return;   // the WideLeft paints both columns

        var dst = target[column, row];
        var sourceStyle = opacity == 255 ? source.Style : ScaleSourceAlpha(source.Style, opacity);
        var targetStyle = dst.Style;
        var mergedBackground = CompositeColor(sourceStyle.Background, targetStyle.Background, mode, intermediate);

        // The glyph the blanking branches below store. On the final target that is a plain blank — the write
        // is the answer and nothing reads it back. On a surface it has to carry replace semantics onwards,
        // which is what the marker says. See ReplacingBlankGrapheme.
        var replacingBlank = intermediate ? ReplacingBlankGrapheme : null;

        if (string.IsNullOrEmpty(source.Grapheme))
        {
            // The tint dims what the cell will actually SHOW. On an intermediate surface the stored foreground
            // is verbatim (see the glyph-bearing path below), so it may still be translucent and sitting over
            // its own background — resolve it first, or the tint lands on an unresolved color and the final
            // pass folds the background in a second time. This is what makes an opacity-1 group bit-exact.
            var destinationForeground = intermediate
                                            ? Color.CompositeOver(targetStyle.Foreground, targetStyle.Background, mode)
                                            : targetStyle.Foreground;

            var blendedForeground = CompositeColor(sourceStyle.Background, destinationForeground, mode, intermediate);

            var tinted = dst.Style with
                         {
                             Foreground = blendedForeground,
                             Background = mergedBackground,
                             // Let certain attributes (like 'Faint') pass through, but no others.
                             Attributes = dst.Style.Attributes | (source.Style.Attributes & PassthroughAttributes)
                         };

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
                    // The stomp ERASES a glyph, so it replaces the destination rather than merging into it.
                    // The pair partner (the emoji's other half, which may lie OUTSIDE this layer's footprint
                    // and never be recomposited this pass) is the buffer's job: its hygiene strips the orphan
                    // to a blank single that KEEPS its own style — a blank in the emoji's colors, not a
                    // terminal-default hole at the cover's edge — and ReplaceCell carries the replace
                    // semantics of that blank onto a surface.
                    ReplaceCell(target, column, row, new Cell(replacingBlank, CellKind.Single, tinted), in dst,
                                intermediate);
                    return;
                }

                // Covering only the RIGHT half of an emoji (a layer edge landing mid-pair) must
                // stomp the whole glyph too — the terminal cannot render half a bitmap, and the
                // surviving WideLeft would paint it over this layer's first column.
                if (dst.Kind == CellKind.WideContinuation && column > 0 &&
                    target[column - 1, row] is { Kind: CellKind.WideLeft, Grapheme: { Length: > 0 } leftGlyph } &&
                    GraphemeWidth.IsEmojiPresentation(leftGlyph))
                {
                    ReplaceCell(target, column, row, new Cell(replacingBlank, CellKind.Single, tinted), in dst,
                                intermediate);
                    return;
                }
            }

            // Raw indexer — the compositor already ran Color.Composite, so routing through Set
            // (which composites again) would double-composite.
            target[column, row] = dst with { Style = tinted };
            return;
        }

        // Glyph-bearing: the scene owns the glyph (and its hyperlink); composite fg over merged bg.
        // On an intermediate surface the fold is DEFERRED and the foreground stored verbatim: the merged
        // background here is only the group's own, and the final pass folds fg over the background the group
        // actually lands on. Folding at both levels applies the group's backdrop twice — for a glyph
        // {fg=red@50%, bg=opaque blue} in a group at 0.5 over green that is (64,63,127) instead of (64,95,95).
        var mergedForeground = intermediate
                                   ? sourceStyle.Foreground
                                   : Color.Composite(sourceStyle.Foreground, mergedBackground, mode);

        var style = sourceStyle with { Foreground = mergedForeground, Background = mergedBackground };

        if (source.Kind == CellKind.WideLeft)
        {
            // A WideLeft at the composite union's right edge would write its WideContinuation one column
            // past the reset + MarkDirty range, stranding a stale continuation a RestrictToDirtyRegions
            // renderer never revisits (a dirty-region hole, not just a glitch); one at the layer's own
            // clip edge would bleed the continuation outside the clip onto a neighbor's cells. Degrade to
            // a blank single cell at either edge — the same trick CellBuffer.Set uses at the buffer edge.
            // (The maintaining indexer blanks a previously-paired continuation next door, so the degrade
            // never strands the old right half.) The degrade erases the glyph, so like the stomp it must
            // still REPLACE the destination — hence the marker on a surface.
            if (column + 1 >= wideColumnEnd)
                ReplaceCell(target, column, row, new Cell(replacingBlank, CellKind.Single, style), in dst, intermediate);
            else
                ReplaceWidePair(target, column, row, source.Grapheme, in style, in dst, intermediate);
        }
        else
        {
            ReplaceCell(target, column, row,
                        new Cell(EmittedGrapheme(source.Grapheme, intermediate), source.Kind, style), in dst,
                        intermediate);
        }
    }

    /// <summary>
    /// Write a cell that <b>replaces</b> the destination, keeping replace semantics intact for the blank the
    /// buffer's pair hygiene leaves next door.
    /// </summary>
    /// <remarks>
    /// Overwriting one half of a wide pair strips the surviving half to a blank single that keeps its own
    /// style (<see cref="CellBuffer"/>'s maintaining indexer). On the final target that blank is the answer —
    /// it replaced whatever the base held there. On a surface it has to say so, or the pass that blends the
    /// surface down reads a glyphless cell and merges it, leaving the destination's glyph standing where the
    /// flat path erased it. The orphan test mirrors the indexer's own three-part rule — previous kind /
    /// stored kind / the neighbour really is the pairing half — evaluated <i>before</i> the write, so it
    /// never marks a cell the hygiene did not touch.
    /// </remarks>
    private static void ReplaceCell(in CellBufferView target, int column, int row, in Cell cell, in Cell previous,
                                    bool intermediate)
    {
        if (!intermediate)
        {
            target[column, row] = cell;
            return;
        }

        bool orphansLeft = previous.Kind == CellKind.WideContinuation && cell.Kind != CellKind.WideContinuation &&
                           column > 0 && target[column - 1, row].Kind == CellKind.WideLeft;

        bool orphansRight = previous.Kind == CellKind.WideLeft && cell.Kind != CellKind.WideLeft &&
                            column + 1 < target.Columns && target[column + 1, row].Kind == CellKind.WideContinuation;

        target[column, row] = cell;

        if (orphansLeft) MarkReplacing(target, column - 1, row);
        if (orphansRight) MarkReplacing(target, column + 1, row);
    }

    // The wide-pair write's own hygiene, same rule as ReplaceCell's: Set strips the WideLeft to the left of an
    // overwritten continuation, and WriteWidePair strips the NEXT pair's continuation two columns over when
    // this pair lands one column short of it. Both blanks replace, so on a surface both carry the marker.
    private static void ReplaceWidePair(in CellBufferView target, int column, int row, string? grapheme,
                                        in Style style, in Cell previous, bool intermediate)
    {
        if (!intermediate)
        {
            target.Set(column, row, grapheme, in style);   // Set cleans up the orphaned neighbor
            return;
        }

        bool orphansLeft = previous.Kind == CellKind.WideContinuation &&
                           column > 0 && target[column - 1, row].Kind == CellKind.WideLeft;

        bool orphansNextPair = column + 2 < target.Columns &&
                               target[column + 1, row].Kind == CellKind.WideLeft &&
                               target[column + 2, row].Kind == CellKind.WideContinuation;

        target.Set(column, row, grapheme, in style);

        if (orphansLeft) MarkReplacing(target, column - 1, row);
        if (orphansNextPair) MarkReplacing(target, column + 2, row);
    }

    // Stamp the marker on a blank the pair hygiene just wrote, keeping the style it preserved.
    private static void MarkReplacing(in CellBufferView target, int column, int row) =>
        target[column, row] = target[column, row] with { Grapheme = ReplacingBlankGrapheme };

    // The marker is a compositor-internal signal, not content: only another compositing pass may see it, so
    // the pass that writes the final target translates it back to the plain blank the flat path writes there.
    // Intermediate passes carry it through untouched, so a marker crossing N nested groups is translated
    // exactly once — at the Nth+1 pass, the one that writes a buffer a FrameRenderer will emit.
    private static string? EmittedGrapheme(string? grapheme, bool intermediate) =>
        !intermediate && IsReplacingBlank(grapheme) ? null : grapheme;

    /// <summary>
    /// Translate a cell read straight off an <b>intermediate</b> surface (<see cref="ForIntermediate"/>) into
    /// the cell it will contribute to the final target. Inspectors and diagnostics that sample a scene which
    /// may be a group surface must go through this, or they report a compositor-internal grapheme where the
    /// screen shows a blank.
    /// </summary>
    /// <remarks>
    /// Safe to apply to any cell, group surface or not: the marker is a noncharacter, so a cell that is not
    /// the marker cannot compare equal to it and is returned untouched. (That is a property the previous
    /// NBSP-valued marker did not have — this helper could not have existed, because it would have stripped
    /// the durable empty out of every ordinary opaque fill.)
    /// </remarks>
    public static Cell ResolveSurfaceCell(in Cell cell) =>
        IsReplacingBlank(cell.Grapheme) ? cell with { Grapheme = null } : cell;

    // The marker is compositor-INTERNAL: the only buffers allowed to hold it are intermediate surfaces. Sweep
    // the region this pass just rewrote on a final target and report a cell still carrying it — a write path
    // that forgot to translate, or a base layer that is itself a group surface. It cannot fire on the
    // legitimate translate path: EmittedGrapheme runs at the write, so by the time this reads the cell back
    // the marker is already gone. Compiles away entirely in release builds (the extra read pass with it).
    [Conditional("DEBUG")]
    private void VerifyMarkerDidNotReachTheTarget(in CellBufferView target, in Rect rewritten)
    {
        if (_intermediate) return;

        for (int row = rewritten.Row; row < rewritten.RowEnd; row++)
        for (int column = rewritten.Column; column < rewritten.ColumnEnd; column++)
        {
            if (!IsReplacingBlank(target[column, row].Grapheme)) continue;

            // One report per pass: the cause is a single bad write path, not a per-cell accident, and a
            // full-screen composite would otherwise emit thousands of identical events.
            DrawingDiagnostics.Emit(DrawingDiagnosticKind.ReplacingBlankReachedFinalTarget,
                                    $"The compositor's replacing-blank marker reached a final target at " +
                                    $"({column}, {row}). It is an intermediate-surface signal and must be " +
                                    $"translated by the pass that writes a buffer a FrameRenderer emits; " +
                                    $"reaching one means a write path skipped EmittedGrapheme, or a base " +
                                    $"layer handed to this compositor is itself a group surface.");
            return;
        }
    }

    // Compositing into an intermediate surface must PRESERVE alpha: Color.Composite resolves every
    // non-degenerate result to opaque, which is right for the terminal's final target (output is
    // fundamentally opaque) and wrong for a surface with another composite still ahead of it — two
    // translucent members would stack up opaque and the group's own translucency would be lost.
    private static Color CompositeColor(Color source, Color backdrop, IBlendingMode mode, bool intermediate) =>
        intermediate ? Color.CompositeOver(source, backdrop, mode) : Color.Composite(source, backdrop, mode);

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
