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

    // Reused fold scratch (few groups per band — allocate once).
    private readonly List<RibbonGroup> _foldGroups = [];
    private readonly List<RibbonGroupDensity> _foldTiers = [];

    // The discrete density fold (the design guide's "three discrete tiers, not a fluid slide"). SINGLE PASS: it
    // computes every group's target tier by a greedy largest-first demotion over each group's STABLE per-tier widths
    // (RibbonGroup.TierWidth — a frozen Normal measure + an analytic Compact estimate + an analytic Collapsed
    // constant), then assigns them. It deliberately does NOT read the live post-restyle DesiredSize: the
    // :density-compact face-shrink is a DEFERRED restyle that only materializes next frame, so a live-width fold would
    // never see the compacted width within one frame and would skip Compact straight to Collapsed. Because the tier
    // widths are stable, the fold recomputes the SAME targets every pass (SetDensity is a no-op once assigned), so it
    // converges immediately; a resize recomputes fresh from all-Normal, restoring groups on widen with no hysteresis
    // dance (the discrete boundary is a clean 1-cell flip, not an oscillation, since the inputs don't move).
    private void ApplyDensityFold(int available)
    {
        var children = Children;
        _foldGroups.Clear();
        _foldTiers.Clear();

        var total = 0;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is RibbonGroup g && g.Visibility != Visibility.Collapsed)
            {
                _foldGroups.Add(g);
                _foldTiers.Add(RibbonGroupDensity.Normal);
                total = LayoutMath.Add(total, g.TierWidth(RibbonGroupDensity.Normal));
            }
        }

        while (total > available)
        {
            // Demote the widest demotable group one tier (largest-first sheds the most cells per step; ties go to the
            // RIGHTMOST so the primary left groups hold their full form longest — Office parity).
            var victim = -1;
            var widest = -1;
            for (var i = 0; i < _foldGroups.Count; i++)
            {
                var cur = _foldTiers[i];
                if (DeeperTier(_foldGroups[i], cur) == cur)
                    continue; // at its MinDensity / FoldCap floor — not demotable
                var w = _foldGroups[i].TierWidth(cur);
                if (w >= widest)
                {
                    widest = w;
                    victim = i;
                }
            }

            if (victim < 0)
                break; // nothing left to demote — the collapsed row clips at the zone edge

            var oldTier = _foldTiers[victim];
            var newTier = DeeperTier(_foldGroups[victim], oldTier);
            _foldTiers[victim] = newTier;
            total += _foldGroups[victim].TierWidth(newTier) - _foldGroups[victim].TierWidth(oldTier);
        }

        for (var i = 0; i < _foldGroups.Count; i++)
            _foldGroups[i].SetDensity(_foldTiers[i]); // guarded no-op when the tier is unchanged
    }

    // The next tier DOWN from `current`: one step deeper (Normal→Compact→Collapsed), clamped to the band's FoldCap and
    // the group's own Ribbon.MinDensity floor. Returns `current` when it is already as deep as allowed (not demotable).
    private static RibbonGroupDensity DeeperTier(RibbonGroup group, RibbonGroupDensity current)
    {
        var floor = (RibbonGroupDensity) Math.Min((int) Ribbon.GetMinDensity(group), (int) FoldCap);
        var next = (RibbonGroupDensity) ((int) current + 1);
        return (int) next <= (int) floor ? next : current;
    }
}
