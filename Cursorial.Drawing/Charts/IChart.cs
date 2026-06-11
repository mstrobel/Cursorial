using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A chart is a draw-op, not a surface: it paints itself into a rectangular <c>area</c> of a
/// <see cref="DrawingContext"/>. "A chart is a scene" is then just the consumer wrapping it —
/// <c>scene.Draw(ctx =&gt; chart.Render(ctx, area))</c> — so a chart drops into a region of a larger
/// composed scene with no compositing of its own. Implementations clip to the context bounds and never
/// throw.
/// </summary>
public interface IChart
{
    /// <summary>Paint the chart into <paramref name="area"/> (scene-local cell coordinates) of <paramref name="context"/>.</summary>
    void Render(DrawingContext context, in Rect area);
}
