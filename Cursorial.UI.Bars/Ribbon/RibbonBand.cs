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
        FocusManager.SetIsFocusScope(this, true);
        FocusManager.SetRetainsFocus(this, false);
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

    // The deepest tier the fold demotes to (Collapsed = the group-dropdown); a group's own Ribbon.MinDensity may cap it
    // shallower still.
    private const RibbonGroupDensity FoldCap = RibbonGroupDensity.Collapsed;

    // Hysteresis: promote a demoted group back only when the fuller tier fits with this gutter, so a width parked at a
    // tier boundary can't demote/promote flip every frame (the RibbonStripPanel.MinGutter precedent).
    private const int PromoteGutter = 1;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        ApplyDensityFold(finalSize.Columns); // may demote/promote ONE group; the re-invalidation iterates the fixpoint

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

    // The discrete density fold (the design guide's "three discrete tiers, not a fluid slide"). It applies AT MOST ONE
    // tier change per arrange and relies on the LayoutManager fixpoint to iterate: a demote/promote invalidates the
    // group's measure, the band re-measures + re-arranges, and the fold re-runs against the new widths until it
    // settles. Reading the LIVE DesiredSize (the current-tier width) plus each group's frozen Normal width is enough —
    // no destructive per-tier probe. Convergence: each demote strictly shrinks the widest group; the promote gutter
    // opens an asymmetric dead band so the boundary can't oscillate; the tier lattice is finite.
    private void ApplyDensityFold(int available)
    {
        var children = Children;
        var total = 0;
        for (var i = 0; i < children.Count; i++)
            if (children[i] is RibbonGroup g && g.Visibility != Visibility.Collapsed)
                total = LayoutMath.Add(total, g.DesiredSize.Columns);

        if (total > available)
        {
            // DEMOTE the widest demotable group one tier (largest-first sheds the most cells per step; ties go to the
            // RIGHTMOST so the primary left groups hold their full form longest — Office parity).
            RibbonGroup? victim = null;
            var widest = -1;
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is not RibbonGroup g || g.Visibility == Visibility.Collapsed || DeeperTier(g) == g.Density)
                    continue;
                var w = g.DesiredSize.Columns;
                if (w >= widest) // >= ⇒ the rightmost among equal widths wins the tie
                {
                    widest = w;
                    victim = g;
                }
            }

            victim?.SetDensity(DeeperTier(victim));
            return;
        }

        // PROMOTE a demoted group ONE tier back when its shallower tier fits with the gutter — deepest-demoted first
        // (the last group to collapse is the first to recover, the reverse staircase). The shallower tier's width is
        // the frozen sample from the pass that last rendered it (Normal or Compact).
        RibbonGroup? recover = null;
        var recoverTier = RibbonGroupDensity.Normal;
        var deepest = RibbonGroupDensity.Normal;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not RibbonGroup g || g.Visibility == Visibility.Collapsed || g.Density == RibbonGroupDensity.Normal)
                continue;

            var shallower = (RibbonGroupDensity) ((int) g.Density - 1);
            var shallowerWidth = shallower == RibbonGroupDensity.Normal ? g.NaturalWidthNormal : g.NaturalWidthCompact;
            var promotedTotal = LayoutMath.Add(total - g.DesiredSize.Columns, shallowerWidth);
            if (promotedTotal + PromoteGutter <= available && (recover is null || g.Density > deepest))
            {
                recover = g;
                recoverTier = shallower;
                deepest = g.Density;
            }
        }

        recover?.SetDensity(recoverTier);
    }

    // The next tier DOWN a group may occupy: one step deeper (Normal→Compact), clamped to the band's FoldCap and the
    // group's own Ribbon.MinDensity floor. Returns the group's CURRENT tier when it is already as deep as allowed
    // (⇒ "not demotable").
    private static RibbonGroupDensity DeeperTier(RibbonGroup group)
    {
        var floor = (RibbonGroupDensity) Math.Min((int) Ribbon.GetMinDensity(group), (int) FoldCap);
        var next = (RibbonGroupDensity) ((int) group.Density + 1);
        return (int) next <= (int) floor ? next : group.Density;
    }
}
