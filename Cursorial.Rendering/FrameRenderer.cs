using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;

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
    private readonly Dictionary<(int Row, int Column), CellBuffer.FragmentEntry> _frontFragments = new();

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

        bool fullRedraw = _firstFrame ||
                          _frontCols != back.Columns ||
                          _frontRows != back.Rows ||
                          _options.ForceFullRedraw;

        if (fullRedraw)
        {
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

            // Full redraw nukes any prior fragment snapshot — none of those fragments can
            // possibly survive on the cleared screen, so the next fragment-emit pass treats
            // every registered fragment as new.
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
        if (!fullRedraw && back.Fragments.Count == 0)
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

        foreach (var ((anchorRow, anchorCol), entry) in back.Fragments)
        {
            if (entry.Fragment.Layer != FragmentLayer.Cells) continue;
            if (!entry.Fragment.IsSupported(caps)) continue;

            var size = entry.Fragment.GetSize();
            int rowEnd = Math.Min(back.Rows, anchorRow + Math.Max(1, size.Rows));
            int colEnd = Math.Min(back.Columns, anchorCol + Math.Max(1, size.Columns));

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
    private Cell IntendedCellFor(int row, int column, Cell backCell, CellBuffer back)
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
        // Also reset our tracked SGR / hyperlink because SU/SD don't carry SGR state in a
        // well-defined way.
        _cursorCol = -1;
        _cursorRow = -1;
        _currentStyle = Style.Default;
        _currentHyperlink = Hyperlink.None;
        SgrEncoder.WriteReset(output);
    }

    private void EmitDiff(CellBuffer back, IBufferWriter<byte> output)
    {
        for (int r = 0; r < back.Rows; r++)
        {
            ReadOnlySpan<Cell> row = back.GetRowSpan(r);

            for (int c = 0; c < back.Columns; c++)
            {
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
                var intended = IntendedCellFor(r, c, row[c], back);
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

                // Re-position the cursor if our tracked position isn't (r, c). After writing a cell
                // at the right edge, the cursor's "next" position would equal Columns — we mark
                // ourselves as out-of-position so the next emit triggers an explicit move.
                if (_cursorRow != r || _cursorCol != c)
                {
                    CursorWriter.WriteMoveTo(output, r, c);
                    _cursorRow = r;
                    _cursorCol = c;
                }

                // Hyperlink state is a separate OSC 8 channel — emit close then open at
                // boundaries, independent of the SGR delta. The hyperlink is part of Style, so
                // the inequality check above already covers the case where only the link
                // changed (in which case SgrEncoder.WriteDelta below produces no bytes).
                if (cell.Style.Hyperlink != _currentHyperlink)
                {
                    if (!_currentHyperlink.IsEmpty)
                        HyperlinkWriter.WriteClose(output);
                    if (!cell.Style.Hyperlink.IsEmpty)
                        HyperlinkWriter.WriteOpen(output, cell.Style.Hyperlink.Uri.AsSpan(), cell.Style.Hyperlink.Id.AsSpan());
                    _currentHyperlink = cell.Style.Hyperlink;
                }

                if (cell.Style != _currentStyle)
                {
                    SgrEncoder.WriteDelta(output, _currentStyle, cell.Style);
                    _currentStyle = cell.Style;
                }

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

                if (wideDefense)
                {
                    var twoSpaces = output.GetSpan(2);
                    twoSpaces[0] = (byte) ' ';
                    twoSpaces[1] = (byte) ' ';
                    output.Advance(2);

                    CursorWriter.WriteMoveTo(output, r, c);
                    _cursorRow = r;
                    _cursorCol = c;
                }

                WriteGraphemeUtf8(output, cell);
                _frontCells[frontIdx] = cell;

                if (wideDefense)
                {
                    // We don't know if the terminal actually advanced 1 or 2 cells after the
                    // wide-glyph emission, so force CUP before the next emit instead of
                    // trusting cell.Width.
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

        // Pass 1 — erase fragments that were in the front but aren't in the back. The cells
        // pass above already repainted cells under removed Cell-layer fragments (front cells
        // there held the bg-only-space form; back cells now want the real glyphs, so the diff
        // fired). Overlay-layer fragments need their EmitErase to actually disappear from the
        // terminal's overlay plane.
        foreach (var (anchor, frontEntry) in _frontFragments)
        {
            if (back.Fragments.ContainsKey(anchor)) continue;
            if (!frontEntry.Fragment.IsSupported(caps)) continue;
            EmitFragmentEraseBytes(anchor.Row, anchor.Column, frontEntry, output, caps);
        }

        // Pass 2 — emit new or changed fragments. Reference equality on the IBufferFragment
        // instance + value equality on the AnchorStyle is the diff key; callers that want
        // stable skipping reuse instances across frames.
        foreach (var ((row, col), entry) in back.Fragments)
        {
            if (!entry.Fragment.IsSupported(caps)) continue;
            if (row < 0 || row >= back.Rows || col < 0 || col >= back.Columns) continue;

            if (_frontFragments.TryGetValue((row, col), out var frontEntry) &&
                ReferenceEquals(frontEntry.Fragment, entry.Fragment) &&
                frontEntry.AnchorStyle == entry.AnchorStyle)
            {
                // Same fragment instance with the same anchor style — terminal already shows
                // its current payload, nothing to emit.
                continue;
            }

            // If a different fragment occupied this anchor on the previous render, erase it
            // before painting the new one. Cell-layer EmitErase is a no-op; overlay-layer
            // implementations send the protocol's delete command.
            if (frontEntry.Fragment is not null &&
                !ReferenceEquals(frontEntry.Fragment, entry.Fragment) &&
                frontEntry.Fragment.IsSupported(caps))
            {
                EmitFragmentEraseBytes(row, col, frontEntry, output, caps);
            }

            EmitFragmentBytes(row, col, entry, output, caps);
        }

        // Snapshot for next render's diff. Copy keys/values rather than aliasing back. Fragments
        // — if the caller mutates the buffer between renders, we still want the comparison to
        // be against what we last emitted.
        _frontFragments.Clear();
        foreach (var (key, entry) in back.Fragments)
            _frontFragments[key] = entry;
    }

    /// <summary>Bracket-emit a fragment's payload with DECSC / DECRC + cursor + SGR backdrop.</summary>
    private void EmitFragmentBytes(int row, int col, CellBuffer.FragmentEntry entry,
                                   IBufferWriter<byte> output, OutputCapabilities caps)
    {
        CursorWriter.WriteSavePosition(output);
        CursorWriter.WriteMoveTo(output, row, col);

        if (entry.AnchorStyle != Style.Default)
            SgrEncoder.WriteAbsolute(output, entry.AnchorStyle);

        entry.Fragment.Emit(row, col, output, caps);

        CursorWriter.WriteRestorePosition(output);

        // DECRC's SGR-restore behavior varies across terminals (xterm restores it; some VT
        // emulators don't). Explicitly resync our SGR tracking by writing an SGR reset.
        SgrEncoder.WriteReset(output);
        _currentStyle = Style.Default;
    }

    /// <summary>
    /// Bracket-emit a fragment's erase sequence. Cell-layer fragments default to a no-op
    /// erase since cell repainting handles the visual removal; overlay-layer fragments emit
    /// protocol-specific delete sequences.
    /// </summary>
    private void EmitFragmentEraseBytes(int row, int col, CellBuffer.FragmentEntry entry,
                                        IBufferWriter<byte> output, OutputCapabilities caps)
    {
        CursorWriter.WriteSavePosition(output);
        CursorWriter.WriteMoveTo(output, row, col);

        entry.Fragment.EmitErase(row, col, output, caps);

        CursorWriter.WriteRestorePosition(output);

        SgrEncoder.WriteReset(output);
        _currentStyle = Style.Default;
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
            CursorWriter.WriteMoveTo(output, back.CursorRow, back.CursorColumn);
            _cursorRow = back.CursorRow;
            _cursorCol = back.CursorColumn;
        }
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