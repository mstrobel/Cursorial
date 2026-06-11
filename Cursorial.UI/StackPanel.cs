using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// Stacks children sequentially along <see cref="Orientation"/> (default
/// <see cref="Orientation.Vertical"/>), with <see cref="Spacing"/> cells between consecutive
/// non-collapsed children (LD7). Children are measured <see cref="LayoutMath.Unbounded"/> on the
/// stacking axis; the cross-axis slot is <c>max(panel's arranged cross size, child desired)</c> —
/// a child wider than the panel overflows, and clipping is the zone edge's job (LD17).
/// </summary>
public class StackPanel : Panel
{
    /// <summary>The stacking axis (default <see cref="Orientation.Vertical"/>). <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<StackPanel, Orientation>(nameof(Orientation), defaultValue: Orientation.Vertical);

    /// <summary>
    /// Cells of space between consecutive non-collapsed children (default 0; LD7). Negative values
    /// coerce to 0 (the <c>ItemWidth</c>/<c>Margin</c> precedent — v1 layout has no signed
    /// geometry, doc §5.2; a negative gap would wrap the ushort-backed arrange <c>Rect</c>).
    /// <c>[AffectsMeasure]</c>
    /// </summary>
    public static readonly StyledProperty<int> SpacingProperty =
        UIProperty.Register<StackPanel, int>(nameof(Spacing), coerce: static (_, value) => Math.Max(0, value));

    static StackPanel()
    {
        AffectsMeasure<StackPanel>(OrientationProperty, SpacingProperty);
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <inheritdoc cref="SpacingProperty"/>
    public int Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var vertical = Orientation == Orientation.Vertical;
        var childConstraint = vertical
            ? new Size(availableSize.Columns, LayoutMath.Unbounded)
            : new Size(LayoutMath.Unbounded, availableSize.Rows);

        var main = 0;
        var cross = 0;
        var visibleCount = 0;
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(childConstraint);
            if (child.Visibility == Visibility.Collapsed)
                continue; // no slot, no spacing gap (LD7)

            visibleCount++;
            var desired = child.DesiredSize;
            main = LayoutMath.Add(main, vertical ? desired.Rows : desired.Columns);
            cross = Math.Max(cross, vertical ? desired.Columns : desired.Rows);
        }

        if (visibleCount > 1)
            main = LayoutMath.Add(main, Spacing * (visibleCount - 1));

        return vertical ? new Size(cross, main) : new Size(main, cross);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var vertical = Orientation == Orientation.Vertical;
        var spacing = Spacing;
        var offset = 0;
        var anyVisible = false;
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            if (anyVisible)
                offset += spacing;
            anyVisible = true;

            var desired = child.DesiredSize;
            if (vertical)
            {
                var crossSlot = Math.Max(finalSize.Columns, desired.Columns); // LD17 — overflow, don't clip
                child.Arrange(new Rect(0, offset, crossSlot, desired.Rows));
                offset += desired.Rows;
            }
            else
            {
                var crossSlot = Math.Max(finalSize.Rows, desired.Rows);
                child.Arrange(new Rect(offset, 0, desired.Columns, crossSlot));
                offset += desired.Columns;
            }
        }

        return finalSize;
    }
}
