using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A wrapping panel whose slots are all the <b>same size</b> (WPF <c>UniformGrid</c> ✕
/// <c>WrapPanel</c>; Avalonia has no direct analog). Where <see cref="WrapPanel"/> packs each line
/// greedily from the children's individual desired sizes, this panel first derives ONE cell extent —
/// the per-axis maximum of every child's desired size, or the explicit
/// <see cref="ItemWidth"/>/<see cref="ItemHeight"/> — and then tiles a perfect grid with it. That
/// makes the geometry <i>addressable</i>: <see cref="ItemsPerLine"/> and <see cref="LineCount"/> are
/// published as read-only properties, so a keyboard-navigating owner (see <see cref="ListView"/>)
/// can map ←/→/↑/↓ onto index arithmetic without re-deriving the layout.
/// <para>
/// <b>Both orientations.</b> <see cref="Orientation.Horizontal"/> is row-major — fill across, then
/// wrap DOWN, which grows the panel vertically (pair it with vertical scrolling). <see
/// cref="Orientation.Vertical"/> is <b>column-major</b> — fill DOWN a column, then wrap ACROSS,
/// which grows the panel horizontally (pair it with horizontal scrolling). Column-major wrap is the
/// classic file-manager "List"/"Small Icons" layout and is the mode this panel was written for; it
/// is exactly the mode a plain <c>StackPanel</c> cannot express.
/// </para>
/// <para>
/// Collapsed children are skipped entirely — no slot, no line participation (the
/// <see cref="WrapPanel"/> L169 rule) — so a hidden item does not punch a hole in the grid.
/// </para>
/// </summary>
public class UniformWrapPanel : Panel
{
    /// <summary>
    /// The flow axis (default <see cref="Orientation.Horizontal"/>). <see cref="Orientation.Vertical"/>
    /// selects the column-major fill (down, then across). <c>[AffectsMeasure]</c>
    /// </summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<UniformWrapPanel, Orientation>(nameof(Orientation), defaultValue: Orientation.Horizontal);

    /// <summary>
    /// The uniform slot width in cells, or <see langword="null"/> (default) to derive it from the widest
    /// child. <c>[AffectsMeasure]</c>; negative values coerce to 0.
    /// </summary>
    public static readonly StyledProperty<int?> ItemWidthProperty =
        UIProperty.Register<UniformWrapPanel, int?>(nameof(ItemWidth), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>
    /// The maximum slot width in cells, or <see langword="null"/> (default) to derive it from the widest
    /// child. <c>[AffectsMeasure]</c>; negative values coerce to 0.
    /// </summary>
    public static readonly StyledProperty<int?> MaxItemWidthProperty =
        UIProperty.Register<UniformWrapPanel, int?>(nameof(MaxItemWidth), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>
    /// The uniform slot height in cells, or <see langword="null"/> (default) to derive it from the tallest
    /// child. <c>[AffectsMeasure]</c>; negative values coerce to 0.
    /// </summary>
    public static readonly StyledProperty<int?> ItemHeightProperty =
        UIProperty.Register<UniformWrapPanel, int?>(nameof(ItemHeight), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>
    /// The maximum slot height in cells, or <see langword="null"/> (default) to derive it from the widest
    /// child. <c>[AffectsMeasure]</c>; negative values coerce to 0.
    /// </summary>
    public static readonly StyledProperty<int?> MaxItemHeightProperty =
        UIProperty.Register<UniformWrapPanel, int?>(nameof(MaxItemHeight), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>The number of slots on one line — items per ROW when horizontal, items per COLUMN when
    /// vertical. Read-only; republished by every arrange (and, so an un-arranged panel still answers, by
    /// every measure). Always ≥ 1.</summary>
    public static readonly DirectProperty<UniformWrapPanel, int> ItemsPerLineProperty =
        UIProperty.RegisterDirect<UniformWrapPanel, int>(nameof(ItemsPerLine), static p => p._itemsPerLine);

    /// <summary>The number of lines the children occupy — ROWS when horizontal, COLUMNS when vertical. Read-only.</summary>
    public static readonly DirectProperty<UniformWrapPanel, int> LineCountProperty =
        UIProperty.RegisterDirect<UniformWrapPanel, int>(nameof(LineCount), static p => p._lineCount);

    /// <summary>The resolved uniform slot width in cells (the explicit <see cref="ItemWidth"/>, else the widest child). Read-only.</summary>
    public static readonly DirectProperty<UniformWrapPanel, int> CellWidthProperty =
        UIProperty.RegisterDirect<UniformWrapPanel, int>(nameof(CellWidth), static p => p._cellWidth);

    /// <summary>The resolved uniform slot height in cells (the explicit <see cref="ItemHeight"/>, else the tallest child). Read-only.</summary>
    public static readonly DirectProperty<UniformWrapPanel, int> CellHeightProperty =
        UIProperty.RegisterDirect<UniformWrapPanel, int>(nameof(CellHeight), static p => p._cellHeight);

    private int _itemsPerLine = 1;
    private int _lineCount;
    private int _cellWidth = 1;
    private int _cellHeight = 1;

    static UniformWrapPanel()
    {
        AffectsMeasure<UniformWrapPanel>(OrientationProperty, ItemWidthProperty, ItemHeightProperty);
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <inheritdoc cref="ItemWidthProperty"/>
    public int? ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

    /// <inheritdoc cref="MaxItemWidthProperty"/>
    public int? MaxItemWidth { get => GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }

    /// <inheritdoc cref="ItemHeightProperty"/>
    public int? ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    /// <inheritdoc cref="MaxItemHeightProperty"/>
    public int? MaxItemHeight { get => GetValue(MaxItemHeightProperty); set => SetValue(MaxItemHeightProperty, value); }

    /// <inheritdoc cref="ItemsPerLineProperty"/>
    public int ItemsPerLine => _itemsPerLine;

    /// <inheritdoc cref="LineCountProperty"/>
    public int LineCount => _lineCount;

    /// <inheritdoc cref="CellWidthProperty"/>
    public int CellWidth => _cellWidth;

    /// <inheritdoc cref="CellHeightProperty"/>
    public int CellHeight => _cellHeight;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;

        var viewportSize = /*VisualParent is ItemsPresenter { UIParent: ScrollViewer { ResolvedViewport: var vp } }
                               ? vp
                               : */default(Size?);

        var maxWidth = MaxItemWidth ?? viewportSize?.Columns ?? LayoutMath.Unbounded;
        var maxHeight = MaxItemHeight ?? viewportSize?.Rows ?? LayoutMath.Unbounded;
        var explicitWidth = ItemWidth;
        var explicitHeight = ItemHeight;

        // ───────────────────────────── pass 1: derive the uniform cell ─────────────────────────────
        //
        // Children are probed at the natural constraint (the explicit slot per axis when given, else the
        // panel's own constraint) purely to read their desired size; the winning maximum becomes the slot
        // and pass 2 re-measures everyone AT that slot, so a child that stretches (a selection bar) fills
        // its cell instead of collapsing to its text width.
        var probe = new Size(explicitWidth ?? availableSize.Columns, explicitHeight ?? availableSize.Rows);
        // var probe = new Size(Math.Max(explicitWidth ?? availableSize.Columns, maxWidth),
        //                      Math.Max(explicitHeight ?? availableSize.Rows, maxHeight));

        var cellWidth = explicitWidth ?? 0;
        var cellHeight = explicitHeight ?? 0;
        var visibleCount = 0;

        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            child.Measure(probe);

            if (child.Visibility == Visibility.Collapsed)
                continue;

            visibleCount++;

            var desired = child.DesiredSize;

            if (explicitWidth is null)
                cellWidth = Math.Min(maxWidth, Math.Max(cellWidth, desired.Columns));

            if (explicitHeight is null)
                cellHeight = Math.Min(maxHeight, Math.Max(cellHeight, desired.Rows));
        }

        // A zero-extent cell would make items-per-line infinite; one cell is the terminal's atom.
        cellWidth = Math.Max(1, Math.Min(cellWidth, LayoutMath.MaxExtent));
        cellHeight = Math.Max(1, Math.Min(cellHeight, LayoutMath.MaxExtent));

        var lineConstraint = horizontal ? availableSize.Columns : availableSize.Rows;
        var cellMain = horizontal ? cellWidth : cellHeight;
        var itemsPerLine = SlotsIn(lineConstraint, cellMain);
        var lineCount = visibleCount == 0 ? 0 : (visibleCount + itemsPerLine - 1) / itemsPerLine;

        PublishGeometry(itemsPerLine, lineCount, cellWidth, cellHeight);

        // ───────────────────────────── pass 2: measure at the slot ─────────────────────────────
        var slot = new Size(cellWidth, cellHeight);
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility != Visibility.Collapsed)
                child.Measure(slot);
        }

        var mainExtent = Math.Min(visibleCount, itemsPerLine) * (long)cellMain;
        var crossExtent = lineCount * (long)(horizontal ? cellHeight : cellWidth);

        var main = (int) Math.Clamp(mainExtent, 0, LayoutMath.MaxExtent);
        var cross = (int) Math.Clamp(crossExtent, 0, LayoutMath.MaxExtent);

        return horizontal ? new Size(main, cross) : new Size(cross, main);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var cellWidth = _cellWidth;
        var cellHeight = _cellHeight;

        // Re-derive items-per-line from the FINAL size on the non-scrolling axis: under a
        // ScrollContentPresenter the scrolling axis is arranged at the full extent while the other keeps
        // the viewport, so this reproduces the measure answer — and when the parent squeezed us below the
        // measure constraint it correctly re-wraps to what we actually got.
        var lineConstraint = horizontal ? finalSize.Columns : finalSize.Rows;
        var cellMain = horizontal ? cellWidth : cellHeight;
        var itemsPerLine = SlotsIn(lineConstraint, cellMain);

        var children = Children;
        var visible = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var line = visible / itemsPerLine;
            var slot = visible % itemsPerLine;
            visible++;

            var mainOffset = (int) Math.Clamp(slot * (long)cellMain, 0, LayoutMath.MaxExtent);
            var crossOffset = (int) Math.Clamp(line * (long)(horizontal ? cellHeight : cellWidth), 0, LayoutMath.MaxExtent);

            child.Arrange(horizontal
                ? new Rect(mainOffset, crossOffset, cellWidth, cellHeight)
                : new Rect(crossOffset, mainOffset, cellWidth, cellHeight));
        }

        PublishGeometry(itemsPerLine, visible == 0 ? 0 : (visible + itemsPerLine - 1) / itemsPerLine, cellWidth, cellHeight);

        return finalSize;
    }

    // Unbounded / oversized line constraints must not produce a zero or negative slot count: a single
    // slot is the floor, and the Unbounded sentinel means "everything on one line".
    private static int SlotsIn(int lineConstraint, int cellMain)
    {
        if (LayoutMath.IsUnbounded(lineConstraint))
            return int.MaxValue / 2;

        return Math.Max(1, lineConstraint / Math.Max(1, cellMain));
    }

    // DirectProperty writes: no store, no effects lane — so republishing from ArrangeOverride cannot
    // re-invalidate layout (which would oscillate). Observers still see the change.
    private void PublishGeometry(int itemsPerLine, int lineCount, int cellWidth, int cellHeight)
    {
        SetAndRaise(ItemsPerLineProperty, ref _itemsPerLine, itemsPerLine);
        SetAndRaise(LineCountProperty, ref _lineCount, lineCount);
        SetAndRaise(CellWidthProperty, ref _cellWidth, cellWidth);
        SetAndRaise(CellHeightProperty, ref _cellHeight, cellHeight);
    }
}
