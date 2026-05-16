using Cursorial.Output;
using Cursorial.Rendering.Fragments;
using Cursorial.Terminal;
using Cursorial.Text;

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
    private readonly Stack<IBlendingMode> _blendStack = new();
    private readonly Dictionary<(int Row, int Column), FragmentEntry> _fragments = new();

    /// <summary>Construct a buffer of the given dimensions, initialized to blank cells.</summary>
    public CellBuffer(int columns, int rows, TerminalCapabilities? capabilities = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];
        Capabilities = capabilities;
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
    /// Represents the terminal capabilities associated with the cell buffer,
    /// including information about the terminal's identification, input capabilities,
    /// and output capabilities. This property can be used to determine the features
    /// supported by the terminal.
    /// </summary>
    public TerminalCapabilities? Capabilities { get; }
    
    /// <summary>
    /// The blending mode applied to each <see cref="Set"/> and <see cref="Fill"/> call. The top
    /// of the buffer's blend stack, or <see cref="BlendingModes.Default"/> when the stack is
    /// empty. <see cref="Clear"/> and the raw indexer setter do NOT consult this — they assign
    /// cells verbatim.
    /// </summary>
    public IBlendingMode CurrentBlendingMode =>
        _blendStack.Count > 0 ? _blendStack.Peek() : BlendingModes.Default;

    /// <summary>Push a blending mode onto the stack; subsequent <see cref="Set"/> / <see cref="Fill"/> calls use it.</summary>
    public void PushBlendingMode(IBlendingMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _blendStack.Push(mode);
    }

    /// <summary>
    /// Pop the most recently pushed blending mode. Throws <see cref="InvalidOperationException"/>
    /// if the stack is empty — pop/push pairing is the caller's responsibility.
    /// </summary>
    public IBlendingMode PopBlendingMode()
    {
        if (_blendStack.Count == 0)
            throw new InvalidOperationException("Blending-mode stack is empty; nothing to pop.");

        return _blendStack.Pop();
    }

    /// <summary>
    /// Direct access to a cell. Setting via the indexer bypasses wide-cell consistency handling
    /// AND the active blending mode — use <see cref="Set(int, int, string, in Style)"/> for
    /// normal text content.
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

        // Apply the active blending mode against the cell being overwritten. SourceOver / empty
        // stack is a no-op (the mode just returns source), but every other mode tints / darkens /
        // lightens based on the existing color at this position.
        var blended = BlendStyle(style, previous.Style);

        // Cleanup: were we overwriting a wide-left's right half?
        if (previous.Kind == CellKind.WideContinuation && column > 0)
            _cells[index - 1] = Cell.Blank;

        // Cleanup: was the previous occupant of (row, col) a wide-left whose continuation we now orphan?
        if (previous.Kind == CellKind.WideLeft && column + 1 < _columns)
            _cells[index + 1] = Cell.Blank;

        if (width == 2)
        {
            _cells[index] = new Cell(grapheme, CellKind.WideLeft, blended);
            // The right-half continuation carries the style too so background paints continuously
            // across the wide glyph.
            _cells[index + 1] = Cell.WideContinuation with { Style = blended };
            return 2;
        }

        _cells[index] = new Cell(grapheme, CellKind.Single, blended);
        return 1;
    }

    /// <summary>
    /// Reset every cell to <see cref="Cell.Blank"/>. Does NOT apply the active blending mode —
    /// clear is an explicit reset. Also removes every registered fragment.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_cells);
        _fragments.Clear();
    }

    // ---- Fragment sidecar -----------------------------------------------------------------

    /// <summary>
    /// All fragments currently registered against the buffer, keyed by anchor cell. The renderer
    /// iterates this collection after the regular cell-grid pass and emits each fragment's
    /// protocol bytes at its anchor. Order is iteration order of the underlying dictionary —
    /// fragments must not depend on each other's visual ordering at the cell layer.
    /// </summary>
    public IReadOnlyDictionary<(int Row, int Column), FragmentEntry> Fragments => _fragments;

    /// <summary>
    /// Register <paramref name="fragment"/> at the anchor cell <c>(row, column)</c>. Pure
    /// metadata registration — the cell grid is <b>not</b> modified, so anything the caller
    /// previously painted under the fragment's footprint continues to render in the cell pass
    /// and shows through wherever the fragment's protocol payload doesn't draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="anchorStyle"/> is the style the renderer applies as the SGR backdrop
    /// when it positions the cursor at the anchor — useful when the fragment needs an SGR
    /// state to inherit. Fragments are free to emit their own SGR inside <c>Emit</c>; this
    /// style only governs the entry state.
    /// </para>
    /// <para>
    /// If a fragment is already registered at this anchor, it is replaced. Removing a
    /// fragment leaves the cells under it untouched — whatever was painted before the
    /// fragment was added remains. Callers who want a clean state when removing a fragment
    /// should explicitly repaint the region afterwards.
    /// </para>
    /// </remarks>
    public void AddFragment(int row, int column, IBufferFragment fragment, in Style anchorStyle = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ValidateCoordinates(row, column);

        _fragments[(row, column)] = new FragmentEntry(fragment, anchorStyle);
    }

    /// <summary>
    /// Remove the fragment anchored at <c>(row, column)</c>. Returns true when a fragment was
    /// removed. Cells under the removed fragment retain whatever they held before — see
    /// <see cref="AddFragment"/> for the layering contract.
    /// </summary>
    public bool RemoveFragment(int row, int column)
    {
        ValidateCoordinates(row, column);
        return _fragments.Remove((row, column));
    }

    /// <summary>
    /// Replace every cell with <paramref name="cell"/>, blending its <see cref="Style"/> against
    /// each position's existing cell through <see cref="CurrentBlendingMode"/>. When the active
    /// mode is <see cref="BlendingModes.Default"/>, the fast path is a single <c>Array.Fill</c>
    /// — every cell becomes <paramref name="cell"/> verbatim.
    /// </summary>
    public void Fill(in Cell cell)
    {
        var mode = CurrentBlendingMode;

        if (ReferenceEquals(mode, BlendingModes.Default))
        {
            Array.Fill(_cells, cell);
            return;
        }

        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = cell with { Style = BlendStyle(cell.Style, _cells[i].Style, mode) };
    }

    private Style BlendStyle(in Style source, in Style backdrop)
        => source.BlendOver(backdrop, CurrentBlendingMode);

    private static Style BlendStyle(in Style source, in Style backdrop, IBlendingMode mode)
    {
        return source with
               {
                   Foreground = Color.Composite(source.Foreground, backdrop.Background, mode),
                   Background = source.Background != Color.Default ? Color.Composite(source.Background, backdrop.Background, mode) : backdrop.Background,
                   UnderlineColor = Color.Composite(source.UnderlineColor, backdrop.UnderlineColor, mode),
               };
    }

    /// <summary>
    /// Resize the buffer to <paramref name="columns"/> × <paramref name="rows"/>. Contents are
    /// discarded; the new buffer is initialized to blank cells. The cursor state is preserved.
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
        _fragments.Clear();
    }

    /// <summary>
    /// A fragment registered against a <see cref="CellBuffer"/>: the fragment itself and the
    /// anchor style the renderer uses as the SGR backdrop before invoking the fragment's emit
    /// callback.
    /// </summary>
    public readonly record struct FragmentEntry(IBufferFragment Fragment, Style AnchorStyle);

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