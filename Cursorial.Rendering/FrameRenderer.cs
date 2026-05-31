using System.Buffers;
using System.Text;

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
/// (Full redraw only) <see cref="ScreenWriter.WriteClearScreen"/> + SGR reset.
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

    // Reusable scratch buffer for the per-render "is this cell inside a dirty region?"
    // lookup. Only built when CellBuffer.DirtyRegions is non-empty; sized parallel to
    // _coveredCells. When the back buffer doesn't supply dirty regions, this field stays
    // logically inactive — the cell loop falls back to the full-buffer diff.
    private bool[]? _dirtyCells;
    private bool _hasDirtyRegions;

    private Style _currentStyle;
    private Hyperlink _currentHyperlink;
    private int _cursorRow;
    private int _cursorCol;
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
    /// Emit the byte sequence that brings the terminal to match <paramref name="back"/>.
    /// Stateful across calls — subsequent renders emit only the differences.
    /// </summary>
    public void Render(CellBuffer back, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(back);
        ArgumentNullException.ThrowIfNull(output);

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

            ScreenWriter.WriteClearScreen(output);
            SgrEncoder.WriteReset(output);

            CursorWriter.WriteMoveTo(output, 0, 0);
            _currentStyle = Style.Default;
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

        ComputeCoveredCells(back);
        ComputeDirtyCells(back);

        // Scroll detection — only meaningful on incremental renders, only safe when no
        // fragments are anchored (fragments shouldn't scroll with cell content). When the
        // back buffer is the front shifted up/down by K rows, emit SU/SD and shift _frontCells
        // in place so the subsequent EmitDiff only repaints the K newly-uncovered rows.
        if (!fullRedraw && back.FragmentsInternal.Any(o => o.Value.Fragment.Layer is FragmentLayer.Overlay) is false)
            TryDetectAndApplyScroll(back, output);

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
        _currentStyle = Style.Default;

        // Same reasoning for OSC 8 — leaving a hyperlink open at frame boundary would extend
        // the link target into the next prompt or any subsequent ad-hoc terminal output.
        if (!_currentHyperlink.IsEmpty)
        {
            HyperlinkWriter.WriteClose(output);
            _currentHyperlink = Hyperlink.None;
        }

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
    /// <see cref="CellBuffer.DirtyRegions"/>. When the back buffer has no dirty regions, this
    /// is a no-op and <see cref="_hasDirtyRegions"/> remains false — the cell loop falls back
    /// to a full-buffer diff (the safe default). When regions are present, only cells inside
    /// the union of those regions are eligible for emission.
    /// </summary>
    private void ComputeDirtyCells(CellBuffer back)
    {
        _hasDirtyRegions = back.DirtyRegions.Count > 0;
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

        // Drop the foreground. Keep just the background — that's what carries through.
        return new Cell(
            Grapheme: " ",
            Kind: CellKind.Single,
            Style: Style.Default.WithBackground(backCell.Style.Background));
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
                var backAdapted = Adapt(backSpan[c]);
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
        // to default so the cell diff sees back != front and repaints with whatever the
        // caller has there.
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
        _currentStyle = Style.Default;
        _currentHyperlink = Hyperlink.None;
        SgrEncoder.WriteReset(output);
    }

    // Re-position the cursor to (r, c) if our tracked position differs.
    private void SyncCursor(IBufferWriter<byte> output, int r, int c)
    {
        if (_cursorRow == r && _cursorCol == c) return;
        CursorWriter.WriteMoveTo(output, c, r);
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

    // Emit the SGR delta needed to move from the current style to <paramref name="target"/>.
    private void SyncStyle(IBufferWriter<byte> output, in Style target)
    {
        if (target == _currentStyle) return;
        SgrEncoder.WriteDelta(output, _currentStyle, target);
        _currentStyle = target;
    }

    private void EmitDiff(CellBuffer back, IBufferWriter<byte> output)
    {
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

                // Dirty-region opt-in: cells outside any marked region are skipped entirely.
                // The renderer trusts that the consumer is responsible for marking every
                // cell they've changed; cells outside the union of regions stay as the front
                // believed they were. This shortcut applies only when DirtyRegions is non-
                // empty — empty regions fall back to a full-buffer diff.
                if (_hasDirtyRegions && !_dirtyCells![frontIdx]) continue;

                // Compute the cell we actually want on the terminal for this position: under
                // a Cell-layer fragment, that's a bg-only space; everywhere else it's the back
                // cell verbatim. Then quantize for capability-aware emission. The same value
                // gets compared against the front and snapshotted into it, so a stable rendered
                // frame produces an empty delta.
                var intended = IntendedCellFor(c, r, row[c], back);
                var cell = Adapt(intended);

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

                if (cell == _frontCells![frontIdx]) continue;

                // Wide-glyph defense for terminals that don't reliably render two-cell glyphs:
                // pre-paint cells c and c+1 with the wide-left's style by emitting two spaces,
                // then CUP back to c so the wide glyph emits at the right column. On an
                // honoring terminal the wide glyph overpaints both spaces, and the cursor
                // advances by 2; on a non-honoring one, the wide glyph shrinks to a single
                // cell, but our pre-painted space at c+1 keeps the cell's background/style
                // intact. Either way we mark the cursor dirty afterward, so the next emit
                // issues an explicit CUP rather than trusting the actual advance count.
                bool wideDefense = cell.Kind == CellKind.WideLeft &&
                                   _capabilities?.TextSizing.WideGlyphs is false &&
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
                // same WideGlyphs capability as the wide-glyph defense.
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
                                        _capabilities?.TextSizing.WideGlyphs is false &&
                                        IsAmbiguousWidthGrapheme(cell.Grapheme);

                if (ambiguousDefense)
                {
                    // Paint the right neighbor with its own content first (so a narrow render
                    // keeps it), then the ambiguous glyph at c (so a wide render covers it).
                    var neighbor = Adapt(IntendedCellFor(c + 1, r, row[c + 1], back));

                    SyncCursor(output, r, c + 1);
                    SyncHyperlink(output, neighbor.Style.Hyperlink);
                    SyncStyle(output, neighbor.Style);
                    WriteGraphemeUtf8(output, neighbor);
                    _frontCells![frontIdx + 1] = neighbor;

                    SyncCursor(output, r, c);
                    SyncHyperlink(output, cell.Style.Hyperlink);
                    SyncStyle(output, cell.Style);
                    WriteGraphemeUtf8(output, cell);
                    _frontCells![frontIdx] = cell;

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

                    CursorWriter.WriteMoveTo(output, c, r);
                    _cursorRow = r;
                    _cursorCol = c;
                }

                WriteGraphemeUtf8(output, cell);
                _frontCells![frontIdx] = cell;

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

    private void EmitFragments(CellBuffer back, IBufferWriter<byte> output)
    {
        // Use OutputCapabilities.None when the renderer wasn't constructed with capabilities —
        // fragments that strictly require a feature will report unsupported and skip, which is
        // the right answer when we have no information.
        var caps = _capabilities ?? OutputCapabilities.None;

        // Pass 1 — erase any front fragment whose instance is no longer registered at the same
        // anchor in the back buffer. Identity comparison on <see cref="IBufferFragment"/> covers
        // three cases in one check:
        //   * removed entirely      — front had F at A, back has nothing at A.
        //   * replaced at anchor    — front had F1 at A, back has F2 at A (different instance).
        //   * moved to a different  — front had F at A, back has F at B (anchor empty at A).
        //     anchor
        // The cells pass above already repainted glyphs under removed Cell-layer fragments;
        // EmitErase is the only way to remove overlay-layer protocols (Kitty graphics, iTerm2
        // inline images) since they live on a separate display plane. Cell-layer's default
        // no-op EmitErase makes the iteration safe regardless of layer.
        foreach (var (anchor, frontEntry) in _frontFragments)
        {
            if (back.Fragments.TryGetValue(anchor, out var backEntry) &&
                ReferenceEquals(backEntry.Fragment, frontEntry.Fragment))
                continue;

            if (!frontEntry.Fragment.IsSupported(caps)) continue;
            EmitFragmentEraseBytes(anchor.Column, anchor.Row, frontEntry, output, caps);
        }

        // Pass 2 — emit new or changed fragments. The Pass-1 identity check already issued
        // erases for everything that needed one; this pass only writes the new payloads. The
        // diff skip uses <see cref="IBufferFragment.Key"/> + AnchorStyle so callers that
        // reconstruct fragments per frame can still participate via content-derived keys.
        foreach (var ((col, row), entry) in back.Fragments)
        {
            if (!entry.Fragment.IsSupported(caps)) continue;
            if (col < 0 || col >= back.Columns || row < 0 || row >= back.Rows) continue;

            if (_frontFragments.TryGetValue((col, row), out var frontEntry) &&
                Equals(frontEntry.Fragment.Key, entry.Fragment.Key) &&
                frontEntry.AnchorStyle == entry.AnchorStyle)
            {
                // Same key + same anchor style — terminal already shows the current payload.
                continue;
            }

            EmitFragmentBytes(col, row, entry, output, caps);
        }

        // Snapshot for next render's diff. Copy keys/values rather than aliasing back. Fragments
        // — if the caller mutates the buffer between renders, we still want the comparison to
        // be against what we last emitted.
        _frontFragments.Clear();
        foreach (var (key, entry) in back.Fragments)
            _frontFragments[key] = entry;
    }

    /// <summary>Bracket-emit a fragment's payload with DECSC / DECRC + cursor + SGR backdrop.</summary>
    private void EmitFragmentBytes(int col, int row, CellBuffer.FragmentEntry entry,
                                   IBufferWriter<byte> output, OutputCapabilities caps)
    {
        CursorWriter.WriteSavePosition(output);
        CursorWriter.WriteMoveTo(output, col, row);

        if (entry.AnchorStyle != Style.Default)
            SgrEncoder.WriteAbsolute(output, entry.AnchorStyle);

        entry.Fragment.Emit(col, row, output, caps);

        CursorWriter.WriteRestorePosition(output);

        // DECRC's SGR-restore behavior varies across terminals (xterm restores it; some VT
        // emulators don't). Explicitly resync our SGR tracking by writing an SGR reset.
        SgrEncoder.WriteReset(output);
        _currentStyle = Style.Default;

        // DECRC's *cursor*-restore behavior is also implementation-defined when the saved-state
        // stack has been disturbed in between (some conhost versions, ConEmu/Cmder, …).
        // Invalidate the tracked position so the next emission issues an explicit CUP rather
        // than trusting DECRC to have landed us where DECSC saved. Symptom of getting this
        // wrong: rows downstream of fragment emissions shift left by N (where N is roughly the
        // count of fragments emitted this frame), producing the "first few characters of each
        // labeled row are missing" wrap that conhost / WT exhibit.
        _cursorRow = -1;
        _cursorCol = -1;
    }

    /// <summary>
    /// Bracket-emit a fragment's erase sequence. Cell-layer fragments default to a no-op
    /// erase since cell repainting handles the visual removal; overlay-layer fragments emit
    /// protocol-specific delete sequences.
    /// </summary>
    private void EmitFragmentEraseBytes(int col, int row, CellBuffer.FragmentEntry entry,
                                        IBufferWriter<byte> output, OutputCapabilities caps)
    {
        CursorWriter.WriteSavePosition(output);
        CursorWriter.WriteMoveTo(output, col, row);

        entry.Fragment.EmitErase(col, row, output, caps);

        CursorWriter.WriteRestorePosition(output);

        SgrEncoder.WriteReset(output);
        _currentStyle = Style.Default;

        // See EmitFragmentBytes for why we invalidate the tracked cursor after DECRC: the
        // restore is implementation-defined when the SC/RC stack has been disturbed, and
        // trusting DECRC to land us at the pre-DECSC position produces silent drift across
        // frames on conhost / ConEmu.
        _cursorRow = -1;
        _cursorCol = -1;
    }

    private Cell Adapt(in Cell cell)
    {
        if (_quantizer is null) return cell;
        var quantized = _quantizer.Quantize(cell.Style);
        return quantized == cell.Style ? cell : cell with { Style = quantized };
    }

    private static void WriteGraphemeUtf8(IBufferWriter<byte> output, in Cell cell)
    {
        // Empty grapheme on a Single cell renders as a space; that paints the cell's background
        // and advances the cursor. WideLeft with empty grapheme is degenerate — emit two spaces
        // so the terminal still advances by 2.
        string grapheme = string.IsNullOrEmpty(cell.Grapheme)
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
        if (_firstFrame || back.CursorShape != _cursorShape)
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
            CursorWriter.WriteMoveTo(output, back.CursorColumn, back.CursorRow);
            _cursorRow = back.CursorRow;
            _cursorCol = back.CursorColumn;
        }
    }

    public void Close(IBufferWriter<byte> output)
    {
        var fragments = _frontFragments.ToList();

        _frontFragments.Clear();

        foreach (var f in fragments)
            f.Value.Fragment.EmitErase(f.Key.Column, f.Key.Row, output, _capabilities ?? OutputCapabilities.None);

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
public readonly record struct FrameRendererOptions(bool ForceFullRedraw = false);