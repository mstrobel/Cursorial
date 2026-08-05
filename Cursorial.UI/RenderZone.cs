using Cursorial.Drawing;
using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// Per-boundary render state owned by a <see cref="RenderTree"/>: the zone's cached
/// <see cref="Scene"/>, the published <see cref="CompositeParameters"/>, the raster-dirty bit, the
/// boundary caches (effective offset / clip / opacity) the §5.6 walk refreshes and hit testing
/// reads, and — for a group root — the private surface its whole boundary subtree composites into.
/// A <b>zone</b> = the boundary element plus all non-boundary descendants; descendant boundaries
/// start their own zones.
/// </summary>
internal sealed class RenderZone(UIElement boundary)
{
    /// <summary>The boundary element that roots this zone.</summary>
    internal UIElement Boundary { get; } = boundary;

    /// <summary>The zone's cached raster, rented from the shared <see cref="ScenePool"/>; recreated on size change (scenes don't resize).</summary>
    internal Scene? Scene;

    /// <summary>The published composite parameters — rewritten only when different (equality is the compositor's change detector).</summary>
    internal CompositeParameters Parameters;

    /// <summary>Whether the zone needs a re-raster on the next render pass (whole-zone; probe-1 verdict).</summary>
    internal bool RasterDirty = true;

    /// <summary>The boundary's effective window-coordinate column offset (bounds chain + render offsets).</summary>
    internal int OffsetColumn;

    /// <summary>The boundary's effective window-coordinate row offset.</summary>
    internal int OffsetRow;

    /// <summary>
    /// The boundary's clamped opacity. Its <b>own</b>, not a product with its ancestors': an ancestor
    /// boundary's translucency reaches this zone by compositing it — and everything else in the
    /// ancestor's subtree — through <see cref="GroupScene"/> and fading the surface once
    /// (<see cref="IsGroupRoot"/>). The one exception is a translucent ancestor that could not
    /// materialise a group (the feature gated off): nothing else would carry its opacity down, so
    /// there, and only there, the pre-group multiplied-down approximation still applies.
    /// </summary>
    internal double EffectiveOpacity = 1.0;

    /// <summary>Whether every element from this boundary up through the root is <see cref="Visibility.Visible"/>.</summary>
    internal bool EffectiveVisible = true;

    /// <summary>
    /// The cached effective clip in window coordinates: own footprint ∩ translated
    /// <see cref="UIElement.CompositeClip"/> ∩ ancestor boundary clips; <see cref="Rect.Empty"/> for
    /// hidden / zero-sized boundaries (the layer slot survives — the empty-clip trick).
    /// </summary>
    internal Rect EffectiveClip;

    // ───────────────────────────── boundary-subtree run (opacity groups) ─────────────────────────────

    /// <summary>
    /// This zone's index in its tree's layer list, assigned by the zone-set rebuild. The list is
    /// boundary-tree DFS pre-order, so this is also the zone's bottom-up composite position.
    /// </summary>
    internal int LayerIndex;

    /// <summary>
    /// The <b>exclusive</b> end of this boundary's subtree in its tree's layer list: the zones of
    /// this boundary and every boundary beneath it occupy the contiguous run
    /// <c>[LayerIndex, SubtreeEnd)</c>. Pre-order makes the run contiguous, which is what lets a
    /// group collect its members with two integers instead of a parallel zone tree.
    /// </summary>
    internal int SubtreeEnd;

    /// <summary>
    /// Whether this boundary roots an <b>opacity group</b>: its whole subtree composites into
    /// <see cref="GroupScene"/> at full strength and leaves as a single layer faded by
    /// <see cref="EffectiveOpacity"/>, so the boundary's translucency is applied exactly once
    /// instead of once per overlapping descendant. Sticky while groups are enabled (materialising
    /// moves the emitted layer count, the compositor's full-recomposite signal, so an opacity
    /// animation crossing 1.0 must not toggle it per frame) — but a group de-materialises when
    /// group compositing is switched off, which is a structural change and not a value change.
    /// </summary>
    internal bool IsGroupRoot;

    /// <summary>The group's private intermediate surface — non-null only while <see cref="IsGroupRoot"/>.</summary>
    internal Scene? GroupScene;

    /// <summary>
    /// The region of <see cref="GroupScene"/> the most recent inner composite that changed anything
    /// rewrote — published on the emitted layer as <see cref="SceneLayer.Damage"/> so the screen pass
    /// repaints a one-cell change inside the group as one cell, not as the group's whole footprint.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT reset by a pass that composited nothing: the group surface's
    /// <see cref="Scene.RasterVersion"/> does not move on such a pass either, so this still describes the
    /// one bump anybody downstream can still be behind on. It is the surface's damage, so it dies with the
    /// surface (<see cref="ReleaseGroupScene"/>) — a re-rented surface is a new
    /// <see cref="Scene"/> identity, which is a full repaint by itself.
    /// </remarks>
    internal Rect? GroupDamage;

    /// <summary>
    /// The intermediate-mode compositor driving <see cref="GroupScene"/>
    /// (<see cref="SceneCompositor.ForIntermediate"/>). Dropped whenever the surface is re-rented:
    /// a fresh compositor's first pass takes the full-recomposite path, which is exactly right over
    /// a freshly cleared surface.
    /// </summary>
    internal SceneCompositor? GroupCompositor;

    /// <summary>Marks the zone for re-raster on the next render pass.</summary>
    internal void MarkRasterDirty() => RasterDirty = true;

    /// <summary>Returns the zone's scenes to their pool (detach path); the next pass re-rents if the zone survives.</summary>
    internal void ReleaseScene()
    {
        Scene?.Dispose();
        Scene = null;
        ReleaseGroupScene();
        RasterDirty = true;
    }

    /// <summary>
    /// Returns the group surface to the pool and drops its compositor (de-materialisation, or a
    /// group-surface resize). Idempotent; the zone's own raster is untouched — a de-materialised
    /// group still composites, just flat.
    /// </summary>
    internal void ReleaseGroupScene()
    {
        GroupScene?.Dispose();
        GroupScene = null;
        GroupCompositor = null;
        GroupDamage = null;
    }
}
