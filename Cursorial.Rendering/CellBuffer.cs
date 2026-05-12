using Cursorial.Core.Output;
using Cursorial.Core.Text;

namespace Cursorial.Rendering;

/// <summary>
/// A 2D grid of <see cref="Cell"/> values plus cursor state — the back-buffer that a
/// <see cref="FrameRenderer"/> emits to a terminal. The buffer owns its storage and re-uses
/// it across frames; resizing reallocates and clears.
/// </summary>
/// <remarks>
/// <para>
/// Storage is a 1D <see cref="Cell"/> array indexed as <c>row * Columns + column</c>. This is
/// one allocation per resize, no per-cell heap pressure, and friendly to span-based access if
/// we expose row spans later.
/// </para>
/// <para>
/// <b>Wide cells.</b> <see cref="Set(int, int, string, in Style)"/> computes grapheme width via
/// <see cref="GraphemeWidth"/>, stores the cluster at <c>(row, col)</c> as
/// <see cref="CellKind.WideLeft"/> when its width is 2, and writes the right-half marker
/// (<see cref="Cell.WideContinuation"/>) into <c>(row, col + 1)</c>. Overwrites also clean up:
/// if the previous occupant of <c>(row, col)</c> was a wide-left, its dangling continuation is
/// reset to blank; if the previous occupant was a continuation, the wide-left to its left is
/// reset. The buffer therefore never exposes orphan continuations or partial wide cells.
/// </para>
/// <para>
/// <b>Cursor state.</b> <see cref="CursorRow"/>, <see cref="CursorColumn"/>,
/// <see cref="CursorVisible"/>, and <see cref="CursorShape"/> live alongside the cell grid and
/// are emitted by <see cref="FrameRenderer"/> as a separate concern from the cell content.
/// Don't try to encode the cursor as a cell-level attribute; the renderer needs explicit cursor
/// state to do its job efficiently.
/// </para>
/// </remarks>
public sealed class CellBuffer
{
    private Cell[] _cells;
    private int _columns;
    private int _rows;

    /// <summary>Construct a buffer of the given dimensions, initialized to blank cells.</summary>
    public CellBuffer(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];
    }

    /// <summary>Width of the buffer in cells.</summary>
    public int Columns => _columns;

    /// <summary>Height of the buffer in rows.</summary>
    public int Rows => _rows;

    /// <summary>Cursor row position (0-based). Used by the renderer at frame emission.</summary>
    public int CursorRow { get; set; }

    /// <summary>Cursor column position (0-based). Used by the renderer at frame emission.</summary>
    public int CursorColumn { get; set; }

    /// <summary>Whether the cursor should be visible after this frame is rendered.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Cursor shape applied at frame emission.</summary>
    public CursorShape CursorShape { get; set; } = CursorShape.Default;

    /// <summary>Total cell count (Columns × Rows). Useful for sized-array operations.</summary>
    public int CellCount => _cells.Length;

    /// <summary>
    /// Direct access to a cell. Setting via the indexer bypasses wide-cell consistency handling —
    /// use <see cref="Set(int, int, string, in Style)"/> for normal text content.
    /// </summary>
    public Cell this[int row, int column]
    {
        get
        {
            ValidateCoordinates(row, column);
            return _cells[row * _columns + column];
        }
        set
        {
            ValidateCoordinates(row, column);
            _cells[row * _columns + column] = value;
        }
    }

    /// <summary>
    /// Place <paramref name="grapheme"/> at <c>(row, column)</c> with the given <paramref name="style"/>,
    /// handling wide-cell width and adjacent-cell cleanup. Returns the number of cells the
    /// placement occupied (1 or 2).
    /// </summary>
    public int Set(int row, int column, string? grapheme, in Style style)
    {
        ValidateCoordinates(row, column);

        int width = string.IsNullOrEmpty(grapheme) ? 1 : GraphemeWidth.ClusterWidth(grapheme.AsSpan());
        if (width < 1) width = 1; // Defensive — a "wide" zero-width is meaningless here.
        if (width == 2 && column + 1 >= _columns)
        {
            // No room for the right half. Degrade to single-cell blank.
            width = 1;
            grapheme = null;
        }

        int index = row * _columns + column;
        var previous = _cells[index];

        // Cleanup: were we overwriting a wide-left's right half?
        if (previous.Kind == CellKind.WideContinuation && column > 0)
        {
            _cells[index - 1] = Cell.Blank;
        }
        // Cleanup: was the previous occupant of (row, col) a wide-left whose continuation we now orphan?
        if (previous.Kind == CellKind.WideLeft && column + 1 < _columns)
        {
            _cells[index + 1] = Cell.Blank;
        }

        if (width == 2)
        {
            _cells[index] = new Cell(grapheme, CellKind.WideLeft, style);
            // The right-half continuation carries the style too so background paints continuously
            // across the wide glyph.
            _cells[index + 1] = Cell.WideContinuation with { Style = style };
            return 2;
        }

        _cells[index] = new Cell(grapheme, CellKind.Single, style);
        return 1;
    }

    /// <summary>Reset every cell to <see cref="Cell.Blank"/>.</summary>
    public void Clear() => Array.Clear(_cells);

    /// <summary>Fill the entire buffer with <paramref name="cell"/>.</summary>
    public void Fill(in Cell cell) => Array.Fill(_cells, cell);

    /// <summary>
    /// Resize the buffer to <paramref name="columns"/> × <paramref name="rows"/>. Contents are
    /// discarded; the new buffer is initialized to blank cells. Cursor state is preserved.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        if (columns == _columns && rows == _rows)
        {
            Clear();
            return;
        }
        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];
    }

    /// <summary>Internal: raw row span for renderer access. Not part of the public-API stability guarantee.</summary>
    internal ReadOnlySpan<Cell> GetRowSpan(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows);
        return _cells.AsSpan(row * _columns, _columns);
    }

    private void ValidateCoordinates(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, _columns);
    }
}
