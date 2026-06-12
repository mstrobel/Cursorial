using Cursorial.Input;
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
public sealed class CellBuffer : ICellSurface
{
    private Cell[] _cells;
    private int _columns;
    private int _rows;

    private readonly Style _defaultStyle;
    private readonly Stack<IBlendingMode> _blendStack = new();
    private readonly Dictionary<(int Column, int Row), FragmentEntry> _fragments = new();

    // Secondary index — maps each registered fragment's Key to the anchor it lives at, so
    // ContainsFragment / TryGetFragmentAnchor are O(1) instead of scanning. Last-write-wins
    // when two fragments share a Key at different anchors (an unusual case but legal — Key is
    // implementation-defined, and two anchors holding the same logical fragment is a valid
    // shape for repeating content).
    private readonly Dictionary<object, (int Column, int Row)> _fragmentsByKey = new();
    private readonly List<Rect> _dirtyRegions = [];

    /// <summary>Construct a buffer of the given dimensions, initialized to blank cells.</summary>
    public CellBuffer(int columns, int rows, TerminalCapabilities? capabilities = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];

        Capabilities = capabilities;

        if (capabilities is { Output.Color: { DefaultForeground: var fg, DefaultBackground: var bg } } &&
            (fg is { Kind: ColorKind.Rgb } || bg is { Kind: ColorKind.Rgb }))
        {
            // If the default foreground or background color is known, use the actual color in RGB
            // form so we can take advantage of alpha blending.
            _defaultStyle = Style.Default with
                            {
                                Foreground = fg ?? Color.Default,
                                Background = bg ?? Color.Default
                            };
            Clear();
        }
    }

    /// <summary>Width of the buffer in cells.</summary>
    public int Columns => _columns;

    /// <summary>Height of the buffer in rows.</summary>
    public int Rows => _rows;

    /// <summary>The buffer's dimensions, in cells.</summary>   
    public (int Columns, int Rows) Dimensions => (_columns, _rows);

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
    /// The blending mode applied to each <see cref="Set"/> and <see cref="Fill(in Cell)"/> call. The top
    /// of the buffer's blend stack, or <see cref="BlendingModes.Default"/> when the stack is
    /// empty. <see cref="Clear()"/> and the raw indexer setter do NOT consult this — they assign
    /// cells verbatim.
    /// </summary>
    public IBlendingMode CurrentBlendingMode =>
        _blendStack.Count > 0 ? _blendStack.Peek() : BlendingModes.Default;

    /// <summary>
    /// Push a blending mode onto the stack; subsequent <see cref="Set"/> / <see cref="Fill(in Cell)"/>
    /// calls use it.
    /// </summary>
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
    public Cell this[int column, int row]
    {
        get
        {
            ValidateCoordinates(column, row);
            return _cells[row * _columns + column];
        }
        set
        {
            ValidateCoordinates(column, row);
            _cells[row * _columns + column] = value;
        }
    }

    /// <summary>
    /// Place <paramref name="grapheme"/> at <c>(column, row)</c> with the given <paramref name="style"/>,
    /// handling wide-cell width and adjacent-cell cleanup. Returns the number of cells the
    /// placement occupied (1 or 2).
    /// </summary>
    public int Set(int column, int row, string? grapheme, in Style style)
    {
        ValidateCoordinates(column, row);

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

        if (string.IsNullOrWhiteSpace(grapheme) && !previous.Grapheme.IsWhiteSpace() && style.Background.IsOpaque is false)
        {
            var foregroundUnderneath = Color.Composite(style.Background, previous.Style.Foreground, CurrentBlendingMode);
            grapheme = previous.Grapheme;
            blended = blended with { Foreground = foregroundUnderneath };
        }

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
    /// Write the grapheme clusters of <paramref name="text"/> across a single row starting at
    /// <c>(column, row)</c>, advancing the column by each cluster's width (1 for normal, 2 for
    /// wide) and applying the active blending mode per cell — exactly as
    /// <see cref="Set(int, int, string?, in Style)"/> does for a single cluster. A cluster that
    /// would not fit in the remaining columns stops the write rather than being clipped to a
    /// partial glyph. The write is single-row by contract: it <b>stops at the first C0/C1 control
    /// character</b> (including newlines and tabs) rather than storing it as a junk cell — split
    /// text into lines (or use the drawing layer's multi-line text) for multi-row layout, and
    /// expand tabs upstream. Returns the number of columns written.
    /// </summary>
    public int Write(int column, int row, ReadOnlySpan<char> text, in Style style)
    {
        ValidateCoordinates(column, row);
        if (text.IsEmpty) return 0;

        int start = column;
        var clusters = text.GetGraphemeEnumerator();

        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;

            // Stop at the first control character — this is a single-row, printable-text write.
            if (IsC0OrC1Control(cluster[0])) break;

            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;

            // Stop at the right edge rather than placing a degraded glyph.
            if (column + width > _columns) break;

            column += Set(column, row, cluster.ToString(), style);
        }

        return column - start;
    }

    // C0 (U+0000–U+001F), DEL (U+007F), and C1 (U+0080–U+009F). Controls are grapheme-cluster
    // boundaries on both sides (UAX #29), so checking a cluster's first char classifies the cluster.
    internal static bool IsC0OrC1Control(char c) => c < 0x20 || (c >= 0x7F && c <= 0x9F);

    /// <summary>
    /// Reset every cell to <see cref="Cell.Blank"/>. Does NOT apply the active blending mode —
    /// clear is an explicit reset. Also removes every registered fragment.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_cells);

        _fragments.Clear();
        _fragmentsByKey.Clear();
        _dirtyRegions.Clear();

        FillWithDefaultStyleIfKnown();
    }

    // ---- Fragment sidecar -----------------------------------------------------------------

    /// <summary>
    /// All fragments currently registered against the buffer, keyed by anchor cell. The renderer
    /// iterates this collection after the regular cell-grid pass and emits each fragment's
    /// protocol bytes at its anchor. Order is iteration order of the underlying dictionary —
    /// fragments must not depend on each other's visual ordering at the cell layer.
    /// </summary>
    public FragmentDictionary Fragments => new(_fragments, 0, 0);

    internal Dictionary<(int Column, int Row), FragmentEntry> FragmentsInternal => _fragments;

    /// <summary>
    /// Register <paramref name="fragment"/> at the anchor cell <c>(column, row)</c>. Pure
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
    /// should explicitly repaint the region afterward.
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> when the fragment was registered. The buffer always
    /// registers within its bounds (an out-of-range anchor throws); the <see langword="bool"/>
    /// return exists for parity with <see cref="CellBufferView.AddFragment"/>, which returns
    /// <see langword="false"/> when the anchor falls outside the view.
    /// </para>
    /// </remarks>
    public bool AddFragment(int column, int row, IBufferFragment fragment, in Style anchorStyle = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ValidateCoordinates(column, row);

        var anchor = (column, row);

        // If something was already registered at this anchor, drop its Key from the secondary
        // index — otherwise that stale key would still resolve to this anchor after we replace
        // the entry below.
        if (_fragments.TryGetValue(anchor, out var existing))
            _fragmentsByKey.Remove(existing.Fragment.Key);

        _fragments[anchor] = new FragmentEntry(fragment, anchorStyle);
        _fragmentsByKey[fragment.Key] = anchor;
        return true;
    }

    /// <summary>
    /// Remove the fragment anchored at <c>(column, row)</c>. Returns true when a fragment was
    /// removed. Cells under the removed fragment retain whatever they held before — see
    /// <see cref="AddFragment"/> for the layering contract.
    /// </summary>
    public bool RemoveFragment(int column, int row)
    {
        ValidateCoordinates(column, row);

        if (!_fragments.TryGetValue((column, row), out var existing)) return false;

        _fragments.Remove((column, row));

        // Only drop the Key from the secondary index when it actually points at this anchor.
        // A stale Key entry (from a fragment that moved or was replaced) would otherwise be
        // erased here, breaking lookups for whatever's currently registered under the Key.
        if (_fragmentsByKey.TryGetValue(existing.Fragment.Key, out var indexedAnchor) &&
            indexedAnchor == (column, row))
        {
            _fragmentsByKey.Remove(existing.Fragment.Key);
        }

        var fragmentSize = existing.Fragment.GetSize();

        MarkDirty(column, row, fragmentSize.Columns, fragmentSize.Rows);

        return true;
    }

    /// <summary>
    /// Remove the fragment anchored at the specified position. Returns true when a fragment was
    /// removed. Cells under the removed fragment retain whatever they held before — see
    /// <see cref="AddFragment"/> for the layering contract.
    /// </summary>
    public bool RemoveFragment(CellPosition position) => RemoveFragment(position.Column, position.Row);

    /// <summary>
    /// True when a fragment with the given <paramref name="key"/> is currently registered on
    /// the buffer. Useful for "is this image already on screen?" checks without scanning the
    /// fragment dictionary. Comparison uses <see cref="object.Equals(object)"/>, so value-type
    /// keys (records, tuples, <see cref="uint"/>, …) compare by value.
    /// </summary>
    public bool ContainsFragment(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _fragmentsByKey.ContainsKey(key);
    }

    /// <summary>
    /// Look up the anchor of the fragment registered under <paramref name="key"/>. Returns
    /// <see langword="true"/> with the anchor when one is registered, <see langword="false"/>
    /// otherwise. Combine with the <see cref="Fragments"/> dictionary to fetch the full entry
    /// (<c>Fragments[anchor]</c>).
    /// </summary>
    public bool TryGetFragmentAnchor(object key, out (int Column, int Row) anchor)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _fragmentsByKey.TryGetValue(key, out anchor);
    }

    // ---- Dirty-region tracking ------------------------------------------------------------

    /// <summary>
    /// Rectangles the caller has marked as needing inspection in the next render. The renderer
    /// short-circuits to only the union of these regions when the list is non-empty —
    /// everything outside is assumed unchanged. An empty list lets the renderer fall back to
    /// its default full-buffer diff (safe for callers that don't bother tracking dirtiness).
    /// </summary>
    /// <remarks>
    /// Marking is an <em>optimization hint</em>: cells inside marked regions still benefit from
    /// the renderer's back-vs-front comparison (no emit when unchanged); cells outside any
    /// marked region are skipped entirely (no comparison, no emit). Consumers using dirty
    /// regions accept the responsibility of marking every position they've actually changed —
    /// missing a change leaves the terminal showing stale content for that cell.
    /// </remarks>
    public IReadOnlyList<Rect> DirtyRegions => _dirtyRegions;

    /// <summary>
    /// Mark a rectangular region as needing the renderer's attention on the next render. Empty
    /// rectangles (zero width or height) are dropped silently — callers can call <c>MarkDirty</c>
    /// with computed rectangles without pre-checking the result.
    /// </summary>
    public void MarkDirty(int column, int row, int columns, int rows)
        => MarkDirty(new Rect(column, row, columns, rows));

    /// <summary>Mark a <see cref="Rect"/> as needing the renderer's attention on the next render.</summary>
    public void MarkDirty(in Rect region)
    {
        if (region.IsEmpty) return;
        if (region.Row >= _rows || region.Column >= _columns) return;
        if (region.RowEnd <= 0 || region.ColumnEnd <= 0) return;

        // Clamp to buffer bounds — out-of-range marks waste renderer work and confuse the
        // bitmask computation.
        int col = Math.Max(0, region.Column);
        int row = Math.Max(0, region.Row);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        _dirtyRegions.Add(new Rect(col, row, colEnd - col, rowEnd - row));
    }

    /// <summary>
    /// Drop all dirty-region marks. The <see cref="FrameRenderer"/> calls this automatically at
    /// the end of each render so consumers don't have to manage the lifecycle themselves;
    /// callers performing manual emission can invoke it explicitly when needed.
    /// </summary>
    public void ClearDirty() => _dirtyRegions.Clear();

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

    /// <summary>
    /// Replace every cell inside <paramref name="region"/> with <paramref name="cell"/>. Under the
    /// default blending mode the cell is written verbatim (a replace — matching the whole-buffer
    /// <see cref="Fill(in Cell)"/>, so a transparent cell clears the region); under any other mode the
    /// cell's style is blended against each existing cell through <see cref="CurrentBlendingMode"/>.
    /// The rect is clamped to the buffer bounds — an out-of-buffer or empty rect is a no-op.
    /// </summary>
    public void Fill(in Rect region, in Cell cell)
    {
        if (region.IsEmpty) return;

        int row = Math.Max(0, region.Row);
        int col = Math.Max(0, region.Column);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        var mode = CurrentBlendingMode;
        // Default mode is a verbatim replace (consistent with Fill(in Cell)'s Array.Fill fast path),
        // so a transparent fill actually clears; non-default modes blend per cell.
        bool fast = ReferenceEquals(mode, BlendingModes.Default);

        for (int r = row; r < rowEnd; r++)
        {
            int rowStart = r * _columns;
            for (int c = col; c < colEnd; c++)
            {
                int idx = rowStart + c;
                _cells[idx] = fast
                    ? cell
                    : cell with { Style = BlendStyle(cell.Style, _cells[idx].Style, mode) };
            }
        }
    }

    /// <summary>
    /// Reset every cell inside <paramref name="region"/> to <see cref="Cell.Blank"/>. Does NOT
    /// apply blending. Out-of-buffer or empty rects are no-ops. Fragments and dirty regions are
    /// untouched — this is a cell-only operation. Use <see cref="Clear()"/> (parameterless) to
    /// wipe the whole buffer including fragments / dirty state, or remove fragments individually
    /// via <see cref="RemoveFragment(int, int)"/>.
    /// </summary>
    public void ClearCells(in Rect region)
    {
        if (region.IsEmpty) return;

        int row = Math.Max(0, region.Row);
        int col = Math.Max(0, region.Column);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        var blank = Cell.Blank with { Style = _defaultStyle };

        for (int r = row; r < rowEnd; r++)
        {
            int rowStart = r * _columns;
            for (int c = col; c < colEnd; c++)
                _cells[rowStart + c] = blank;
        }
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

    // ---- View factories -------------------------------------------------------------------

    /// <summary>
    /// A <see cref="CellBufferView"/> spanning the entire buffer. The view translates 0-based
    /// view-local coordinates to backing-buffer coordinates and clips writes to a rectangle —
    /// useful for handing a widget its own coordinate space without exposing the surrounding
    /// surface. See <see cref="CellBufferView"/> for the full contract.
    /// </summary>
    public CellBufferView AsView() => new(this);

    /// <summary>
    /// A <see cref="CellBufferView"/> over the rectangle
    /// (<paramref name="offsetRow"/>, <paramref name="offsetColumn"/>) × (<paramref name="columns"/>,
    /// <paramref name="rows"/>). Negative offsets / dimensions are clamped, and the rect is
    /// clipped against the buffer's bounds.
    /// </summary>
    public CellBufferView View(int offsetColumn, int offsetRow, int columns, int rows)
        => new(this, offsetColumn, offsetRow, columns, rows);

    /// <summary>
    /// A <see cref="CellBufferView"/> over the rectangle <paramref name="region"/>. Negative offsets /
    /// dimensions are clamped, and the rect is clipped against the buffer's bounds.
    /// </summary>
    public CellBufferView View(in Rect region)
        => new(this, region.Column, region.Row, region.Columns, region.Rows);

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
        _fragmentsByKey.Clear();
        _dirtyRegions.Clear();

        FillWithDefaultStyleIfKnown();
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

    private void ValidateCoordinates(int column, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, _columns);
    }
    
    private void FillWithDefaultStyleIfKnown()
    {
        if (_defaultStyle != default)
            _cells.AsSpan().Fill(Cell.Blank with { Style = _defaultStyle });
    }
    
    public static implicit operator CellBufferView(CellBuffer buffer) => buffer.AsView();
}