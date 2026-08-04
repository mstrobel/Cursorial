using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The items panel behind <see cref="BreadcrumbBar"/>'s <b>leading</b> overflow: a single horizontal row of chips
/// that, when the row is too narrow, drops segments from the <b>left</b> — the deepest context (the trailing
/// segments) is what a breadcrumb is for, so the ancestors are what give way. The elided prefix is reachable
/// through the bar's leading ellipsis chip.
/// <para>
/// Unlike the bars <c>ToolbarOverflowPanel</c> this panel does <b>not</b> re-parent anything: the elided chips stay
/// its own children and are simply <see cref="Visibility.Collapsed"/>d, which is the framework's own "paints
/// nothing, measures nothing" contract (the zone painter skips non-<see cref="Visibility.Visible"/> subtrees). That
/// keeps the panel a plain <see cref="Panel"/> — the <see cref="ItemsPresenter"/> keeps its index-aligned container
/// adoption, so no <c>IItemsHostPanel</c> ownership dance is needed.
/// </para>
/// </summary>
public class BreadcrumbBarPanel : Panel
{
    // ─────────────────────────── the natural-width cache (the trap this exists for) ───────────────────────────
    //
    // A Collapsed element measures to Size.Empty — Measure() early-outs before MeasureOverride. So once a chip is
    // elided, its live DesiredSize is 0, and a fold that read live widths would conclude the elided chip "fits",
    // un-elide it, measure it wide again, re-elide it — flapping forever, one chip wide, every layout pass. The
    // fold therefore reads the LAST KNOWN non-collapsed width per child and refreshes an entry only from a real
    // (> 0) measure. (Same defect, same defense as ToolbarOverflowPanel._naturalWidths, whose overflowed items sit
    // in a closed popup band and measure 0 for the same reason.)
    private readonly Dictionary<UIElement, int> _naturalWidths = [];

    // The fold result: the index of the first chip that survives (everything below it is elided). Recomputed in
    // ARRANGE, not measure — measure's available width is unreliable (a Stretch bar is measured to content with an
    // unbounded width and only arranged at its real width, so a measure-time fold would never see the truth and the
    // row would never unfold when the bar widens). ToolbarOverflowPanel folds in arrange for exactly this reason.
    private int _firstVisible;

    // Anti-thrash latch: the fold writes child Visibility, which invalidates measure and re-enters layout. Writing
    // only on a real change already converges (the second pass computes the same set and writes nothing); the latch
    // also skips the recomputation itself when none of its inputs moved.
    private bool _hasFolded;
    private int _lastFinalWidth = -1;
    private int _lastChildCount = -1;
    private int _lastWidthHash;

    // The child constraint the last MeasureOverride used, replayed when arrange has to (re-)measure a chip it just
    // un-elided. Reusing the same constraint keeps the per-child measure cache hot instead of thrashing it.
    private Size _childConstraint = new(LayoutMath.Unbounded, LayoutMath.Unbounded);

    /// <summary>The index of the first non-elided chip (0 when everything fits) — the bar reads it to fill the
    /// leading ellipsis drop-down with the elided ancestors.</summary>
    public int FirstVisibleIndex => _firstVisible;

    /// <summary>The owning bar, resolved up the <c>UIParent</c> bridge (panel → <see cref="ItemsPresenter"/> →
    /// templated parent), mirroring <see cref="ItemsPresenter"/>'s own owner walk.</summary>
    private BreadcrumbBar? OwnerBar
    {
        get
        {
            for (var node = UIParent; node is not null; node = node.UIParent)
            {
                if (node is BreadcrumbBar bar)
                    return bar;
            }

            return null;
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;

        // Chips are measured with an UNBOUNDED width: their natural width is the fold's input, and the fold runs in
        // arrange against the real allocated width.
        _childConstraint = new Size(LayoutMath.Unbounded, availableSize.Rows);

        if (_naturalWidths.Count != children.Count)
            PruneWidthCache(children); // a container left the panel — never pin a dead one in the cache

        var width = 0;
        var height = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(_childConstraint);

            // A 0 width means "collapsed (elided) right now" — keep the last real width; a real measure refreshes it.
            var measured = child.DesiredSize.Columns;
            if (measured > 0)
                _naturalWidths[child] = measured;

            width = LayoutMath.Add(width, NaturalWidthOf(child));
            height = Math.Max(height, child.DesiredSize.Rows);
        }

        // The natural width is the WHOLE trail (elided chips included, from the cache) so an auto-sized bar asks for
        // the room it would need un-elided; a bounded parent then clamps and the arrange fold does the eliding.
        return new Size(width, children.Count == 0 ? 0 : Math.Max(1, height));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;

        if (children.Count == 0)
        {
            _firstVisible = 0;
            _hasFolded = false;
            OwnerBar?.SetOverflowState(0, 0);
            return finalSize;
        }

        // The width signal is a per-child polynomial hash, NOT the sum: a plain sum hides two chips trading widths
        // even though the fold boundary moved, which would wrongly reuse a stale fold.
        var widthHash = 17;
        for (var i = 0; i < children.Count; i++)
            widthHash = widthHash * 31 + NaturalWidthOf(children[i]);

        // The latch's width/count/hash inputs are PROXIES; the invariant they stand for is "every child's
        // Visibility reflects the fold". Regenerated containers (an ItemsSource reset re-adding equal items)
        // arrive Visible with the same count and the same value-based width hash, so the proxies can all
        // coincidentally match — skipping ApplyElision and leaving the new ancestor chips Visible with the
        // stale bounds of their initial un-elided arrange, painted straight over the surviving chips.
        // Verify the invariant itself before trusting the latch.
        if (!(_hasFolded
              && finalSize.Columns == _lastFinalWidth
              && children.Count == _lastChildCount
              && widthHash == _lastWidthHash
              && ElisionHolds(children)))
        {
            _firstVisible = ComputeFirstVisible(children, finalSize.Columns);
            ApplyElision(children);

            _lastFinalWidth = finalSize.Columns;
            _lastChildCount = children.Count;
            _lastWidthHash = widthHash;
            _hasFolded = true;

            // Reported AFTER the Visibility writes so the bar's ellipsis-chip flip and the elision land in the same
            // layout pass. Showing the chip narrows the width the DockPanel hands this panel on the next pass, which
            // can only elide MORE (never fewer) chips — so the feedback loop is monotone and settles at once.
            OwnerBar?.SetOverflowState(_firstVisible, children.Count);
        }

        var offset = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            if (i < _firstVisible)
            {
                child.Arrange(Rect.Empty); // collapsed ⇒ Arrange early-outs to empty bounds
                continue;
            }

            // A chip un-elided by THIS fold has not been measured since (its last measure was the collapsed
            // early-out), so its DesiredSize is still 0 — re-measure it with the pass's own constraint first.
            if (!child.IsMeasureValid)
                child.Measure(_childConstraint);

            var desired = child.DesiredSize;
            var crossSlot = Math.Max(finalSize.Rows, desired.Rows); // LD17 — overflow, don't clip
            child.Arrange(new Rect(offset, 0, desired.Columns, crossSlot));
            offset = LayoutMath.Add(offset, desired.Columns);
        }

        return finalSize;
    }

    // The pack: walk from the RIGHT accumulating natural widths and keep everything that fits. The trailing chip is
    // unconditional — a breadcrumb with no current segment is meaningless, so an over-wide last chip stays and
    // clips at the zone edge rather than leaving an empty bar.
    private int ComputeFirstVisible(UIElementCollection children, int availableWidth)
    {
        var last = children.Count - 1;
        var first = last;
        var total = NaturalWidthOf(children[last]);

        for (var i = last - 1; i >= 0; i--)
        {
            var width = NaturalWidthOf(children[i]);
            if (total + width > availableWidth)
                break;

            total += width;
            first = i;
        }

        return first;
    }

    // True when every child's Visibility already agrees with the current fold — the converged state the
    // anti-thrash latch is allowed to preserve.
    private bool ElisionHolds(UIElementCollection children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var wanted = i < _firstVisible ? Visibility.Collapsed : Visibility.Visible;
            if (children[i].Visibility != wanted)
                return false;
        }

        return true;
    }

    // Writes ONLY where the state differs: a same-value Visibility set is a no-op in the store, but going through
    // SetCurrentValue unconditionally would still churn the (already converged) latch inputs on every pass.
    private void ApplyElision(UIElementCollection children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var wanted = i < _firstVisible ? Visibility.Collapsed : Visibility.Visible;

            if (child.Visibility != wanted)
                child.SetCurrentValue(VisibilityProperty, wanted); // SetCurrentValue keeps a host's own binding alive
        }
    }

    private int NaturalWidthOf(UIElement child)
        => _naturalWidths.TryGetValue(child, out var width) ? width : child.DesiredSize.Columns;

    // Containers come and go with the item collection; drop cache entries for children that left so the dictionary
    // can't pin unrealized containers for the panel's lifetime.
    private void PruneWidthCache(UIElementCollection children)
    {
        List<UIElement>? stale = null;

        foreach (var key in _naturalWidths.Keys)
        {
            if (!children.Contains(key))
                (stale ??= []).Add(key);
        }

        if (stale is null)
            return;

        foreach (var key in stale)
            _naturalWidths.Remove(key);
    }
}
