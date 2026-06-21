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
}
