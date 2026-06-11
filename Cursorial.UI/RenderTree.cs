using Cursorial.Drawing;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The per-root render orchestrator (design doc §5.5–§5.6, §5.8): owns zone partitioning (one
/// Drawing <see cref="Scene"/> per <b>render boundary</b>, never per element), scene ownership via
/// the shared <see cref="ScenePool"/>, re-raster scheduling from <see cref="UIElement.InvalidateVisual"/>,
/// the unconditional per-pass boundary walk that refreshes <see cref="CompositeParameters"/>
/// (offset / opacity / clip changes <b>never</b> re-raster — invariant 3), the flat bottom-up
/// boundary-layer list (<see cref="CollectLayers"/>), and composite-order hit testing
/// (<see cref="HitTest"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>P1 single-root stand-in.</b> At P1 there is no windowing: this tree renders <b>one</b> root
/// element tree onto a single full-screen surface, and the root element is a render boundary by
/// construction — it stands in for design doc §5.5's predicate ① "window root". At P7, S4's
/// <c>WindowManager</c>/<c>TopLevelSurface</c> wraps one <see cref="RenderTree"/> per window and
/// concatenates each window's <see cref="CollectLayers"/> output in window z-order into one flat
/// <see cref="SceneCompositor.Composite"/> call; this class's public surface
/// (<see cref="RunRenderPass"/> / <see cref="CollectLayers"/> / <see cref="LayerCount"/> /
/// <see cref="HitTest"/> / <see cref="InvalidateAll"/> / <see cref="Detach"/>) is exactly what that
/// window plumbing consumes — only the "create me directly over the root" construction path is the
/// P1 stand-in S4 replaces.
/// </para>
/// <para>
/// <b>Boundary predicates</b> (§5.5): ① root, ② <c>Opacity &lt; 1</c>, ③ <c>RenderOffset* ≠ 0</c>,
/// ④ <c>ClipToBounds</c>, ⑤ <c>CompositeClip != null</c>, ⑥ always-boundary elements
/// (<c>ScrollContentPresenter</c>, T4 — the <c>IsAlwaysRenderBoundary</c> seam), ⑦
/// <c>IsRenderBoundary</c>. <b>Promotion is sticky until detach</b> — the layer count never
/// oscillates (the compositor full-recomposites on count change); no demotion valve in v1.
/// </para>
/// <para>
/// <b>Per-pass sequence</b> (§5.6): ① pending promotions (a zone-set rebuild — the four-step
/// promotion falls out of zone-membership change detection: the old zone re-rasters excluding the
/// promoted subtree, the new zone rents + rasters, zone pointers rebuild, and the layer-count
/// change triggers the compositor's full recomposite); ② re-raster dirty zones, whole-zone (the
/// probe-1 verdict: no partial-raster machinery); ③ walk the boundary tree <b>unconditionally</b>
/// (tens of boundaries, integer math — eliminates stale-accumulation bugs), accumulating absolute
/// origin (+<c>RenderOffset*</c>, −ancestor scroll), opacity product, clip intersection, and effective visibility,
/// publishing <see cref="CompositeParameters"/> only when different. A clean pass performs zero
/// allocation and zero <see cref="UIElement.Render"/> calls.
/// </para>
/// </remarks>
public sealed class RenderTree
{
    private readonly UIElement _root;
    private readonly ScenePool _scenePool;
    private readonly List<RenderZone> _layers = [];
    private readonly RenderContext _renderContext = new();
    private readonly Action<DrawingContext> _drawCallback;
    private RenderZone? _rasterZone;
    private bool _layersDirty = true;
    private bool _parametersDirty;
    private bool _detached;

    /// <summary>
    /// Creates the render tree over an attached root element (the P1 single-root stand-in — see the
    /// class remarks; at P7 S4's window plumbing owns construction).
    /// </summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="root"/> has a visual parent or is not attached as a root.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="root"/> already has a render tree.</exception>
    public RenderTree(UIElement root, ScenePool scenePool, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(scenePool);
        ArgumentNullException.ThrowIfNull(capabilities);
        root.VerifyAccess();

        if (root.VisualParent is not null)
            throw new ArgumentException("A render tree's root must not have a visual parent.", nameof(root));
        if (!root.IsAttachedToTree)
            throw new ArgumentException("The root must be attached (via its LayoutManager) before a RenderTree is created over it.", nameof(root));
        if (root.RenderTreeHost is not null)
            throw new InvalidOperationException("This element already has a RenderTree; one render tree per root.");

        _root = root;
        _scenePool = scenePool;
        Capabilities = capabilities;
        _drawCallback = OnDrawZone;
        root.RenderTreeHost = this;
    }

    /// <summary>
    /// The negotiated output capabilities supplied to <see cref="RenderContext"/>. S6 re-stamps this
    /// inside the renegotiation transaction (P-later) and follows with <see cref="InvalidateAll"/>.
    /// </summary>
    public OutputCapabilities Capabilities { get; set; }

    /// <summary>
    /// The number of boundary layers. Stable unless the boundary set changed — a change is the
    /// compositor's full-recomposite signal, which is why promotion is sticky (§5.5).
    /// </summary>
    public int LayerCount => _layers.Count;

    /// <summary>
    /// The funnel for app draw code (design doc §10.8): when set, every zone raster — which runs
    /// the elements' <see cref="UIElement.Render"/> overrides — goes through
    /// <see cref="IUserCodeGuard.Run{TState}"/>. A handled draw exception keeps whatever the zone
    /// rastered before the throw and clears the dirty bit (the next invalidation re-rasters); an
    /// unhandled one records fatal and the pass unwinds immediately.
    /// </summary>
    internal IUserCodeGuard? UserCodeGuard { get; set; }

    /// <summary>
    /// Whether the next <see cref="RunRenderPass"/> has work to do: a pending zone-set rebuild, a
    /// raster-dirty zone, or a composite-lane invalidation since the last pass. This is the
    /// substance behind <see cref="IRenderSystem.HasDirtyVisuals"/> — S6's Phase-6 render gate and
    /// Phase-7 idle guard. A clean tree reports <see langword="false"/> and the frame loop skips
    /// rendering entirely.
    /// </summary>
    internal bool HasPendingRenderWork
    {
        get
        {
            if (_layersDirty || _parametersDirty)
                return true;

            for (var i = 0; i < _layers.Count; i++)
            {
                // Mirror RunRenderPass's raster predicate exactly: a Hidden / Collapsed / zero-area
                // zone's raster is DEFERRED (the dirty bit deliberately survives — see the pass), so
                // counting it here would pin the Phase-7 idle guard true forever and the loop would
                // never park while any collapsed-dirty boundary exists. Re-arm is sound without it:
                // the flip that un-defers (Visibility / bounds) raises a measure or composite
                // invalidation, which surfaces the pending work through the other flags.
                var zone = _layers[i];
                if (zone.RasterDirty
                    && zone.Boundary.Visibility == Visibility.Visible
                    && !IsZeroArea(zone.Boundary.Bounds.Size))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Runs one render pass: pending boundary promotions → re-raster dirty zones (whole-zone) →
    /// the unconditional boundary walk publishing <see cref="CompositeParameters"/> on change.
    /// Run the layout pass first — zone scenes size to arranged bounds.
    /// </summary>
    /// <exception cref="InvalidOperationException">The tree has been detached.</exception>
    public void RunRenderPass()
    {
        _root.VerifyAccess();
        ThrowIfDetached();

        if (_layersDirty)
            RebuildZones();

        for (var i = 0; i < _layers.Count; i++)
        {
            var zone = _layers[i];
            EnsureScene(zone);

            // A zero-sized or Hidden/Collapsed boundary publishes Clip = Rect.Empty and keeps its
            // cached raster; rastering is deferred (the dirty bit survives) so a boundary hidden at
            // its FIRST raster doesn't cache an empty scene that a later parameters-only Visible
            // flip would expose blank.
            if (zone.RasterDirty
                && zone.Scene is not null
                && zone.Boundary.Visibility == Visibility.Visible
                && !IsZeroArea(zone.Boundary.Bounds.Size))
            {
                if (UserCodeGuard is { } guard)
                {
                    if (guard.IsFatal)
                        return; // a previous phase already recorded fatal — unwind promptly

                    var completed = guard.Run((Tree: this, Zone: zone), static s => s.Tree.Raster(s.Zone));

                    // Handled draw exception (doc §10.8): keep whatever rastered before the throw,
                    // clear the dirty bit so the failing draw doesn't re-run every frame, and
                    // continue with the remaining zones. The next invalidation re-rasters normally.
                    zone.RasterDirty = false;
                    if (!completed)
                        return; // fatal recorded — the frame loop unwinds to teardown
                }
                else
                {
                    Raster(zone);
                }
            }
        }

        RefreshParameters();
        _parametersDirty = false;
    }

    /// <summary>
    /// Appends this tree's boundary layers to <paramref name="target"/> <b>bottom-up</b> in screen
    /// coordinates (the window position/opacity folded in): pre-order DFS of the boundary tree with
    /// the stable <c>(ZIndex, index)</c> sibling sort — a zone's own scene is always the lowest layer
    /// of its subtree (the zone-base rule). Call after <see cref="RunRenderPass"/>. Allocation-free
    /// beyond the caller's list growth.
    /// </summary>
    public void CollectLayers(List<SceneLayer> target, int windowOffsetColumn = 0, int windowOffsetRow = 0, double windowOpacity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(target);
        _root.VerifyAccess();
        ThrowIfDetached();

        var folded = windowOffsetColumn != 0 || windowOffsetRow != 0 || windowOpacity != 1.0;
        for (var i = 0; i < _layers.Count; i++)
        {
            var zone = _layers[i];
            if (zone.Scene is null)
                continue; // RunRenderPass has not run yet for this zone

            var parameters = zone.Parameters;
            if (folded)
            {
                var clip = parameters.Clip is { } c
                    ? TranslateClip(c, windowOffsetColumn, windowOffsetRow)
                    : (Rect?)null;
                parameters = new CompositeParameters(
                    parameters.OffsetColumn + windowOffsetColumn,
                    parameters.OffsetRow + windowOffsetRow,
                    OpacityByte(zone.OpacityProduct * windowOpacity),
                    clip,
                    parameters.Mode);
            }

            target.Add(new SceneLayer(zone.Scene, parameters));
        }
    }

    /// <summary>
    /// The hit test (design doc §5.8) — provably mirrors composite order: walks the flat boundary-layer
    /// list <b>reversed</b> (topmost-first, the exact reverse of <see cref="CollectLayers"/>),
    /// clip-rejects on the cached boundary clips the §5.6 walk just refreshed (<see cref="Rect.Empty"/>
    /// skips hidden-ancestor layers), transforms by the layer's effective offset, then descends the
    /// zone over the cached <c>(ZIndex, index)</c> order descending, reading live <see cref="UIElement.Bounds"/>.
    /// <see cref="UIElement.IsHitTestVisible"/> gates the <b>leaf only</b> (children stay hittable);
    /// <see cref="UIElement.Visibility"/> gates subtrees; <c>HitTestCore</c> is the shaped-control
    /// escape hatch. Allocation-free integer-rect arithmetic. Valid after <see cref="RunRenderPass"/>.
    /// </summary>
    /// <param name="column">The window-local column.</param>
    /// <param name="row">The window-local row.</param>
    public UIElement? HitTest(int column, int row)
    {
        _root.VerifyAccess();
        ThrowIfDetached(); // the layer list is stale after Detach — fail loudly, not with ghost hits

        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            var zone = _layers[i];
            if (!zone.EffectiveClip.Contains(column, row))
                continue; // Rect.Empty contains nothing — hidden-ancestor layers skip here

            var hit = HitZone(zone.Boundary, column - zone.OffsetColumn, row - zone.OffsetRow);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    /// <summary>Marks every zone for re-raster (resize, renegotiation, palette swap).</summary>
    public void InvalidateAll()
    {
        _root.VerifyAccess();
        ThrowIfDetached();

        for (var i = 0; i < _layers.Count; i++)
            _layers[i].MarkRasterDirty();
    }

    /// <summary>
    /// Returns all zone scenes to the pool and unhooks from the root (root detach / window close).
    /// The tree is unusable afterwards; idempotent.
    /// </summary>
    public void Detach()
    {
        _root.VerifyAccess();
        if (_detached)
            return;

        _detached = true;
        if (_root.IsAttachedToTree)
            ClearZonePointers(_root);

        for (var i = 0; i < _layers.Count; i++)
            _layers[i].ReleaseScene();

        _layers.Clear();
        if (ReferenceEquals(_root.RenderTreeHost, this))
            _root.RenderTreeHost = null;
    }

    // ───────────────────────────── invalidation intake (UIElement routes here) ─────────────────────────────

    /// <summary>Schedules a zone-set rebuild at the start of the next pass (attach/detach/promotion/z-order churn).</summary>
    internal void MarkLayersDirty() => _layersDirty = true;

    /// <summary>The <see cref="UIElement.InvalidateVisual"/> route: marks the element's owning zone for re-raster.</summary>
    internal void OnRenderInvalidated(UIElement element)
        // A null/stale ZoneRoot means a rebuild is pending — the rebuild's membership-change
        // detection marks the affected zones itself.
        => element.ZoneRoot?.Zone?.MarkRasterDirty();

    /// <summary>
    /// The <see cref="UIElement.InvalidateComposite"/> route: parameters refresh is unconditional
    /// every pass, so the only work here is promotion detection — a composite-lane write that makes a
    /// non-boundary satisfy a boundary predicate schedules the (sticky) promotion.
    /// </summary>
    internal void OnCompositeInvalidated(UIElement element)
    {
        // The flag is what lets a pure composite-lane change (scroll offset, opacity, render
        // offset) reach the screen without any raster-dirty zone: S6's render gate reads
        // HasPendingRenderWork, runs the pass, and the unconditional walk publishes the new
        // parameters for the compositor's slide.
        _parametersDirty = true;

        if (!ReferenceEquals(element.ZoneRoot, element) && element.IsRenderBoundaryCandidate)
            _layersDirty = true;
    }

    // ───────────────────────────── test observability (internal) ─────────────────────────────

    /// <summary>The published parameters for <paramref name="boundary"/>'s layer (test observability).</summary>
    internal CompositeParameters GetPublishedParameters(UIElement boundary)
        => boundary.Zone?.Parameters
           ?? throw new InvalidOperationException($"'{boundary.GetType().Name}' is not a render boundary in this tree.");

    /// <summary>The zone scene for <paramref name="boundary"/> (test observability).</summary>
    internal Scene? GetScene(UIElement boundary) => boundary.Zone?.Scene;

    /// <summary>The boundary element of the layer at <paramref name="index"/>, bottom-up (test observability).</summary>
    internal UIElement GetLayerBoundary(int index) => _layers[index].Boundary;

    // ───────────────────────────── zone-set rebuild (promotions, attach/detach, z-order) ─────────────────────────────

    private void RebuildZones()
    {
        _layers.Clear();
        VisitRebuild(_root, zoneRoot: null);
        _layersDirty = false;
    }

    private void VisitRebuild(UIElement element, UIElement? zoneRoot)
    {
        // Sticky promotion: once a zone root, always a zone root until detach (IsPromotedBoundary).
        var isBoundary = zoneRoot is null || element.IsPromotedBoundary || element.IsRenderBoundaryCandidate;
        var newZoneRoot = isBoundary ? element : zoneRoot!;

        if (!ReferenceEquals(element.ZoneRoot, newZoneRoot))
        {
            // Membership changed (promotion / reparent / fresh attach): the old zone re-rasters
            // without this element, the new zone re-rasters with it — the four-step promotion's two
            // re-rasters fall out of exactly this. (For a boundary, the "new zone" is its own —
            // born dirty below.)
            element.ZoneRoot?.Zone?.MarkRasterDirty();
            element.ZoneRoot = newZoneRoot;
            if (!isBoundary)
                newZoneRoot.Zone?.MarkRasterDirty(); // ancestor visited first — its zone exists
        }

        if (isBoundary)
        {
            var zone = element.Zone;
            if (zone is null)
            {
                zone = new RenderZone(element);
                element.Zone = zone;
                element.IsPromotedBoundary = true; // sticky until detach
            }

            _layers.Add(zone);
        }

        var children = element.VisualChildrenList;
        if (children is null)
            return;

        var order = element.GetZOrder();
        for (var i = 0; i < order.Length; i++)
            VisitRebuild(children[order[i]], newZoneRoot);
    }

    private static void ClearZonePointers(UIElement element)
    {
        element.Zone?.ReleaseScene();
        element.Zone = null;
        element.ZoneRoot = null;
        element.IsPromotedBoundary = false;

        if (element.VisualChildrenList is { } children)
        {
            for (var i = 0; i < children.Count; i++)
                ClearZonePointers(children[i]);
        }
    }

    // ───────────────────────────── zone raster ─────────────────────────────

    private void EnsureScene(RenderZone zone)
    {
        var size = zone.Boundary.Bounds.Size;
        if (IsZeroArea(size))
        {
            // Zero-sized / Collapsed boundary (§5.5 pin): keep the previous scene if there is one
            // (else rent 1×1) and publish Clip = Rect.Empty — the layer slot survives, LayerCount
            // stays stable, and collapse/expand never trips the compositor's count-change path.
            zone.Scene ??= _scenePool.Rent(1, 1);
            return;
        }

        if (zone.Boundary is ScrollContentPresenter presenter)
        {
            // Banded scene (doc §13.2 / LD11): full content width, viewport + 2K rows — never the
            // extent (LD13: no budget knob, no degraded mode; memory bounded by construction).
            size = presenter.ComputeSceneSize();
        }

        if (zone.Scene is { } scene && scene.Columns == size.Columns && scene.Rows == size.Rows)
            return;

        zone.Scene?.Dispose(); // back to the pool
        zone.Scene = _scenePool.Rent(size.Columns, size.Rows);
        zone.RasterDirty = true; // scenes don't resize — a size change recreates and re-rasters
    }

    private void Raster(RenderZone zone)
    {
        _rasterZone = zone;
        try
        {
            zone.Scene!.Invalidate();
            zone.Scene.Draw(_drawCallback);
            zone.RasterDirty = false;
        }
        finally
        {
            _rasterZone = null;
        }
    }

    private void OnDrawZone(DrawingContext context)
    {
        var zone = _rasterZone!;

        // A banded scroll zone rasters CONTENT coordinates shifted up by the band start: the four
        // push-stack-covered paths (Set / fills / DrawText) ride one PushTranslate — negative-
        // capable, per-cell clipped, gradient-correct; the uncovered paths (strokes, formatted
        // text, content, shadows) fold the shift manually inside RenderContext and drop when they
        // straddle the band's top edge (K sizes those edges outside the viewport clip — doc §5.7).
        var bandStartRow = (zone.Boundary as ScrollContentPresenter)?.BandStartRow ?? 0;
        var bandScope = bandStartRow > 0 ? context.PushTranslate(0, -bandStartRow) : default;

        RenderPassGuard.Active = true;
        try
        {
            _renderContext.Begin(context, Capabilities, bandShiftRow: -bandStartRow, boundary: zone.Boundary);
            try
            {
                PaintElement(zone.Boundary, originColumn: 0, originRow: 0);
            }
            finally
            {
                _renderContext.End();
            }
        }
        finally
        {
            RenderPassGuard.Active = false;
            bandScope.Dispose();
        }
    }

    private void PaintElement(UIElement element, int originColumn, int originRow)
    {
        if (element.Visibility != Visibility.Visible)
            return; // Hidden subtrees paint nothing (terminal deviation ⑧: erasing them is the zone re-raster)

        _renderContext.PointAt(originColumn, originRow, element.Bounds.Size);
        _renderContext.OpenAmbientFigure();
        try
        {
            element.InvokeRender(_renderContext);
        }
        finally
        {
            _renderContext.CloseAmbientFigure();
        }

        var children = element.VisualChildrenList;
        if (children is null)
            return;

        var order = element.GetZOrder();
        for (var i = 0; i < order.Length; i++)
        {
            var child = children[order[i]];
            if (ReferenceEquals(child.ZoneRoot, child))
                continue; // a boundary child rasters in its own zone

            PaintElement(child, originColumn + child.Bounds.Column, originRow + child.Bounds.Row);
        }
    }

    // ───────────────────────────── boundary walk (composite parameters) ─────────────────────────────

    private void RefreshParameters()
    {
        // _layers is boundary-tree DFS pre-order: every parent boundary precedes its descendants, so
        // each zone reads its parent's freshly-computed caches.
        for (var i = 0; i < _layers.Count; i++)
        {
            var zone = _layers[i];
            var boundary = zone.Boundary;
            var parentZone = boundary.VisualParent?.ZoneRoot?.Zone;
            var stop = parentZone?.Boundary;

            var offsetColumn = parentZone?.OffsetColumn ?? 0;
            var offsetRow = parentZone?.OffsetRow ?? 0;
            var visible = parentZone?.EffectiveVisible ?? true;

            // Accumulate across the intermediate non-boundary chain from the parent boundary
            // (exclusive) down to this boundary (inclusive). Intermediates cannot carry composite
            // state (a non-zero RenderOffset / sub-1 Opacity promotes them before this walk runs),
            // but their offsets are folded anyway for robustness. Crossing into a scroll host's
            // frame subtracts its scroll (doc §5.7 — nested boundaries inherit scroll through this
            // walk; the cached zone offset stays the boundary's window origin in its own frame).
            for (var element = boundary; !ReferenceEquals(element, stop); element = element.VisualParent!)
            {
                var bounds = element.Bounds;
                offsetColumn += bounds.Column + element.RenderOffsetColumn;
                offsetRow += bounds.Row + element.RenderOffsetRow;
                if (element.Visibility != Visibility.Visible)
                    visible = false;
                if (element.VisualParent is not { } parent)
                    break;

                offsetColumn -= parent.ChildScrollOffsetColumn;
                offsetRow -= parent.ChildScrollOffsetRow;
            }

            var opacityProduct = Math.Clamp(boundary.Opacity, 0.0, 1.0) * (parentZone?.OpacityProduct ?? 1.0);
            var clip = ComputeClip(boundary, parentZone, offsetColumn, offsetRow, visible);

            zone.OffsetColumn = offsetColumn;
            zone.OffsetRow = offsetRow;
            zone.EffectiveVisible = visible;
            zone.OpacityProduct = opacityProduct;
            zone.EffectiveClip = clip;

            // The scene offset: where scene (0, 0) lands in window coordinates. For a scroll host
            // the scene holds content rows [BandStart, BandStart + bandLen) slid by −ScrollOffset —
            // the scroll slide is exactly this fold (invariant 3: a pure parameters change). The
            // clip above stays the viewport footprint (L216), so band content outside the viewport
            // never reaches the screen.
            var sceneOffsetColumn = offsetColumn;
            var sceneOffsetRow = offsetRow;
            if (boundary is ScrollContentPresenter presenter)
            {
                sceneOffsetColumn -= presenter.ScrollOffsetColumn;
                sceneOffsetRow += presenter.BandStartRow - presenter.ScrollOffsetRow;
            }

            var parameters = new CompositeParameters(sceneOffsetColumn, sceneOffsetRow, OpacityByte(opacityProduct), clip);
            if (parameters != zone.Parameters)
                zone.Parameters = parameters; // publish only when different — equality is the change detector
        }
    }

    private static Rect ComputeClip(UIElement boundary, RenderZone? parentZone, int offsetColumn, int offsetRow, bool visible)
    {
        var size = boundary.Bounds.Size;
        if (!visible || IsZeroArea(size))
            return Rect.Empty; // the empty-clip trick: layer retained, content hidden

        // Own footprint at the effective offset (zone content hard-clips at the scene extent, so
        // every boundary's clip is bounds-derived) ∩ the element-local CompositeClip translated to
        // window coordinates ∩ the parent boundary's effective clip.
        var columnStart = offsetColumn;
        var rowStart = offsetRow;
        var columnEnd = offsetColumn + size.Columns;
        var rowEnd = offsetRow + size.Rows;

        if (boundary.CompositeClip is { } composite)
        {
            columnStart = Math.Max(columnStart, offsetColumn + composite.Column);
            rowStart = Math.Max(rowStart, offsetRow + composite.Row);
            columnEnd = Math.Min(columnEnd, offsetColumn + composite.ColumnEnd);
            rowEnd = Math.Min(rowEnd, offsetRow + composite.RowEnd);
        }

        if (parentZone is not null)
        {
            var parentClip = parentZone.EffectiveClip;
            columnStart = Math.Max(columnStart, parentClip.Column);
            rowStart = Math.Max(rowStart, parentClip.Row);
            columnEnd = Math.Min(columnEnd, parentClip.ColumnEnd);
            rowEnd = Math.Min(rowEnd, parentClip.RowEnd);
        }

        columnStart = Math.Max(0, columnStart);
        rowStart = Math.Max(0, rowStart);
        columnEnd = Math.Min(columnEnd, LayoutMath.MaxExtent);
        rowEnd = Math.Min(rowEnd, LayoutMath.MaxExtent);

        return columnEnd > columnStart && rowEnd > rowStart
            ? new Rect(columnStart, rowStart, columnEnd - columnStart, rowEnd - rowStart)
            : Rect.Empty;
    }

    private static bool IsZeroArea(Size size) => size.Columns < 1 || size.Rows < 1;

    private static byte OpacityByte(double product)
        => (byte)Math.Round(Math.Clamp(product, 0.0, 1.0) * 255.0);

    private static Rect TranslateClip(in Rect clip, int offsetColumn, int offsetRow)
    {
        if (clip.IsEmpty)
            return Rect.Empty;

        var columnStart = Math.Clamp(clip.Column + offsetColumn, 0, LayoutMath.MaxExtent);
        var rowStart = Math.Clamp(clip.Row + offsetRow, 0, LayoutMath.MaxExtent);
        var columnEnd = Math.Clamp(clip.ColumnEnd + offsetColumn, 0, LayoutMath.MaxExtent);
        var rowEnd = Math.Clamp(clip.RowEnd + offsetRow, 0, LayoutMath.MaxExtent);
        return columnEnd > columnStart && rowEnd > rowStart
            ? new Rect(columnStart, rowStart, columnEnd - columnStart, rowEnd - rowStart)
            : Rect.Empty;
    }

    // ───────────────────────────── hit testing ─────────────────────────────

    private static UIElement? HitZone(UIElement element, int column, int row)
    {
        // p is in element's zone-local coordinates; this walks element's zone only — descendant
        // boundaries were already handled at the layer level (their layers sit above this zone's).
        if (element.VisualChildrenList is { } children)
        {
            // Children live in the element's CONTENT frame: a scroll host's children slide by
            // −ScrollOffset at composite time, so the content-frame point adds it back — hit
            // testing inherits scroll for free (doc §5.8 / L218).
            var contentColumn = column + element.ChildScrollOffsetColumn;
            var contentRow = row + element.ChildScrollOffsetRow;

            var order = element.GetZOrder();
            for (var i = order.Length - 1; i >= 0; i--)
            {
                var child = children[order[i]];
                if (ReferenceEquals(child.ZoneRoot, child))
                    continue; // boundary subtree — its own layer handles it
                if (child.Visibility != Visibility.Visible)
                    continue; // Hidden/Collapsed subtrees are not hit-testable

                // No parent-rect pre-clip (L200): the painter does not clip children to their
                // parent's rect, so a leaf outside its bounds cannot be hit, but a container is
                // descended regardless — its descendants may overflow it (LD17-style cross-axis
                // overflow stays hittable exactly where it painted).
                if (!child.Bounds.Contains(contentColumn, contentRow) && child.VisualChildrenList is not { Count: > 0 })
                    continue;

                var hit = HitZone(child, contentColumn - child.Bounds.Column, contentRow - child.Bounds.Row);
                if (hit is not null)
                    return hit;
            }
        }

        // The leaf gate: IsHitTestVisible applies to this element only (children above stayed
        // hittable — doc §5.8 "gate the leaf", a deliberate deviation from WPF subtree semantics).
        var bounds = element.Bounds;
        return element.IsHitTestVisible
               && column >= 0 && row >= 0 && column < bounds.Columns && row < bounds.Rows
               && element.InvokeHitTestCore(column, row)
            ? element
            : null;
    }

    private void ThrowIfDetached()
    {
        if (_detached)
            throw new InvalidOperationException("This RenderTree has been detached (its scenes were returned to the pool).");
    }
}
