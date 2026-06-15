using Cursorial.Rendering;

namespace Cursorial.UI;

public abstract partial class UIElement
{
    private Size _desiredSize;
    private Size _naturalSize; // post-MinMax, pre-margin (WPF's _unclippedDesiredSize) — arrange content sizing (L225)
    private LayoutRect _bounds;
    private Size _lastMeasureConstraint;
    private Rect _lastArrangeRect;
    private bool _hasMeasured;
    private bool _hasArranged;
    private bool _measuring;

    /// <summary>The layout-manager queue this element is enqueued in for measure (lazy-deletion marker).</summary>
    internal LayoutManager? MeasureQueuedIn;

    /// <summary>The layout-manager queue this element is enqueued in for arrange (lazy-deletion marker).</summary>
    internal LayoutManager? ArrangeQueuedIn;

    /// <summary>
    /// The size this element desires including <see cref="Margin"/> (the WPF rule, clamped ≥ 0 per
    /// axis — negative margins can shrink it below the content size, LD19), produced by
    /// <see cref="Measure"/>. May exceed the measure constraint — parents own overflow policy
    /// (LD2); never <see cref="LayoutMath.Unbounded"/> on any axis.
    /// </summary>
    public Size DesiredSize => _desiredSize;

    /// <summary>
    /// The parent-relative arranged bounds, produced by <see cref="Arrange"/>. The origin is
    /// <b>signed</b> (LD19): negative <see cref="Margin"/> components may place an element
    /// above/left of its parent's slot; cells outside the zone clip at composite time.
    /// </summary>
    public LayoutRect Bounds => _bounds;

    /// <summary>Whether the element's measure state is current.</summary>
    public bool IsMeasureValid { get; private set; }

    /// <summary>Whether the element's arrange state is current.</summary>
    public bool IsArrangeValid { get; private set; }

    /// <summary>Whether the element has ever been arranged (its initial layout has settled at least once — §9.5 transitions).</summary>
    internal bool HasBeenArranged => _hasArranged;

    /// <summary>The constraint the element was last measured with (valid when <see cref="HasMeasureConstraint"/>).</summary>
    internal Size LastMeasureConstraint => _lastMeasureConstraint;

    /// <summary>Whether the element has ever been measured.</summary>
    internal bool HasMeasureConstraint => _hasMeasured;

    /// <summary>The slot rect the element was last arranged into (valid when <see cref="HasArrangeRect"/>).</summary>
    internal Rect LastArrangeRect => _lastArrangeRect;

    /// <summary>Whether the element has ever been arranged.</summary>
    internal bool HasArrangeRect => _hasArranged;

    // ───────────────────────────── measure ─────────────────────────────

    /// <summary>
    /// Measures the element (design doc §5.3): Collapsed early-out (precedes
    /// <see cref="ApplyTemplate"/>) → constraint cache hit (keys the <em>raw</em>
    /// <paramref name="availableSize"/>, LD6) → <see cref="ApplyTemplate"/> → margin-deflate
    /// (negative margins <b>enlarge</b> the inner constraint — LD19) + min/max clamp (explicit
    /// <see cref="Width"/>/<see cref="Height"/> fold into both min and max — the WPF <c>MinMax</c>
    /// rule, LD1) → <see cref="MeasureOverride"/> → clamp → <c>DesiredSize = natural + Margin</c>
    /// (saturating, floored at 0 per axis) → <see cref="OnChildDesiredSizeChanged"/> on the parent
    /// when the desired size changed.
    /// </summary>
    public void Measure(Size availableSize)
    {
        VerifyAccess();

        if (Visibility == Visibility.Collapsed)
        {
            // Early-out precedes ApplyTemplate: no template / name scope until the first
            // non-collapsed measure (documented for S8 / XAML FindName).
            _lastMeasureConstraint = availableSize;
            _hasMeasured = true;
            IsMeasureValid = true;

            if (SetAndRaise(DesiredSizeProperty, ref _desiredSize, Size.Empty))
                NotifyParentDesiredSizeChanged();

            return;
        }

        if (IsMeasureValid && _hasMeasured && availableSize == _lastMeasureConstraint)
            return; // cache hit — before ApplyTemplate and before any override (LD6)

        ApplyTemplate();

        _lastMeasureConstraint = availableSize;
        _hasMeasured = true;

        // Validity is set BEFORE the override so an in-flight self-invalidation (the oscillator
        // pattern, or a property write from within MeasureOverride) sticks and re-queues.
        IsMeasureValid = true;

        var margin = Margin;
        var inner = LayoutMath.Sub(availableSize, margin);
        var (minWidth, maxWidth, minHeight, maxHeight) = ResolveMinMax();

        var constraint = new Size(
            LayoutMath.Clamp(inner.Columns, minWidth, maxWidth),
            LayoutMath.Clamp(inner.Rows, minHeight, maxHeight));

        Size natural;
        _measuring = true;

        try
        {
            natural = MeasureOverride(constraint);
        }
        finally
        {
            _measuring = false;
        }

        if (LayoutMath.IsUnbounded(natural.Columns) || LayoutMath.IsUnbounded(natural.Rows))
        {
            LayoutDiagnostics.Emit(
                LayoutDiagnosticKind.DesiredSizeUnbounded, this,
                $"MeasureOverride on '{GetType().Name}' returned Unbounded ({natural}); DesiredSize is never " +
                $"Unbounded (LD2) — clamped to MaxExtent.");

            natural = new Size(
                LayoutMath.IsUnbounded(natural.Columns) ? LayoutMath.MaxExtent : natural.Columns,
                LayoutMath.IsUnbounded(natural.Rows) ? LayoutMath.MaxExtent : natural.Rows);
        }

        natural = new Size(
            LayoutMath.Clamp(natural.Columns, minWidth, maxWidth),
            LayoutMath.Clamp(natural.Rows, minHeight, maxHeight));

        // Cached for Arrange (L225): with a negative margin the DesiredSize floor can clamp to 0,
        // and reconstructing content size as `DesiredSize − margin` would "recover" |margin| —
        // inflating the element past its natural size. WPF caches _unclippedDesiredSize the same way.
        _naturalSize = natural;

        var desired = LayoutMath.Add(natural, margin);

        if (LayoutMath.IsUnbounded(desired.Columns) || LayoutMath.IsUnbounded(desired.Rows))
        {
            // A pathological Min/Max (e.g. MinWidth = Unbounded) or saturated overflow; LD2 holds.
            desired = new Size(
                LayoutMath.IsUnbounded(desired.Columns) ? LayoutMath.MaxExtent : desired.Columns,
                LayoutMath.IsUnbounded(desired.Rows) ? LayoutMath.MaxExtent : desired.Rows);
        }

        // A measure that actually ran invalidates the element's own arrange: child positions are a
        // function of children's desired sizes, so ArrangeOverride must re-run even when the slot
        // is unchanged (the parent-desired-unchanged reflow case).
        InvalidateArrangeAfterMeasure();

        if (SetAndRaise(DesiredSizeProperty, ref _desiredSize, desired))
            NotifyParentDesiredSizeChanged();
    }

    /// <summary>
    /// The element's content measurement. The default measures every visual child with
    /// <paramref name="availableSize"/> and returns the per-axis maximum of their desired sizes.
    /// <paramref name="availableSize"/> is the margin-deflated, min/max-coerced constraint.
    /// </summary>
    protected virtual Size MeasureOverride(Size availableSize)
    {
        if (_visualChildren is not {} children)
            return Size.Empty;

        var columns = 0;
        var rows = 0;

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(availableSize);
            columns = Math.Max(columns, child.DesiredSize.Columns);
            rows = Math.Max(rows, child.DesiredSize.Rows);
        }

        return new Size(columns, rows);
    }

    /// <summary>
    /// Called on the parent when a child's <see cref="DesiredSize"/> changed. The default
    /// invalidates this element's measure — except while this element is itself inside
    /// <see cref="MeasureOverride"/> (it reads the fresh value immediately).
    /// </summary>
    protected virtual void OnChildDesiredSizeChanged(UIElement child)
    {
        if (!_measuring)
            InvalidateMeasure();
    }

    /// <summary>
    /// The S8 template seam, called by <see cref="Measure"/> before <see cref="MeasureOverride"/>
    /// (never while Collapsed — the early-out precedes it). The base implementation does nothing
    /// and returns <see langword="false"/>; the template engine lands at P5.
    /// </summary>
    public virtual bool ApplyTemplate() => false;

    private void NotifyParentDesiredSizeChanged() => _visualParent?.OnChildDesiredSizeChanged(this);

    /// <summary>LD1 — the WPF <c>MinMax</c> resolve: Min &gt; Max &gt; explicit &gt; content, per axis.</summary>
    private (int MinWidth, int MaxWidth, int MinHeight, int MaxHeight) ResolveMinMax()
    {
        var width = Width;
        var height = Height;

        var maxWidth = Math.Max(Math.Min(width ?? LayoutMath.Unbounded, MaxWidth), MinWidth);
        var minWidth = Math.Max(Math.Min(maxWidth, width ?? 0), MinWidth);
        var maxHeight = Math.Max(Math.Min(height ?? LayoutMath.Unbounded, MaxHeight), MinHeight);
        var minHeight = Math.Max(Math.Min(maxHeight, height ?? 0), MinHeight);

        return (minWidth, maxWidth, minHeight, maxHeight);
    }

    // ───────────────────────────── arrange ─────────────────────────────

    /// <summary>
    /// Arranges the element into the parent-relative, margin-inclusive slot
    /// <paramref name="finalRect"/> (design doc §5.3): self-heals an invalid measure (LD4 — a
    /// never-measured element measures with the slot size), caches on <paramref name="finalRect"/>
    /// (LD6), computes the aligned size (Stretch fills the slot; non-Stretch takes the
    /// measure-cached <b>natural</b> size, never <c>DesiredSize − margin</c> — L225;
    /// explicit/Min/Max still bind), runs <see cref="ArrangeOverride"/>, and places the result
    /// with the LD3 offset algebra
    /// (Stretch-that-cannot-fill centers; all offsets clamp ≥ 0 — overflow pins Left/Top).
    /// The final position fold adds the <b>signed</b> margin (LD19): a negative
    /// <see cref="Margin"/> component may place <see cref="Bounds"/> at a negative origin —
    /// slots themselves stay non-negative (panels never produce negative slot rects).
    /// </summary>
    public void Arrange(in Rect finalRect)
    {
        VerifyAccess();

        if (Visibility == Visibility.Collapsed)
        {
            _lastArrangeRect = finalRect;
            _hasArranged = true;
            IsArrangeValid = true;
            SetBoundsAndRoute(LayoutRect.Empty);
            Transitions?.OnArranged(); // §9.5 first-arrange go-live trigger (idempotent)
            return;
        }

        if (!IsMeasureValid)
            Measure(_hasMeasured ? _lastMeasureConstraint : finalRect.Size); // self-heal (LD4)

        if (IsArrangeValid && _hasArranged && finalRect == _lastArrangeRect)
            return; // cache hit (LD6)

        _lastArrangeRect = finalRect;
        _hasArranged = true;
        IsArrangeValid = true; // before the override — in-flight invalidations stick
        Transitions?.OnArranged(); // §9.5 first-arrange go-live trigger (idempotent; the initial layout is now settled)

        var margin = Margin;
        var slot = LayoutMath.Sub(finalRect.Size, margin);
        var desiredContent = _naturalSize; // cached at measure — NOT `DesiredSize − margin` (L225)
        var horizontalAlignment = HorizontalAlignment;
        var verticalAlignment = VerticalAlignment;
        var (minWidth, maxWidth, minHeight, maxHeight) = ResolveMinMax();

        var arrangeSize = new Size(
            LayoutMath.Clamp(
                horizontalAlignment == HorizontalAlignment.Stretch ? slot.Columns : Math.Min(desiredContent.Columns, slot.Columns),
                minWidth, maxWidth),
            LayoutMath.Clamp(
                verticalAlignment == VerticalAlignment.Stretch ? slot.Rows : Math.Min(desiredContent.Rows, slot.Rows),
                minHeight, maxHeight));

        var used = ArrangeOverride(arrangeSize);
        var usedColumns = used.Columns;
        var usedRows = used.Rows;

        if (usedColumns is < 0 or > LayoutMath.MaxExtent || usedRows is < 0 or > LayoutMath.MaxExtent)
        {
            LayoutDiagnostics.Emit(
                LayoutDiagnosticKind.ArrangeRectClamped, this,
                $"ArrangeOverride on '{GetType().Name}' returned {used}; extents clamp to [0, MaxExtent] " +
                "before Rect construction (doc §5.2).");

            usedColumns = Math.Clamp(usedColumns, 0, LayoutMath.MaxExtent);
            usedRows = Math.Clamp(usedRows, 0, LayoutMath.MaxExtent);
        }

        var offsetColumn =
            horizontalAlignment switch
            {
                HorizontalAlignment.Left  => 0,
                HorizontalAlignment.Right => Math.Max(0, slot.Columns - usedColumns),
                _                         => LayoutMath.CenterOffset(slot.Columns, usedColumns), // Center, and Stretch that cannot fill (LD3)
            };

        var offsetRow =
            verticalAlignment switch
            {
                VerticalAlignment.Top    => 0,
                VerticalAlignment.Bottom => Math.Max(0, slot.Rows - usedRows),
                _                        => LayoutMath.CenterOffset(slot.Rows, usedRows),
            };

        // The signed position fold (LD19): margin.Left / margin.Top may be negative, producing a
        // negative origin. The fold itself computes in long — an int fold could wrap (e.g.
        // margin.Left near int.MaxValue) and silently clamp to the WRONG edge (L11). Positions
        // clamp symmetrically into [−MaxExtent, MaxExtent] (the L11 clamp, made two-sided) so
        // pathological inputs can't overflow downstream int arithmetic.
        var column = (long) finalRect.Column + margin.Left + offsetColumn;
        var row = (long) finalRect.Row + margin.Top + offsetRow;

        if (column is > LayoutMath.MaxExtent or < -LayoutMath.MaxExtent ||
            row is > LayoutMath.MaxExtent or < -LayoutMath.MaxExtent)
        {
            LayoutDiagnostics.Emit(
                LayoutDiagnosticKind.ArrangeRectClamped, this,
                $"Arrange position ({column}, {row}) for '{GetType().Name}' exceeds ±MaxExtent; clamped (doc §5.2).");

            column = Math.Clamp(column, -LayoutMath.MaxExtent, LayoutMath.MaxExtent);
            row = Math.Clamp(row, -LayoutMath.MaxExtent, LayoutMath.MaxExtent);
        }

        SetBoundsAndRoute(new LayoutRect((int) column, (int) row, usedColumns, usedRows));
    }

    /// <summary>
    /// The element's content placement. Receives the aligned size <c>size_a</c> (not the slot);
    /// returns the actual content size. The default arranges every visual child at
    /// <c>(0, 0, finalSize)</c> and returns <paramref name="finalSize"/>.
    /// </summary>
    protected virtual Size ArrangeOverride(Size finalSize)
    {
        if (_visualChildren is { } children)
        {
            var rect = new Rect(0, 0, finalSize.Columns, finalSize.Rows);
            for (var i = 0; i < children.Count; i++)
                children[i].Arrange(rect);
        }

        return finalSize;
    }

    /// <summary>
    /// Publishes new bounds and routes the change (doc §5.3 <c>SetBoundsAndRoute</c>): a <b>size</b>
    /// change re-rasters (and at T3 recreates a boundary's scene — scenes don't resize); a
    /// <b>position-only</b> change is a cheap composite move for boundaries and a zone re-raster
    /// for non-boundaries — which is why position animations must use <c>RenderOffset*</c>, never
    /// <c>Margin</c>/<c>Canvas.Left</c> (invariant 3).
    /// </summary>
    private void SetBoundsAndRoute(LayoutRect newBounds)
    {
        var oldBounds = _bounds;
        if (!SetAndRaise(BoundsProperty, ref _bounds, newBounds))
            return;

        if (oldBounds.Size != newBounds.Size)
        {
            InvalidateVisual();
        }
        else if (IsEffectiveRenderBoundary)
        {
            InvalidateComposite();
        }
        else
            // Non-boundary position-only: the element physically moved within its zone's raster
            // space, so the zone re-rasters WHOLE — there is no partial-raster machinery in v1
            // (the probe-1 verdict). A future dirty-region implementation would narrow this branch
            // first; today it is deliberate, not a fallthrough.
        {
            InvalidateVisual();
        }
    }

    // ───────────────────────────── invalidation ─────────────────────────────

    /// <summary>
    /// Invalidates the element's measure and walks up: every ancestor to the root is invalidated
    /// too (parent desired size depends on children), stopping at the first already-invalid
    /// ancestor. Newly-invalidated elements enqueue in the root's <see cref="LayoutManager"/>.
    /// </summary>
    public void InvalidateMeasure()
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();

        if (!IsMeasureValid)
            return; // the walk already happened (or the element is freshly constructed/attached)

        IsMeasureValid = false;
        GetLayoutManager()?.EnqueueMeasure(this);
        _visualParent?.InvalidateMeasure();
    }

    /// <summary>Invalidates the element's arrange — self only (no ancestor walk), enqueued in the root's <see cref="LayoutManager"/>.</summary>
    public void InvalidateArrange()
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();

        if (!IsArrangeValid)
            return;

        IsArrangeValid = false;
        GetLayoutManager()?.EnqueueArrange(this);
    }

    /// <summary>The post-measure self-arrange invalidation (no <see cref="UIObject.VerifyAccess"/> — already on the layout path).</summary>
    private void InvalidateArrangeAfterMeasure()
    {
        if (!IsArrangeValid)
            return;

        IsArrangeValid = false;
        GetLayoutManager()?.EnqueueArrange(this);
    }
}
