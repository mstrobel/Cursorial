using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Rendering;

/// <summary>
/// A windowed view over a <see cref="CellBuffer"/> that translates view-local coordinates into
/// backing-buffer coordinates and clips writes to a fixed rectangle. The intended use is letting
/// a higher-level draw routine — typically a widget in a UI framework — render in its own
/// 0-based coordinate space without knowing where it sits on the underlying surface, while the
/// view enforces that nothing leaks outside the allowed region.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinate translation.</b> The view's <c>(0, 0)</c> maps to
/// <c>(<see cref="OffsetRow"/>, <see cref="OffsetColumn"/>)</c> on the backing buffer. All
/// coordinate-bearing operations on the view — <see cref="Set"/>, the indexer,
/// <see cref="Fill"/>, <see cref="Clear"/>, fragment / dirty-region calls — accept view-local
/// coordinates and translate them to backing-buffer coordinates internally.
/// </para>
/// <para>
/// <b>Clipping.</b> Writes outside <c>[0, Columns) × [0, Rows)</c> are silently dropped by
/// <see cref="Set"/>, <see cref="Fill"/>, <see cref="Clear"/>, and the fragment / dirty-region
/// methods. The indexer (<c>view[r, c]</c>) instead <em>validates</em> coordinates and throws on
/// out-of-bounds access — it is the explicit form for when the caller has already proven the
/// write is in range. The semantic split matches <see cref="CellBuffer"/>'s indexer-vs-Set
/// behavior: indexers are "I know this is in bounds, do it"; <c>Set</c> is "place this if
/// visible, otherwise no-op."
/// </para>
/// <para>
/// <b>Sub-views</b> via <see cref="View(in Rect)"/> compose: the new view's offset is added to the
/// parent's, and its dimensions are clipped against the parent's rect. Two clip levels never
/// get violated — a sub-view that asks for a region extending past its parent's bounds is
/// silently trimmed.
/// </para>
/// <para>
/// <b>Wide cells</b> that would extend past the view's right edge degrade to a blank single
/// cell, mirroring how <see cref="CellBuffer.Set"/> handles the buffer's own right edge. This
/// keeps the view safe to use as a coordinate filter without leaking wide-cell continuations
/// outside its bounds.
/// </para>
/// <para>
/// <b>Pass-throughs.</b> Cursor state, blending-mode stack, fragments, and dirty regions all
/// forward to the underlying buffer (coordinates translated where applicable). The view is a
/// coordinate / clip filter, not a separate state container — pushing a blending mode through
/// one view affects every other view on the same buffer until it's popped. Treat the blending
/// stack as a shared resource and pair push / pop within a single draw scope.
/// </para>
/// <para>
/// <b>Cost.</b> A <see cref="CellBufferView"/> is a small <c>readonly struct</c> — passing it
/// through a draw call chain is allocation-free and effectively the cost of copying a couple of
/// integers plus the <see cref="CellBuffer"/> reference.
/// </para>
/// </remarks>
public readonly struct CellBufferView
{
    private readonly CellBuffer _buffer;

    /// <summary>
    /// Construct a view spanning the entire <paramref name="buffer"/>. Equivalent to
    /// <c>new CellBufferView(buffer, 0, 0, buffer.Columns, buffer.Rows)</c>.
    /// </summary>
    public CellBufferView(CellBuffer buffer)
        : this(buffer ?? throw new ArgumentNullException(nameof(buffer)), 0, 0, buffer.Columns, buffer.Rows) {}

    /// <summary>
    /// Construct a view covering the rectangle
    /// <c>(<paramref name="offsetRow"/>, <paramref name="offsetColumn"/>)</c> for
    /// <paramref name="columns"/> × <paramref name="rows"/> on <paramref name="buffer"/>.
    /// Negative offsets / dimensions are clamped to zero, and the rectangle is clipped against
    /// the buffer bounds. Passing a region entirely outside the buffer produces a zero-sized
    /// view — every coordinate-bearing operation on such a view is a no-op.
    /// </summary>
    public CellBufferView(CellBuffer buffer, int offsetColumn, int offsetRow, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Resolve the requested view rect to an inclusive-start / exclusive-end pair, then clip
        // both edges against the buffer. This drops any portion of the rect that falls outside
        // the buffer in either direction — negative offsets lose the off-buffer prefix; oversized
        // dimensions lose the off-buffer suffix.
        int requestedRowEnd = offsetRow + Math.Max(0, rows);
        int requestedColumnEnd = offsetColumn + Math.Max(0, columns);

        int clampedOffsetRow = Math.Clamp(offsetRow, 0, buffer.Rows);
        int clampedOffsetColumn = Math.Clamp(offsetColumn, 0, buffer.Columns);
        int clampedRowEnd = Math.Clamp(requestedRowEnd, 0, buffer.Rows);
        int clampedColumnEnd = Math.Clamp(requestedColumnEnd, 0, buffer.Columns);

        _buffer = buffer;
        OffsetRow = clampedOffsetRow;
        OffsetColumn = clampedOffsetColumn;
        Rows = Math.Max(0, clampedRowEnd - clampedOffsetRow);
        Columns = Math.Max(0, clampedColumnEnd - clampedOffsetColumn);
    }

    /// <summary>
    /// The underlying buffer. Internal so external paint code can't escape the view's clip
    /// contract — framework-internal consumers (renderer, fragment dispatch) reach across via
    /// <c>InternalsVisibleTo</c>. Null on a default-constructed view (<c>default(CellBufferView)</c>),
    /// in which case every public operation on the view is a safe no-op.
    /// </summary>
    internal CellBuffer Buffer => _buffer;

    /// <summary>Row offset of the view's <c>(0, 0)</c> on the backing buffer.</summary>
    public int OffsetRow { get; }

    /// <summary>Column offset of the view's <c>(0, 0)</c> on the backing buffer.</summary>
    public int OffsetColumn { get; }

    /// <summary>Width of the view in cells. May be zero if the view was constructed entirely outside the buffer.</summary>
    public int Columns { get; }

    /// <summary>Height of the view in rows. May be zero if the view was constructed entirely outside the buffer.</summary>
    public int Rows { get; }

    /// <summary>True when the view has zero area — no cells are addressable.</summary>
    public bool IsEmpty => Columns == 0 || Rows == 0;

    /// <summary>The view's rectangle in view-local coordinates: anchored at <c>(0, 0)</c>.</summary>
    public Rect Bounds => new(0, 0, Columns, Rows);

    /// <summary>The view's rectangle in backing-buffer coordinates.</summary>
    public Rect BufferBounds => new(OffsetColumn, OffsetRow, Columns, Rows);

    /// <summary>The view's dimensions, in cells.</summary>   
    public (int Columns, int Rows) Dimensions => (Columns, Rows);
    
    /// <summary>True when (<paramref name="row"/>, <paramref name="column"/>) is inside the view.</summary>
    public bool Contains(int column, int row)
        => row >= 0 && row < Rows && column >= 0 && column < Columns;

    // ---- Cursor pass-through ------------------------------------------------------------

    /// <summary>
    /// Cursor row in view-local coordinates: <c>buffer.CursorRow - OffsetRow</c>. The returned
    /// value may be outside <c>[0, Rows)</c> when another caller has placed the cursor outside
    /// this view's region. Setting validates the value is inside the view and throws on
    /// out-of-range.
    /// </summary>
    public int CursorRow
    {
        get => _buffer is null ? 0 : _buffer.CursorRow - OffsetRow;
        set
        {
            if ((uint) value >= (uint) Rows)
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    $"Cursor row {value} is outside the view's rows [0, {Rows}). Use the underlying buffer if an out-of-view cursor position is intentional.");
            _buffer!.CursorRow = value + OffsetRow;
        }
    }

    /// <summary>Cursor column in view-local coordinates. See <see cref="CursorRow"/> for semantics.</summary>
    public int CursorColumn
    {
        get => _buffer is null ? 0 : _buffer.CursorColumn - OffsetColumn;
        set
        {
            if ((uint) value >= (uint) Columns)
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    $"Cursor column {value} is outside the view's columns [0, {Columns}).");
            _buffer!.CursorColumn = value + OffsetColumn;
        }
    }

    /// <summary>Whether the cursor should be visible. Forwards directly to the backing buffer.</summary>
    public bool CursorVisible
    {
        get => _buffer is not null && _buffer.CursorVisible;
        set { if (_buffer is not null) _buffer.CursorVisible = value; }
    }

    /// <summary>Cursor shape. Forwards directly to the backing buffer.</summary>
    public CursorShape CursorShape
    {
        get => _buffer is null ? default : _buffer.CursorShape;
        set { if (_buffer is not null) _buffer.CursorShape = value; }
    }

    // ---- Blending stack pass-through ----------------------------------------------------

    /// <summary>Active blending mode on the backing buffer.</summary>
    public IBlendingMode CurrentBlendingMode => _buffer?.CurrentBlendingMode ?? BlendingModes.Default;

    /// <summary>Push a blending mode onto the backing buffer's stack. Pair with <see cref="PopBlendingMode"/>.</summary>
    public void PushBlendingMode(IBlendingMode mode) => _buffer?.PushBlendingMode(mode);

    /// <summary>Pop the topmost blending mode from the backing buffer's stack.</summary>
    public IBlendingMode PopBlendingMode() => _buffer is null ? BlendingModes.Default : _buffer.PopBlendingMode();

    // ---- Cell access --------------------------------------------------------------------

    /// <summary>
    /// Direct cell access in view-local coordinates. Validates the coordinates and throws on
    /// out-of-bounds — for clipping semantics, use <see cref="Set"/> instead. The setter
    /// bypasses wide-cell consistency handling and the active blending mode (matching
    /// <see cref="CellBuffer"/>'s indexer behavior).
    /// </summary>
    public Cell this[int column, int row]
    {
        get
        {
            ValidateCoordinates(column, row);
            return _buffer![column + OffsetColumn, row + OffsetRow];
        }
        set
        {
            ValidateCoordinates(column, row);
            _buffer![column + OffsetColumn, row + OffsetRow] = value;
        }
    }

    /// <summary>
    /// Place <paramref name="grapheme"/> at the view-local <c>(<paramref name="row"/>,
    /// <paramref name="column"/>)</c> with the given <paramref name="style"/>. Coordinates
    /// outside the view rect are silently dropped (returns 0). Wide glyphs that would extend
    /// past the view's right edge degrade to a blank single cell. Returns the number of cells
    /// the placement occupied (0, 1, or 2).
    /// </summary>
    public int Set(int column, int row, string? grapheme, in Style style)
    {
        if (_buffer is null) return 0;
        if ((uint) row >= (uint) Rows || (uint) column >= (uint) Columns) return 0;

        int width = string.IsNullOrEmpty(grapheme) ? 1 : GraphemeWidth.ClusterWidth(grapheme.AsSpan());
        if (width < 1) width = 1;

        // If the wide right-half would land outside the view, degrade to single-cell blank — we
        // can't safely write a continuation that points to a column outside our region. The
        // backing buffer applies the same logic at its own right edge; this is the view's
        // version anchored on the view's right edge.
        if (width == 2 && column + 1 >= Columns)
        {
            return _buffer.Set(column + OffsetColumn, row + OffsetRow, null, style);
        }

        return _buffer.Set(column + OffsetColumn, row + OffsetRow, grapheme, style);
    }

    /// <summary>
    /// Replace every cell in the view with <paramref name="cell"/>, blending against existing
    /// contents through <see cref="CurrentBlendingMode"/>. Scoped to the view rect — cells
    /// outside are untouched.
    /// </summary>
    public void Fill(in Cell cell)
    {
        if (IsEmpty) return;
        _buffer.Fill(BufferBounds, cell);
    }

    /// <summary>
    /// Reset every cell in the view to <see cref="Cell.Blank"/>. Does NOT apply blending.
    /// Scoped to the view rect — cells outside are untouched, and the buffer's fragments /
    /// dirty regions are also left alone (matching <see cref="CellBuffer.ClearCells(in Rect)"/>).
    /// </summary>
    public void Clear()
    {
        if (_buffer is null || IsEmpty) return;

        // Drop fragments anchored inside the view's rect. The rect-scoped CellBuffer.Clear is
        // contracted as cell-only — for parity with the parameterless CellBuffer.Clear (which
        // wipes fragments wholesale), the view's Clear must take care of its own subset. Without
        // this, callers that loop "Clear → repaint cells → re-attach fragments" (the format
        // demo's scroll painter, future widget systems) end up with overlay protocols stacking
        // up across frames because the previous frame's fragment anchors stay registered.
        var bufferBounds = BufferBounds;
        List<(int Column, int Row)>? toRemove = null;
        foreach (var ((col, row), _) in _buffer.FragmentsInternal)
        {
            if (col >= bufferBounds.Column && col < bufferBounds.ColumnEnd &&
                row >= bufferBounds.Row && row < bufferBounds.RowEnd)
            {
                (toRemove ??= new List<(int, int)>()).Add((col, row));
            }
        }
        if (toRemove is not null)
            foreach (var (col, row) in toRemove)
                _buffer.RemoveFragment(col, row);

        _buffer.ClearCells(bufferBounds);
    }

    // ---- Fragments ----------------------------------------------------------------------

    /// <summary>
    /// All fragments currently registered against the buffer, keyed by anchor cell. The renderer
    /// iterates this collection after the regular cell-grid pass and emits each fragment's
    /// protocol bytes at its anchor. Order is iteration order of the underlying dictionary —
    /// fragments must not depend on each other's visual ordering at the cell layer.
    /// </summary>
    public FragmentDictionary Fragments =>
        _buffer is null ? FragmentDictionary.Empty : new(_buffer.FragmentsInternal, Bounds);

    /// <summary>
    /// Register <paramref name="fragment"/> at the view-local <c>(column, row)</c>. Translates
    /// to backing-buffer coordinates. Anchors outside the view rect are silently dropped —
    /// returns false. The cell grid is not modified (see <see cref="CellBuffer.AddFragment"/>
    /// for the layering contract).
    /// </summary>
    public bool AddFragment(int column, int row, IBufferFragment fragment, in Style anchorStyle = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (_buffer is null || !Contains(column, row)) return false;
        _buffer.AddFragment(column + OffsetColumn, row + OffsetRow, fragment, anchorStyle);
        return true;
    }

    /// <summary>
    /// Remove the fragment anchored at the view-local <c>(column, row)</c>. Returns false when
    /// the coordinates are outside the view or no fragment was registered there.
    /// </summary>
    public bool RemoveFragment(int column, int row)
    {
        if (_buffer is null || !Contains(column, row)) return false;
        return _buffer.RemoveFragment(column + OffsetColumn, row + OffsetRow);
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
        return _buffer is not null && _buffer.ContainsFragment(key);
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

        if (_buffer is not null && _buffer.TryGetFragmentAnchor(key, out var parentAnchor))
        {
            anchor = TranslateFromParent(parentAnchor);
            return true;
        }

        anchor = default;
        return false;
    }

    // ---- Dirty-region tracking ----------------------------------------------------------

    /// <summary>
    /// Mark a rectangular region of the view as dirty. The rect is translated to backing-buffer
    /// coordinates and clipped against the view's bounds before being recorded.
    /// </summary>
    public void MarkDirty(int column, int row, int columns, int rows)
        => MarkDirty(new Rect(column, row, columns, rows));

    /// <summary>Mark a <see cref="Rect"/> (in view-local coordinates) as dirty.</summary>
    public void MarkDirty(in Rect region)
    {
        if (_buffer is null || region.IsEmpty || IsEmpty) return;

        // Clip the rect against the view's bounds in view-local space first.
        int row = Math.Max(0, region.Row);
        int col = Math.Max(0, region.Column);
        int rowEnd = Math.Min(Rows, region.RowEnd);
        int colEnd = Math.Min(Columns, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        // Translate to backing-buffer coords and forward.
        _buffer.MarkDirty(new Rect(col + OffsetColumn, row + OffsetRow, colEnd - col, rowEnd - row));
    }

    // ---- Sub-views ----------------------------------------------------------------------

    /// <summary>
    /// Create a view nested inside this one. The new view's offset is this view's offset plus
    /// <c>(<paramref name="offsetRow"/>, <paramref name="offsetColumn"/>)</c>, and its
    /// dimensions are clipped against this view's bounds so the sub-view can never address
    /// cells the parent can't.
    /// </summary>
    public CellBufferView View(int offsetColumn, int offsetRow, int columns, int rows)
    {
        // No backing buffer → no addressable cells anywhere; sub-viewing returns a default view
        // (which is equivalently empty) rather than throwing through the constructor.
        if (_buffer is null) return default;

        // Compute the requested rect in this view's local space, then clip against the view.
        int localRow = Math.Max(0, offsetRow);
        int localCol = Math.Max(0, offsetColumn);
        int localRowEnd = Math.Min(Rows, offsetRow + Math.Max(0, rows));
        int localColEnd = Math.Min(Columns, offsetColumn + Math.Max(0, columns));

        int clippedRows = Math.Max(0, localRowEnd - localRow);
        int clippedCols = Math.Max(0, localColEnd - localCol);

        // The sub-view's offset is in backing-buffer coordinates.
        return new CellBufferView(
            _buffer,
            OffsetColumn + localCol,
            OffsetRow + localRow,
            clippedCols,
            clippedRows);
    }

    /// <summary>
    /// Create a view nested inside this one. The new view's subregion is defined by the
    /// given <paramref name="region"/>, which is in view-local coordinates.
    /// </summary>
    public CellBufferView View(in Rect region)
        => View(region.Column, region.Row, region.Columns, region.Rows);

    private void ValidateCoordinates(int column, int row)
    {
        if ((uint) row >= (uint) Rows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(row), row,
                $"Row {row} is outside the view's rows [0, {Rows}).");
        }

        if ((uint) column >= (uint) Columns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(column), column,
                $"Column {column} is outside the view's columns [0, {Columns}).");
        }
    }

    internal (int Column, int Row) TranslateToParent(in (int Column, int Row) position) => new(OffsetColumn + position.Column, OffsetRow + position.Row);

    internal CellPosition TranslateToParent(in CellPosition position) => new(OffsetColumn + position.Column, OffsetRow + position.Row);

    internal Rect TranslateToParent(in Rect bounds) => bounds.Translate(OffsetColumn, OffsetRow);
    
    internal (int Column, int Row) TranslateFromParent(in (int Column, int Row) position) => new(position.Column - OffsetColumn, position.Row - OffsetRow);
    
    internal CellPosition TranslateFromParent(in CellPosition position) => new(position.Column - OffsetColumn, position.Row - OffsetRow);
    
    internal Rect TranslateFromParent(in Rect bounds) => bounds.Translate(-OffsetColumn, -OffsetRow);
}