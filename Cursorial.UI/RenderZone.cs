using Cursorial.Drawing;
using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// Per-boundary render state owned by a <see cref="RenderTree"/>: the zone's cached
/// <see cref="Scene"/>, the published <see cref="CompositeParameters"/>, the raster-dirty bit, and
/// the boundary caches (effective offset / clip / opacity product) the §5.6 walk refreshes and hit
/// testing reads. A <b>zone</b> = the boundary element plus all non-boundary descendants; descendant
/// boundaries start their own zones.
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

    /// <summary>The multiplied-down opacity product of this boundary and its ancestor boundaries (the opacity-group approximation).</summary>
    internal double OpacityProduct = 1.0;

    /// <summary>Whether every element from this boundary up through the root is <see cref="Visibility.Visible"/>.</summary>
    internal bool EffectiveVisible = true;

    /// <summary>
    /// The cached effective clip in window coordinates: own footprint ∩ translated
    /// <see cref="UIElement.CompositeClip"/> ∩ ancestor boundary clips; <see cref="Rect.Empty"/> for
    /// hidden / zero-sized boundaries (the layer slot survives — the empty-clip trick).
    /// </summary>
    internal Rect EffectiveClip;

    /// <summary>Marks the zone for re-raster on the next render pass.</summary>
    internal void MarkRasterDirty() => RasterDirty = true;

    /// <summary>Returns the zone's scene to its pool (detach path); the next pass re-rents if the zone survives.</summary>
    internal void ReleaseScene()
    {
        Scene?.Dispose();
        Scene = null;
        RasterDirty = true;
    }
}
