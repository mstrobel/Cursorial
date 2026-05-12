using System.Buffers;
using System.Text;
using Cursorial.Core.Output;

namespace Cursorial.Rendering;

/// <summary>
/// Stateful diff renderer. Holds the previously-emitted frame plus the SGR / cursor state
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
/// <see cref="FrameRendererOptions.ForceFullRedraw"/> is set on the renderer. Otherwise the
/// renderer diffs cell-by-cell.
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

    private Cell[]? _frontCells;
    private int _frontCols;
    private int _frontRows;

    private Style _currentStyle;
    private int _cursorRow;
    private int _cursorCol;
    private bool _firstFrame = true;
    private bool _cursorVisible = true;
    private CursorShape _cursorShape = CursorShape.Default;

    public FrameRenderer()
        : this(default)
    {
    }

    public FrameRenderer(FrameRendererOptions options)
    {
        _options = options;
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

        bool fullRedraw = _firstFrame
            || _frontCols != back.Columns
            || _frontRows != back.Rows
            || _options.ForceFullRedraw;

        if (fullRedraw)
        {
            ScreenWriter.WriteClearScreen(output);
            SgrEncoder.WriteReset(output);
            CursorWriter.WriteMoveTo(output, 0, 0);
            _currentStyle = Style.Default;
            _cursorRow = 0;
            _cursorCol = 0;
            _frontCols = back.Columns;
            _frontRows = back.Rows;
            _frontCells = new Cell[_frontCols * _frontRows];
        }

        EmitDiff(back, output);

        // Snapshot the back buffer's cell content for next frame's comparison.
        for (int r = 0; r < back.Rows; r++)
        {
            back.GetRowSpan(r).CopyTo(_frontCells.AsSpan(r * _frontCols, _frontCols));
        }

        EmitCursor(back, output);
        _firstFrame = false;
    }

    private void EmitDiff(CellBuffer back, IBufferWriter<byte> output)
    {
        for (int r = 0; r < back.Rows; r++)
        {
            ReadOnlySpan<Cell> row = back.GetRowSpan(r);
            for (int c = 0; c < back.Columns; c++)
            {
                var cell = row[c];

                // Wide-continuation cells aren't directly rendered — they're the right half of
                // the WideLeft we already emitted (or will, when we visit it).
                if (cell.Kind == CellKind.WideContinuation) continue;

                int frontIdx = r * _frontCols + c;
                if (cell == _frontCells![frontIdx]) continue;

                // Re-position cursor if our tracked position isn't (r, c). After writing a cell
                // at the right edge, the cursor's "next" position would equal Columns — we mark
                // ourselves as out-of-position so the next emit triggers an explicit move.
                if (_cursorRow != r || _cursorCol != c)
                {
                    CursorWriter.WriteMoveTo(output, r, c);
                    _cursorRow = r;
                    _cursorCol = c;
                }

                if (cell.Style != _currentStyle)
                {
                    SgrEncoder.WriteDelta(output, _currentStyle, cell.Style);
                    _currentStyle = cell.Style;
                }

                WriteGraphemeUtf8(output, cell);

                _cursorCol += cell.Width;
                // If the next cursor position would be at or past the right edge, force a
                // re-position before the next emit so terminal autowrap can't surprise us.
                if (_cursorCol >= back.Columns)
                {
                    _cursorCol = -1;
                }
            }
        }
    }

    private static void WriteGraphemeUtf8(IBufferWriter<byte> output, in Cell cell)
    {
        // Empty grapheme on a Single cell renders as a space; that paints the cell's background
        // and advances the cursor. WideLeft with empty grapheme is degenerate — emit two spaces
        // so the terminal still advances by 2.
        string grapheme = cell.Grapheme is null || cell.Grapheme.Length == 0
            ? (cell.Kind == CellKind.WideLeft ? "  " : " ")
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
