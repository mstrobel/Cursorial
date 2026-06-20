using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// The scroll-mechanics element (design doc §5.7): hosts a single <see cref="Content"/> child,
/// measures it at <see cref="LayoutLimits.MaxScrollExtent"/> on scrollable axes (never
/// <see cref="LayoutMath.Unbounded"/> — the doc's §12 scrolling note), publishes
/// <see cref="Extent"/>/<see cref="Viewport"/> readbacks, and slides the content at composite time
/// via the <b>styled</b> <see cref="ScrollOffsetColumn"/>/<see cref="ScrollOffsetRow"/> offsets
/// (<c>[AffectsComposite]</c> — storyboard-animatable; smooth scrolling never re-rasters,
/// invariant 3). S8's <c>ScrollViewer</c> templates around it; its <c>DirectProperty</c> offsets
/// are two-way mirrors of these.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always a render boundary</b> (doc §5.5 predicate ⑥) — from attach, never via mid-life
/// promotion. The boundary's published clip is its viewport footprint ∩ ancestor clips, so band
/// content outside the viewport never reaches the screen.
/// </para>
/// <para>
/// <b>Banded scene policy (doc §13.2 DECISION, matrix LD11/LD13).</b> The zone scene covers the
/// row band <c>[bandStart, bandStart + bandLength)</c> of the content, not the full extent —
/// memory bounded by construction, no budget knob, no degraded mode. (This deliberately
/// supersedes spec-tree-layout §2.4/§3.9's extent-sized scenes + <c>SceneBudgetCells</c>
/// budget-fallback design — the doc's §13 resolutions win; the deviation is pinned in the matrix
/// header and LD13.) With
/// <c>K = max(viewportRows, 8)</c>: <c>bandLength = min(contentRows, viewportRows + 2K)</c> and
/// <c>bandStart = clamp(anchor − K, 0, contentRows − bandLength)</c>, where <c>anchor</c> is the
/// offset at the last re-anchor. An offset write within <c>±K</c> of the anchor is a pure
/// composite slide (zero <c>Render</c> calls); a write past <c>K</c> re-anchors — the metadata
/// changed handler runs the <em>check</em> only and marks the zone raster-dirty; the one band
/// re-raster happens in the next <see cref="RenderTree.RunRenderPass"/>. When the band covers the
/// whole content (<c>bandLength ≥ contentRows</c>) no offset ever re-anchors. v1 bands the
/// vertical axis only; a horizontally scrollable presenter's scene spans the full content width.
/// </para>
/// <para>
/// <b>Offset coercion.</b> Offsets coerce into <c>[0, Extent − Viewport]</c> at set time (0 when
/// the axis cannot scroll) and are <em>re</em>-coerced at the end of arrange — content shrinking
/// while scrolled snaps the offset back the same frame, firing the composite lane only on actual
/// movement, so the cached raster is never left slid past the content. Coercion applies to
/// animated writes too (it runs at effective-value computation).
/// </para>
/// <para>
/// The presenter itself paints nothing and should stay that way: its zone raster is in scrolled
/// <em>content</em> coordinates, so viewport-anchored chrome (scrollbars, edge fades) belongs to
/// the templating parent (S8's <c>ScrollViewer</c>), not to a <c>Render</c> override here.
/// </para>
/// </remarks>
public class ScrollContentPresenter : UIElement
{
    /// <summary>The minimum band padding in rows — <c>K = max(viewportRows, 8)</c> (matrix LD11).</summary>
    private const int MinBandPadding = 8;

    /// <summary>Whether the content can scroll horizontally (default <see langword="false"/>). <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<bool> CanScrollHorizontallyProperty =
        UIProperty.Register<ScrollContentPresenter, bool>(nameof(CanScrollHorizontally));

    /// <summary>Whether the content can scroll vertically (default <see langword="true"/>). <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<bool> CanScrollVerticallyProperty =
        UIProperty.Register<ScrollContentPresenter, bool>(nameof(CanScrollVertically), defaultValue: true);

    /// <summary>
    /// The horizontal scroll offset in cells, coerced into <c>[0, Extent − Viewport]</c> (0 when
    /// <see cref="CanScrollHorizontally"/> is false). <c>[AffectsComposite]</c> — a change is a pure
    /// composite slide (the scene spans the full content width; v1 bands the vertical axis only).
    /// </summary>
    public static readonly StyledProperty<int> ScrollOffsetColumnProperty =
        UIProperty.Register<ScrollContentPresenter, int>(nameof(ScrollOffsetColumn), coerce: CoerceScrollOffsetColumn);

    /// <summary>
    /// The vertical scroll offset in cells, coerced into <c>[0, Extent − Viewport]</c> (0 when
    /// <see cref="CanScrollVertically"/> is false). <c>[AffectsComposite]</c>; the metadata changed
    /// handler runs the band re-anchor <em>check</em> (see the class remarks) — never raster work.
    /// </summary>
    public static readonly StyledProperty<int> ScrollOffsetRowProperty =
        UIProperty.Register<ScrollContentPresenter, int>(
            nameof(ScrollOffsetRow), coerce: CoerceScrollOffsetRow, changed: OnScrollOffsetRowChanged);

    /// <summary>
    /// The content's desired size from the last measure, capped at
    /// <see cref="LayoutLimits.MaxScrollExtent"/> per axis (with a one-time DEBUG diagnostic when
    /// the cap engages). Read-only direct property with change notifications.
    /// </summary>
    public static readonly DirectProperty<ScrollContentPresenter, Size> ExtentProperty =
        UIProperty.RegisterDirect<ScrollContentPresenter, Size>(nameof(Extent), static e => e._extent);

    /// <summary>The presenter's own arranged content size. Read-only direct property with change notifications.</summary>
    public static readonly DirectProperty<ScrollContentPresenter, Size> ViewportProperty =
        UIProperty.RegisterDirect<ScrollContentPresenter, Size>(nameof(Viewport), static e => e._viewport);

    static ScrollContentPresenter()
    {
        AffectsMeasure<ScrollContentPresenter>(CanScrollHorizontallyProperty, CanScrollVerticallyProperty);
        // Exactly AffectsComposite (matrix L214) — the lane that guarantees zero re-raster.
        AffectsComposite<ScrollContentPresenter>(ScrollOffsetColumnProperty, ScrollOffsetRowProperty);
    }

    private UIElement? _content;
    private IScrollContentHost? _scrollHost; // the content's opt-in delegation seam (null / IsScrollClient false ⇒ legacy path)
    private Size _extent;
    private Size _viewport;
    private bool _extentClampDiagnosed;

    /// <inheritdoc cref="CanScrollHorizontallyProperty"/>
    public bool CanScrollHorizontally { get => GetValue(CanScrollHorizontallyProperty); set => SetValue(CanScrollHorizontallyProperty, value); }

    /// <inheritdoc cref="CanScrollVerticallyProperty"/>
    public bool CanScrollVertically { get => GetValue(CanScrollVerticallyProperty); set => SetValue(CanScrollVerticallyProperty, value); }

    /// <inheritdoc cref="ScrollOffsetColumnProperty"/>
    public int ScrollOffsetColumn { get => GetValue(ScrollOffsetColumnProperty); set => SetValue(ScrollOffsetColumnProperty, value); }

    /// <inheritdoc cref="ScrollOffsetRowProperty"/>
    public int ScrollOffsetRow { get => GetValue(ScrollOffsetRowProperty); set => SetValue(ScrollOffsetRowProperty, value); }

    /// <inheritdoc cref="ExtentProperty"/>
    public Size Extent => _extent;

    /// <inheritdoc cref="ViewportProperty"/>
    public Size Viewport => _viewport;

    /// <summary>
    /// The scrolled content — a single child, adopted visually and logically. Arranged at
    /// <c>(0, 0, contentSize)</c> in content coordinates (always non-negative; the scroll slide is
    /// composite-time).
    /// </summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            VerifyAccess();

            if (ReferenceEquals(_content, value))
                return;

            if (_content is { } old)
            {
                // Symmetric with the adoption below: a visual-only host releases only the visual link.
                if (_contentLogicallyOwned)
                    DisownChild(old);
                else
                    RemoveVisualChild(old);
            }

            // Disown the old scroll host's back-channel before re-discovering — no stale ScrollOwner across a swap (VV1.2).
            if (_scrollHost is not null)
            {
                _scrollHost.ScrollOwner = null;
                _scrollHost = null;
            }

            _content = value;
            _contentLogicallyOwned = false;

            // Discover the delegation seam: a content that opts into IScrollContentHost (the ItemsPresenter forwarding
            // to a virtualizing panel, or a test host) gets the SCP injected as its back-channel ScrollOwner. The
            // gated branches only ENGAGE when IsScrollClient is true, so a non-virtualizing host is harmless (VV1.2/VV1.3).
            _scrollHost = value as IScrollContentHost;
            if (_scrollHost is not null)
                _scrollHost.ScrollOwner = this;

            if (value is not null)
            {
                // Content the ScrollViewer (a ContentControl) already owns logically is hosted
                // visual-only — adopting it logically here would double-parent it (a logical-tree
                // violation). Free-standing content (no logical parent) is adopted both ways so
                // DataContext inheritance flows.
                if (value.LogicalParent is null)
                {
                    AdoptChild(value, -1);
                    _contentLogicallyOwned = true;
                }
                else
                {
                    AddVisualChildOnly(value, -1);
                }
            }

            InvalidateMeasure();
        }
    }

    private bool _contentLogicallyOwned;

    /// <summary>Always a render boundary — design doc §5.5 predicate ⑥ (a boundary from attach, never via mid-life promotion).</summary>
    internal override bool IsAlwaysRenderBoundary => true;

    /// <inheritdoc/>
    internal override int ChildScrollOffsetColumn => ScrollOffsetColumn;

    /// <inheritdoc/>
    internal override int ChildScrollOffsetRow => ScrollOffsetRow;

    // ───────────────────────────── band state (vertical axis only in v1 — LD11) ─────────────────────────────

    /// <summary>The vertical offset at the last band re-anchor (initially 0).</summary>
    internal int BandAnchorRow { get; private set; }

    /// <summary>The content row the zone scene's row 0 maps to.</summary>
    internal int BandStartRow { get; private set; }

    /// <summary>The band padding <c>K = max(viewportRows, 8)</c> (matrix LD11).</summary>
    internal int BandPadding => Math.Max(_viewport.Rows, MinBandPadding);

    /// <summary>The banded scene height: <c>min(contentRows, viewportRows + 2K)</c>.</summary>
    internal int BandLength => Math.Min(ArrangedContentRows, _viewport.Rows + 2 * BandPadding);

    /// <summary>The content's arranged height — <c>max(Extent, Viewport)</c> rows when vertically scrollable, else the viewport.</summary>
    private int ArrangedContentRows => CanScrollVertically ? Math.Max(_extent.Rows, _viewport.Rows) : _viewport.Rows;

    /// <summary>
    /// The zone-scene size the <see cref="RenderTree"/> rents for this presenter: full content
    /// width (horizontal scrolling is a pure composite slide in v1), banded height (LD11/LD13).
    /// </summary>
    internal Size ComputeSceneSize()
    {
        var bounds = Bounds.Size;
        var columns = CanScrollHorizontally ? Math.Max(_extent.Columns, bounds.Columns) : bounds.Columns;
        return new Size(Math.Max(1, columns), Math.Max(1, BandLength));
    }

    // ───────────────────────────── measure / arrange (spec §3.9 mechanics, doc-adjusted) ─────────────────────────────

    /// <summary>
    /// Measures <see cref="Content"/> with <see cref="LayoutLimits.MaxScrollExtent"/> on scrollable
    /// axes (the constraint passes through unchanged on non-scrollable axes), publishes the capped
    /// <see cref="Extent"/>, and desires <c>min(extent, constraint)</c> per axis.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_scrollHost is { IsScrollClient: true } host)
            return MeasureWithHost(host, availableSize);

        var canScrollHorizontally = CanScrollHorizontally;
        var canScrollVertically = CanScrollVertically;
        var extent = Size.Empty;

        if (_content is { } content)
        {
            content.Measure(
                new Size(canScrollHorizontally ? LayoutLimits.MaxScrollExtent : availableSize.Columns,
                         canScrollVertically ? LayoutLimits.MaxScrollExtent : availableSize.Rows));

            var desired = content.DesiredSize;
            var columns = Math.Min(desired.Columns, LayoutLimits.MaxScrollExtent);
            var rows = Math.Min(desired.Rows, LayoutLimits.MaxScrollExtent);

            if (columns != desired.Columns || rows != desired.Rows)
            {
                if (!_extentClampDiagnosed)
                {
                    _extentClampDiagnosed = true;

                    LayoutDiagnostics.Emit(
                        LayoutDiagnosticKind.ScrollExtentClamped, this,
                        $"Content desired size {desired} exceeds LayoutLimits.MaxScrollExtent " +
                        $"({LayoutLimits.MaxScrollExtent}); the published Extent is capped (doc §5.7 — " +
                        "virtualization is the real answer for huge extents).");
                }
            }
            else
            {
                _extentClampDiagnosed = false; // re-arm the one-time diagnostic once back in range
            }

            extent = new Size(columns, rows);
        }

        SetAndRaise(ExtentProperty, ref _extent, extent);

        return new Size(Math.Min(extent.Columns, availableSize.Columns), Math.Min(extent.Rows, availableSize.Rows));
    }

    /// <summary>
    /// The host-active measure (VV1.4/VV1.5): the content is measured at the <b>available size (the viewport)</b> on
    /// scrollable axes — NOT <see cref="LayoutLimits.MaxScrollExtent"/>, since the host reports the extent via
    /// <see cref="IScrollContentHost.GetExtent"/> rather than its desired size — and the published <see cref="Extent"/>
    /// is the host's estimate, <b>uncapped</b> (the band bounds the scene under a virtualizing host, so the 32K cap's
    /// only effect — an unreachable tail — is removed; only the legacy path caps).
    /// </summary>
    private Size MeasureWithHost(IScrollContentHost host, Size availableSize)
    {
        var canScrollHorizontally = CanScrollHorizontally;
        var canScrollVertically = CanScrollVertically;

        // Flow the SCP's axis enables to the host (VV1.4) BEFORE measuring it — the panel sizes its realization
        // window / decides its scroll axis from these during its own MeasureOverride (triggered by the Measure below).
        host.CanScrollHorizontally = canScrollHorizontally;
        host.CanScrollVertically = canScrollVertically;

        // Measure the content (the ItemsPresenter → panel) at the viewport, never MaxScrollExtent (a virtualizing
        // panel realizes only its band, so it must be constrained to what it will get, not the full extent). On a
        // scrollable axis the available size can be Unbounded under an unconstrained parent; clamp it to a FINITE
        // ceiling exactly as the legacy path does, so a virtualizing panel never receives an int.MaxValue window.
        if (_content is { } content)
            content.Measure(
                new Size(canScrollHorizontally ? Math.Min(availableSize.Columns, LayoutLimits.MaxScrollExtent) : availableSize.Columns,
                         canScrollVertically ? Math.Min(availableSize.Rows, LayoutLimits.MaxScrollExtent) : availableSize.Rows));

        var extent = CapHostExtent(host.GetExtent());
        SetAndRaise(ExtentProperty, ref _extent, extent);
        _extentClampDiagnosed = false; // the host path uses the Rect ceiling, not the legacy diagnostic — keep it re-armed

        // Desire min(extent, available) per axis — the viewport when the parent constrains (the normal case); under
        // an Unbounded scrollable axis, fall back to the same finite ceiling the legacy path uses (never the full extent).
        return new Size(
            Math.Min(extent.Columns, canScrollHorizontally ? Math.Min(availableSize.Columns, LayoutLimits.MaxScrollExtent) : availableSize.Columns),
            Math.Min(extent.Rows, canScrollVertically ? Math.Min(availableSize.Rows, LayoutLimits.MaxScrollExtent) : availableSize.Rows));
    }

    // The host path lifts the legacy MaxScrollExtent (32K) cap. The content is arranged at the full extent height,
    // which the layout clamps to LayoutMath.MaxExtent — so the host extent caps THERE (≈ 1.07 billion cells, the
    // layout ceiling, distinct from the wider Rect geometry cap), effectively unbounded for any real list. The legacy
    // (non-host) path keeps the lower MaxScrollExtent sanity cap (its content genuinely allocates that many rows).
    private static Size CapHostExtent(Size extent)
        => new(Math.Clamp(extent.Columns, 0, LayoutMath.MaxExtent), Math.Clamp(extent.Rows, 0, LayoutMath.MaxExtent));

    /// <summary>
    /// The host's estimate-refinement back-channel (the WPF <c>InvalidateScrollInfo</c> analog, VV1.7): re-publishes
    /// <see cref="Extent"/> from <see cref="IScrollContentHost.GetExtent"/>, re-coerces both offsets, and marks
    /// measure dirty. Called by a host through the injected <see cref="IScrollContentHost.ScrollOwner"/>.
    /// </summary>
    internal void InvalidateScrollExtent()
    {
        if (_scrollHost is not { IsScrollClient: true } host)
            return;

        SetAndRaise(ExtentProperty, ref _extent, CapHostExtent(host.GetExtent()));
        CoerceValue(ScrollOffsetColumnProperty);
        CoerceValue(ScrollOffsetRowProperty);
        InvalidateMeasure();
    }

    /// <summary>
    /// Publishes <see cref="Viewport"/>, arranges the content at
    /// <c>(0, 0, max(extent, viewport))</c> per scrollable axis in content coordinates
    /// (non-negative — the scroll slide is composite-time), then <b>re-coerces both offsets</b>
    /// (content shrank while scrolled ⇒ same-frame snap-back, firing only on actual movement) and
    /// refreshes the band geometry.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        SetAndRaise(ViewportProperty, ref _viewport, finalSize);

        // Hand the host its viewport BEFORE it next measures its band (VV1.6) — the host sizes its realization window
        // from this. A non-engaged host (IsScrollClient false) is skipped, keeping the OFF-path untouched.
        if (_scrollHost is { IsScrollClient: true } host)
            host.SetViewport(finalSize);

        if (_content is { } content)
        {
            content.Arrange(
                new Rect(
                    0,
                    0,
                    CanScrollHorizontally ? Math.Max(_extent.Columns, finalSize.Columns) : finalSize.Columns,
                    CanScrollVertically ? Math.Max(_extent.Rows, finalSize.Rows) : finalSize.Rows));
        }

        // End-of-arrange re-coercion (doc §5.7): re-runs the coercer against the stored raw value
        // under the NEW extent/viewport; the AffectsComposite lane fires only on actual movement.
        CoerceValue(ScrollOffsetColumnProperty);
        CoerceValue(ScrollOffsetRowProperty);
        RefreshBandGeometry();

        return finalSize;
    }

    // ───────────────────────────── offset coercion + re-anchor check ─────────────────────────────

    private static int CoerceScrollOffsetColumn(UIObject sender, int value)
        => sender is ScrollContentPresenter presenter && presenter.CanScrollHorizontally
            ? Math.Clamp(value, 0, Math.Max(0, presenter._extent.Columns - presenter._viewport.Columns))
            : 0;

    private static int CoerceScrollOffsetRow(UIObject sender, int value)
        => sender is ScrollContentPresenter presenter && presenter.CanScrollVertically
            ? Math.Clamp(value, 0, Math.Max(0, presenter._extent.Rows - presenter._viewport.Rows))
            : 0;

    private static void OnScrollOffsetRowChanged(UIObject sender, int oldValue, int newValue)
        => ((ScrollContentPresenter)sender).RunReAnchorCheck(newValue);

    /// <summary>
    /// The re-anchor <em>check</em> (matrix L211 — the metadata handler does no raster work):
    /// an offset within <c>±K</c> of the anchor is a pure composite slide; past <c>K</c> the band
    /// re-centers on the new offset and the zone is marked raster-dirty for the <em>next</em>
    /// render pass. A band covering the whole content never re-anchors (L212).
    /// </summary>
    private void RunReAnchorCheck(int newOffset)
    {
        var contentRows = ArrangedContentRows;
        var bandLength = BandLength;
        if (bandLength >= contentRows)
            return; // the band is the whole extent — every offset is a composite slide (L212)

        if (Math.Abs(newOffset - BandAnchorRow) <= BandPadding)
            return; // in-band: pure composite slide (zero re-raster — invariant 3)

        BandAnchorRow = newOffset;
        BandStartRow = Math.Clamp(newOffset - BandPadding, 0, contentRows - bandLength);
        Zone?.MarkRasterDirty(); // check only — the band re-raster happens in the next RunRenderPass

        // A virtualizing host must realize the new band BEFORE that re-raster — schedule its re-measure (the re-anchor
        // cadence; an in-band slide never reaches here, so invariant 3 is preserved).
        if (_scrollHost is { IsScrollClient: true } host)
            host.InvalidateRealization();
    }

    // ───────────────────────────── bring-into-view (content-coordinate translation) ─────────────────────────────

    /// <summary>
    /// Computes <paramref name="descendant"/>'s bounds in this presenter's <b>content coordinate</b>
    /// space — the space <see cref="ScrollOffsetRow"/>/<see cref="ScrollOffsetColumn"/> index (content
    /// origin at 0, pre-slide; the scroll is a composite-time slide above the content child). Folds each
    /// hop's <see cref="UIElement.Bounds"/> position + render offset from <paramref name="descendant"/>
    /// up to (and excluding) the content child. Returns <see langword="false"/> when
    /// <paramref name="descendant"/> is not within the content subtree. The <see cref="ScrollViewer"/>
    /// calls this to scroll a newly-focused descendant into view (bring-focused-into-view).
    /// </summary>
    internal bool TryGetContentRect(UIElement descendant, out Rect rect)
    {
        rect = default;
        if (_content is null)
            return false;

        var column = 0;
        var row = 0;
        for (UIElement? node = descendant; node is not null; node = node.VisualParent)
        {
            if (ReferenceEquals(node, _content))
            {
                // Reached the content child without crossing a scroll host — (column,row) is the
                // descendant's origin in content coordinates; size is the descendant's own footprint.
                rect = new Rect(column, row, Math.Max(1, descendant.Bounds.Columns), Math.Max(1, descendant.Bounds.Rows));
                return true;
            }

            column += node.Bounds.Column + node.RenderOffsetColumn;
            row += node.Bounds.Row + node.RenderOffsetRow;
        }

        return false; // descendant is not under this presenter's content
    }

    /// <summary>
    /// Normalizes the band after extent/viewport changes (end of arrange): clamps the anchor into
    /// the new offset range and re-derives <see cref="BandStartRow"/>; a moved band start marks the
    /// zone dirty (the cached raster's row mapping is stale). Band-length changes re-rent the scene
    /// in <c>RenderTree.EnsureScene</c>, which re-rasters by construction.
    /// </summary>
    private void RefreshBandGeometry()
    {
        var contentRows = ArrangedContentRows;
        var bandLength = BandLength;
        var anchor = Math.Clamp(BandAnchorRow, 0, Math.Max(0, contentRows - _viewport.Rows));
        var start = bandLength >= contentRows ? 0 : Math.Clamp(anchor - BandPadding, 0, contentRows - bandLength);

        BandAnchorRow = anchor;

        if (start == BandStartRow)
            return;

        BandStartRow = start;
        Zone?.MarkRasterDirty();

        // The band moved on an extent/viewport change — a virtualizing host re-realizes the new band next measure.
        if (_scrollHost is { IsScrollClient: true } host)
            host.InvalidateRealization();
    }
}
