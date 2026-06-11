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
}
