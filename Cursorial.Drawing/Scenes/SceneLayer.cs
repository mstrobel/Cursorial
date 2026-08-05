using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A <see cref="Scene"/> paired with the <see cref="CompositeParameters"/> that place it on a
/// target. The z-stack passed to <see cref="SceneCompositor.Composite"/> is an ordered span of
/// these — earlier entries are lower (composited first).
/// </summary>
/// <param name="Scene">The scene to composite.</param>
/// <param name="Parameters">Offset / opacity / clip / blend for this layer.</param>
public readonly record struct SceneLayer(Scene Scene, CompositeParameters Parameters)
{
    /// <summary>A layer at the opaque identity (offset 0, opacity 255, no clip).</summary>
    public SceneLayer(Scene scene) : this(scene, CompositeParameters.Default) { }

    /// <summary>
    /// The z-index of the SURFACE this layer belongs to (all of a surface's layers share it). Used by the
    /// compositor to occlude a lower surface's graphics-protocol fragments under a higher OPAQUE surface
    /// without a same-surface zone falsely occluding its own image. 0 for the single-root path.
    /// </summary>
    public int SurfaceZ { get; init; }

    /// <summary>
    /// Whether this layer's surface is an OPAQUE occluder (a window / popup / badge — not the root). A
    /// graphics-protocol image the terminal draws above the cell grid is cropped (or suppressed) where a
    /// higher occluder surface overlaps it, so it can't show through the popup. False for the root / single-root.
    /// </summary>
    public bool IsOccluder { get; init; }

    /// <summary>
    /// The region of <see cref="Scene"/>, in the <b>scene's own</b> coordinates, that the single
    /// <see cref="Scene.RasterVersion"/> bump this layer is reporting actually rewrote — everything else in
    /// the scene is byte-for-byte what it was. <see langword="null"/> (the default) means "unknown", and a
    /// version change then costs the layer's whole footprint, which is what every producer but an
    /// intermediate surface can honestly say: <see cref="Scene.Draw"/> re-rasters the whole scene.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Scene.CompositeInto"/> can report something narrower, and this is how it travels: an
    /// opacity group collapses a whole subtree into one layer, so without it a one-cell change anywhere
    /// inside a translucent window would recomposite the entire window every frame — a caret blink in a
    /// background editor. The compositor takes this route <b>only</b> when the layer is otherwise completely
    /// unchanged (same scene, same <see cref="Parameters"/>) and the version moved by exactly one, so a
    /// stale or skipped hand-off degrades to the full footprint rather than to a stale cell.
    /// </remarks>
    public Rect? Damage { get; init; }
}
