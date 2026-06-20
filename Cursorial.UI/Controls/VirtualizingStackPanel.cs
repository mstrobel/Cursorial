using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The panel that makes container virtualization render (design doc §12.6 / control-matrix-virt §V2). It drives
/// realization from its OWN <see cref="MeasureOverride"/> — the sanctioned §5.3 self-mutation (the panel <i>is</i>
/// the element being measured, like WPF/Avalonia) — realizing only the items whose content rows intersect the SCP's
/// band, arranges the realized containers at their TRUE content-row positions inside the full-extent rect, and
/// reports the estimated extent through the V1 <see cref="IScrollContentHost"/> contract (so the
/// <see cref="ScrollContentPresenter"/> publishes a proportional extent without measuring all N items).
/// </summary>
/// <remarks>
/// v1 is <b>uniform-height</b>: every item is assumed <c>avgItemRows</c> tall (seeded at 1, refined from the first
/// measured container). Variable-height sticky caching is V4. The headline invariant: the panel realizes a
/// <b>superset of the band</b> (<c>[BandStartRow, BandStartRow + BandLength)</c> + band-derived slack), so an in-band
/// composite slide (the offset moving within ±K) never re-measures the panel and never re-realizes — zero re-raster,
/// zero churn (invariant 3). Only a band re-anchor (the SCP calls <see cref="IScrollContentHost.InvalidateRealization"/>)
/// re-measures, at the re-anchor cadence.
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

    // Uniform-height estimate.
    private double _avgItemRows = 1;
    private bool _avgMeasured;

    // No-op measure guard — an in-band re-measure with the same band/itemCount/width realizes nothing (VV2.7/VV2.10).
    private bool _hasMeasured;
    private int _lastBandStart = -1;
    private int _lastBandLength = -1;
    private int _lastItemCount = -1;
    private int _lastAvailWidth = -1;
    private Size _cachedDesired;
    private Size _extentEstimate;

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
            foreach (var container in removed)
                Children.Remove(container);
        }
    }

    private void OnContainersStructurallyChanged(object? sender, ContainersChangedEventArgs e)
        => InvalidateMeasure(); // item count / order changed → recompute + reconcile the window next measure

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
        if (_hasMeasured && bandStart == _lastBandStart && bandLength == _lastBandLength
            && itemCount == _lastItemCount && availableSize.Columns == _lastAvailWidth)
            return _cachedDesired;

        var avg = Math.Max(1.0, _avgItemRows);
        var (firstItem, lastItem) = ComputeWindow(bandStart, bandLength, _scrollOwner.BandPadding, itemCount, avg);

        // Reconcile the generator to EXACTLY the window (robust to any prior window / structural shift / keep-alive).
        _generator.UnrealizeOutside(firstItem, lastItem);
        if (lastItem > firstItem)
            _generator.RealizeRange(firstItem, lastItem - firstItem);

        // Measure the realized containers; refine the uniform estimate from the first measured one.
        var maxWidth = 0;
        var refined = false;
        for (var i = firstItem; i < lastItem; i++)
        {
            if (_generator.ContainerFromIndex(i) is not { } container)
                continue;

            container.Measure(new Size(availableSize.Columns, LayoutMath.Unbounded));
            maxWidth = Math.Max(maxWidth, container.DesiredSize.Columns);

            if (!_avgMeasured && container.DesiredSize.Rows > 0)
            {
                _avgItemRows = container.DesiredSize.Rows;
                _avgMeasured = true;
                refined = true;
            }
        }

        avg = Math.Max(1.0, _avgItemRows);

        // Extent: the uniform estimate; a short list (all items realized) reports the EXACT sum so it never lies.
        long extentRows = firstItem == 0 && lastItem == itemCount
            ? RealizedHeightSum(itemCount)
            : (long)Math.Ceiling(itemCount * avg);
        extentRows = Math.Clamp(extentRows, 0, LayoutMath.MaxExtent);

        _extentEstimate = new Size(Math.Max(maxWidth, _viewport.Columns), (int)extentRows);
        _cachedDesired = _extentEstimate;
        _lastBandStart = bandStart;
        _lastBandLength = bandLength;
        _lastItemCount = itemCount;
        _lastAvailWidth = availableSize.Columns;
        _hasMeasured = true;

        // The first real avgItemRows changed the extent + the band ⇒ recompute the window next pass (the LayoutManager
        // fixpoint converges in a bounded number of passes; uniform 1-row items seed correctly and need no refinement).
        if (refined)
        {
            _hasMeasured = false;
            InvalidateMeasure();
        }

        return _cachedDesired;
    }

    private (int First, int Last) ComputeWindow(int bandStart, int bandLength, int bandPadding, int itemCount, double avg)
    {
        if (itemCount <= 0 || bandLength <= 0)
            return (0, 0);

        var slack = (int)Math.Ceiling(bandPadding / avg);
        var first = Math.Max(0, (int)(bandStart / avg) - slack);
        var last = Math.Min(itemCount, (int)Math.Ceiling((bandStart + bandLength) / avg) + slack);
        return (first, Math.Max(first, last));
    }

    private long RealizedHeightSum(int itemCount)
    {
        long sum = 0;
        for (var i = 0; i < itemCount; i++)
            if (_generator!.ContainerFromIndex(i) is { } container)
                sum += container.DesiredSize.Rows;
        return sum;
    }

    // ───────────────────────────── arrange (true content rows) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_isScrollClient || _generator is null)
            return ArrangeUnvirtualized(finalSize);

        var avg = Math.Max(1.0, _avgItemRows);
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var index = _generator.IndexFromContainer(child);
            if (index < 0)
                continue; // not a current container

            var top = (int)Math.Min(index * avg, LayoutMath.MaxExtent);
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

    // Cells from the offset to the next item top (item mode), else a single cell (cell mode).
    int IScrollContentHost.LineStep(int currentOffset, int sign, bool vertical)
    {
        if (!_isLogicalScroll || !vertical)
            return 1;

        var avg = Math.Max(1.0, _avgItemRows);
        var item = (int)(currentOffset / avg);
        var nextTop = (int)((sign >= 0 ? item + 1 : item) * avg);
        var step = Math.Abs(nextTop - currentOffset);
        return Math.Max(1, step);
    }

    int IScrollContentHost.PageStep(int currentOffset, int sign, bool vertical)
    {
        var viewport = Math.Max(1, _viewport.Rows);
        if (!_isLogicalScroll || !vertical)
            return viewport;

        var avg = Math.Max(1.0, _avgItemRows);
        var itemsPerPage = Math.Max(1, (int)(viewport / avg));
        return (int)Math.Min(itemsPerPage * avg, LayoutMath.MaxExtent);
    }

    Rect ILogicalScrollHost.BringItemIntoView(int itemIndex)
    {
        var avg = Math.Max(1.0, _avgItemRows);
        var top = (int)Math.Min(itemIndex * avg, LayoutMath.MaxExtent);
        return new Rect(0, top, Math.Max(1, _viewport.Columns), (int)Math.Ceiling(avg));
    }

    int ILogicalScrollHost.ItemCount => _generator?.ContainerCount ?? 0;

    int ILogicalScrollHost.EstimateItemAt(int offsetRow)
    {
        var avg = Math.Max(1.0, _avgItemRows);
        return Math.Max(0, (int)(offsetRow / avg));
    }
}
