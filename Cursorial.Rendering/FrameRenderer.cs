using System.Buffers;
using System.Text;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Rendering;

/// <summary>
/// Stateful diff renderer. Holds the previously emitted frame plus the SGR / cursor state
/// believed-active on the terminal, and emits the minimum sequence of VT bytes to bring the
/// terminal to match a supplied <see cref="CellBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// One instance per output target. The renderer assumes it is the sole owner of the
/// terminal's rendering state across frames — interleaving raw output that mutates SGR or
/// cursor position behind the renderer's back will cause incorrect deltas on the next frame.
/// </para>
/// <para>
/// <b>Frame structure.</b> Each call to <see cref="Render"/> emits:
/// </para>
/// <list type="number">
/// <item><description>
/// (Full redraw only) SGR reset + <see cref="ScreenWriter.WriteClearScreen"/>, in that order —
/// the erase paints the terminal's current background, so the reset has to land first.
/// </description></item>
/// <item><description>
/// For each cell that differs from the corresponding cell in the previous frame: a cursor
/// move, an SGR delta, and the UTF-8 grapheme bytes.
/// </description></item>
/// <item><description>
/// End-of-frame cursor: visibility / shape changes, then a position move to
/// (<see cref="CellBuffer.CursorRow"/>, <see cref="CellBuffer.CursorColumn"/>).
/// </description></item>
/// </list>
/// <para>
/// <b>Full redraws fire when:</b> the renderer has no prior frame; the back buffer's
/// dimensions don't match the front buffer's (the terminal resized); or
/// <see cref="FrameRendererOptions.ForceFullRedraw"/> is set on the renderer. Otherwise,
/// the renderer diffs cell-by-cell.
/// </para>
/// <para>
/// <b>Wide cells.</b> <see cref="CellKind.WideContinuation"/> cells are skipped during
/// emission — the terminal's cursor advance from the wide-left cell covers their position.
/// Wide-cell consistency in the buffer is <see cref="CellBuffer"/>'s job.
/// </para>
/// <para>
/// <b>Multicell fragments.</b> Cells-layer fragments (OSC 66 sized text) leave multicell blocks
/// on the terminal whose overwrite semantics are non-local — a partial write blanks whole blocks
/// or gets cursor-skipped. The renderer therefore never diffs into a previously-committed
/// fragment rect partially: see <see cref="ComputeFragmentGuardCells"/> for the frame-over-frame
/// accounting (stale-rect force-rewrite on removal/move, whole-rect expansion of intersecting
/// writes, and the resulting re-emit rules).
/// </para>
/// </remarks>
public sealed class FrameRenderer
{
    private readonly FrameRendererOptions _options;
    private readonly OutputCapabilities? _capabilities;
    private readonly StyleQuantizer? _quantizer;

    private Cell[]? _frontCells;
    private int _frontCols;
    private int _frontRows;

    // Snapshot of fragments emitted on the previous render. Compared against the back buffer's
    // fragments on each render to decide which ones to (re-)emit and which removed-fragments
    // need EmitErase. Reference equality on IBufferFragment + value equality on AnchorStyle —
    // callers that want stable diff-skipping reuse the same fragment instance across frames.
    private readonly Dictionary<(int Column, int Row), CellBuffer.FragmentEntry> _frontFragments = new();

    // Reusable scratch buffer for the per-render "is this cell covered by a Cell-layer
    // fragment?" lookup. Sized to the current frontCells dimensions on full-redraw and reused
    // each render; cleared (set to false) at the start of each ComputeCoveredCells pass.
    private bool[]? _coveredCells;

    // Reusable scratch buffer recording which cells EmitDiff actually re-emitted this frame.
    // A FragmentLayer.Cells fragment whose footprint contains a re-emitted cell has had its
    // payload overpainted by the cell pass (the covered-cell bg-only write), so it must re-emit
    // even when its Key/AnchorStyle are unchanged — see FragmentFootprintTouched. Parallel to
    // _coveredCells; cleared at the top of each EmitDiff.
    private bool[]? _touchedCells;

    // Reusable scratch buffer for the per-render "must this cell re-emit unconditionally?" lookup,
    // populated from CellBuffer.ForceRepaintRegions. A force-repaint cell emits even when it compares
    // equal to the front buffer — the channel exists to overwrite content the front buffer can't tell
    // is stale, chiefly the lingering pixels of a removed Cells-layer image (iTerm2/Sixel), which has
    // no protocol erase. The compositor marks the vacated footprint; see SceneCompositor.
    private bool[]? _forceCells;
    private bool _hasForceRepaint;

    // Scratch list for ComputeFragmentGuardCells: the clamped screen rects of Cells-layer fragments
    // that were live on the terminal last frame AND are still registered, unchanged, at the same
    // anchor this frame. The guard's write-expansion fixpoint consumes it; only meaningful within a
    // single render.
    private readonly List<Rect> _liveFrontFragmentRects = [];

    // Reusable scratch buffer for the per-render "is this cell inside a dirty region?"
    // lookup. Only populated when FrameRendererOptions.RestrictToDirtyRegions is opted in AND
    // CellBuffer.DirtyRegions is non-empty; sized parallel to _coveredCells. Otherwise this field
    // stays logically inactive — the cell loop falls back to the full-buffer diff (the default).
    private bool[]? _dirtyCells;
    private bool _hasDirtyRegions;

    // The colors the terminal reported as its own defaults, in the only form SGR 39 / 49 can
    // reproduce — see CaptureTerminalDefaultColors. Color.Default in either field means "no
    // substitution for this channel". Refreshed per render because the back buffer is a source.
    private Color _terminalDefaultForeground = Color.Default;
    private Color _terminalDefaultBackground = Color.Default;

    // CACHE KEY: resolved value, never the template. The SGR state believed live on the
    // terminal; SyncStyle's == against it is the sole gate on emitting SGR bytes. A policy
    // carrier here could neither be compared (reference equality re-emits every run) nor
    // encoded (SgrEncoder needs channel values, and the coordinates are gone by now).
    private CellStyle _currentStyle;
    private Hyperlink _currentHyperlink;
    private int _cursorRow;
    private int _cursorCol;
    private int _rowOffset;
    private bool _firstFrame = true;
    private bool _cursorVisible = true;
    private CursorShape _cursorShape = CursorShape.Default;

    public FrameRenderer()
        : this(capabilities: null, options: default) {}

    public FrameRenderer(FrameRendererOptions options)
        : this(capabilities: null, options: options) {}

    public FrameRenderer(OutputCapabilities? capabilities, FrameRendererOptions options = default)
    {
        _options = options;
        _capabilities = capabilities;
        _quantizer = capabilities is null ? null : new StyleQuantizer(capabilities);
    }

    /// <summary>The options the renderer was constructed with.</summary>
    public FrameRendererOptions Options => _options;

    // Relative-inline positioning is active — every cursor move is a CUU/CUD delta + CHA rather than
    // absolute CUP. Requires both flags; a no-op for full-screen or absolute-inline rendering.
    private bool RelativeInline => _options is { Inline: true, RelativeInline: true };

    /// <summary>
    /// The absolute terminal row of buffer row 0 — every emitted cursor address adds this offset.
    /// Zero (the default) for full-screen rendering; an inline host
    /// (<see cref="FrameRendererOptions.Inline"/>) sets it to the region's top row and updates it
    /// whenever the region moves. Changing the offset forgets the prior frame (<see cref="Reset"/>):
    /// every believed terminal position is expressed in absolute screen rows, so a moved region
    /// invalidates all of them and the next <see cref="Render"/> repaints the region in full.
    /// </summary>
    public int RowOffset
    {
        get => _rowOffset;
        set
        {
            if (value == _rowOffset)
                return;

            _rowOffset = value;
            Reset();
        }
    }

    /// <summary>
    /// Forget any prior frame state. The next <see cref="Render"/> call will do a full redraw.
    /// Useful when the application has bypassed the renderer to emit raw output and wants the
    /// renderer to re-sync from scratch.
    /// </summary>
    public void Reset()
    {
        _frontCells = null;
        _firstFrame = true;
    }

    /// <summary>
    /// True when the next <see cref="Render"/> emits a full redraw rather than a delta — after
    /// construction or <see cref="Reset"/>. Lets a frame loop that normally skips emission on a
    /// clean frame (nothing composited, no resize) force one when a reset is pending: a reset on
    /// an otherwise-idle screen would never reach the terminal by diffing alone.
    /// </summary>
    public bool NeedsFullRedraw => _firstFrame;

    /// <summary>
    /// Emit the byte sequence that brings the terminal to match <paramref name="back"/>.
    /// Stateful across calls — subsequent renders emit only the differences.
    /// </summary>
    public void Render(CellBuffer back, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(back);
        ArgumentNullException.ThrowIfNull(output);

        CaptureTerminalDefaultColors(back);

        // Synchronized output (DECSET 2026) brackets the entire frame so the terminal commits
        // all paints atomically — eliminates mid-frame tearing on supporting terminals. We
        // gate on the negotiated capability; non-supporting terminals would just print junk.
        bool syncOutput = _capabilities?.Protocol.SynchronizedOutput == true;
        if (syncOutput)
        {
            var begin = VtOutputSequences.SynchronizedOutput.Begin;
            var span = output.GetSpan(begin.Length);
            begin.CopyTo(span);
            output.Advance(begin.Length);
        }

        // Disable autowrap for the duration of the frame. The renderer emits exactly
        // `back.Columns` cells per row and uses absolute CUP for positioning — autowrap is
        // never beneficial, and on conhost / ConEmu families the deferred-wrap state that
        // builds up at the right margin doesn't always clear cleanly on the next CUP,
        // producing the symptom where the first few characters of subsequent rows shift to
        // the right edge ("Wide glyphs" rendering as "e glyphs" with "Wid" at the right
        // margin) or the clock's last digit wrapping to the next line after several ticks.
        // Re-emit every frame because some terminals silently toggle DECAWM back on as a
        // side effect of other commands; the cost is ~5 bytes per frame and the alternative
        // is silent failure on those terminals. DECSET 7 is restored in Close().
        ScreenWriter.WriteDisableAutowrap(output);

        bool fullRedraw = _firstFrame ||
                          _frontCols != back.Columns ||
                          _frontRows != back.Rows ||
                          _options.ForceFullRedraw;

        // Relative-inline park invariant: the physical cursor sits at the region bottom-left at the
        // start of every frame (the host's make-room leaves it there; the frame-end park below keeps
        // it there). Assert that as the tracked delta origin so the full-redraw climb to the top and
        // the first in-frame move are correct — and, on the first frame, so the reserved-bottom climb
        // isn't skipped (tracked row defaults to 0, but the cursor is physically at the bottom).
        if (RelativeInline)
        {
            _cursorRow = back.Rows - 1;
            _cursorCol = 0;
        }

        if (fullRedraw)
        {
            // Erase outgoing fragments BEFORE the clear-screen. Overlay-layer protocols (Kitty
            // graphics, iTerm2 inline images, …) don't always remove their payloads on a plain
            // CSI 2 J — the image plane is independent of the cell grid on most implementations
            // — so we have to issue the protocol's explicit delete command first. Cell-layer
            // EmitErase is a no-op by default, so iterating everything is safe, and the
            // pre-clear emission is the same regardless of layer.
            if (_frontFragments.Count > 0)
            {
                var caps = _capabilities ?? OutputCapabilities.None;
                foreach (var (anchor, frontEntry) in _frontFragments)
                {
                    if (!frontEntry.Fragment.IsSupported(caps)) continue;
                    EmitFragmentEraseBytes(anchor.Column, anchor.Row, frontEntry, output, caps);
                }
            }

            // The reset goes BEFORE the erase, not after. ED (CSI 2 J / CSI 0 J) erases to the terminal's
            // CURRENT background — the same rule the end-of-frame reset below exists to contain — so
            // clearing first would fill the screen with whatever SGR state we inherited: the shell's on
            // the very first frame, or the last cell's colour on a ForceFullRedraw. Resetting first makes
            // the erase land on the terminal's own default, which is what the freshly-zeroed front buffer
            // below is asserting the screen now holds (see Adapt).
            SgrEncoder.WriteReset(output);

            if (_options.Inline)
            {
                // Inline: the screen is NOT ours — never ED 2. Move to the region's top-left and erase
                // cursor-to-end-of-screen instead: the shell's content sits ABOVE the region by contract,
                // so everything from the region top down is region extent. ED 0 both clears the band the
                // diff is about to repaint and wipes any taller extent a shrink left standing (the host
                // resizes the back buffer, the dimension change lands here, and the erase sweeps the
                // orphaned rows in the same stroke).
                MoveTo(output, 0, 0);
                ScreenWriter.WriteClearScreenAfter(output);
            }
            else
            {
                ScreenWriter.WriteClearScreen(output);
                CursorWriter.WriteMoveTo(output, 0, 0);
            }

            _currentStyle = CellStyle.Default;
            _currentHyperlink = Hyperlink.None;
            _cursorRow = 0;
            _cursorCol = 0;
            _frontCols = back.Columns;
            _frontRows = back.Rows;
            _frontCells = new Cell[_frontCols * _frontRows];

            // Front-fragment snapshot is now consumed (we just emitted erase for each); clear
            // so the next fragment-emit pass treats every registered fragment as new.
            _frontFragments.Clear();
        }

        int cellCount = back.Rows * back.Columns;
        if (_coveredCells is null || _coveredCells.Length != cellCount)
            _coveredCells = new bool[cellCount];
        if (_dirtyCells is null || _dirtyCells.Length != cellCount)
            _dirtyCells = new bool[cellCount];
        if (_forceCells is null || _forceCells.Length != cellCount)
            _forceCells = new bool[cellCount];
        if (_touchedCells is null || _touchedCells.Length != cellCount)
            _touchedCells = new bool[cellCount];

        ComputeCoveredCells(back);
        ComputeDirtyCells(back);
        ComputeForceCells(back);

        // Scroll detection — only meaningful on incremental renders, and only safe when NO
        // fragments are in play on either side of the diff (fragments shouldn't scroll with cell
        // content). For Cells-layer fragments the hazard is physical, not just logical: an SU/SD
        // slides the multicell glyphs a fragment committed to the terminal (OSC 66 sized text)
        // away from the rect the renderer tracks for them, stranding ghosts the diff can no
        // longer see — so the previous overlay-only test was not enough. When the back buffer is
        // the front shifted up/down by K rows, emit SU/SD and shift _frontCells in place so the
        // subsequent EmitDiff only repaints the K newly-uncovered rows. Never inline: SU/SD move
        // the WHOLE screen (the default scroll region), shell history included — a scrolled chat
        // view would drag the user's prompt along with it.
        if (!fullRedraw && !_options.Inline && back.FragmentsInternal.Count == 0 && _frontFragments.Count == 0)
            TryDetectAndApplyScroll(back, output);

        ComputeFragmentGuardCells(back);

        EmitDiff(back, output);
        EmitFragments(back, output);
        EmitCursor(back, output);

        // End-of-frame SGR reset. Without this, the terminal's SGR state at frame boundary is
        // whatever the last-emitted cell's style was — so when the terminal has to fill new
        // rows (because the user enlarged the window, or content scrolled), those rows inherit
        // the colored background and the user sees a "bleed" effect. Resetting puts the
        // terminal back into default style; the next frame's first non-default cell pays the
        // SGR re-establishment cost (a handful of bytes).
        SgrEncoder.WriteReset(output);
        _currentStyle = CellStyle.Default;

        // Same reasoning for OSC 8 — leaving a hyperlink open at frame boundary would extend
        // the link target into the next prompt or any subsequent ad-hoc terminal output.
        if (!_currentHyperlink.IsEmpty)
        {
            HyperlinkWriter.WriteClose(output);
            _currentHyperlink = Hyperlink.None;
        }

        // Relative-inline frame-end park: return the cursor to the region bottom-left — the defined
        // anchor the NEXT frame's relative moves (and the full-redraw climb) start from. EmitCursor
        // left it at the caret; SyncCursor moves it back (the visible-caret refinement is phase 2).
        if (RelativeInline)
            SyncCursor(output, back.Rows - 1, 0);

        // Close the synchronized-output frame opened at the top — the terminal commits the
        // buffered paints atomically here. Sequence is symmetric with the begin emit above.
        if (syncOutput)
        {
            var end = VtOutputSequences.SynchronizedOutput.End;
            var span = output.GetSpan(end.Length);
            end.CopyTo(span);
            output.Advance(end.Length);
        }

        // Consume the buffer's dirty regions so the next render starts fresh. Consumers using
        // explicit dirty tracking re-mark on each frame; consumers that never mark are
        // unaffected (the list was empty going in and goes out the same way).
        back.ClearDirty();

        // Same for force-repaint regions — they're one-shot (the cells they cover were emitted this
        // frame, so the front buffer is now correct and a plain diff handles them going forward).
        back.ClearForceRepaint();

        _firstFrame = false;
    }

    /// <summary>
    /// Recompute <see cref="_coveredCells"/> for the current frame. A cell is covered when it
    /// falls inside the footprint of a <see cref="FragmentLayer.Cells"/>-layer fragment whose
    /// <see cref="IBufferFragment.IsSupported"/> returns true. The cell pass uses this to
    /// substitute a background-only paint for the cell's normal emission — the fragment's own
    /// payload will paint the foreground.
    /// </summary>
    private void ComputeCoveredCells(CellBuffer back)
    {
        var covered = _coveredCells!;
        Array.Clear(covered);

        if (back.Fragments.Count == 0) return;

        var caps = _capabilities ?? OutputCapabilities.None;

        foreach (var ((anchorCol, anchorRow), entry) in back.Fragments)
        {
            if (entry.Fragment.Layer != FragmentLayer.Cells) continue;
            if (!entry.Fragment.IsSupported(caps)) continue;

            var size = entry.Fragment.GetSize();
            int colEnd = Math.Min(back.Columns, anchorCol + Math.Max(1, size.Columns));
            int rowEnd = Math.Min(back.Rows, anchorRow + Math.Max(1, size.Rows));

            for (int r = Math.Max(0, anchorRow); r < rowEnd; r++)
            {
                for (int c = Math.Max(0, anchorCol); c < colEnd; c++)
                    covered[r * back.Columns + c] = true;
            }
        }
    }

    /// <summary>
    /// Recompute the dirty-cells bitmask for the current frame from
    /// <see cref="CellBuffer.DirtyRegions"/>. This only takes effect when
    /// <see cref="FrameRendererOptions.RestrictToDirtyRegions"/> is opted in; otherwise (and when
    /// the back buffer has no dirty regions) it is a no-op and <see cref="_hasDirtyRegions"/>
    /// remains false — the cell loop falls back to a full-buffer diff (the safe default). When
    /// the opt-in is on and regions are present, only cells inside the union of those regions are
    /// eligible for emission.
    /// </summary>
    private void ComputeDirtyCells(CellBuffer back)
    {
        // Dirty-region-exclusive emission is opt-in (FrameRendererOptions.RestrictToDirtyRegions).
        // When it's off we leave _hasDirtyRegions false so EmitDiff falls back to a full-buffer
        // diff — a stray MarkDirty (e.g. from RemoveFragment) then can't silently restrict the
        // frame's repaint and drop unrelated changed cells. The caller still clears the regions.
        _hasDirtyRegions = _options.RestrictToDirtyRegions && back.DirtyRegions.Count > 0;
        if (!_hasDirtyRegions) return;

        var dirty = _dirtyCells!;
        Array.Clear(dirty);

        foreach (var region in back.DirtyRegions)
        {
            int rowEnd = Math.Min(back.Rows, region.RowEnd);
            int colEnd = Math.Min(back.Columns, region.ColumnEnd);
            for (int r = Math.Max(0, region.Row); r < rowEnd; r++)
                for (int c = Math.Max(0, region.Column); c < colEnd; c++)
                    dirty[r * back.Columns + c] = true;
        }
    }

    /// <summary>
    /// Recompute the force-repaint bitmask for the current frame from
    /// <see cref="CellBuffer.ForceRepaintRegions"/>. Unlike the dirty-region mask, this is <b>always</b>
    /// active (no opt-in) and <i>expands</i> emission: a cell inside a force-repaint region emits even
    /// when it compares equal to the front buffer. The motivating case is a removed
    /// <see cref="FragmentLayer.Cells"/> image whose lingering pixels the front buffer can't see are
    /// stale (it recorded the bg-only covered placeholder). When no regions are marked,
    /// <see cref="_hasForceRepaint"/> stays false and the diff is unaffected.
    /// </summary>
    private void ComputeForceCells(CellBuffer back)
    {
        _hasForceRepaint = back.ForceRepaintRegions.Count > 0;
        if (!_hasForceRepaint) return;

        var force = _forceCells!;
        Array.Clear(force);

        foreach (var region in back.ForceRepaintRegions)
        {
            int rowEnd = Math.Min(back.Rows, region.RowEnd);
            int colEnd = Math.Min(back.Columns, region.ColumnEnd);
            for (int r = Math.Max(0, region.Row); r < rowEnd; r++)
                for (int c = Math.Max(0, region.Column); c < colEnd; c++)
                    force[r * back.Columns + c] = true;
        }
    }

    /// <summary>
    /// The multicell guard: expand this frame's cell emissions so the cell pass can never partially
    /// overwrite a multicell block (Kitty OSC 66 sized text) that a Cells-layer fragment committed
    /// to the terminal on a previous frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kitty text-sizing protocol gives multicells non-local overwrite semantics
    /// (https://sw.kovidgoyal.net/kitty/text-sizing-protocol/): a write into a multicell's top-left
    /// cell erases the ENTIRE block, a write into any other top-row cell replaces the block with
    /// spaces, and a write landing in a row BELOW the first makes the terminal skip the cursor past
    /// the block before printing. A minimal cell diff that touches only the cells it believes
    /// changed therefore (a) blanks neighboring cells it believed unchanged (torn rows), (b) leaves
    /// stale scaled glyphs standing where its model says spaces, and (c) desyncs the tracked cursor
    /// column from the terminal's when a write is skipped, displacing every subsequent glyph emitted
    /// on that row by the skipped width.
    /// </para>
    /// <para>
    /// Two guarantees restore coherence, both expressed as force-repaint bits consumed by
    /// <see cref="EmitDiff"/>:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Stale rects.</b> A Cells-layer fragment live last frame but gone from its anchor this
    /// frame (removed, moved — e.g. a composite-only slide re-anchoring it — or replaced) leaves
    /// its glyphs physically on the terminal while the cells under its old rect may compare equal
    /// (the front recorded the same bg-only placeholders the back still holds), so a plain diff
    /// would never revisit them. Its entire old rect is force-rewritten this frame.
    /// </description></item>
    /// <item><description>
    /// <b>Write expansion.</b> When any cell emission would land inside the rect of a fragment that
    /// is still live at its anchor (the panel behind sized text repainting, a popup shadow's tint
    /// band grazing the footprint, any partial overlap), the emission expands to the fragment's
    /// whole rect — the terminal blanks whole blocks in response to a one-cell write, so the
    /// renderer must repaint everything the terminal may blank. Expansion runs to a fixpoint, since
    /// forcing one rect adds writes that may reach into another.
    /// </description></item>
    /// </list>
    /// <para>
    /// Because <see cref="EmitDiff"/> walks row-major, a forced rect's TOP row is always written
    /// first; per the protocol's overwrite rules those writes erase every multicell in the rect
    /// before any lower-row cell is written, so lower-row writes land on ordinary cells, the
    /// terminal never skips the cursor, and the <c>_cursorCol += cell.Width</c> advance model stays
    /// truthful. Fragment re-emission is downstream and needs no extra wiring: every forced cell the
    /// cell pass emits lands in <see cref="_touchedCells"/>, which
    /// <see cref="FragmentFootprintTouched"/> already turns into a re-emit — so a fragment repaints
    /// exactly when it is new, moved, changed, or had any cell of its rect rewritten, and an
    /// untouched steady fragment still emits nothing.
    /// </para>
    /// </remarks>
    private void ComputeFragmentGuardCells(CellBuffer back)
    {
        if (_frontFragments.Count == 0) return;

        // ComputeForceCells leaves the scratch array stale when no force-repaint regions were
        // marked this frame (the _hasForceRepaint gate makes that safe for readers). The guard is
        // about to OR its own bits into the same array, so give it a clean slate first.
        if (!_hasForceRepaint)
            Array.Clear(_forceCells!);

        var caps = _capabilities ?? OutputCapabilities.None;

        _liveFrontFragmentRects.Clear();

        foreach (var (anchor, frontEntry) in _frontFragments)
        {
            if (frontEntry.Fragment.Layer != FragmentLayer.Cells) continue;
            if (!frontEntry.Fragment.IsSupported(caps)) continue;
            if (!TryGetFragmentRect(anchor.Column, anchor.Row, frontEntry.Fragment, back, out var rect)) continue;

            if (back.Fragments.TryGetValue(anchor, out var backEntry) && FragmentsMatch(frontEntry, backEntry))
                _liveFrontFragmentRects.Add(rect); // still live — a candidate for write expansion
            else
                ForceFragmentRect(rect, back);     // stale — its on-terminal glyphs must be overwritten
        }

        // Fixpoint: each pass either consumes at least one live rect or terminates, so this is
        // bounded by the (small) fragment count.
        bool expanded = true;
        while (expanded)
        {
            expanded = false;
            for (int i = _liveFrontFragmentRects.Count - 1; i >= 0; i--)
            {
                if (!FragmentRectWillBeTouched(_liveFrontFragmentRects[i], back)) continue;
                ForceFragmentRect(_liveFrontFragmentRects[i], back);
                _liveFrontFragmentRects.RemoveAt(i);
                expanded = true;
            }
        }
    }

    /// <summary>
    /// The clamped screen rect of the fragment anchored at (<paramref name="anchorCol"/>,
    /// <paramref name="anchorRow"/>) — the same footprint walk as <see cref="ComputeCoveredCells"/>,
    /// expressed as a <see cref="Rect"/>. False when the footprint lies entirely outside the buffer.
    /// </summary>
    private static bool TryGetFragmentRect(int anchorCol, int anchorRow, IBufferFragment fragment, CellBuffer back, out Rect rect)
    {
        var size = fragment.GetSize();
        int colStart = Math.Max(0, anchorCol);
        int rowStart = Math.Max(0, anchorRow);
        int colEnd = Math.Min(back.Columns, anchorCol + Math.Max(1, size.Columns));
        int rowEnd = Math.Min(back.Rows, anchorRow + Math.Max(1, size.Rows));

        if (colStart >= colEnd || rowStart >= rowEnd)
        {
            rect = Rect.Empty;
            return false;
        }

        rect = new Rect(colStart, rowStart, colEnd - colStart, rowEnd - rowStart);
        return true;
    }

    /// <summary>
    /// Mark every cell of <paramref name="rect"/> force-repaint so <see cref="EmitDiff"/> rewrites
    /// it even where the diff sees no change. When the rect's left edge splits a wide pair, the
    /// <see cref="CellKind.WideLeft"/> just outside is forced too — a continuation cell can only be
    /// repainted by emitting its left half.
    /// </summary>
    private void ForceFragmentRect(in Rect rect, CellBuffer back)
    {
        var force = _forceCells!;
        _hasForceRepaint = true;

        for (int r = rect.Row; r < rect.RowEnd; r++)
        {
            if (rect.Column > 0 && back[rect.Column, r].Kind == CellKind.WideContinuation)
                force[r * back.Columns + rect.Column - 1] = true;

            for (int c = rect.Column; c < rect.ColumnEnd; c++)
                force[r * back.Columns + c] = true;
        }
    }

    /// <summary>
    /// Predict whether this frame's cell pass will emit anything inside <paramref name="rect"/> —
    /// the same per-cell tests <see cref="EmitDiff"/> applies (force-repaint override, dirty-region
    /// gating, covered-cell substitution, quantization), without emitting. The one-column look-back
    /// covers writes that start just left of the rect but paint into it: a wide glyph covering its
    /// continuation column, and the ambiguous-width defense pre-painting its right neighbor.
    /// </summary>
    private bool FragmentRectWillBeTouched(in Rect rect, CellBuffer back)
    {
        var force = _forceCells!;
        var front = _frontCells!;

        for (int r = rect.Row; r < rect.RowEnd; r++)
        {
            var row = back.GetRowSpan(r);

            for (int c = Math.Max(0, rect.Column - 1); c < rect.ColumnEnd; c++)
            {
                int idx = r * back.Columns + c;

                bool forced = _hasForceRepaint && force[idx];
                if (_hasDirtyRegions && !_dirtyCells![idx] && !forced) continue;

                var cell = Adapt(IntendedCellFor(c, r, row[c], back), c, r);
                if (cell.Kind == CellKind.WideContinuation) continue; // never emitted directly
                if (!forced && cell == front[idx]) continue;

                if (c >= rect.Column) return true;

                // c == rect.Column - 1: the write itself lands outside the rect and only touches it
                // when the emission also paints the column to its right.
                if (cell.Kind == CellKind.WideLeft) return true;

                if (_capabilities?.TextSizing.ReliableWideGlyphs is false && IsAmbiguousWidthGrapheme(cell.Grapheme))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build the cell the renderer should actually emit for <paramref name="backCell"/> at
    /// (<paramref name="row"/>, <paramref name="column"/>). For uncovered cells this is the
    /// back cell itself. For cells covered by a Cell-layer fragment, this is a space carrying
    /// only the cell's background — the foreground glyph is dropped (the fragment's payload
    /// owns the foreground), but the background still paints so UI panels show consistently
    /// behind the fragment.
    /// </summary>
    private Cell IntendedCellFor(int column, int row, Cell backCell, CellBuffer back)
    {
        if (_coveredCells is null) return backCell;
        if (!_coveredCells[row * back.Columns + column]) return backCell;

        // Cover splits a wide glyph into two independent bg-only spaces, so the right half needs a
        // background of its own — and a continuation carries no style (CellBuffer stores Kind alone
        // there; the wide-left's SGR is what paints both columns). Read it off the wide-left, or the
        // covered pair emits one styled space beside one default-styled hole.
        var background = backCell.Kind == CellKind.WideContinuation && column > 0
                             ? back[column - 1, row].Style.Background
                             : backCell.Style.Background;

        // Drop the foreground. Keep just the background — that's what carries through.
        return new Cell(
            Grapheme: " ",
            Kind: CellKind.Single,
            Style: CellStyle.Default.WithBackground(background));
    }

    /// <summary>
    /// Maximum number of rows to consider for a scroll-detection match. The cost of detection
    /// is O(rows × cols × MaxScroll); 8 covers the practical cases (a log scrolling one or
    /// two lines at a time, a chat view paging up by a handful) without making the detection
    /// itself expensive on large buffers.
    /// </summary>
    private const int MaxScrollDetect = 8;

    /// <summary>
    /// Detect whether the back buffer matches the front buffer shifted up or down by some K
    /// rows. On a match, emit SU / SD K, shift <see cref="_frontCells"/> in place, and the
    /// subsequent <see cref="EmitDiff"/> naturally repaints only the K newly-uncovered rows
    /// (the shifted region matches by construction).
    /// </summary>
    private void TryDetectAndApplyScroll(CellBuffer back, IBufferWriter<byte> output)
    {
        if (_frontCells is null) return;

        // Ordered dither makes the emitted color position-dependent (each cell carries a Bayer phase from
        // its row/column). A vertical scroll by k rows would shift content onto a different phase, so the
        // shifted cells wouldn't match the front buffer's dithered values — and even if matched, a real SU/SD
        // would move the wrong dither pattern. Disable scroll detection while dithering; the per-cell diff
        // still produces a correct (just unscrolled) frame.
        if (_options.OrderedDither) return;

        int cols = back.Columns;
        int rows = back.Rows;
        int maxK = Math.Min(rows / 2, MaxScrollDetect);

        // Scroll up: back[k..] == front[..rows - k]. New content arrives at the bottom rows.
        for (int k = 1; k <= maxK; k++)
        {
            if (!CellsShiftedMatch(back, _frontCells, cols, rows, k, scrollUp: true)) continue;
            ApplyScroll(output, cols, rows, k, scrollUp: true);
            return;
        }

        // Scroll down: back[..rows - k] == front[k..]. New content arrives at the top rows.
        for (int k = 1; k <= maxK; k++)
        {
            if (!CellsShiftedMatch(back, _frontCells, cols, rows, k, scrollUp: false)) continue;
            ApplyScroll(output, cols, rows, k, scrollUp: false);
            return;
        }
    }

    /// <summary>
    /// Compare a shifted slice of the back buffer to the corresponding slice of the front
    /// buffer.
    /// <para>
    /// <b>Scroll up</b> (top content moves off, new content at bottom): the back's top rows
    /// equal the front's lower rows — <c>back[r] == front[r + k]</c> for <c>r ∈ [0, rows-k)</c>.
    /// </para>
    /// <para>
    /// <b>Scroll down</b> (bottom content moves off, new content at top): the back's lower rows
    /// equal the front's top rows — <c>back[r + k] == front[r]</c> for <c>r ∈ [0, rows-k)</c>.
    /// </para>
    /// </summary>
    private bool CellsShiftedMatch(CellBuffer back, Cell[] front, int cols, int rows, int k, bool scrollUp)
    {
        int compareRows = rows - k;
        for (int r = 0; r < compareRows; r++)
        {
            int backRow = scrollUp ? r : r + k;
            int frontRow = scrollUp ? r + k : r;

            var backSpan = back.GetRowSpan(backRow);
            for (int c = 0; c < cols; c++)
            {
                var backAdapted = Adapt(backSpan[c], c, backRow);
                if (!backAdapted.Equals(front[frontRow * cols + c])) return false;
            }
        }
        return true;
    }

    private void ApplyScroll(IBufferWriter<byte> output, int cols, int rows, int k, bool scrollUp)
    {
        if (scrollUp) ScreenWriter.WriteScrollUp(output, k);
        else          ScreenWriter.WriteScrollDown(output, k);

        // Shift the front buffer to reflect the scroll. The newly-uncovered rows become blank
        // on the terminal (per SU/SD semantics) — we initialize the corresponding front cells
        // to default so the cell diff repaints them with whatever the caller has there.
        //
        // SU/SD fill those rows with the terminal's CURRENT background, and because Adapt now stores
        // the emitted form, a zeroed front cell asserts they hold the terminal's own default — so that
        // fill has to land under a reset SGR state. It does: Render's end-of-frame reset leaves every
        // incremental frame starting at CellStyle.Default, and nothing between there and here touches
        // SGR (scroll detection never runs on a full redraw). The unpainted tail of a newly-uncovered
        // row then costs nothing, which on a log-shaped screen is most of the row.
        var front = _frontCells!;
        if (scrollUp)
        {
            Array.Copy(front, k * cols, front, 0, (rows - k) * cols);
            Array.Clear(front, (rows - k) * cols, k * cols);
        }
        else
        {
            Array.Copy(front, 0, front, k * cols, (rows - k) * cols);
            Array.Clear(front, 0, k * cols);
        }

        // The scroll command moves the cursor to (0, 0) on most terminals — force CUP next.
        // Also, reset our tracked SGR / hyperlink because SU/SD don't carry SGR state in a
        // well-defined way.
        _cursorCol = -1;
        _cursorRow = -1;
        _currentStyle = CellStyle.Default;
        _currentHyperlink = Hyperlink.None;
        SgrEncoder.WriteReset(output);
    }

    // Emit a cursor move to buffer position (row, column) — the one place buffer rows become
    // terminal position. Absolute (CUP, `row + RowOffset`) for full-screen and absolute-inline
    // rendering; RELATIVE (a CUU/CUD row delta from the tracked cursor row + a column-absolute CHA)
    // under RelativeInline. The relative form reads the CURRENT tracked row as the delta origin, so
    // callers must not have advanced _cursorRow past the real position before calling (SyncCursor and
    // the direct callers update it AFTER). A relative row delta is invariant under the absolute row
    // shift an unobserved clear causes — that invariance is the whole point (plan §2/§4b).
    private void MoveTo(IBufferWriter<byte> output, int column, int row)
    {
        if (RelativeInline)
        {
            int dr = row - _cursorRow;
            if (dr < 0) CursorWriter.WriteMoveUp(output, -dr);
            else if (dr > 0) CursorWriter.WriteMoveDown(output, dr);
            CursorWriter.WriteColumnAbsolute(output, column); // deterministic regardless of column drift
        }
        else
        {
            CursorWriter.WriteMoveTo(output, column, row + _rowOffset);
        }
    }

    // Re-position the cursor to (r, c) if our tracked position differs.
    private void SyncCursor(IBufferWriter<byte> output, int r, int c)
    {
        if (_cursorRow == r && _cursorCol == c) return;
        MoveTo(output, c, r);
        _cursorRow = r;
        _cursorCol = c;
    }

    // Emit the OSC 8 close/open needed to move from the current hyperlink to <paramref name="target"/>.
    private void SyncHyperlink(IBufferWriter<byte> output, in Hyperlink target)
    {
        if (target == _currentHyperlink) return;
        if (!_currentHyperlink.IsEmpty)
            HyperlinkWriter.WriteClose(output);
        if (!target.IsEmpty)
            HyperlinkWriter.WriteOpen(output, target.Uri.AsSpan(), target.Id.AsSpan());
        _currentHyperlink = target;
    }

    /// <summary>
    /// Record the colors the terminal reported as its own defaults, which is what SGR 39 / 49 select.
    /// Only a color the terminal can actually paint qualifies: an absent report and
    /// <see cref="Color.Transparent"/> both leave the channel at <see cref="Color.Default"/>, which
    /// disables the substitution for it (transparent especially — <see cref="SgrEncoder.WriteDelta"/>
    /// deliberately emits nothing at all for a transparent background, and substituting would start
    /// emitting <c>49</c> where today nothing goes out).
    /// </summary>
    /// <remarks>
    /// The renderer's own capabilities win: they describe the terminal these bytes are going to. The
    /// back buffer is the fallback for the capability-less constructor (tests, upstream quantizers) —
    /// it derived its blank style from the same negotiation, so it knows the same answer.
    /// </remarks>
    private void CaptureTerminalDefaultColors(CellBuffer back)
    {
        var color = _capabilities?.Color ?? back.Capabilities?.Output.Color;

        _terminalDefaultForeground = Reproducible(color?.DefaultForeground);
        _terminalDefaultBackground = Reproducible(color?.DefaultBackground);

        static Color Reproducible(Color? reported)
            => reported is { IsDefault: false, IsTransparent: false } value ? value : Color.Default;
    }

    /// <summary>
    /// Replace a color that is already the terminal's own default with <see cref="Color.Default"/>, so
    /// the delta emits the default-color code (SGR 39 / 49) instead of naming the color.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two callers, and the rule has to be identical for both: <see cref="Adapt"/> (whose result is what
    /// the cell pass emits <em>and</em> what the front buffer stores) and the fragment backdrop in
    /// <see cref="EmitFragmentBytes"/>.
    /// </para>
    /// <para>
    /// <see cref="CellBuffer.DefaultStyle"/> promotes the terminal's reported default fg/bg to a
    /// concrete color so blank cells can alpha-blend, and the frame compositor bases itself on that
    /// blank — so an <em>unpainted</em> cell arrives here carrying a real color rather than
    /// <see cref="Color.Default"/>. Naming it costs bytes, and at a reduced color depth it is also less
    /// accurate: <see cref="StyleQuantizer"/> snaps it to the nearest palette entry, an approximation of
    /// a color the terminal reproduces <em>exactly</em> when told to use its default. Identical at
    /// truecolor, strictly closer below it, shorter either way.
    /// </para>
    /// <para>
    /// The decision is made on <paramref name="source"/> — the style <em>before</em> capability
    /// adaptation — while the replacement is applied to <paramref name="adapted"/>. Comparing the
    /// quantized forms instead would also catch a <em>different</em> color that merely lands on the same
    /// palette entry, and paint the terminal's default over it.
    /// </para>
    /// <para>
    /// Two of the three color channels are substitutable. The third is not: the underline channel's
    /// default code (SGR 59) means "follow the foreground", not "the terminal's default underline
    /// color" — no such color is reported, and <see cref="Color.Default"/> already <em>is</em>
    /// follow-the-foreground here, so there is nothing to substitute.
    /// </para>
    /// </remarks>
    private CellStyle SubstituteTerminalDefaultColors(in CellStyle adapted, in CellStyle source)
    {
        var style = adapted;

        if (!_terminalDefaultForeground.IsDefault && source.Foreground == _terminalDefaultForeground)
            style = style.WithForeground(Color.Default);

        if (!_terminalDefaultBackground.IsDefault && source.Background == _terminalDefaultBackground)
            style = style.WithBackground(Color.Default);

        return style;
    }

    // Emit the SGR delta needed to move from the current style to <paramref name="target"/>, which has
    // already been through Adapt — capability quantization AND the default-color substitution — so it
    // IS the emitted form. _currentStyle therefore tracks the state the TERMINAL is in rather than what
    // the caller asked for; recording the request instead would leave every subsequent delta computed
    // against a state the terminal is not in.
    private void SyncStyle(IBufferWriter<byte> output, in CellStyle target)
    {
        if (target == _currentStyle) return;
        SgrEncoder.WriteDelta(output, _currentStyle, target);
        _currentStyle = target;
    }

    private void EmitDiff(CellBuffer back, IBufferWriter<byte> output)
    {
        // Reset the per-frame touched-cell record; EmitFragments reads it to decide whether a
        // Cells-layer fragment's footprint was overpainted this frame and must re-emit.
        Array.Clear(_touchedCells!);

        for (int r = 0; r < back.Rows; r++)
        {
            ReadOnlySpan<Cell> row = back.GetRowSpan(r);

            // When an ambiguous-width glyph fires its defense it paints its right neighbor as
            // part of the same operation and then skips it (see below). Reset per row so a value
            // armed by the previous row's last column can't bleed across the row boundary.
            int skipColumn = -1;

            for (int c = 0; c < back.Columns; c++)
            {
                if (c == skipColumn)
                {
                    // Already painted by the preceding ambiguous-width glyph's defense, and we
                    // must NOT write into this cell again — on a terminal rendering the glyph as
                    // two cells, this is the glyph's second half, and any write here blanks the
                    // whole glyph. The front buffer was updated when the pair was emitted.
                    skipColumn = -1;
                    continue;
                }

                int frontIdx = r * _frontCols + c;

                // Force-repaint: this cell must emit even if it matches the front buffer (the front
                // can't tell its content is stale — e.g. a removed Cells-layer image's lingering
                // pixels). Computed once and honored by both the dirty-region skip and the equality
                // short-circuit below.
                bool forced = _hasForceRepaint && _forceCells![frontIdx];

                // Dirty-region opt-in: cells outside any marked region are skipped entirely.
                // The renderer trusts that the consumer is responsible for marking every
                // cell they've changed; cells outside the union of regions stay as the front
                // believed they were. This shortcut applies only when DirtyRegions is non-
                // empty — empty regions fall back to a full-buffer diff. A force-repaint cell is
                // never skipped, even when outside every dirty region.
                if (_hasDirtyRegions && !_dirtyCells![frontIdx] && !forced) continue;

                // Compute the cell we actually want on the terminal for this position: under
                // a Cell-layer fragment, that's a bg-only space; everywhere else it's the back
                // cell verbatim. Then quantize for capability-aware emission. The same value
                // gets compared against the front and snapshotted into it, so a stable rendered
                // frame produces an empty delta.
                var intended = IntendedCellFor(c, r, row[c], back);
                var cell = Adapt(intended, c, r);

                // Wide-continuation cells are skipped from emission here. They aren't "left
                // undrawn" — the wide glyph emitted at the corresponding WideLeft position
                // paints both cell columns (foreground and background) as a single terminal
                // operation. Trying to emit anything at the right-half column is undefined for
                // most terminals (the cursor is already advanced past it after the wide-glyph
                // emission, and moving back into the glyph corrupts it). We still snapshot the
                // continuation into the front buffer so subsequent frames diff correctly.
                if (cell.Kind == CellKind.WideContinuation)
                {
                    _frontCells![frontIdx] = cell;
                    continue;
                }

                if (!forced && cell == _frontCells![frontIdx]) continue;

                // Wide-glyph defense for terminals that don't reliably render two-cell glyphs:
                // pre-paint cells c and c+1 with the wide-left's style by emitting two spaces,
                // then CUP back to c so the wide glyph emits at the right column. On an
                // honoring terminal the wide glyph overpaints both spaces, and the cursor
                // advances by 2; on a non-honoring one, the wide glyph shrinks to a single
                // cell, but our pre-painted space at c+1 keeps the cell's background/style
                // intact. Either way we mark the cursor dirty afterward, so the next emit
                // issues an explicit CUP rather than trusting the actual advance count.
                bool wideDefense = cell.Kind == CellKind.WideLeft &&
                                   _capabilities?.TextSizing.ReliableWideGlyphs is false &&
                                   c + 1 < back.Columns;

                // Ambiguous-width defense: a glyph we count as a single cell (CellKind.Single,
                // Width 1) but whose codepoint is East-Asian-Ambiguous (box-drawing rules, block
                // elements, geometric shapes, arrows, sub/superscripts, …) may be rendered TWO
                // cells wide by a terminal configured to treat ambiguous width as wide. On such a
                // terminal we must treat it exactly like a wide glyph: paint its right neighbor's
                // real content FIRST, emit the glyph at c, then SKIP c+1 — never writing into the
                // glyph's second half, because that write blanks the whole glyph (the cause of
                // the "horizontal rules vanish on GNOME Terminal with ambiguous=wide" symptom).
                // Painting the neighbor first means a narrow-rendering terminal keeps c+1's
                // content, while a wide-rendering one has it covered by the glyph. Gated on the
                // same ReliableWideGlyphs capability as the wide-glyph defense.
                //
                // KNOWN LIMITATION — distinct ambiguous neighbors on an ambiguous-wide terminal.
                // This is correct when the neighbor is identical to or coverable by the glyph (a
                // run of identical rule glyphs renders as a continuous line). When neighbors are
                // DISTINCT (e.g. the "012345" of [font=superscript]), it can't be: N distinct
                // glyphs each rendered two-wide need 2N columns, but the content was measured and
                // laid out at width 1, so it only has N. The glyph at c covers c+1, eating the
                // neighbor — every other distinct glyph disappears. There is no emission strategy
                // that fits N distinct double-width glyphs into N cells; the only complete fix is
                // to MEASURE ambiguous glyphs as width 2 (so layout allocates 2N cells), which
                // requires detecting the terminal's ambiguous-width preference (a CPR probe) and
                // is deliberately out of scope — it's a non-default terminal setting, and this
                // defense at least contains the damage (no overflow, no drift, no erased
                // neighbors outside the run) and renders half the run rather than none. The
                // behavior is identical whether the cells came from the text formatter or a
                // direct CellBuffer write, which is the property we want.
                bool ambiguousDefense = !wideDefense &&
                                        cell.Kind == CellKind.Single &&
                                        cell.Width == 1 &&
                                        c + 1 < back.Columns &&
                                        _capabilities?.TextSizing.ReliableWideGlyphs is false &&
                                        IsAmbiguousWidthGrapheme(cell.Grapheme);

                if (ambiguousDefense)
                {
                    // Paint the right neighbor with its own content first (so a narrow render
                    // keeps it), then the ambiguous glyph at c (so a wide render covers it).
                    var neighborIntended = IntendedCellFor(c + 1, r, row[c + 1], back);
                    var neighbor = Adapt(neighborIntended, c + 1, r);

                    SyncCursor(output, r, c + 1);
                    SyncHyperlink(output, neighbor.Style.Hyperlink);
                    SyncStyle(output, neighbor.Style);
                    WriteGraphemeUtf8(output, neighbor);
                    _frontCells![frontIdx + 1] = neighbor;
                    _touchedCells![frontIdx + 1] = true;

                    SyncCursor(output, r, c);
                    SyncHyperlink(output, cell.Style.Hyperlink);
                    SyncStyle(output, cell.Style);
                    WriteGraphemeUtf8(output, cell);
                    _frontCells![frontIdx] = cell;
                    _touchedCells![frontIdx] = true;

                    // We can't trust the post-glyph cursor column (terminal advanced 1 or 2),
                    // and c+1 is already painted — skip it and force a CUP for whatever follows.
                    _cursorCol = -1;
                    skipColumn = c + 1;
                    continue;
                }

                SyncCursor(output, r, c);
                SyncHyperlink(output, cell.Style.Hyperlink);
                SyncStyle(output, cell.Style);

                if (wideDefense)
                {
                    var twoSpaces = output.GetSpan(2);
                    twoSpaces[0] = (byte) ' ';
                    twoSpaces[1] = (byte) ' ';
                    output.Advance(2);

                    MoveTo(output, c, r);
                    _cursorRow = r;
                    _cursorCol = c;
                }

                WriteGraphemeUtf8(output, cell);
                _frontCells![frontIdx] = cell;
                _touchedCells![frontIdx] = true;

                // A wide glyph paints its continuation column as part of the same emission —
                // record that touch too, so a Cells-layer fragment whose rect starts at the
                // continuation column still re-emits (FragmentFootprintTouched reads this).
                if (cell.Kind == CellKind.WideLeft && c + 1 < back.Columns)
                    _touchedCells![frontIdx + 1] = true;

                if (wideDefense)
                {
                    // We don't know if the terminal advanced 1 or 2 cells after the wide-glyph
                    // emission, so force a CUP before the next emit instead of trusting
                    // cell.Width.
                    _cursorCol = -1;
                }
                else
                {
                    _cursorCol += cell.Width;

                    // If the next cursor position would be at or past the right edge, force a
                    // re-position before the next emit so terminal autowrap can't surprise us.
                    if (_cursorCol >= back.Columns)
                        _cursorCol = -1;
                }
            }
        }
    }

    /// <summary>
    /// True when <see cref="EmitDiff"/> re-emitted at least one cell inside the footprint of the
    /// Cells-layer fragment anchored at (<paramref name="anchorCol"/>, <paramref name="anchorRow"/>).
    /// Such a fragment has had its payload overpainted by the cell pass's covered-cell (bg-only)
    /// write, so it must re-emit even when its <see cref="IBufferFragment.Key"/> and anchor style are
    /// unchanged — otherwise a change UNDER the fragment (the panel behind sized text repainting on a
    /// theme flip, a spinner cycling, …) erases the fragment and nothing draws it back. Mirrors the
    /// <see cref="ComputeCoveredCells"/> footprint walk.
    /// </summary>
    private bool FragmentFootprintTouched(int anchorCol, int anchorRow, IBufferFragment fragment, CellBuffer back)
    {
        if (_touchedCells is not { } touched) return false;

        var size = fragment.GetSize();
        int colEnd = Math.Min(back.Columns, anchorCol + Math.Max(1, size.Columns));
        int rowEnd = Math.Min(back.Rows, anchorRow + Math.Max(1, size.Rows));

        for (int r = Math.Max(0, anchorRow); r < rowEnd; r++)
            for (int c = Math.Max(0, anchorCol); c < colEnd; c++)
                if (touched[r * back.Columns + c]) return true;

        return false;
    }

    private void EmitFragments(CellBuffer back, IBufferWriter<byte> output)
    {
        // Use OutputCapabilities.None when the renderer wasn't constructed with capabilities —
        // fragments that strictly require a feature will report unsupported and skip, which is
        // the right answer when we have no information.
        var caps = _capabilities ?? OutputCapabilities.None;

        // Pass 1 — erase any front fragment not still present at the same anchor with the same
        // identity. Identity is Key + AnchorStyle (NOT reference equality): a consumer that
        // reconstructs an identical fragment every frame produces a new instance but the same
        // content-derived Key, and we must recognize it as unchanged. Otherwise we'd erase and
        // re-transmit it every frame — which for an overlay protocol like Kitty graphics churns
        // a fresh image ID per frame and exhausts the terminal's image store on a long session.
        // Cases covered: removed entirely, replaced (different Key at anchor), moved (anchor now
        // empty). Cell-layer's default no-op EmitErase makes iterating all layers safe.
        foreach (var (anchor, frontEntry) in _frontFragments)
        {
            if (back.Fragments.TryGetValue(anchor, out var backEntry) && FragmentsMatch(frontEntry, backEntry))
                continue;

            if (!frontEntry.Fragment.IsSupported(caps)) continue;
            EmitFragmentEraseBytes(anchor.Column, anchor.Row, frontEntry, output, caps);
        }

        // Pass 2 — emit new or changed fragments (those with no matching front entry at the
        // anchor). Pass 1 already erased everything that needed it.
        foreach (var ((col, row), entry) in back.Fragments)
        {
            if (!entry.Fragment.IsSupported(caps)) continue;
            if (col < 0 || col >= back.Columns || row < 0 || row >= back.Rows) continue;

            if (_frontFragments.TryGetValue((col, row), out var frontEntry) &&
                FragmentsMatch(frontEntry, entry) &&
                !(entry.Fragment.Layer == FragmentLayer.Cells &&
                  FragmentFootprintTouched(col, row, entry.Fragment, back)))
            {
                // Same Key + anchor style, and the cell pass didn't overpaint the footprint — the
                // terminal already shows the current payload. A Cells-layer fragment whose footprint
                // WAS repainted this frame falls through to re-emit: the covered-cell bg-only writes
                // just clobbered its payload (e.g. the panel behind sized text repainting on a theme flip).
                continue;
            }

            var style = back[col, row].Style;
            EmitFragmentBytes(col, row, entry, output, caps, style);
        }

        // Snapshot for next render's diff. On a Key match we keep the FRONT entry, not the back
        // one: the matched back fragment (same content Key, fresh instance) was never
        // transmitted, so the wire identity actually on the terminal is the front fragment's
        // (e.g. its Kitty image ID). Preserving it means a future erase targets the id we really
        // sent. New/changed fragments snapshot the back entry we just emitted.
        var refreshed = new Dictionary<(int Column, int Row), CellBuffer.FragmentEntry>(back.Fragments.Count);
        foreach (var (anchor, entry) in back.Fragments)
        {
            refreshed[anchor] = _frontFragments.TryGetValue(anchor, out var frontEntry) && FragmentsMatch(frontEntry, entry)
                                    ? frontEntry
                                    : entry;
        }

        _frontFragments.Clear();
        foreach (var (anchor, entry) in refreshed)
            _frontFragments[anchor] = entry;
    }

    /// <summary>
    /// Two fragment registrations are "the same" for diff purposes when their content-derived
    /// <see cref="IBufferFragment.Key"/> and anchor style match. Key defaults to reference
    /// identity, so reference-keyed fragments compare exactly as before; content-keyed fragments
    /// (e.g. <see cref="KittyImageFragment"/>) additionally let an identical per-frame
    /// reconstruction diff-skip instead of churning a re-transmit.
    /// </summary>
    private static bool FragmentsMatch(in CellBuffer.FragmentEntry a, in CellBuffer.FragmentEntry b)
        => Equals(a.Fragment.Key, b.Fragment.Key) && a.AnchorStyle == b.AnchorStyle;

    /// <summary>Bracket-emit a fragment's payload with DECSC / DECRC + cursor + SGR backdrop.</summary>
    internal void EmitFragmentBytes(int col, int row, CellBuffer.FragmentEntry entry,
                                    IBufferWriter<byte> output, OutputCapabilities caps,
                                    CellStyle existingStyle)
    {
        // Save the cursor, move to the anchor. Absolute: DECSC + CUP (DECRC restores below). Relative:
        // no DECSC — DECRC's cursor restore is implementation-defined, and a relative model has no
        // absolute CUP to correct its drift, so we track through and return explicitly (see the end).
        int savedFragRow = _cursorRow, savedFragCol = _cursorCol;
        if (!RelativeInline)
            CursorWriter.WriteSavePosition(output);
        MoveTo(output, col, row);
        if (RelativeInline) { _cursorRow = row; _cursorCol = col; }

        // The anchor cell's style before capability adaptation. The default-color substitution decides
        // on the unadapted color and applies the replacement to the adapted one — see
        // SubstituteTerminalDefaultColors for why the decision cannot be made on the quantized form.
        var source = existingStyle;

        existingStyle = AdaptStyle(existingStyle, col, row);

        // The backdrop gets the same substitution the cell pass applies, or a fragment anchored over
        // unpainted background paints the quantized approximation of the terminal's default while every
        // cell around it now shows the terminal's true default — a seam at any depth below truecolor.
        // It cooperates with WriteAbsolute rather than duplicating it: the leading SGR 0 already puts
        // the terminal at its own default fg/bg, which is exactly why WriteAbsolute skips a channel
        // holding Color.Default.
        //
        // The delta below keeps the UNSUBSTITUTED style as its starting point, deliberately. The two
        // differ only in a channel the terminal is already displaying its default in, so the delta's
        // "unchanged" branch leaves that default standing — which is the substitution propagating to
        // the anchor style, not a state-tracking slip. Substituting the delta's origin instead would
        // make an anchor style that IS the derived default compare unequal and re-emit the very
        // approximation this removes.
        SgrEncoder.WriteAbsolute(output, SubstituteTerminalDefaultColors(existingStyle, source));

        var anchorStyle = entry.Fragment.StyleOverride?.BlendOver(entry.AnchorStyle, entry.Fragment.StyleBlendMode) ?? entry.AnchorStyle;

        // Transparency is resolved here or not at all, and the three colour channels do not resolve the
        // same way.
        //
        // Cell painting composites transparency away against the back buffer (Color.Composite returns the
        // backdrop for a transparent source), so by emission time a cell holds resolved colours. A
        // FRAGMENT is never painted into cells — OSC 66 sized text draws over a region whose cells the
        // renderer does not own — so it gets an absolute SGR instead, and whatever alpha survives to this
        // point goes on the wire. Leaving it alone is not neutral: Color.Transparent is Rgb(0,0,0,0), and
        // SgrEncoder's transparency check covers the BACKGROUND channel (WriteDelta, WriteAbsolute) while
        // the foreground is written unconditionally, so a transparent FOREGROUND escapes as an explicit
        // BLACK — a colour the author never wrote, and one that can render the fragment invisible on a
        // dark terminal.
        //
        // BACKGROUND has a backdrop to inherit: the fragment is drawn over the anchor cell's surface, so
        // that cell's background is what "show what is behind" means here, and taking it is the nearest
        // thing to compositing this path can do.
        //
        // FOREGROUND and UNDERLINE COLOUR have no counterpart — there is no ink underneath a glyph for it
        // to show through, so every concrete colour would be invented, including the anchor cell's own
        // (that is one arbitrary cell's ink, and the fragment covers a whole region). Naming no colour is
        // the honest statement: a transparent foreground asked not to be seen, and SGR 39 hands the choice
        // to the terminal. For the underline channel Color.Default additionally means "follow the
        // foreground" (see SubstituteTerminalDefaultColors), which is exactly where a transparent underline
        // belongs once the foreground has stopped naming a colour.
        //
        // Deciding this before AdaptStyle settles a tier disagreement rather than adding a rule:
        // StyleQuantizer maps Color.Transparent to Color.Default at every depth BELOW truecolor, so the
        // reduced depths already emitted the bare 39 while full depth — where quantization passes the
        // colour through untouched — emitted the black. Substituting here makes truecolor agree with them.
        // Captured before the substitutions erase the evidence: the delta guard below has to know that a
        // transparent channel WAS declared, since resolving one can land the style on CellStyle.Default.
        bool resolvedTransparency = anchorStyle.Background.IsTransparent ||
                                    anchorStyle.Foreground.IsTransparent ||
                                    anchorStyle.UnderlineColor.IsTransparent;

        if (anchorStyle is { Background.IsTransparent: true })
            anchorStyle = anchorStyle.WithBackground(existingStyle.Background);

        if (anchorStyle is { Foreground.IsTransparent: true })
            anchorStyle = anchorStyle.WithForeground(Color.Default);

        if (anchorStyle is { UnderlineColor.IsTransparent: true })
            anchorStyle = anchorStyle.WithUnderlineColor(Color.Default);

        anchorStyle = AdaptStyle(anchorStyle, col, row);

        // A fragment that declared NO style leaves the backdrop the absolute SGR just wrote standing:
        // AddFragment's anchorStyle defaults to CellStyle.Default, and that is an absence, not a request
        // for the terminal's own fg/bg. Asking whether the style IS CellStyle.Default is how that absence
        // is recognised — but only for a style the substitutions above did not touch. A DECLARED
        // transparency can land on CellStyle.Default too (a transparent foreground resolves to
        // Color.Default; over an anchor whose background is already Color.Default nothing fills the other
        // channel in), and there the absence test reads a request as an absence: it skips the delta, leaves
        // the terminal in the backdrop, and paints the fragment in the ANCHOR CELL'S INK — the inheritance
        // the foreground rule exists to refuse. So a substitution having fired is itself proof the style was
        // declared, and it lets the delta through.
        //
        // Comparing against existingStyle instead does not work: it makes the no-style case emit a delta
        // (39;49) that erases the backdrop, which is the carve-out this guard was written for.
        if (anchorStyle != CellStyle.Default || resolvedTransparency)
            SgrEncoder.WriteDelta(output, in existingStyle, in anchorStyle);

        entry.Fragment.Emit(col, row, output, caps);

        if (RelativeInline)
        {
            // Explicit relative return to the pre-fragment position — no DECRC (its cursor restore is
            // implementation-defined and a relative model can't correct the drift with an absolute CUP).
            MoveTo(output, savedFragCol, savedFragRow);
            _cursorRow = savedFragRow;
            _cursorCol = savedFragCol;
        }
        else
        {
            CursorWriter.WriteRestorePosition(output);
        }

        // DECRC's SGR-restore behavior varies across terminals (xterm restores it; some VT
        // emulators don't). Explicitly resync our SGR tracking by writing an SGR reset.
        SgrEncoder.WriteReset(output);
        _currentStyle = CellStyle.Default;

        if (!RelativeInline)
        {
            // DECRC's *cursor*-restore behavior is also implementation-defined when the saved-state
            // stack has been disturbed in between (some conhost versions, ConEmu/Cmder, …).
            // Invalidate the tracked position so the next emission issues an explicit CUP rather
            // than trusting DECRC to have landed us where DECSC saved. Symptom of getting this
            // wrong: rows downstream of fragment emissions shift left by N (where N is roughly the
            // count of fragments emitted this frame), producing the "first few characters of each
            // labeled row are missing" wrap that conhost / WT exhibit. (Relative mode returns
            // explicitly above, so the tracked position stays truthful and needs no invalidation.)
            _cursorRow = -1;
            _cursorCol = -1;
        }
    }

    /// <summary>
    /// Bracket-emit a fragment's erase sequence. Cell-layer fragments default to a no-op
    /// erase since cell repainting handles the visual removal; overlay-layer fragments emit
    /// protocol-specific delete sequences.
    /// </summary>
    private void EmitFragmentEraseBytes(int col, int row, CellBuffer.FragmentEntry entry,
                                        IBufferWriter<byte> output, OutputCapabilities caps)
    {
        // See EmitFragmentBytes for the absolute-vs-relative bracket rationale.
        int savedEraseRow = _cursorRow, savedEraseCol = _cursorCol;
        if (!RelativeInline)
            CursorWriter.WriteSavePosition(output);
        MoveTo(output, col, row);
        if (RelativeInline) { _cursorRow = row; _cursorCol = col; }

        entry.Fragment.EmitErase(col, row, output, caps);

        if (RelativeInline)
        {
            MoveTo(output, savedEraseCol, savedEraseRow);
            _cursorRow = savedEraseRow;
            _cursorCol = savedEraseCol;
        }
        else
        {
            CursorWriter.WriteRestorePosition(output);
        }

        SgrEncoder.WriteReset(output);
        _currentStyle = CellStyle.Default;

        if (!RelativeInline)
        {
            // See EmitFragmentBytes for why we invalidate the tracked cursor after DECRC: the
            // restore is implementation-defined when the SC/RC stack has been disturbed, and
            // trusting DECRC to land us at the pre-DECSC position produces silent drift across
            // frames on conhost / ConEmu. (Relative mode returns explicitly and stays truthful.)
            _cursorRow = -1;
            _cursorCol = -1;
        }
    }

    /// <summary>
    /// The cell the renderer will actually put on the wire: quantized for the terminal's colour depth,
    /// then with any colour still equal to the terminal's own reported default replaced by
    /// <see cref="Color.Default"/> — the form SGR 39 / 49 emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The front buffer stores <em>this</em>, deliberately — the emitted form, not the intended one.
    /// <see cref="CellBuffer.DefaultStyle"/> promotes the terminal's reported default fg/bg to a concrete
    /// colour so blank cells can alpha-blend, so an UNPAINTED cell arrives here as a null grapheme
    /// carrying real colours. Normalising them away collapses it to <c>default(Cell)</c>, which is
    /// precisely what a freshly-allocated front buffer holds — so it drops out of the frame diff
    /// altogether instead of costing a cursor move plus a space. On a mostly-empty screen that is most
    /// of the frame.
    /// </para>
    /// <para>
    /// What makes the claim true is the full redraw's ordering: <c>SGR 0</c> precedes <c>CSI 2 J</c>, so
    /// the erase leaves the terminal's own default behind and a zeroed front cell is an accurate
    /// statement about the screen. Reverse them and a screen cleared under an inherited background
    /// would never be repainted.
    /// </para>
    /// </remarks>
    private Cell Adapt(in Cell cell, int column, int row)
    {
        // The substitution decides on the UNADAPTED colour and applies to the adapted one — see
        // SubstituteTerminalDefaultColors for why it cannot be decided on the quantized form.
        var style = SubstituteTerminalDefaultColors(AdaptStyle(cell.Style, column, row), cell.Style);
        return style == cell.Style ? cell : cell with { Style = style };
    }

    private CellStyle AdaptStyle(in CellStyle style, int column, int row)
    {
        if (_quantizer is null) return style;
        // Ordered dither perturbs RGB by the cell's position before palette reduction (no-op at full
        // depth / for non-RGB). Off → the plain position-independent quantize.
        return _options.OrderedDither
                   ? _quantizer.QuantizeDithered(style, column, row)
                   : _quantizer.Quantize(style);
    }

    private static void WriteGraphemeUtf8(IBufferWriter<byte> output, in Cell cell)
    {
        // Empty grapheme on a Single cell renders as a space; that paints the cell's background
        // and advances the cursor. WideLeft with empty grapheme is degenerate — emit two spaces
        // so the terminal still advances by 2.
        string grapheme = string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme == CellBuffer.DurableEmptyGrapheme
                              ? cell.Kind == CellKind.WideLeft ? "  " : " "
                              : cell.Grapheme;

        int max = Encoding.UTF8.GetMaxByteCount(grapheme.Length);
        var dest = output.GetSpan(max);
        int written = Encoding.UTF8.GetBytes(grapheme, dest);

        output.Advance(written);
    }

    /// <summary>
    /// True when <paramref name="grapheme"/>'s leading codepoint is East-Asian-Ambiguous width —
    /// a single cell in our model that an ambiguous-as-wide terminal may render across two. Used
    /// to gate the ambiguous-width cursor defense in <see cref="EmitDiff"/>. An empty grapheme
    /// (blank cell rendered as a space) is unambiguously narrow.
    /// </summary>
    private static bool IsAmbiguousWidthGrapheme(string? grapheme)
    {
        if (string.IsNullOrEmpty(grapheme)) return false;
        return GraphemeWidth.IsAmbiguousWidth(char.ConvertToUtf32(grapheme, 0));
    }

    private void EmitCursor(CellBuffer back, IBufferWriter<byte> output)
    {
        // Shape first, then visibility, then position — the canonical order keeps the cursor
        // glyph from being drawn momentarily at the old position with the new shape.
        //
        // Gate DECSCUSR on the negotiated ShapeControl capability. DECSCUSR is `CSI Ps SP q` —
        // the only sequence in our output vocabulary ending in 'q', and on terminals that don't
        // implement it (Apple Terminal) the `SP q` form is mis-parsed and the literal 'q' is
        // printed. Since the renderer otherwise emits this on every first frame (even to set the
        // default shape, which is a no-op on a fresh session), an unsupporting terminal shows a
        // stray 'q'. When capabilities are unknown (no-caps constructor — tests / upstream
        // quantizers) we keep emitting, preserving the prior behavior.
        bool shapeControl = _capabilities?.Cursor.ShapeControl ?? true;
        if (shapeControl && (_firstFrame || back.CursorShape != _cursorShape))
        {
            CursorWriter.WriteShape(output, back.CursorShape);
            _cursorShape = back.CursorShape;
        }

        if (_firstFrame || back.CursorVisible != _cursorVisible)
        {
            if (back.CursorVisible) CursorWriter.WriteShow(output);
            else CursorWriter.WriteHide(output);

            _cursorVisible = back.CursorVisible;
        }

        if (_cursorRow != back.CursorRow || _cursorCol != back.CursorColumn)
        {
            MoveTo(output, back.CursorColumn, back.CursorRow);
            _cursorRow = back.CursorRow;
            _cursorCol = back.CursorColumn;
        }
    }

    /// <param name="output">The frame output target.</param>
    /// <param name="eraseFragments">
    /// Whether standing fragments emit their protocol erase sequences (the default). An inline host
    /// retaining its last frame on exit (<c>InlineExitBehavior.Retain</c>) passes false so images /
    /// sized text survive in the retained region; the tracked fragment set is forgotten either way.
    /// </param>
    public void Close(IBufferWriter<byte> output, bool eraseFragments = true)
    {
        var fragments = _frontFragments.ToList();

        _frontFragments.Clear();

        if (eraseFragments)
        {
            foreach (var f in fragments)
                f.Value.Fragment.EmitErase(f.Key.Column, f.Key.Row, output, _capabilities ?? OutputCapabilities.None);
        }

        // Restore autowrap to the terminal's default-on state. Pairs with the WriteDisableAutowrap
        // call in the first-frame full-redraw branch — without this the next program to use the
        // shell would inherit a no-wrap terminal, which is wrong outside a cell-grid renderer.
        ScreenWriter.WriteEnableAutowrap(output);
    }
}

/// <summary>
/// Tunables for <see cref="FrameRenderer"/>. Default-constructed instance is "normal renderer
/// behavior" — diff per frame, no debug overrides.
/// </summary>
/// <param name="ForceFullRedraw">
/// When true, every <see cref="FrameRenderer.Render"/> call is treated as a full redraw rather
/// than a diff. Intended for debugging / profiling — disables the renderer's diff optimization
/// without changing the API surface.
/// </param>
/// <param name="RestrictToDirtyRegions">
/// Opt-in for dirty-region-exclusive rendering. When true and the back buffer has marked dirty
/// regions (<see cref="CellBuffer.DirtyRegions"/>), the diff only emits cells inside the union of
/// those regions — the consumer takes on the contract that it has marked <i>every</i> cell it
/// changed. When false (the default), <see cref="CellBuffer.DirtyRegions"/> are ignored for
/// emission and the renderer always diffs the full buffer. This default keeps the renderer robust
/// against stray dirty marks (e.g. an internal <see cref="CellBuffer.RemoveFragment(int, int)"/>,
/// which marks the removed footprint dirty): without the opt-in, such a mark can't silently drop
/// unrelated changed cells. The dirty regions are still cleared each frame regardless of this flag.
/// </param>
/// <param name="OrderedDither">
/// Opt-in for ordered (Bayer) dithering of RGB colors during capability-aware quantization. When the
/// renderer was constructed with an <see cref="OutputCapabilities"/> whose color depth reduces RGB
/// (256- or 16-color), this spatially perturbs each cell's color by its position before snapping to the
/// palette, trading a stipple for perceived depth so gradients don't band into flat stripes. A no-op at
/// truecolor (nothing to reduce) and for non-RGB colors. Has no effect when the renderer has no
/// capabilities (the raw-style constructor). <b>Disables vertical scroll detection</b> while on, because
/// the per-cell dither phase is position-dependent and would not survive a row shift.
/// </param>
/// <param name="Inline">
/// Opt-in for INLINE rendering: the buffer is a region of the main screen buffer rather than the whole
/// screen, anchored at <see cref="FrameRenderer.RowOffset"/>. Three behaviors change: full redraws
/// erase with "CUP to region top + ED 0" (cursor-to-end-of-screen) instead of ED 2 — the rows above
/// the region belong to the shell and must never clear; every emitted cursor address adds
/// <see cref="FrameRenderer.RowOffset"/>; and vertical scroll detection (SU/SD) is disabled, since
/// those sequences scroll the whole screen, shell history included. The host owns the offset: it
/// discovers where the region starts, keeps the region's bottom on-screen (scrolling with literal
/// line-feeds when the region grows past the last row), and updates <see cref="FrameRenderer.RowOffset"/>
/// before rendering.
/// </param>
/// <param name="RelativeInline">
/// Opt-in (inline only) for RELATIVE cursor positioning instead of absolute <c>CUP</c>: each move is a
/// <c>CUU</c>/<c>CUD</c> row delta from the tracked cursor plus a column-absolute <c>CHA</c>, and the
/// region floats relative to the cursor's physical position rather than an absolute
/// <see cref="FrameRenderer.RowOffset"/>. Because a relative row delta is invariant under the absolute
/// row shift an <em>unobserved</em> terminal clear causes, the region survives such a clear on
/// terminals that retain it (see <c>docs/ui-layer-design/inline-relative-move-plan.md</c>). Requires
/// <see cref="Inline"/>; ignored otherwise. Contract: the host keeps the physical cursor parked at the
/// region bottom-left at the start of every frame (the renderer parks it there at frame end).
/// </param>
public readonly record struct FrameRendererOptions(
    bool ForceFullRedraw = false, bool RestrictToDirtyRegions = false, bool OrderedDither = false,
    bool Inline = false, bool RelativeInline = false);