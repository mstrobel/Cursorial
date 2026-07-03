using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The panel that makes container virtualization render (design doc §12.6 / control-matrix-virt §V2+§V4). It drives
/// realization from its OWN <see cref="MeasureOverride"/> — the sanctioned §5.3 self-mutation (the panel <i>is</i>
/// the element being measured, like WPF/Avalonia) — realizing only the items whose content rows intersect the SCP's
/// band, arranges the realized containers at their TRUE content-row positions inside the full-extent rect, and
/// reports the estimated extent through the V1 <see cref="IScrollContentHost"/> contract (so the
/// <see cref="ScrollContentPresenter"/> publishes a proportional extent without measuring all N items).
/// </summary>
/// <remarks>
/// <para><b>Variable height (§V4).</b> Each item's measured row count is kept in a <b>sticky per-item cache</b>
/// (<see cref="_heights"/>) that survives unrealization — only a <i>never</i>-measured item falls back to the running
/// <see cref="Estimate"/>. A dense prefix-sum (<see cref="_prefix"/>, rebuilt lazily and clamped → monotone) gives the
/// O(1) cumulative top of any item, the O(1) total extent, and an O(log n) <see cref="EstimateItemAtCore"/> binary
/// search (offset → item). A uniform list degenerates to V2's exact behavior (every effective height equal).</para>
/// <para>The headline invariant is unchanged: the panel realizes a <b>superset of the band</b>
/// (<c>[BandStartRow, BandStartRow + BandLength)</c> + band-derived slack), so an in-band composite slide (the offset
/// moving within ±K) never re-measures the panel, never rebuilds the prefix, and never re-realizes — zero re-raster,
/// zero churn (invariant 3). Only a band re-anchor (the SCP calls
/// <see cref="IScrollContentHost.InvalidateRealization"/>) re-measures, at the re-anchor cadence. A measured height
/// that differs from the cache busts the no-op guard for ONE more pass; stickiness makes the next pass stable, so the
/// refine→invalidate loop converges well under the <c>LayoutManager</c> 16-pass fixpoint.</para>
/// </remarks>
public sealed class VirtualizingStackPanel : VirtualizingPanel, ILogicalScrollHost
{
    private ItemsControl? _owner;
    private ItemContainerGenerator? _generator;
    private bool _isScrollClient;
    private bool _isLogicalScroll;
    private VirtualizationMode _mode;

    // ILogicalScrollHost state (set by the SCP via the ItemsPresenter forwarding).
    private ScrollContentPresenter? _scrollOwner;
    private bool _canScrollHorizontally;
    private bool _canScrollVertically = true;
    private Size _viewport;

    // §V4 sticky per-item measured-height cache: item index → measured rows. Populated at measure, kept across
    // unrealization ("estimate only the truly-unrealized"), re-keyed on a structural insert/remove, cleared on
    // Move/Reset/disconnect. _measuredSum/_measuredCount are the running totals behind Estimate.
    private readonly Dictionary<int, int> _heights = new();
    private long _measuredSum;
    private int _measuredCount;

    // Dense prefix-sum of effective heights — _prefix[i] is the cell-row TOP of item i (Σ effective-height(j<i),
    // effective = measured-or-Estimate). Rebuilt lazily (gated by _prefixDirty + the measured item count); monotone
    // non-decreasing and clamped to MaxExtent by construction.
    private long[] _prefix = [];
    private bool _prefixDirty = true;
    private int _prefixCount = -1;

    // The running mean measured height (seeded 1 before any measurement) — the fallback for never-measured items.
    private double Estimate => _measuredCount > 0 ? (double) _measuredSum / _measuredCount : 1.0;

    // No-op measure guard — an in-band re-measure with the same band/itemCount/width realizes nothing (VV2.7/VV2.10).
    private bool _hasMeasured;
    private int _lastBandStart = -1;
    private int _lastBandLength = -1;
    private int _lastItemCount = -1;
    private int _lastAvailWidth = -1;
    private Size _cachedDesired;
    private Size _extentEstimate;

    // Session-scoped realization window (the convergence fix). The window's first/last item are recomputed from the
    // prefix only on a REAL re-anchor (bandStart/bandLength change); across the refine→re-measure passes at one scroll
    // position they only EXPAND to keep covering the band as measurements refine the prefix. Monotone-within-a-session
    // (first only shrinks, last only grows, bounded by [0, itemCount]) ⇒ the window can't crawl/oscillate, so the loop
    // converges well under the LayoutManager fixpoint even on a bimodal list where re-deriving the window from the
    // (band-perturbed) global estimate each pass would chase its own tail. A genuine scroll resets the session to the
    // now-refined prefix → a tight window again. (Tight for realistic lists; the rare first-visit bimodal-deep-scroll
    // case may transiently over-realize until the cache fills — proper offset-anchoring is a V5 follow-up.)
    private int _sessionBandStart = -1;
    private int _sessionBandLength = -1;
    private int _sessionFirst;
    private int _sessionLast;

    // ───────────────────────────── owner wiring (V2) ─────────────────────────────

    internal override void OnItemsHostConnected(ItemsControl owner)
    {
        _owner = owner;
        _generator = owner.ItemContainerGenerator;
        _isScrollClient = GetIsVirtualizing(owner);
        _mode = GetVirtualizationMode(owner);
        _isLogicalScroll = GetScrollUnit(owner) == ScrollUnit.Item;

        if (!_isScrollClient)
            return;

        _generator.EnableVirtualization(_mode);
        _generator.ContainersRealizedChanged += OnContainersRealizedChanged; // materialization → adopt/release Children
        _generator.ContainersChanged += OnContainersStructurallyChanged;     // structural item change → re-realize the band
    }

    internal override void OnItemsHostDisconnected()
    {
        if (_generator is not null)
        {
            _generator.ContainersRealizedChanged -= OnContainersRealizedChanged;
            _generator.ContainersChanged -= OnContainersStructurallyChanged;
        }

        ClearHeights();
        _prefix = [];
        _prefixDirty = true;
        _prefixCount = -1;
        _generator = null;
        _owner = null;
    }

    private void OnContainersRealizedChanged(object? sender, ContainersChangedEventArgs e)
    {
        if (e.Action == ContainersChangedAction.Realized && e.RealizedContainers is { } realized)
        {
            foreach (var container in realized)
                Children.Add(container); // visual-only adoption (IsItemsHost); arranged by item index in ArrangeOverride
            InvalidateArrange();
        }
        else if (e.Action == ContainersChangedAction.Unrealized && e.RemovedContainers is { } removed)
        {
            // NOTE: the height cache is NOT touched here — it is sticky across unrealization (VV4.1).
            foreach (var container in removed)
                Children.Remove(container);
        }
    }

    private void OnContainersStructurallyChanged(object? sender, ContainersChangedEventArgs e)
    {
        // Keep the sticky height cache aligned with the item indices (VV4.10): an insert/remove shifts the cached
        // heights; a Move/Reset clears them (re-measured). Then bust the no-op guard — a Move / equal-count Replace
        // changes item ORDER or IDENTITY but not the guard key (bandStart/bandLength/itemCount/width), so without this
        // the next measure short-circuits and never re-runs UnrealizeOutside/RealizeRange (blank band rows).
        switch (e.Action)
        {
            case ContainersChangedAction.Realized:   // structural insert at StartIndex, Count items
                ShiftHeights(e.StartIndex, e.Count);
                break;
            case ContainersChangedAction.Unrealized: // structural remove at StartIndex, Count items
                ShiftHeights(e.StartIndex, -e.Count);
                break;
            default:                                 // Moved / Reset → clear (rare; re-measured next pass)
                ClearHeights();
                break;
        }

        _prefixDirty = true;
        _hasMeasured = false;
        InvalidateMeasure();
    }

    // ───────────────────────────── measure (the realization driver — §5.3) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_isScrollClient || _generator is null || _scrollOwner is null)
            return MeasureUnvirtualized(availableSize);

        var itemCount = _generator.ContainerCount;
        var bandStart = _scrollOwner.BandStartRow;
        var bandLength = _scrollOwner.BandLength;

        // No-op guard: nothing that moves the realization window changed ⇒ realize nothing, return the cached size.
        // (The prefix is NOT rebuilt here — an in-band slide is zero re-measure / zero prefix churn — VV4.12.)
        if (_hasMeasured && bandStart == _lastBandStart && bandLength == _lastBandLength
            && itemCount == _lastItemCount && availableSize.Columns == _lastAvailWidth)
            return _cachedDesired;

        EnsurePrefix(itemCount);
        var (firstItem, lastItem) = ComputeWindow(bandStart, bandLength, itemCount);

        // Reconcile the generator to EXACTLY the window (robust to any prior window / structural shift / keep-alive).
        _generator.UnrealizeOutside(firstItem, lastItem);
        if (lastItem > firstItem)
            _generator.RealizeRange(firstItem, lastItem - firstItem);

        // Measure the realized containers; record each height into the sticky cache. A height that DIFFERS from the
        // cache is a refinement that may move the window / extent ⇒ one more pass (converges once all band items are
        // measured, since stickiness makes the next pass stable).
        var maxWidth = 0;
        var refined = false;
        for (var i = firstItem; i < lastItem; i++)
        {
            if (_generator.ContainerFromIndex(i) is not { } container)
                continue;

            container.Measure(new Size(availableSize.Columns, LayoutMath.Unbounded));
            maxWidth = Math.Max(maxWidth, container.DesiredSize.Columns);

            if (RecordHeight(i, container.DesiredSize.Rows))
                refined = true;
        }

        if (refined)
        {
            _prefixDirty = true;
            EnsurePrefix(itemCount);
        }

        var extentRows = itemCount > 0 ? _prefix[itemCount] : 0;
        _extentEstimate = new Size(Math.Max(maxWidth, _viewport.Columns), (int) extentRows);
        _cachedDesired = _extentEstimate;
        _lastBandStart = bandStart;
        _lastBandLength = bandLength;
        _lastItemCount = itemCount;
        _lastAvailWidth = availableSize.Columns;
        _hasMeasured = true;

        // A refined height changed the prefix (extent + the band→item map) ⇒ recompute the window next pass. The
        // LayoutManager fixpoint converges in a bounded number of passes — a uniform list seeds correctly and never
        // refines after the first measurement; a heterogeneous list settles once its band items are all cached.
        if (refined)
        {
            _hasMeasured = false;
            InvalidateMeasure();
        }

        return _cachedDesired;
    }

    private (int First, int Last) ComputeWindow(int bandStart, int bandLength, int itemCount)
    {
        if (itemCount <= 0 || bandLength <= 0)
        {
            _sessionBandStart = -1;
            return (0, 0);
        }

        // The window that covers the band per the CURRENT prefix (+1 on last to include the item straddling the band
        // end) — a superset of the band even with variable heights (VV2.6). The band ALREADY spans
        // viewport + 2·bandPadding rows (its own smooth-scroll margin), so only a small ESTIMATE-ERROR slack is
        // needed here, not a second bandPadding-worth of items (which ~doubled realization); the session expand-only
        // window (the convergence fix) backstops any residual under-coverage on the next refine pass.
        const int slack = 2;
        var coverFirst = Math.Max(0, EstimateItemAtCore(bandStart) - slack);
        var coverLast = Math.Max(coverFirst, Math.Min(itemCount, EstimateItemAtCore(bandStart + bandLength) + slack + 1));

        if (bandStart != _sessionBandStart || bandLength != _sessionBandLength)
        {
            // A real re-anchor (genuine scroll): seed the session window from the current (refined) prefix.
            _sessionBandStart = bandStart;
            _sessionBandLength = bandLength;
            _sessionFirst = coverFirst;
            _sessionLast = coverLast;
        }
        else
        {
            // A refine pass at the same scroll position: EXPAND-ONLY to keep covering as the prefix refines — never
            // re-derive from scratch (that's the crawl). Monotone ⇒ bounded ⇒ converges; coverage preserved.
            _sessionFirst = Math.Min(_sessionFirst, coverFirst);
            _sessionLast = Math.Max(_sessionLast, coverLast);
        }

        return (_sessionFirst, _sessionLast);
    }

    // Record a measured height into the sticky cache; returns true when it CHANGED the cache (a refinement that can
    // move the extent / window). A stable re-measure of an already-cached item returns false (the convergence hook).
    private bool RecordHeight(int index, int rows)
    {
        if (rows < 0)
            rows = 0;

        if (_heights.TryGetValue(index, out var old))
        {
            if (old == rows)
                return false;
            _measuredSum += rows - old;
            _heights[index] = rows;
            return true;
        }

        _heights[index] = rows;
        _measuredSum += rows;
        _measuredCount++;
        return true;
    }

    // Rebuild the dense prefix-sum from the sticky cache + the current estimate (effective height = measured-or-
    // estimate). Cumulative double accumulation, rounded per index and clamped to MaxExtent → monotone. Skipped when
    // clean for the same item count (so an in-band slide that bypasses measure keeps the prior prefix).
    private void EnsurePrefix(int itemCount)
    {
        if (!_prefixDirty && _prefixCount == itemCount)
            return;

        if (_prefix.Length < itemCount + 1)
            _prefix = new long[itemCount + 1];

        // Floor the NEVER-measured fallback at 1 row (a genuinely measured 0-row item still uses its true 0 below).
        // Without this, an all-Collapsed / empty-content list drives Estimate to 0, collapsing the whole prefix to
        // zero — EstimateItemAtCore then maps every offset to the tail and ComputeWindow realizes ALL N items
        // (virtualization defeated). The floor keeps never-measured items in distinct prefix slots.
        var est = Math.Max(1.0, Estimate);
        double acc = 0;
        _prefix[0] = 0;
        for (var i = 0; i < itemCount; i++)
        {
            acc += _heights.TryGetValue(i, out var h) ? h : est;
            _prefix[i + 1] = (long) Math.Min(acc + 0.5, LayoutMath.MaxExtent);
        }

        _prefixCount = itemCount;
        _prefixDirty = false;
    }

    // The cumulative top of item `index` (clamped). Assumes EnsurePrefix has run for the current item count.
    private long OffsetOf(int index)
    {
        var itemCount = _generator?.ContainerCount ?? 0;
        EnsurePrefix(itemCount);
        index = Math.Clamp(index, 0, itemCount);
        return _prefix[index];
    }

    // The item whose [top, top+height) contains `offsetRow` — the largest i with _prefix[i] ≤ offsetRow. O(log n).
    private int EstimateItemAtCore(int offsetRow)
    {
        var itemCount = _generator?.ContainerCount ?? 0;
        if (itemCount <= 0)
            return 0;

        EnsurePrefix(itemCount);
        if (offsetRow <= 0)
            return 0;

        int lo = 0, hi = itemCount; // search in [0, itemCount]; find the largest index with prefix ≤ offset
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_prefix[mid] <= offsetRow)
                lo = mid;
            else
                hi = mid - 1;
        }

        var result = Math.Min(lo, itemCount - 1);

        // Resolve a zero-height (measured-Collapsed) tie to the FIRST item attaining this prefix value, so
        // EstimateItemAt(OffsetOf(i)) == i (the VV4.4/VV4.5 inverse): a run of 0-row items shares one prefix value,
        // and the offset of that run's top must map back to the run's first item, not its tail.
        while (result > 0 && _prefix[result - 1] == _prefix[result])
            result--;

        return result;
    }

    private void ShiftHeights(int at, int delta)
    {
        if (delta == 0 || _heights.Count == 0)
            return;

        var rekeyed = new Dictionary<int, int>(_heights.Count);
        _measuredSum = 0;
        _measuredCount = 0;

        foreach (var (key, value) in _heights)
        {
            int newKey;
            if (delta > 0)
            {
                newKey = key >= at ? key + delta : key; // insert: shift the tail up; [at, at+delta) become unmeasured
            }
            else
            {
                var removed = -delta;
                if (key >= at && key < at + removed)
                    continue;                            // dropped by the remove
                newKey = key >= at + removed ? key - removed : key;
            }

            rekeyed[newKey] = value;
            _measuredSum += value;
            _measuredCount++;
        }

        _heights.Clear();
        foreach (var (key, value) in rekeyed)
            _heights[key] = value;
    }

    private void ClearHeights()
    {
        _heights.Clear();
        _measuredSum = 0;
        _measuredCount = 0;
    }

    // ───────────────────────────── arrange (true content rows) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_isScrollClient || _generator is null)
            return ArrangeUnvirtualized(finalSize);

        var itemCount = _generator.ContainerCount;
        EnsurePrefix(itemCount);

        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var index = _generator.IndexFromContainer(child);
            if (index < 0)
                continue; // not a current container

            var top = (int) Math.Min(_prefix[Math.Clamp(index, 0, itemCount)], LayoutMath.MaxExtent);
            var width = Math.Max(finalSize.Columns, child.DesiredSize.Columns);
            child.Arrange(new Rect(0, top, width, child.DesiredSize.Rows));
        }

        return finalSize;
    }

    // ───────────────────────────── unvirtualized fallback (IsVirtualizing off / no SCP host) ─────────────────────────────

    // Behaves like a vertical StackPanel so a VirtualizingStackPanel placed where virtualization isn't wired still works.
    private Size MeasureUnvirtualized(Size availableSize)
    {
        var main = 0;
        var cross = 0;
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(availableSize.Columns, LayoutMath.Unbounded));
            if (child.Visibility == Visibility.Collapsed)
                continue;
            main = LayoutMath.Add(main, child.DesiredSize.Rows);
            cross = Math.Max(cross, child.DesiredSize.Columns);
        }

        return new Size(cross, main);
    }

    private Size ArrangeUnvirtualized(Size finalSize)
    {
        var offset = 0;
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(Rect.Empty);
                continue;
            }

            var crossSlot = Math.Max(finalSize.Columns, child.DesiredSize.Columns);
            child.Arrange(new Rect(0, offset, crossSlot, child.DesiredSize.Rows));
            offset += child.DesiredSize.Rows;
        }

        return finalSize;
    }

    // ───────────────────────────── ILogicalScrollHost ─────────────────────────────

    bool IScrollContentHost.IsScrollClient => _isScrollClient;

    bool IScrollContentHost.IsLogicalScroll => _isLogicalScroll;

    ScrollContentPresenter? IScrollContentHost.ScrollOwner
    {
        get => _scrollOwner;
        set => _scrollOwner = value;
    }

    bool IScrollContentHost.CanScrollHorizontally
    {
        get => _canScrollHorizontally;
        set => _canScrollHorizontally = value;
    }

    bool IScrollContentHost.CanScrollVertically
    {
        get => _canScrollVertically;
        set => _canScrollVertically = value;
    }

    Size IScrollContentHost.GetExtent() => _extentEstimate;

    void IScrollContentHost.SetViewport(Size viewport)
    {
        if (viewport == _viewport)
            return;

        _viewport = viewport;
        InvalidateMeasure(); // the viewport drives the band size ⇒ re-realize next measure
    }

    void IScrollContentHost.InvalidateRealization() => InvalidateMeasure();

    // Cells from the offset to the adjacent item top (item mode), else a single cell (cell mode): down → the NEXT
    // item's top; up → the PREVIOUS item's top when already at an exact item boundary, else THIS item's top (so a
    // partway-scrolled item snaps up to its own top first, then steps item-by-item — without the boundary case, up
    // at an item top would degenerate to a 1-cell nudge). The ScrollViewer applies the sign; this returns magnitude.
    int IScrollContentHost.LineStep(int currentOffset, int sign, bool vertical)
    {
        if (!_isLogicalScroll || !vertical)
            return 1;

        var item = EstimateItemAtCore(currentOffset);
        var atBoundary = OffsetOf(item) == currentOffset;
        var targetItem = sign >= 0 ? item + 1 : (atBoundary && item > 0 ? item - 1 : item);
        var step = (int) Math.Abs(OffsetOf(targetItem) - currentOffset);
        return Math.Max(1, step);
    }

    int IScrollContentHost.PageStep(int currentOffset, int sign, bool vertical)
    {
        var viewport = Math.Max(1, _viewport.Rows);
        if (!_isLogicalScroll || !vertical)
            return viewport;

        // A page lands on the item top ~one viewport away (variable-height aware via the prefix-sum inverse).
        var targetItem = EstimateItemAtCore(currentOffset + sign * viewport);
        var step = (int) Math.Abs(OffsetOf(targetItem) - currentOffset);
        return Math.Max(1, step);
    }

    Rect ILogicalScrollHost.BringItemIntoView(int itemIndex)
    {
        var top = (int) Math.Min(OffsetOf(itemIndex), LayoutMath.MaxExtent);
        var height = _heights.TryGetValue(itemIndex, out var h) ? h : (int) Math.Ceiling(Math.Max(1.0, Estimate));
        return new Rect(0, top, Math.Max(1, _viewport.Columns), Math.Max(1, height));
    }

    int ILogicalScrollHost.ItemCount => _generator?.ContainerCount ?? 0;

    int ILogicalScrollHost.EstimateItemAt(int offsetRow) => EstimateItemAtCore(offsetRow);
}
