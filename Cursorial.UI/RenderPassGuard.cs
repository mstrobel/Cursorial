using System.Diagnostics;

namespace Cursorial.UI;

/// <summary>
/// The DEBUG render-pass read-only guard (design doc §5.5, invariant 2's enforcement leg): while a
/// zone rasters (<see cref="RenderTree"/>'s paint walk around <see cref="UIElement.Render"/>),
/// property writes, tree mutation, and <c>Invalidate*</c> calls throw — an <c>AffectsRender</c>
/// write from inside <c>Render</c> is a self-sustaining raster loop across frames, so the render
/// pass is read-only by contract. Compiles away entirely in Release builds.
/// </summary>
internal static class RenderPassGuard
{
    /// <summary>Whether the current thread is inside a zone raster (set around the paint walk).</summary>
    [ThreadStatic]
    internal static bool Active;

    /// <summary>Throws when called from inside a zone raster (DEBUG only; no-op in Release).</summary>
    [Conditional("DEBUG")]
    internal static void ThrowIfActive()
    {
        if (Active)
        {
            throw new InvalidOperationException(
                "The render pass is read-only (design doc §5.5): property writes, tree mutation, and " +
                "Invalidate* calls are not allowed inside Render(...). An AffectsRender write from Render " +
                "would re-dirty the zone every frame — move the mutation out of the render path.");
        }
    }
}
