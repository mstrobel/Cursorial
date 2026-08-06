using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>Which axis a <see cref="GridSplitter"/> resizes.</summary>
public enum GridResizeDirection
{
    /// <summary>Inferred from the splitter's alignment then its arranged aspect (the WPF heuristic).</summary>
    Auto,

    /// <summary>Resizes the columns to either side (a vertical bar dragged left/right).</summary>
    Columns,

    /// <summary>Resizes the rows above/below (a horizontal bar dragged up/down).</summary>
    Rows,
}

/// <summary>Which pair of definitions a <see cref="GridSplitter"/> redistributes between.</summary>
public enum GridResizeBehavior
{
    /// <summary>Inferred from the splitter's alignment (the WPF default): an edge alignment resizes the
    /// definition on that side plus the splitter's own; a stretched/centered splitter resizes the
    /// definitions on either side of its own.</summary>
    BasedOnAlignment,

    /// <summary>The splitter's own definition and the next one.</summary>
    CurrentAndNext,

    /// <summary>The previous definition and the splitter's own.</summary>
    PreviousAndCurrent,

    /// <summary>The definitions on either side of the splitter's own (the splitter sits in its own track).</summary>
    PreviousAndNext,
}

/// <summary>
/// A draggable divider that resizes the <see cref="Grid"/> tracks on either side (the WPF/Avalonia
/// <c>GridSplitter</c>): a <see cref="Thumb"/> placed in a grid cell whose drag redistributes cells between
/// two adjacent <see cref="ColumnDefinition"/>s or <see cref="RowDefinition"/>s. <see cref="ResizeDirection"/>
/// selects the axis (<see cref="GridResizeDirection.Auto"/> infers it); <see cref="ResizeBehavior"/> selects
/// the pair. Both definitions' <c>Min</c>/<c>Max</c> are honored and the pair's combined size is conserved.
/// A definition keeps its unit type across a resize — a <c>*</c> track stays <c>*</c> (its weight rewritten to
/// the new cell count, so the pair holds its new ratio on a container resize), a fixed/<c>Auto</c> track
/// becomes a fixed cell count. With keyboard focus the arrow keys nudge by <see cref="KeyboardIncrement"/>.
/// </summary>
public class GridSplitter : Thumb
{
    // Resolved once at DragStarted (the grid structure is stable across one drag) and reused per DragDelta.
    private Grid? _dragGrid;
    private GridDefinition? _dragPrev;
    private GridDefinition? _dragNext;
    private bool _dragColumns;
    private int _dragPrevStart;
    private int _dragNextStart;

    private Size _arranged; // cached for the Auto-direction aspect tiebreak + cursor selection

    /// <summary>The axis to resize (default <see cref="GridResizeDirection.Auto"/>).</summary>
    public static readonly StyledProperty<GridResizeDirection> ResizeDirectionProperty =
        UIProperty.Register<GridSplitter, GridResizeDirection>(nameof(ResizeDirection), GridResizeDirection.Auto);

    /// <summary>The pair of definitions to redistribute between (default <see cref="GridResizeBehavior.BasedOnAlignment"/>).</summary>
    public static readonly StyledProperty<GridResizeBehavior> ResizeBehaviorProperty =
        UIProperty.Register<GridSplitter, GridResizeBehavior>(nameof(ResizeBehavior), GridResizeBehavior.BasedOnAlignment);

    /// <summary>Cells moved per arrow-key press while focused (default 1).</summary>
    public static readonly StyledProperty<int> KeyboardIncrementProperty =
        UIProperty.Register<GridSplitter, int>(nameof(KeyboardIncrement), 1);

    static GridSplitter() => FocusableProperty.OverrideDefaultValue<GridSplitter>(true); // arrow-key resize

    /// <summary>Wires the drag handlers (the base <see cref="Thumb"/> raises the routed drag events).</summary>
    public GridSplitter()
    {
        DragStarted += OnDragStarted;
        DragDelta += OnDragDelta;
        DragCompleted += OnDragCompleted;
    }

    /// <inheritdoc cref="ResizeDirectionProperty"/>
    public GridResizeDirection ResizeDirection { get => GetValue(ResizeDirectionProperty); set => SetValue(ResizeDirectionProperty, value); }

    /// <inheritdoc cref="ResizeBehaviorProperty"/>
    public GridResizeBehavior ResizeBehavior { get => GetValue(ResizeBehaviorProperty); set => SetValue(ResizeBehaviorProperty, value); }

    /// <inheritdoc cref="KeyboardIncrementProperty"/>
    public int KeyboardIncrement { get => GetValue(KeyboardIncrementProperty); set => SetValue(KeyboardIncrementProperty, value); }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _arranged = finalSize;
        // The resting cursor reflects the resolved axis: col/row-resize is the table-divider shape. Set
        // unconditionally (equality-gated, so no layout churn) — a splitter's purpose IS resize.
        if (TryResolveDirectionColumns() is { } columns)
            SetValue(CursorProperty, columns ? MouseCursorShape.ColResize : MouseCursorShape.RowResize);
        return base.ArrangeOverride(finalSize);
    }

    private void OnDragStarted(object? sender, ThumbDragEventArgs e)
    {
        _dragGrid = null;
        if (!TryResolvePair(out _dragColumns, out var prev, out var next))
            return;

        _dragGrid = (Grid)VisualParent!;
        _dragPrev = prev;
        _dragNext = next;
        _dragPrevStart = prev.ActualSize;
        _dragNextStart = next.ActualSize;
    }

    private void OnDragDelta(object? sender, ThumbDragEventArgs e)
    {
        if (_dragGrid is null || _dragPrev is null || _dragNext is null)
            return;

        var delta = _dragColumns ? e.HorizontalChange : e.VerticalChange;
        ApplyResize(_dragPrev, _dragNext, _dragColumns, _dragPrevStart, _dragNextStart, delta);
    }

    private void OnDragCompleted(object? sender, ThumbDragEventArgs e)
    {
        _dragGrid = null;
        _dragPrev = null;
        _dragNext = null;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsFocused || e.Modifiers != KeyModifiers.None)
            return;

        if (!TryResolvePair(out var columns, out var prev, out var next))
            return;

        var step = Math.Max(1, KeyboardIncrement);
        int delta;
        if (columns)
            delta = e.Key switch { Key.LeftArrow => -step, Key.RightArrow => step, _ => 0 };
        else
            delta = e.Key switch { Key.UpArrow => -step, Key.DownArrow => step, _ => 0 };

        if (delta == 0)
            return;

        // Keyboard nudges are incremental from the CURRENT sizes (drag is cumulative from a grab snapshot).
        ApplyResize(prev, next, columns, prev.ActualSize, next.ActualSize, delta);
        e.Handled = true;
    }

    // Redistributes `delta` cells from `next` to `prev` (positive grows prev), conserving the pair's combined
    // size and honoring both definitions' Min/Max (Min wins a Min>Max conflict, LD18).
    private static void ApplyResize(
        GridDefinition prev, GridDefinition next, bool columns, int prevStart, int nextStart, int delta)
    {
        var total = prevStart + nextStart;
        var newPrev = ClampSize(prevStart + delta, prev.MinSize, prev.MaxSize);
        var newNext = total - newPrev;

        // Pull `prev` back if the conserved partner would breach ITS bounds.
        if (newNext < next.MinSize)
            newPrev = total - next.MinSize;
        else if (next.MaxSize != LayoutMath.Unbounded && newNext > next.MaxSize)
            newPrev = total - next.MaxSize;

        newPrev = ClampSize(newPrev, prev.MinSize, prev.MaxSize);
        newNext = total - newPrev;

        if (newPrev == prevStart && newNext == nextStart)
            return; // pinned by a Min/Max wall — no-op (no spurious measure invalidation)

        SetSize(prev, newPrev, columns);
        SetSize(next, newNext, columns);
    }

    private static int ClampSize(int value, int min, int max)
    {
        if (max != LayoutMath.Unbounded && value > max)
            value = max;
        if (value < min) // Min wins a Min>Max conflict (LD18)
            value = min;
        return value;
    }

    // Writes a definition's new size, preserving its unit type: a * track stays * (weight = the new cell
    // count, clamped > 0), a Cell/Auto track becomes a fixed cell count.
    private static void SetSize(GridDefinition definition, int cells, bool columns)
    {
        var length = definition.Length.IsStar ? GridLength.Star(Math.Max(1, cells)) : GridLength.FromCells(Math.Max(0, cells));
        if (columns)
            ((ColumnDefinition)definition).Width = length;
        else
            ((RowDefinition)definition).Height = length;
    }

    // Resolves the resize axis only (for the resting cursor). Null when the splitter isn't in a grid with a
    // matching definition collection.
    private bool? TryResolveDirectionColumns()
    {
        if (VisualParent is not Grid grid)
            return null;

        return ResolveColumns(grid) is { } columns && (columns ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count) > 0
            ? columns
            : null;
    }

    private bool? ResolveColumns(Grid grid)
        => ResizeDirection switch
        {
            GridResizeDirection.Columns => true,
            GridResizeDirection.Rows => false,
            _ => AutoColumns(grid),
        };

    // The WPF Auto heuristic: an explicit horizontal alignment ⇒ columns; an explicit vertical alignment ⇒
    // rows; otherwise the narrower arranged axis (a tall thin bar ⇒ columns).
    private bool AutoColumns(Grid grid)
    {
        if (HorizontalAlignment != HorizontalAlignment.Stretch && grid.ColumnDefinitions.Count > 0)
            return true;
        if (VerticalAlignment != VerticalAlignment.Stretch && grid.RowDefinitions.Count > 0)
            return false;
        if (grid.ColumnDefinitions.Count == 0)
            return false;
        if (grid.RowDefinitions.Count == 0)
            return true;
        return _arranged.Columns <= _arranged.Rows;
    }

    // Resolves the axis AND the two definitions to redistribute. False when the splitter isn't a direct child
    // of a grid, the axis has no definitions, or the resolved pair falls outside the definition range.
    private bool TryResolvePair(out bool columns, out GridDefinition prev, out GridDefinition next)
    {
        prev = null!;
        next = null!;
        columns = false;

        if (VisualParent is not Grid grid || ResolveColumns(grid) is not { } resolvedColumns)
            return false;

        columns = resolvedColumns;
        var count = columns ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
        if (count < 2)
            return false;

        var index = columns ? Grid.GetColumn(this) : Grid.GetRow(this);
        var (prevIndex, nextIndex) = ResolvePairIndices(index, columns);
        if (prevIndex < 0 || nextIndex >= count || prevIndex == nextIndex)
            return false;

        prev = columns ? grid.ColumnDefinitions[prevIndex] : grid.RowDefinitions[prevIndex];
        next = columns ? grid.ColumnDefinitions[nextIndex] : grid.RowDefinitions[nextIndex];
        return true;
    }

    private (int Prev, int Next) ResolvePairIndices(int index, bool columns)
        => ResolveBehavior(columns) switch
        {
            GridResizeBehavior.CurrentAndNext => (index, index + 1),
            GridResizeBehavior.PreviousAndCurrent => (index - 1, index),
            _ => (index - 1, index + 1), // PreviousAndNext (and the BasedOnAlignment stretched/centered case)
        };

    private GridResizeBehavior ResolveBehavior(bool columns)
    {
        if (ResizeBehavior != GridResizeBehavior.BasedOnAlignment)
            return ResizeBehavior;

        // BasedOnAlignment: an edge alignment grows toward the edge (the splitter's own track + the one beyond
        // it on that edge); a stretched/centered splitter sits in its own track and resizes its neighbors.
        if (columns)
            return HorizontalAlignment switch
            {
                HorizontalAlignment.Left => GridResizeBehavior.PreviousAndCurrent,
                HorizontalAlignment.Right => GridResizeBehavior.CurrentAndNext,
                _ => GridResizeBehavior.PreviousAndNext,
            };

        return VerticalAlignment switch
        {
            VerticalAlignment.Top => GridResizeBehavior.PreviousAndCurrent,
            VerticalAlignment.Bottom => GridResizeBehavior.CurrentAndNext,
            _ => GridResizeBehavior.PreviousAndNext,
        };
    }
}
