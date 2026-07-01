using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Bars;

/// <summary>
/// Hosts a <see cref="RibbonTab"/>'s <see cref="RibbonGroup"/>s left-to-right (the guide's <c>.rib-body</c>). Each
/// group renders its own footer + trailing <c>│</c> separator; this panel stacks them horizontally and tells the last
/// group to drop its separator so the band never ends on a stray <c>│</c>.
/// </summary>
public sealed class RibbonBand : Panel
{
    /// <summary>Creates a ribbon band. The selected tab's whole content is ONE Tab stop (aligning with the
    /// <c>Toolbar</c> and the tab strip): Tab lands on the content once — entering the first/remembered control — and
    /// the next Tab exits past the WHOLE content, so Tab moves strip ↔ content ↔ out in single steps instead of through
    /// every control. Within the content, arrow (directional) navigation is the sole continuum: the band is the single
    /// <see cref="DirectionalNavigationMode.Contained"/> directional container, so arrows flow across
    /// <see cref="RibbonGroup"/> boundaries to the geometrically-nearest control (the groups are transparent to both —
    /// no directional mode, and Continue tab so directional collection descends into them). <c>IsTabStop</c> defaults
    /// true, so the Once container is collected as a stop; the band isn't focusable, so entry resolves to a control.</summary>
    public RibbonBand()
    {
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(this, DirectionalNavigationMode.Contained);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;

        var lastVisible = -1;
        for (var i = 0; i < children.Count; i++)
            if (children[i].Visibility != Visibility.Collapsed)
                lastVisible = i;

        var width = 0;
        var height = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child is RibbonGroup group)
                group.SetIsLastInBand(i == lastVisible); // set before measure so the group measures the right │ state

            child.Measure(new Size(LayoutMath.Unbounded, availableSize.Rows));
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
            child.Arrange(new Rect(x, 0, w, finalSize.Rows));
            x = LayoutMath.Add(x, w);
        }

        return finalSize;
    }
}
