using System.Collections;
using Cursorial.Input;
using Cursorial.Media;
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
/// <see cref="Fill(in Cell)"/>, <see cref="Clear"/>, fragment / dirty-region calls — accept view-local
/// coordinates and translate them to backing-buffer coordinates internally.
/// </para>
/// <para>
/// <b>Clipping.</b> Writes outside <c>[0, Columns) × [0, Rows)</c> are silently dropped by
/// <see cref="Set"/>, <see cref="Fill(in Cell)"/>, <see cref="Clear"/>, and the fragment / dirty-region
/// methods. (On a view re-based via <see cref="WithOrigin"/> the addressable local range moves with
/// the origin; writes always clip to the same backing window.) The indexer (<c>view[r, c]</c>) instead <em>validates</em> coordinates and throws on
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
public readonly struct CellBufferView : ICellSurface
{
    private readonly CellBuffer _buffer;

    // Backing-buffer coordinates of the addressable window's top-left. For every view produced by the
    // public constructors and View(...) this equals (OffsetColumn, OffsetRow) — the local origin sits at
    // the window's corner and every range check reduces to the classic [0, Columns) / [0, Rows) form.
    // WithOrigin re-bases the origin away from the window (it may even be negative — content scrolled
    // above/left of a viewport) while the window stays fixed; writes always clip to the window.
    private readonly int _windowColumn;
    private readonly int _windowRow;

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
        _windowRow = clampedOffsetRow;
        _windowColumn = clampedOffsetColumn;
        Rows = Math.Max(0, clampedRowEnd - clampedOffsetRow);
        Columns = Math.Max(0, clampedColumnEnd - clampedOffsetColumn);
    }

    // The re-based form produced by WithOrigin: the window is inherited verbatim; only the origin moves.
    private CellBufferView(CellBuffer buffer, int originColumn, int originRow,
                           int windowColumn, int windowRow, int columns, int rows)
    {
        _buffer = buffer;
        OffsetColumn = originColumn;
        OffsetRow = originRow;
        _windowColumn = windowColumn;
        _windowRow = windowRow;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>
    /// Re-base this view's coordinate origin: the returned view addresses the <b>same backing window</b>
    /// (same clip rectangle, same <see cref="Columns"/> × <see cref="Rows"/>), but its local
    /// <c>(0, 0)</c> maps to the backing-buffer cell
    /// (<paramref name="originColumn"/>, <paramref name="originRow"/>) — a point that may lie anywhere
    /// relative to the window, including above/left of it (negative local coordinates of the window are
    /// then valid; positive local coordinates may fall outside it). Writes still clip to the window:
    /// the view is a <em>viewport with a movable content origin</em>, which is what lets a painter that
    /// works in content coordinates (formatted text, embedded content) render scrolled content into a
    /// clip rectangle without translating every coordinate itself.
    /// </summary>
    /// <remarks>
    /// <see cref="Bounds"/> (anchored at <c>(0, 0)</c>) is <b>not</b> meaningful on a re-based view —
    /// the addressable local rectangle starts at the window's local position, not at the origin. The
    /// coordinate-bearing operations (<see cref="Set"/>, <see cref="Write"/>, the indexer, fills,
    /// fragments, dirty regions) all honor the re-based mapping.
    /// </remarks>
    public CellBufferView WithOrigin(int originColumn, int originRow)
        => _buffer is null ? default : new(_buffer, originColumn, originRow, _windowColumn, _windowRow, Columns, Rows);

    // ---- Local-coordinate window edges ----------------------------------------------------
    // The addressable local range is [LocalColumnStart, LocalColumnEnd) × [LocalRowStart, LocalRowEnd).
    // For all non-re-based views these are 0 / Columns / 0 / Rows, so every check below reduces to the
    // historical form. On a re-based view they may start anywhere (including negative).
    //
    // PUBLIC because a painter that does its own bounds arithmetic — a glyph font walking a run, a
    // formatted document walking a paragraph — cannot express "am I past the right edge?" in terms of
    // Columns/Rows on a re-based view: WithOrigin moves the window in local space, so [0, Columns) is
    // simply the wrong interval. Bounds is documented as not meaningful on a re-based view; this is
    // what to use instead. (Set/Write/the fills already clip themselves — these are for the painter's
    // own loop bounds, not for clipping.)

    /// <summary>
    /// First addressable local column: the window's left edge in this view's coordinates. <c>0</c> on
    /// every view except one re-based by <see cref="WithOrigin"/>, where it may be any value —
    /// including negative (content whose origin sits right of the window's left edge).
    /// </summary>
    public int LocalColumnStart => _windowColumn - OffsetColumn;

    /// <inheritdoc cref="LocalColumnStart"/>
    public int LocalRowStart => _windowRow - OffsetRow;

    /// <summary>
    /// One past the last addressable local column. <see cref="Columns"/> on every view except one
    /// re-based by <see cref="WithOrigin"/>, where it is <see cref="LocalColumnStart"/> +
    /// <see cref="Columns"/>.
    /// </summary>
    public int LocalColumnEnd => _windowColumn - OffsetColumn + Columns;

    /// <inheritdoc cref="LocalColumnEnd"/>
    public int LocalRowEnd => _windowRow - OffsetRow + Rows;

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

    /// <summary>
    /// The blank style of the backing buffer — see <see cref="CellBuffer.DefaultStyle"/>. The view is
    /// a coordinate / clip filter, not a separate state container, so it has no blank style of its
    /// own. <see cref="CellStyle.Default"/> on a default-constructed view, where there is no buffer to
    /// ask (every operation on such a view is a safe no-op).
    /// </summary>
    public CellStyle DefaultStyle => _buffer?.DefaultStyle ?? CellStyle.Default;

    /// <summary>The view's rectangle in view-local coordinates: anchored at <c>(0, 0)</c>.</summary>
    public Rect Bounds => new(0, 0, Columns, Rows);

    /// <summary>The view's window rectangle in backing-buffer coordinates.</summary>
    public Rect BufferBounds => new(_windowColumn, _windowRow, Columns, Rows);

    /// <summary>The view's dimensions, in cells.</summary>   
    public (int Columns, int Rows) Dimensions => (Columns, Rows);
    
    /// <summary>True when (<paramref name="row"/>, <paramref name="column"/>) is inside the view's window.</summary>
    public bool Contains(int column, int row)
        => row >= LocalRowStart && row < LocalRowEnd && column >= LocalColumnStart && column < LocalColumnEnd;

    /// <summary>
    /// Reads the cell at the view-local (<paramref name="column"/>, <paramref name="row"/>) through
    /// this view's origin mapping, yielding a blank for anything outside the window instead of
    /// throwing — the non-validating read a bulk copy needs (see <see cref="CellBuffer.Blit"/>).
    /// </summary>
    internal Cell Read(int column, int row)
        => _buffer is not null && Contains(column, row)
               ? _buffer[column + OffsetColumn, row + OffsetRow]
               : default;

    /// <summary>
    /// The <see cref="CellStyle"/> of the <see cref="CellKind.WideLeft"/> immediately left of the
    /// view-local (<paramref name="column"/>, <paramref name="row"/>), or <see cref="CellStyle.Default"/>
    /// when there is no such cell.
    /// </summary>
    /// <remarks>
    /// Deliberately reads the BACKING buffer rather than the window: a continuation carries no style
    /// of its own, so a copy that cuts a pair has to source the blank it leaves behind from the
    /// leading half — and the cut is exactly what puts that half one column outside the view. The
    /// result is a color, never a cell, so it can't smuggle content past the clip.
    /// </remarks>
    internal CellStyle StyleOfLeadingHalf(int column, int row)
    {
        if (_buffer is null) return CellStyle.Default;

        int bufferColumn = column + OffsetColumn - 1;
        int bufferRow = row + OffsetRow;

        if (bufferColumn < 0 || bufferColumn >= _buffer.Columns || bufferRow < 0 || bufferRow >= _buffer.Rows)
            return CellStyle.Default;

        var left = _buffer[bufferColumn, bufferRow];
        return left.Kind == CellKind.WideLeft ? left.Style : CellStyle.Default;
    }

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
            if (value < LocalRowStart || value >= LocalRowEnd)
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    $"Cursor row {value} is outside the view's rows [{LocalRowStart}, {LocalRowEnd}). Use the underlying buffer if an out-of-view cursor position is intentional.");
            _buffer.CursorRow = value + OffsetRow;
        }
    }

    /// <summary>Cursor column in view-local coordinates. See <see cref="CursorRow"/> for semantics.</summary>
    public int CursorColumn
    {
        get => _buffer is null ? 0 : _buffer.CursorColumn - OffsetColumn;
        set
        {
            if (value < LocalColumnStart || value >= LocalColumnEnd)
                throw new ArgumentOutOfRangeException(
                    nameof(value), value,
                    $"Cursor column {value} is outside the view's columns [{LocalColumnStart}, {LocalColumnEnd}).");
            _buffer.CursorColumn = value + OffsetColumn;
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
    /// out-of-bounds — for clipping semantics, use <see cref="Set"/> instead. The setter bypasses
    /// the active blending mode (matching <see cref="CellBuffer"/>'s indexer), but it does NOT
    /// bypass wide-cell consistency: the backing indexer keeps pairs consistent, and a leading half
    /// at the view's own right edge degrades to a blank single here — the view is a clip, so a
    /// pair-write must never land in the column past its window (the backing buffer applies the
    /// same rule at its edge; this is the view's version, anchored on the view's, exactly as
    /// <see cref="Set"/> does).
    /// </summary>
    public Cell this[int column, int row]
    {
        get
        {
            ValidateCoordinates(column, row);
            return _buffer[column + OffsetColumn, row + OffsetRow];
        }
        set
        {
            ValidateCoordinates(column, row);

            if (value.Kind == CellKind.WideLeft && column + 1 >= LocalColumnEnd)
                value = new Cell(null, CellKind.Single, value.Style);

            _buffer[column + OffsetColumn, row + OffsetRow] = value;
        }
    }

    /// <summary>
    /// Place <paramref name="grapheme"/> at the view-local <c>(<paramref name="row"/>,
    /// <paramref name="column"/>)</c> with the given <paramref name="style"/>. Coordinates
    /// outside the view rect are silently dropped (returns 0). Wide glyphs that would extend
    /// past the view's right edge degrade to a blank single cell. Returns the number of cells
    /// the placement occupied (0, 1, or 2).
    /// </summary>
    public int Set(int column, int row, string? grapheme, in CellStyle style)
    {
        if (_buffer is null) return 0;
        if (!Contains(column, row)) return 0;

        int width = string.IsNullOrEmpty(grapheme) ? 1 : GraphemeWidth.ClusterWidth(grapheme.AsSpan());
        if (width < 1) width = 1;

        // If the wide right-half would land outside the view, degrade to single-cell blank — we
        // can't safely write a continuation that points to a column outside our region. The
        // backing buffer applies the same logic at its own right edge; this is the view's
        // version anchored on the view's right edge.
        if (width == 2 && column + 1 >= LocalColumnEnd)
            return _buffer.Set(column + OffsetColumn, row + OffsetRow, null, style);

        return _buffer.Set(column + OffsetColumn, row + OffsetRow, grapheme, style);
    }

    /// <summary>
    /// Write the grapheme clusters of <paramref name="text"/> across a single row starting at the
    /// view-local <c>(column, row)</c>, advancing the column by each cluster's width (1 or 2) and
    /// applying the active blending mode per cell — see <see cref="Set(int, int, string?, in CellStyle)"/>.
    /// A start outside the view is dropped (returns 0); a cluster that would not fit in the
    /// remaining columns stops the write rather than being clipped. The write is single-row by
    /// contract: it <b>stops at the first C0/C1 control character</b> (including newlines and tabs)
    /// rather than storing it as a junk cell — split text into lines (or use the drawing layer's
    /// multi-line text) for multi-row layout, and expand tabs upstream. Returns the number of
    /// columns written.
    /// </summary>
    public int Write(int column, int row, ReadOnlySpan<char> text, in CellStyle style)
    {
        if (_buffer is null || text.IsEmpty) return 0;
        if (!Contains(column, row)) return 0;

        int start = column;
        var clusters = text.GetGraphemeEnumerator();

        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;

            // Stop at the first control character — this is a single-row, printable-text write.
            if (CellBuffer.IsC0OrC1Control(cluster[0])) break;

            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;

            // Stop at the view's right edge rather than placing a degraded glyph.
            if (column + width > LocalColumnEnd) break;

            column += Set(column, row, cluster.ToString(), style);
        }

        return column - start;
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
    /// Replace every cell inside <paramref name="region"/> (view-local coordinates) with
    /// <paramref name="cell"/>, blending against existing contents through the active blending
    /// mode. The rect is clipped to the view's bounds and translated to backing-buffer
    /// coordinates; an out-of-view or empty rect is a no-op.
    /// </summary>
    public void Fill(in Rect region, in Cell cell)
    {
        if (_buffer is null || region.IsEmpty || IsEmpty) return;

        // Clip the rect against the view's window in view-local space, then translate to backing
        // coordinates — the same projection MarkDirty uses.
        int row = Math.Max(LocalRowStart, region.Row);
        int col = Math.Max(LocalColumnStart, region.Column);
        int rowEnd = Math.Min(LocalRowEnd, region.RowEnd);
        int colEnd = Math.Min(LocalColumnEnd, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        _buffer.Fill(new Rect(col + OffsetColumn, row + OffsetRow, colEnd - col, rowEnd - row), cell);
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

        foreach (var ((col, row), e) in _buffer.FragmentsInternal)
        {
            var fragmentRect = new Rect(col, row, e.Fragment.GetSize());
            if (fragmentRect.Intersects(bufferBounds))
                (toRemove ??= (List<(int, int)>) []).Add((col, row));
        }

        if (toRemove is not null)
        {
            foreach (var (col, row) in toRemove)
                _buffer.RemoveFragment(col, row);
        }

        _buffer.ClearCells(bufferBounds);
        _buffer.MarkDirty(bufferBounds);
    }

    // ---- Fragments ----------------------------------------------------------------------

    /// <summary>
    /// All fragments currently registered against the buffer, keyed by anchor cell. The renderer
    /// iterates this collection after the regular cell-grid pass and emits each fragment's
    /// protocol bytes at its anchor. Order is iteration order of the underlying dictionary —
    /// fragments must not depend on each other's visual ordering at the cell layer.
    /// </summary>
    public FragmentDictionary Fragments =>
        _buffer is null ? FragmentDictionary.Empty : new(_buffer.FragmentsInternal, OffsetColumn, OffsetRow);

    /// <summary>
    /// Register <paramref name="fragment"/> at the view-local <c>(column, row)</c>. Translates
    /// to backing-buffer coordinates. Anchors outside the view rect are silently dropped —
    /// returns false. The cell grid is not modified (see <see cref="CellBuffer.AddFragment"/>
    /// for the layering contract).
    /// </summary>
    public bool AddFragment(int column, int row, IBufferFragment fragment, in CellStyle anchorStyle = default)
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
    /// Remove every fragment registered against the backing buffer. The compositor uses a full-buffer
    /// view, so this clears all of them — used on a full recomposite so fragments orphaned by a discarded
    /// compositor (the <c>ResetCompositor</c> path) are dropped, not left stranded on the target.
    /// </summary>
    public void ClearFragments() => _buffer?.ClearFragments();

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
    /// The backing buffer's dirty regions projected into this view's coordinate space: each region
    /// is intersected with the view's bounds and translated to view-local coordinates, and regions
    /// that don't overlap the view are dropped. Unlike <see cref="CellBuffer.DirtyRegions"/> — which
    /// exposes its live backing list — this is a lightweight lazy projection: it applies the
    /// intersect-and-translate transform on demand as elements are read or enumerated, rather than
    /// materializing a new list per access. The common "no dirty regions" case returns an empty
    /// list with no allocation at all.
    /// </summary>
    public IReadOnlyList<Rect> DirtyRegions
    {
        get
        {
            if (_buffer is null) return [];

            var source = _buffer.DirtyRegions;
            if (source.Count == 0) return [];

            return new ProjectedDirtyRegions(source, OffsetColumn, OffsetRow, _windowColumn, _windowRow, Columns, Rows);
        }
    }

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

        // Clip the rect against the view's window in view-local space first.
        int row = Math.Max(LocalRowStart, region.Row);
        int col = Math.Max(LocalColumnStart, region.Column);
        int rowEnd = Math.Min(LocalRowEnd, region.RowEnd);
        int colEnd = Math.Min(LocalColumnEnd, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        // Translate to backing-buffer coords and forward.
        _buffer.MarkDirty(new Rect(col + OffsetColumn, row + OffsetRow, colEnd - col, rowEnd - row));
    }

    /// <summary>
    /// Mark a <see cref="Rect"/> (view-local) for unconditional re-emission on the next render —
    /// the rect is clipped against the view's window and translated to backing-buffer coordinates.
    /// See <see cref="CellBuffer.ForceRepaint(in Rect)"/> for the semantics (expands emission, used
    /// to overwrite a removed Cells-layer image's lingering pixels).
    /// </summary>
    public void ForceRepaint(in Rect region)
    {
        if (_buffer is null || region.IsEmpty || IsEmpty) return;

        int row = Math.Max(LocalRowStart, region.Row);
        int col = Math.Max(LocalColumnStart, region.Column);
        int rowEnd = Math.Min(LocalRowEnd, region.RowEnd);
        int colEnd = Math.Min(LocalColumnEnd, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        _buffer.ForceRepaint(new Rect(col + OffsetColumn, row + OffsetRow, colEnd - col, rowEnd - row));
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

        // Compute the requested rect in this view's local space, then clip against the view's window.
        int localRow = Math.Max(LocalRowStart, offsetRow);
        int localCol = Math.Max(LocalColumnStart, offsetColumn);
        int localRowEnd = Math.Min(LocalRowEnd, offsetRow + Math.Max(0, rows));
        int localColEnd = Math.Min(LocalColumnEnd, offsetColumn + Math.Max(0, columns));

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

    /// <inheritdoc cref="CellBuffer.Blit"/>
    /// <remarks><paramref name="region"/> is view-local: it maps through this view's origin and is
    /// clipped to its window, so a blit can never write outside the view it was handed.</remarks>
    public void Blit(CellBufferView view, in Rect region)
    {
        if (_buffer is null || view.IsEmpty || region.IsEffectivelyEmpty)
            return;

        int columns = Math.Min(region.Columns, view.Columns);
        int rows = Math.Min(region.Rows, view.Rows);

        // Clip the destination to the addressable window, trimming the source in step.
        int column0 = Math.Max(region.Column, LocalColumnStart);
        int row0 = Math.Max(region.Row, LocalRowStart);
        int column1 = Math.Min(region.Column + columns, LocalColumnEnd);
        int row1 = Math.Min(region.Row + rows, LocalRowEnd);

        if (column1 <= column0 || row1 <= row0)
            return;

        var trimmed = view.View(column0 - region.Column, row0 - region.Row, column1 - column0, row1 - row0);

        // Window-relative coordinates are >= the window start, so the mapped anchor is never negative.
        _buffer.Blit(trimmed, new Rect(column0 + OffsetColumn, row0 + OffsetRow, column1 - column0, row1 - row0));
    }

    private void ValidateCoordinates(int column, int row)
    {
        if (row < LocalRowStart || row >= LocalRowEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(row), row,
                $"Row {row} is outside the view's rows [{LocalRowStart}, {LocalRowEnd}).");
        }

        if (column < LocalColumnStart || column >= LocalColumnEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(column), column,
                $"Column {column} is outside the view's columns [{LocalColumnStart}, {LocalColumnEnd}).");
        }
    }

    internal (int Column, int Row) TranslateToParent(in (int Column, int Row) position) => new(OffsetColumn + position.Column, OffsetRow + position.Row);

    internal CellPosition TranslateToParent(in CellPosition position) => new(OffsetColumn + position.Column, OffsetRow + position.Row);

    internal Rect TranslateToParent(in Rect bounds) => bounds.Translate(OffsetColumn, OffsetRow);
    
    internal (int Column, int Row) TranslateFromParent(in (int Column, int Row) position) => new(position.Column - OffsetColumn, position.Row - OffsetRow);
    
    internal CellPosition TranslateFromParent(in CellPosition position) => new(position.Column - OffsetColumn, position.Row - OffsetRow);
    
    internal Rect TranslateFromParent(in Rect bounds) => bounds.Translate(-OffsetColumn, -OffsetRow);

    /// <summary>
    /// A lazy <see cref="IReadOnlyList{T}"/> over a backing buffer's dirty regions, projected into a
    /// view's coordinate space. Each region is intersected with the view's backing rect and
    /// translated to view-local coordinates as it is read; regions that don't overlap the view are
    /// skipped. The transform runs per element access / enumeration — no list is materialized.
    /// </summary>
    /// <remarks>
    /// Because non-overlapping regions are filtered out, <see cref="Count"/> and the indexer scan
    /// the source list (O(n)); the projected count rarely differs from the source count and the
    /// list is small, so this is fine for the occasional read. Enumeration is the natural access
    /// pattern and is single-pass.
    /// </remarks>
    private sealed class ProjectedDirtyRegions(
        IReadOnlyList<Rect> source, int offsetColumn, int offsetRow,
        int windowColumn, int windowRow, int columns, int rows)
        : IReadOnlyList<Rect>
    {
        public int Count
        {
            get
            {
                int count = 0;
                for (int i = 0; i < source.Count; i++)
                    if (TryProject(source[i], out _)) count++;
                return count;
            }
        }

        public Rect this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);

                int seen = 0;
                for (int i = 0; i < source.Count; i++)
                {
                    if (!TryProject(source[i], out var projected)) continue;
                    if (seen == index) return projected;
                    seen++;
                }

                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        public IEnumerator<Rect> GetEnumerator()
        {
            for (int i = 0; i < source.Count; i++)
                if (TryProject(source[i], out var projected))
                    yield return projected;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private bool TryProject(Rect region, out Rect projected)
        {
            // Intersect the backing-buffer region with the view's WINDOW, then translate the overlap
            // into view-local coordinates. A re-based view (WithOrigin) maps part of its window to
            // NEGATIVE local coordinates, and the result carries them: `Rect` is signed, so the
            // honest projection is expressible.
            //
            // This used to also clamp the start to `offsetColumn`/`offsetRow`, back when a negative
            // origin would have thrown. That silently truncated a region straddling the local origin
            // and DROPPED one lying wholly left of / above it — losing a repaint, not merely
            // shrinking one. Only the origin clamp was wrong; the window clip below is the real
            // bound and stays.
            int colStart = Math.Max(region.Column, windowColumn);
            int rowStart = Math.Max(region.Row, windowRow);
            int colEnd = Math.Min(region.ColumnEnd, windowColumn + columns);
            int rowEnd = Math.Min(region.RowEnd, windowRow + rows);

            if (colStart >= colEnd || rowStart >= rowEnd)
            {
                projected = default;
                return false;
            }

            projected = new Rect(colStart - offsetColumn, rowStart - offsetRow,
                                 colEnd - colStart, rowEnd - rowStart);
            return true;
        }
    }
}