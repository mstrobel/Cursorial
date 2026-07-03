namespace Cursorial.UI;

public abstract partial class UIElement
{
    // ───────────────────────────── zone pointers (RenderTree-owned) ─────────────────────────────

    /// <summary>
    /// The boundary element rooting the zone this element rasters into (self for boundaries).
    /// Assigned by the <see cref="RenderTree"/> rebuild walk; cleared on detach; null before the
    /// first render pass over a freshly attached subtree.
    /// </summary>
    internal UIElement? ZoneRoot;

    /// <summary>The element's zone record — non-null only on zone roots (render boundaries).</summary>
    internal RenderZone? Zone;

    /// <summary>
    /// The sticky promotion flag (design doc §5.5): once an element has minted a zone it stays a
    /// boundary until detach — layer count never oscillates. Cleared by the detach walk.
    /// </summary>
    internal bool IsPromotedBoundary;

    /// <summary>The render tree over this root element (root-only field, like the layout manager).</summary>
    internal RenderTree? RenderTreeHost;

    /// <summary>The render tree owning this element's visual root, or null when detached / none created.</summary>
    internal RenderTree? GetRenderTree() => _visualRoot?.RenderTreeHost;

    // ───────────────────────────── boundary predicates ─────────────────────────────

    /// <summary>
    /// Whether a boundary predicate currently holds (design doc §5.5 predicates ①–⑤ and ⑦, plus the
    /// always-boundary seam ⑥). Live values only — the <em>sticky</em> half of the contract is
    /// <see cref="IsPromotedBoundary"/>; use <see cref="IsEffectiveRenderBoundary"/> for routing.
    /// </summary>
    internal bool IsRenderBoundaryCandidate
        => _visualParent is null
           || IsRenderBoundary
           || IsAlwaysRenderBoundary
           || Opacity < 1.0
           || RenderOffsetColumn != 0
           || RenderOffsetRow != 0
           || ClipToBounds
           || CompositeClip is not null;

    /// <summary>Whether the element is (or will be, next pass) a render boundary — promotion is sticky.</summary>
    internal bool IsEffectiveRenderBoundary => IsPromotedBoundary || IsRenderBoundaryCandidate;

    /// <summary>
    /// The always-a-boundary type seam (design doc §5.5 predicate ⑥): <c>ScrollContentPresenter</c>
    /// overrides this when it lands at T4 so it is a boundary from attach, never via mid-life
    /// promotion. Internal-virtual so the type predicate stays a compile-time fact.
    /// </summary>
    internal virtual bool IsAlwaysRenderBoundary => false;

    // ───────────────────────────── the render virtual ─────────────────────────────

    /// <summary>
    /// Paints the element in <b>element-local</b> coordinates onto its zone's scene. Called by the
    /// zone painter in cached <c>(ZIndex, index)</c> order, parent before children, with an ambient
    /// per-element figure open (sibling pen strokes never junction-merge). The render pass is
    /// read-only: property writes, tree mutation, and <c>Invalidate*</c> throw in DEBUG builds. Do
    /// not capture <paramref name="context"/> — it is re-pointed per element.
    /// </summary>
    protected virtual void Render(RenderContext context)
    {
    }

    /// <summary>The zone painter's bridge to the protected <see cref="Render"/> virtual.</summary>
    internal void InvokeRender(RenderContext context) => Render(context);

    /// <summary>The hit-test walk's bridge to the protected <c>HitTestCore</c> virtual.</summary>
    internal bool InvokeHitTestCore(int column, int row) => HitTestCore(column, row);

    // ───────────────────────────── cached z-order ─────────────────────────────

    private int[]? _zOrder;
    private bool _zOrderDirty = true;

    /// <summary>Invalidates the cached <c>(ZIndex, index)</c> sibling order (collection mutation, child ZIndex change).</summary>
    internal void InvalidateZOrder() => _zOrderDirty = true;

    /// <summary>
    /// The cached paint/hit order of this element's visual children: indices into the children list,
    /// stable-sorted by <c>(ZIndex, index)</c> ascending. Shared by the zone painter (ascending),
    /// hit-test descent (descending), and the boundary-layer rebuild. Rebuilt lazily after
    /// <see cref="InvalidateZOrder"/>; allocation-free when clean.
    /// </summary>
    internal ReadOnlySpan<int> GetZOrder()
    {
        if (_visualChildren is not { Count: > 0 } children)
            return default;

        var count = children.Count;
        if (!_zOrderDirty && _zOrder is { } cached && cached.Length >= count)
            return cached.AsSpan(0, count);

        if (_zOrder is null || _zOrder.Length < count)
            _zOrder = new int[count];

        var order = _zOrder;
        for (var i = 0; i < count; i++)
            order[i] = i;

        // Stable insertion sort on ZIndex (ascending; ties keep collection order). Sibling counts
        // are small and the sort runs only after an invalidation.
        for (var i = 1; i < count; i++)
        {
            var index = order[i];
            var zIndex = children[index].ZIndex;
            var j = i - 1;
            while (j >= 0 && children[order[j]].ZIndex > zIndex)
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = index;
        }

        _zOrderDirty = false;
        return order.AsSpan(0, count);
    }

    // ───────────────────────────── render-side invalidation ─────────────────────────────

    /// <summary>Render-dirty bit (the pre-T3 seam; the live route is <see cref="RenderTree.OnRenderInvalidated"/>).</summary>
    internal bool RenderDirty;

    /// <summary>Composite-refresh bit (the pre-T3 seam; the live route is <see cref="RenderTree.OnCompositeInvalidated"/>).</summary>
    internal bool CompositeDirty;

    /// <summary>
    /// Marks the element's render zone for re-raster (content/brush-shaped changes — the
    /// <see cref="PropertyEffects.AffectsRender"/> lane). The zone re-rasters <b>whole</b> on the
    /// next <see cref="RenderTree.RunRenderPass"/> (the probe-1 verdict: no partial-raster
    /// machinery). Never touches layout state.
    /// </summary>
    public void InvalidateVisual()
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();
        RenderDirty = true;
        GetRenderTree()?.OnRenderInvalidated(this);
    }

    /// <summary>
    /// Schedules a composite-parameter refresh on the cached raster (offset/opacity/clip-shaped
    /// changes — the <see cref="PropertyEffects.AffectsComposite"/> lane). Never re-rasters
    /// (invariant 3): animated slides, fades, and reveals ride this path at re-composite cost.
    /// </summary>
    public void InvalidateComposite()
    {
        VerifyAccess();
        RenderPassGuard.ThrowIfActive();
        CompositeDirty = true;
        GetRenderTree()?.OnCompositeInvalidated(this);
    }
}
