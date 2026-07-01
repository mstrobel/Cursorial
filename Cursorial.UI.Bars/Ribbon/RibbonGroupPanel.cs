using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

/// <summary>
/// A <see cref="RibbonGroup"/>'s internal layout (the guide's <c>.gr</c> controls row): a single horizontal row of bar
/// controls of mixed size. The row height is the tallest control (a <see cref="RibbonButtonSize.Large"/> glyph-over-
/// label spans it); shorter controls (small/medium buttons, combos, galleries) are vertically centered in the row.
/// </summary>
public sealed class RibbonGroupPanel : Panel
{
    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        var width = 0;
        var height = 1;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(LayoutMath.Unbounded, LayoutMath.Unbounded));
            if (child.Visibility == Visibility.Collapsed)
                continue;

            width = LayoutMath.Add(width, child.DesiredSize.Columns);
            height = Math.Max(height, child.DesiredSize.Rows);
        }

        return new Size(width, height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        var h = finalSize.Rows;
        var x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var w = child.DesiredSize.Columns;
            var rows = Math.Min(child.DesiredSize.Rows, h);
            var y = Math.Max(0, (h - rows) / 2); // center a short control in the band height; a Large control fills it
            child.Arrange(new Rect(x, y, w, rows));
            x = LayoutMath.Add(x, w);
        }

        return finalSize;
    }
}
