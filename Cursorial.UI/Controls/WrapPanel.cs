using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// Greedy line-packing panel (design doc §5.4): children flow along <see cref="Orientation"/>
/// (default <see cref="Orientation.Horizontal"/>) and wrap when the next item would exceed the
/// line constraint — strictly greater, so an exact fit stays on the line (LD14).
/// <see cref="ItemWidth"/>/<see cref="ItemHeight"/> impose uniform item slots and replace the
/// child measure constraint per axis. An item whose main-axis extent exceeds the line constraint
/// wraps to its own line and is arranged in a slot <b>clamped to the line constraint</b> (LD10 —
/// arrange rects stay inside the panel; overflow rendering is the zone clip's job), and the
/// panel's desired main-axis extent accounts that line at the clamped width for consistency.
/// Collapsed children are skipped entirely — no slot, no line participation (L169).
/// </summary>
public class WrapPanel : Panel
{
    /// <summary>The flow axis (default <see cref="Orientation.Horizontal"/>). <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<WrapPanel, Orientation>(nameof(Orientation), defaultValue: Orientation.Horizontal);

    /// <summary>
    /// The uniform item width in cells, or <see langword="null"/> to use each child's desired width
    /// (default). When set, children are measured with it in place of the panel's horizontal
    /// constraint and every slot uses it regardless of the child's desired size (L164/L165).
    /// <c>[AffectsMeasure]</c>; negative values coerce to 0.
    /// </summary>
    public static readonly StyledProperty<int?> ItemWidthProperty =
        UIProperty.Register<WrapPanel, int?>(nameof(ItemWidth), coerce: static (_, value) => value is < 0 ? 0 : value);

    /// <summary>The uniform item height in cells, or <see langword="null"/> (default); see <see cref="ItemWidthProperty"/>. <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<int?> ItemHeightProperty =
        UIProperty.Register<WrapPanel, int?>(nameof(ItemHeight), coerce: static (_, value) => value is < 0 ? 0 : value);

    static WrapPanel()
    {
        AffectsMeasure<WrapPanel>(OrientationProperty, ItemWidthProperty, ItemHeightProperty);
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <inheritdoc cref="ItemWidthProperty"/>
    public int? ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }

    /// <inheritdoc cref="ItemHeightProperty"/>
    public int? ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var itemWidth = ItemWidth;
        var itemHeight = ItemHeight;
        var itemMain = horizontal ? itemWidth : itemHeight;
        var itemCross = horizontal ? itemHeight : itemWidth;
        var lineConstraint = horizontal ? availableSize.Columns : availableSize.Rows;

        // ItemWidth/ItemHeight replace the child constraint per axis (L165).
        var childConstraint = new Size(itemWidth ?? availableSize.Columns, itemHeight ?? availableSize.Rows);

        var lineUsed = 0;
        var lineCross = 0;
        var lineHasItems = false;
        var maxLine = 0;
        var totalCross = 0;

        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(childConstraint);
            if (child.Visibility == Visibility.Collapsed)
                continue; // no slot, no line participation (L169)

            var desired = child.DesiredSize;
            var extentMain = itemMain ?? (horizontal ? desired.Columns : desired.Rows);
            var extentCross = itemCross ?? (horizontal ? desired.Rows : desired.Columns);

            // Wrap when the item would exceed the line — strictly greater (LD14); a first item
            // never wraps an empty line, so an oversized item occupies its own line (LD10).
            if (lineHasItems && LayoutMath.Add(lineUsed, extentMain) > lineConstraint)
            {
                maxLine = Math.Max(maxLine, Math.Min(lineUsed, lineConstraint));
                totalCross = LayoutMath.Add(totalCross, lineCross);
                lineUsed = 0;
                lineCross = 0;
            }

            lineUsed = LayoutMath.Add(lineUsed, extentMain);
            lineCross = Math.Max(lineCross, extentCross);
            lineHasItems = true;
        }

        if (lineHasItems)
        {
            maxLine = Math.Max(maxLine, Math.Min(lineUsed, lineConstraint));
            totalCross = LayoutMath.Add(totalCross, lineCross);
        }

        // Desired main = max line extent, cross = Σ line extents (L163), transposed when vertical.
        return horizontal ? new Size(maxLine, totalCross) : new Size(totalCross, maxLine);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var itemMain = horizontal ? ItemWidth : ItemHeight;
        var itemCross = horizontal ? ItemHeight : ItemWidth;
        var lineConstraint = horizontal ? finalSize.Columns : finalSize.Rows;

        var children = Children;
        var lineStart = 0;
        var lineUsed = 0;
        var lineCross = 0;
        var lineHasItems = false;
        var crossOffset = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var desired = child.DesiredSize;
            var extentMain = itemMain ?? (horizontal ? desired.Columns : desired.Rows);
            var extentCross = itemCross ?? (horizontal ? desired.Rows : desired.Columns);

            if (lineHasItems && LayoutMath.Add(lineUsed, extentMain) > lineConstraint)
            {
                ArrangeLine(lineStart, i, crossOffset, lineCross, lineConstraint, horizontal, itemMain);
                crossOffset = Math.Min(crossOffset + lineCross, LayoutMath.MaxExtent);
                lineStart = i;
                lineUsed = 0;
                lineCross = 0;
            }

            lineUsed = LayoutMath.Add(lineUsed, extentMain);
            lineCross = Math.Max(lineCross, extentCross);
            lineHasItems = true;
        }

        if (lineHasItems)
            ArrangeLine(lineStart, children.Count, crossOffset, lineCross, lineConstraint, horizontal, itemMain);

        return finalSize;
    }

    private void ArrangeLine(int start, int endExclusive, int crossOffset, int lineCross, int lineConstraint, bool horizontal, int? itemMain)
    {
        var children = Children;
        var mainOffset = 0;
        for (var i = start; i < endExclusive; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
                continue; // already arranged to an empty rect

            var desired = child.DesiredSize;
            var extentMain = itemMain ?? (horizontal ? desired.Columns : desired.Rows);
            var slotMain = Math.Min(Math.Min(extentMain, lineConstraint), LayoutMath.MaxExtent - mainOffset); // LD10 clamp
            var slotCross = Math.Min(lineCross, LayoutMath.MaxExtent - crossOffset);

            child.Arrange(horizontal
                ? new Rect(mainOffset, crossOffset, slotMain, slotCross)
                : new Rect(crossOffset, mainOffset, slotCross, slotMain));

            mainOffset = Math.Min(mainOffset + slotMain, LayoutMath.MaxExtent);
        }
    }
}
