using System.Buffers;
using System.Text;

using Cursorial.Output;
using Cursorial.Output.Capabilities;

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
        }

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

        _firstFrame = false;
    }

    private void EmitDiff(CellBuffer back, IBufferWriter<byte> output)
    {
        for (int r = 0; r < back.Rows; r++)
        {
            ReadOnlySpan<Cell> row = back.GetRowSpan(r);

            for (int c = 0; c < back.Columns; c++)
            {
                // Quantize per cell when a StyleQuantizer is attached. The quantized form is
                // what we emit, what we compare against the front buffer, and what we snapshot
                // for the next frame — all three must agree so a stable rendered frame produces an
                // empty delta.
                var cell = Adapt(row[c]);
                int frontIdx = r * _frontCols + c;

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
                        HyperlinkWriter.WriteOpen(output, cell.Style.Hyperlink.Uri.AsSpan(),
                                                  cell.Style.Hyperlink.Id.AsSpan());
                    _currentHyperlink = cell.Style.Hyperlink;
                }

                if (cell.Style != _currentStyle)
                {
                    SgrEncoder.WriteDelta(output, _currentStyle, cell.Style);
                    _currentStyle = cell.Style;
                }

                // Wide-glyph defense for terminals that don't reliably render two-cell glyphs:
                // pre-paint cells c and c+1 with the wide-left's style by emitting two spaces,
                // then CUP back to c so the wide glyph emits at the right column. On a
                // honoring terminal the wide glyph overpaints both spaces and the cursor
                // advances by 2; on a non-honoring one, the wide glyph shrinks to a single
                // cell but our pre-painted space at c+1 keeps the cell's background/style
                // intact. Either way we mark the cursor dirty afterward so the next emit
                // issues an explicit CUP rather than trusting the actual advance count.
                bool wideDefense = cell.Kind == CellKind.WideLeft &&
                                   _capabilities is not null &&
                                   !_capabilities.TextSizing.WideGlyphs &&
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
        if (back.Fragments.Count == 0) return;

        // Use OutputCapabilities.None when the renderer wasn't constructed with capabilities —
        // fragments that strictly require a feature will report unsupported and skip, which is
        // the right answer when we have no information.
        var caps = _capabilities ?? OutputCapabilities.None;

        foreach (var ((row, col), entry) in back.Fragments)
        {
            if (!entry.Fragment.IsSupported(caps)) continue;
            if (row < 0 || row >= back.Rows || col < 0 || col >= back.Columns) continue;

            // Bracket every fragment with DECSC / DECRC. DECSC saves both cursor position and
            // the SGR state; DECRC restores both. Our _currentStyle and _cursorRow/_cursorCol
            // tracking remain valid across the fragment, since the cursor is brought back to
            // exactly where it was. Position the cursor at the anchor and apply the
            // fragment's anchor style as the SGR backdrop before invoking emit — that gives
            // fragments a defined SGR state to inherit if they want, even though they're free
            // to emit their own SGR over it.
            CursorWriter.WriteSavePosition(output);
            CursorWriter.WriteMoveTo(output, row, col);

            if (entry.AnchorStyle != Style.Default)
                SgrEncoder.WriteAbsolute(output, entry.AnchorStyle);

            entry.Fragment.Emit(row, col, output, caps);

            CursorWriter.WriteRestorePosition(output);

            // DECRC's SGR-restore behavior varies across terminals (xterm restores it, some VT
            // emulators don't). Explicitly resync our SGR tracking by writing an SGR reset; the
            // next cell that needs styling will pay the re-establishment cost. This also keeps
            // the post-fragment SGR state predictable for any subsequent fragments.
            SgrEncoder.WriteReset(output);
            _currentStyle = Style.Default;
        }
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