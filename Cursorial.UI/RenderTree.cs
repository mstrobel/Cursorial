using System.Diagnostics;
using System.Runtime.InteropServices;

using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.UI.Controls;

// ReSharper disable ForCanBeConvertedToForeach

namespace Cursorial.UI;

/// <summary>
/// The per-root render orchestrator (design doc §5.5–§5.6, §5.8): owns zone partitioning (one
/// Drawing <see cref="Scene"/> per <b>render boundary</b>, never per element), scene ownership via
/// the shared <see cref="ScenePool"/>, re-raster scheduling from <see cref="UIElement.InvalidateVisual"/>,
/// the unconditional per-pass boundary walk that refreshes <see cref="CompositeParameters"/>
/// (offset / opacity / clip changes <b>never</b> re-raster — invariant 3), the bottom-up
/// boundary-layer list (<see cref="CollectLayers"/>) with its opacity-group collapse, and
/// composite-order hit testing (<see cref="HitTest"/>).
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
/// origin (+<c>RenderOffset*</c>, −ancestor scroll), clip intersection, and effective visibility,
/// deciding group roots, and publishing <see cref="CompositeParameters"/> only when different. A
/// clean pass performs zero allocation and zero <see cref="UIElement.Render"/> calls.
/// </para>
/// <para>
/// <b>Opacity groups</b> (§5.6): a boundary's published opacity is its <b>own</b>, not a product
/// with its ancestors'. A translucent boundary that has descendant boundaries roots a
/// <b>group</b> — its whole subtree occupies the contiguous layer run
/// <c>[LayerIndex, SubtreeEnd)</c>, composites into one private surface at full strength, and
/// leaves as a single layer faded once. That is what makes a translucent subtree fade as a unit
/// instead of re-blending the translucent ancestor everywhere a descendant overlaps it. Group-ness
/// is sticky (it moves <see cref="EmittedLayerCount"/>, the compositor's full-recomposite signal)
/// but de-materialises when group compositing is switched off entirely — and only there, where no
/// group exists to carry an ancestor's fade, does the pre-group multiplied-down product survive.
/// </para>
/// </remarks>
public sealed class RenderTree
{
    private readonly UIElement _root;
    private readonly ScenePool _scenePool;
    private readonly List<RenderZone> _layers = [];
    private readonly RenderContext _renderContext = new();
    private readonly Action<DrawingContext> _drawCallback;

    // The member list handed to one group's inner composite. Reused across groups and across passes:
    // its capacity settles at the largest group's member count on the first pass that composites it,
    // after which Clear + Add is allocation-free — which is what keeps a clean frame at zero bytes
    // with a group live.
    private readonly List<SceneLayer> _groupScratch = [];
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
    /// The number of layers <see cref="CollectLayers"/> emits: one per boundary, except that a group
    /// root (<see cref="RenderZone.IsGroupRoot"/>) collapses its whole boundary subtree into a single
    /// layer. This — not <see cref="LayerCount"/> — is what the compositor sees, and a change in it is
    /// the compositor's full-recomposite signal. Refreshed by the per-pass boundary walk.
    /// </summary>
    internal int EmittedLayerCount { get; private set; }

    /// <summary>
    /// Whether translucent boundaries may materialise opacity <b>groups</b> (§5.6): a translucent
    /// boundary with descendant boundaries composites its subtree into a private surface and blends
    /// down once, instead of emitting flat siblings that each re-blend the translucent ancestor.
    /// </summary>
    /// <remarks>
    /// Both gates deliberately govern <b>eligibility</b>, not just the published opacity value.
    /// (a) The <b>RGB tiers</b> — <see cref="ColorDepth.Ansi256"/> and up.
    /// <see cref="Color.Composite"/> ignores alpha unless both operands are RGB, and only
    /// <see cref="ColorDepth.Ansi16"/>/<see cref="ColorDepth.NoColor"/> collapse a theme to palette
    /// indices; the Ansi256 spine is RGB (its own dialog/popup brushes ship translucent), so a blend
    /// survives there and quantization to the 256-color cube is the <c>FrameRenderer</c>'s
    /// <c>StyleQuantizer</c>'s business, not this predicate's. Below that a group would spend a
    /// surface and a second composite pass on a blend the tier discards —
    /// <see cref="CollectLayers"/>'s matching gate neutralises only the <em>window</em> opacity,
    /// never a zone's, so gating the value alone would leave the cost in place. (b) The user's
    /// translucency-effects switch. With no application at all (raster-only hosts, unit tests) this
    /// reads as an RGB tier, matching <see cref="CollectLayers"/>. The two tier gates must stay in
    /// step: a tier that materialises groups but forces window opacity to 1 (or the reverse) applies
    /// one half of the translucency model and not the other.
    /// </remarks>
    private static bool GroupCompositingEnabled
        => UIApplication.Current is not {} application ||
           application is { TranslucencyEnabled: true, ActualThemeVariant.Tier: >= ColorDepth.Ansi256 };

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
                if (zone is { RasterDirty: true, Boundary.Visibility: Visibility.Visible } &&
                    !IsZeroArea(zone.Boundary.Bounds.Size))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Runs one render pass: pending boundary promotions → re-raster dirty zones (whole-zone) →
    /// the unconditional boundary walk publishing <see cref="CompositeParameters"/> on change →
    /// the inner composite of every opacity group's private surface.
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
            if (zone is { RasterDirty: true, Scene: not null, Boundary.Visibility: Visibility.Visible } &&
                !IsZeroArea(zone.Boundary.Bounds.Size))
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

        // Deepest-first, which in a pre-order list is plain reverse index order: a nested group has to
        // have composited its own surface before the group that contains it reads that surface as one
        // of its members. Unconditional, like the parameters walk above and for the same reason — the
        // inner compositor's own early-out costs nothing when nothing moved, where a "group dirty"
        // flag would trade that free win for a silent stale-frame bug class.
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            if (_layers[i].IsGroupRoot)
                CompositeGroup(_layers[i]);
        }
    }

    /// <summary>
    /// Appends this tree's boundary layers to <paramref name="target"/> <b>bottom-up</b> in screen
    /// coordinates (the window position/opacity folded in): pre-order DFS of the boundary tree with
    /// the stable <c>(ZIndex, index)</c> sibling sort — a zone's own scene is always the lowest layer
    /// of its subtree (the zone-base rule). A <b>group root</b> emits its private surface instead, once,
    /// and its whole subtree run is skipped: the emitted count is <see cref="EmittedLayerCount"/>, not
    /// <see cref="LayerCount"/>. Call after <see cref="RunRenderPass"/>. Allocation-free beyond the
    /// caller's list growth.
    /// </summary>
    public void CollectLayers(List<SceneLayer> target, int windowOffsetColumn = 0, int windowOffsetRow = 0, double windowOpacity = 1.0,
                              int surfaceZ = 0, bool isOccluder = false, List<string>? boundaryDescriptions = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        _root.VerifyAccess();
        ThrowIfDetached();

        // Honor the opacity capability when switching theme variants at runtime. Opacity needs RGB on
        // both sides of Color.Composite, which is every tier from Ansi256 up; Ansi16/NoColor collapse
        // to palette indices, where the blend is discarded and the fade would only cost. This is the
        // window leg of the TIER half of GroupCompositingEnabled's policy — the two must agree tier for
        // tier. It deliberately does NOT read the translucency switch: that switch picks between group
        // and flat compositing, and opacity itself survives either way (see TranslucencyEnabled).
        if (UIApplication.Current?.ActualThemeVariant is { Tier: < ColorDepth.Ansi256 })
            windowOpacity = 1;

        // ReSharper disable once CompareOfFloatsByEqualityOperator
        var folded = windowOffsetColumn != 0 || windowOffsetRow != 0 || windowOpacity != 1.0;

        // A struct closure (never converted to a delegate), so the shared emit stays allocation-free.
        void Emit(RenderZone zone, Scene scene, bool isGroup)
        {
            var parameters = zone.Parameters;

            if (folded)
            {
                var clip = parameters.Clip is {} c
                               ? TranslateClip(c, windowOffsetColumn, windowOffsetRow)
                               : (Rect?) null;

                parameters = new CompositeParameters(
                    parameters.OffsetColumn + windowOffsetColumn,
                    parameters.OffsetRow + windowOffsetRow,
                    OpacityByte(zone.EffectiveOpacity * windowOpacity),
                    clip,
                    parameters.Mode);
            }

            // Damage travels only on a group layer, and only because a group layer is the only one whose
            // version bump does NOT mean "the whole scene was re-rastered": an ordinary zone's raster is
            // whole-scene (Scene.Draw), so it has nothing narrower to say and null is the honest answer.
            // It is scene-local, so the window fold above — which is entirely about where the scene LANDS —
            // leaves it alone.
            target.Add(new SceneLayer(scene, parameters)
                       {
                           SurfaceZ = surfaceZ,
                           IsOccluder = isOccluder,
                           Damage = isGroup ? zone.GroupDamage : null
                       });

            if (boundaryDescriptions is not null)
            {
                var description = zone.Boundary.GetType().Name;

                if (zone.Boundary.Name is { Length: > 0 } name)
                    description += $"#{name}";

                // The descriptions list is index-locked to the emitted layers (WindowManager.SampleCell
                // pairs them positionally), so a collapsed subtree has to say that it is one — otherwise
                // an inspector reports a group's surface under the group root's own bare name and the
                // boundaries inside it look as though they simply vanished.
                boundaryDescriptions.Add(isGroup ? description + " (group)" : description);
            }
        }

        for (var i = 0; i < _layers.Count; )
        {
            var zone = _layers[i];

            // The group collapse — structurally the same walk as CountEmittedLayers, so the two agree.
            // The emitted parameters are the group root's own, verbatim: the surface's origin IS the
            // root's scene origin, its clip IS the root's clip, and the root's opacity is what fades it —
            // applied here, once, to the whole composed subtree instead of once per member overlapping
            // the root.
            if (zone.IsGroupRoot)
            {
                if (zone.GroupScene is { } surface)
                    Emit(zone, surface, isGroup: true);

                i = Math.Max(zone.SubtreeEnd, i + 1); // never step backwards off a stale/sticky run
                continue;
            }

            if (zone.Scene is { } scene)
                Emit(zone, scene, isGroup: false); // else RunRenderPass has not run yet for this zone

            i++;
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
        _groupScratch.Clear(); // it holds Scene references that just went back to the pool
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
    internal CompositeParameters GetPublishedParameters(UIElement boundary) => RequireZone(boundary).Parameters;

    /// <summary>The zone scene for <paramref name="boundary"/> (test observability).</summary>
    internal Scene? GetScene(UIElement boundary) => boundary.Zone?.Scene;

    /// <summary>Whether <paramref name="boundary"/> roots an opacity group (test observability).</summary>
    internal bool IsGroupRoot(UIElement boundary) => RequireZone(boundary).IsGroupRoot;

    /// <summary>The group's private surface, or null when it has not materialised (test observability).</summary>
    internal Scene? GetGroupScene(UIElement boundary) => RequireZone(boundary).GroupScene;

    /// <summary>The boundary's <b>own</b> effective opacity — no ancestor product (test observability).</summary>
    internal double GetEffectiveOpacity(UIElement boundary) => RequireZone(boundary).EffectiveOpacity;

    /// <summary>The half-open layer-list run <c>[Start, End)</c> this boundary's subtree occupies (test observability).</summary>
    internal (int Start, int End) GetSubtreeRun(UIElement boundary)
    {
        var zone = RequireZone(boundary);
        return (zone.LayerIndex, zone.SubtreeEnd);
    }

    private static RenderZone RequireZone(UIElement boundary)
        => boundary.Zone
           ?? throw new InvalidOperationException($"'{boundary.GetType().Name}' is not a render boundary in this tree.");

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

        RenderZone? zone = null;
        if (isBoundary)
        {
            zone = element.Zone;
            if (zone is null)
            {
                zone = new RenderZone(element);
                element.Zone = zone;
                element.IsPromotedBoundary = true; // sticky until detach
            }

            zone.LayerIndex = _layers.Count;
            _layers.Add(zone);
        }

        if (element.VisualChildrenList is { } children)
        {
            var order = element.GetZOrder();
            for (var i = 0; i < order.Length; i++)
                VisitRebuild(children[order[i]], newZoneRoot);
        }

        // Closing the run on the way out is what makes [LayerIndex, SubtreeEnd) contiguous: every
        // boundary added between this element's own Add and here is a descendant of it.
        if (zone is not null)
            zone.SubtreeEnd = _layers.Count;
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

    // ───────────────────────────── opacity groups (the inner pass) ─────────────────────────────

    /// <summary>
    /// Composites a group root's whole boundary subtree into the group's private surface, at
    /// <b>full strength</b>, and leaves the group's own opacity to the single layer
    /// <see cref="CollectLayers"/> then emits. That split is the fix: the flat model gave every member
    /// a copy of the ancestor's fade, so wherever a member overlapped the ancestor the fade landed
    /// twice; here it lands once, on the composed result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface's origin is the group root's own <b>scene</b> origin, so a member enters at its
    /// published scene offset minus that origin and its window clip translated by the same amount. The
    /// caches themselves stay in window coordinates — group-local coordinates exist only for the
    /// duration of this call, which is why <see cref="HitTest"/> and the caret service need no notion
    /// of groups at all.
    /// </para>
    /// <para>
    /// Every member enters with <c>SurfaceZ = 0, IsOccluder = false</c>. The compositor's fragment
    /// occlusion needs a <em>strictly higher</em> surface z, and all of a group's members belong to one
    /// surface, so the inner pass's occlusion loop is a no-op by construction. Cross-surface occlusion
    /// is unaffected: the group's emitted layer carries the surface's real stamps, and collapsing a
    /// subtree removes nothing from that computation.
    /// </para>
    /// </remarks>
    private void CompositeGroup(RenderZone group)
    {
        if (group.Scene is null)
            return; // the raster loop unwound early (a fatal draw) — nothing to compose yet

        EnsureGroupScene(group);

        _groupScratch.Clear();

        // The zone-base rule, at full strength: a zone's own raster is the bottom layer of its subtree,
        // and here it is also the bottom member of its own group. Opacity 255 — the group's fade is
        // applied to the surface, not to the members, and applying it here as well is the double-blend.
        AddGroupMember(group, group, group.Scene, opacity: 255);

        for (var i = group.LayerIndex + 1; i < group.SubtreeEnd; )
        {
            var member = _layers[i];

            // A nested group has already composited its own surface (this runs deepest-first), so it
            // joins as ONE member faded by its own opacity, and its members never appear here. That is
            // what makes a nested translucent boundary apply its fade on its own way out. The skip is
            // unconditional on group-ness, not on the surface existing: a group whose surface is missing
            // contributes nothing this frame, but its members must not fall back into the enclosing
            // group, where they would composite at full strength and lose their own fade.
            if (member.IsGroupRoot)
            {
                // A nested group carries its own damage inwards for the same reason the outer one carries
                // it onwards: to the enclosing group it is one layer whose version bumps for any change
                // beneath it, so a translucent panel inside a translucent window would otherwise re-blend
                // the whole panel into the window's surface on every caret tick.
                if (member.GroupScene is { } nested)
                    AddGroupMember(group, member, nested, OpacityByte(member.EffectiveOpacity), member.GroupDamage);

                i = Math.Max(member.SubtreeEnd, i + 1);
                continue;
            }

            if (member.Scene is { } scene)
                AddGroupMember(group, member, scene, OpacityByte(member.EffectiveOpacity));

            i++;
        }

        // The rewritten region is the group's DAMAGE, and carrying it is not an optimisation to schedule
        // later — it is what makes collapsing a subtree affordable at all. A group's emitted layer reports
        // one version bump for a change anywhere inside it, and a version bump costs the layer's whole
        // footprint at the screen pass; a translucent window's group subtree is essentially its whole visual
        // tree (ScrollContentPresenter, TextPresenter and DrawnContentPresenter are all boundaries from
        // attach), so without this a caret blink in a background editor recomposites the entire window every
        // frame. A pass that changed nothing leaves the previous region standing: the surface's
        // RasterVersion did not move either, so that region is still the one bump a reader can be behind on.
        if (group.GroupScene!.CompositeInto(group.GroupCompositor!, CollectionsMarshal.AsSpan(_groupScratch)) is { } rewritten)
            group.GroupDamage = rewritten;
    }

    private void AddGroupMember(RenderZone group, RenderZone member, Scene scene, byte opacity, Rect? damage = null)
    {
        var originColumn = group.Parameters.OffsetColumn;
        var originRow = group.Parameters.OffsetRow;
        var clip = TranslateClip(member.EffectiveClip, -originColumn, -originRow);

        VerifyGroupContainment(group, member, clip, originColumn, originRow);

        _groupScratch.Add(new SceneLayer(scene,
                                         new CompositeParameters(member.Parameters.OffsetColumn - originColumn,
                                                                 member.Parameters.OffsetRow - originRow,
                                                                 opacity,
                                                                 clip,
                                                                 member.Parameters.Mode))
                         {
                             Damage = damage
                         });
    }

    /// <summary>
    /// Rents (or re-rents) the group's private surface, mirroring <see cref="EnsureScene"/>. Called from
    /// the group pass rather than the raster loop because group-ness is decided by
    /// <see cref="RefreshParameters"/>, which runs after it — a group materialising this pass would
    /// otherwise have no surface to composite into until the next one.
    /// </summary>
    /// <remarks>
    /// The surface is the group root's own scene geometry, size and origin both. Sufficient by
    /// containment: <see cref="ComputeClip"/> intersects every boundary's clip with its parent
    /// boundary's, so transitively the whole group composites inside the root's own footprint. Matching
    /// the root's <em>scene</em> (band-folded for a scroll host) rather than its bounds additionally
    /// makes the group's contents scroll-invariant, so a scroll stays a pure parameters change on the
    /// emitted layer instead of forcing an inner recomposite per tick.
    /// </remarks>
    private void EnsureGroupScene(RenderZone zone)
    {
        var scene = zone.Scene!;
        if (zone.GroupScene is { } surface && surface.Columns == scene.Columns && surface.Rows == scene.Rows)
            return;

        zone.ReleaseGroupScene();
        zone.GroupScene = _scenePool.Rent(scene.Columns, scene.Rows);

        // A fresh compositor's first pass takes the full-recomposite path, which is exactly right over a
        // surface the pool just cleared to transparent — and mandatory, since a reused compositor's
        // stashed state describes the buffer that went back to the pool.
        zone.GroupCompositor = SceneCompositor.ForIntermediate();
    }

    /// <summary>
    /// The containment invariant the group surface's size rests on, checked from both sides: a member's
    /// window clip never starts before the group origin (so <see cref="TranslateClip"/>'s clamp at zero
    /// never silently swallows a clip edge) and never ends past the surface.
    /// </summary>
    /// <remarks>
    /// A violation is not a glitch. <c>CellBuffer.AddFragment</c> validates its coordinates and throws, so
    /// a member whose rebased clip escaped the surface would take the frame down from the fragment
    /// pass-through rather than paint something wrong — and the failure would surface far from its cause.
    /// </remarks>
    [Conditional("DEBUG")]
    private static void VerifyGroupContainment(RenderZone group, RenderZone member, in Rect clip,
                                               int originColumn, int originRow)
    {
        var window = member.EffectiveClip;
        if (window.IsEmpty)
            return;

        var surface = group.GroupScene!;
        Debug.Assert(window.Column >= originColumn && window.Row >= originRow &&
                     clip.ColumnEnd <= surface.Columns && clip.RowEnd <= surface.Rows,
                     $"'{member.Boundary.GetType().Name}' composites outside the group surface of " +
                     $"'{group.Boundary.GetType().Name}': window clip {window} against origin " +
                     $"({originColumn}, {originRow}) and surface {surface.Columns}×{surface.Rows}.");
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

        // A banded scroll zone rasters CONTENT coordinates shifted up by the band start: every draw
        // path rides this one PushTranslate — negative-capable, per-cell clipped, gradient-correct —
        // since the P2.5 ① Drawing push-stack coverage rework (a draw straddling the band's top edge
        // clips per cell; K keeps those edges outside the viewport clip — doc §5.7).
        var bandStartRow = (zone.Boundary as ScrollContentPresenter)?.BandStartRow ?? 0;
        var bandScope = bandStartRow > 0 ? context.PushTranslate(0, -bandStartRow) : default;

        RenderPassGuard.Active = true;
        try
        {
            _renderContext.Begin(context, Capabilities);
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
        // Read the gate once per pass, not per zone: it reaches a thread-local global, and a flip
        // mid-walk would materialise a group whose members were already decided against.
        var groupsEnabled = GroupCompositingEnabled;

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

            // The boundary's OWN opacity, with no ancestor product folded in: a translucent ancestor's
            // translucency reaches this zone by fading the GROUP SURFACE this zone composites into,
            // exactly once — folding it in here as well would apply it twice, which is precisely the
            // flat model's double-blend bug.
            //
            // The multiply below is that rule's escape hatch, and it is a no-op wherever groups are
            // enabled: a translucent boundary that has a PARENT relationship to this zone necessarily
            // has a descendant boundary, so it satisfies the group-root predicate and contributes 1.0
            // here. Only where groups are gated off does anything survive it — and there nothing else
            // carries an ancestor's opacity down, so the pre-group multiplied-down approximation has
            // to stand in rather than the ancestor's translucency vanishing outright. Now that the tier
            // gate reads ">= Ansi256", the reachable-AND-visible path is the TRANSLUCENCY SWITCH: it
            // gates groups off on the RGB tiers, where the multiplied-down product still blends through
            // Color.Composite and is plainly on screen. The palette tiers reach it too, but there a
            // themed app's colors are palette indices that Composite returns verbatim, so only whatever
            // RGB the app painted itself still shows the fade.
            var ownOpacity = Math.Clamp(boundary.Opacity, 0.0, 1.0);
            var opacity = parentZone is { IsGroupRoot: false } flatParent
                              ? ownOpacity * flatParent.EffectiveOpacity
                              : ownOpacity;

            var clip = ComputeClip(boundary, parentZone, offsetColumn, offsetRow, visible);

            zone.OffsetColumn = offsetColumn;
            zone.OffsetRow = offsetRow;
            zone.EffectiveVisible = visible;
            zone.EffectiveOpacity = opacity;
            zone.EffectiveClip = clip;
            UpdateGroupRoot(zone, ownOpacity, groupsEnabled);

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

            // ReSharper disable RedundantCheckBeforeAssignment
            var parameters = new CompositeParameters(sceneOffsetColumn, sceneOffsetRow, OpacityByte(opacity), clip);
            if (parameters != zone.Parameters)
                zone.Parameters = parameters; // publish only when different — equality is the change detector
            // ReSharper restore RedundantCheckBeforeAssignment
        }

        EmittedLayerCount = CountEmittedLayers();
    }

    /// <summary>
    /// The group-root predicate: a translucent boundary that actually has descendant boundaries to
    /// group. A translucent <em>leaf</em> boundary is not a group root — it has nothing to blend
    /// against inside itself, so a surface would buy nothing and its own opacity already rides its
    /// single layer.
    /// </summary>
    /// <remarks>
    /// Sticky in the same way and for the same reason boundary promotion is (§5.5): materialising
    /// moves <see cref="EmittedLayerCount"/>, which the compositor reads as "recomposite everything",
    /// so an opacity animation crossing 1.0 must not toggle it every frame. Stickiness is only sound
    /// because a group at opacity 1 composites bit-identically to the flat stack it replaces.
    /// Disabling group compositing, on the other hand, <b>does</b> de-materialise: that is a
    /// structural change (the user turned the feature off), not a value change, and leaving the
    /// surfaces rented would keep paying for a feature nobody asked for.
    /// </remarks>
    private static void UpdateGroupRoot(RenderZone zone, double opacity, bool groupsEnabled)
    {
        var isGroupRoot = groupsEnabled &&
                          (zone.IsGroupRoot || (opacity < 1.0 && zone.SubtreeEnd > zone.LayerIndex + 1));

        if (isGroupRoot == zone.IsGroupRoot)
            return;

        zone.IsGroupRoot = isGroupRoot;
        if (!isGroupRoot)
            zone.ReleaseGroupScene();
    }

    private int CountEmittedLayers()
    {
        var count = 0;
        for (var i = 0; i < _layers.Count; count++)
        {
            var zone = _layers[i];

            // Math.Max, not a bare SubtreeEnd: group-ness outlives the subtree that earned it (it is
            // sticky), so a rebuild that detached every descendant boundary can leave SubtreeEnd at
            // LayerIndex + 1 — and a stale/zero one must never walk this loop backwards.
            i = zone.IsGroupRoot ? Math.Max(zone.SubtreeEnd, i + 1) : i + 1;
        }

        return count;
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
