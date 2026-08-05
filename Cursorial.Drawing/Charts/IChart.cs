using Cursorial.Input;
using Cursorial.Rendering;

namespace Cursorial.Drawing.Charts;

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

    /// <summary>
    /// Allows a chart to perform custom hit-testing when hosted in a UI. 
    /// </summary>
    /// <param name="position">The cell to test, in the same frame as the <c>area</c> the chart last
    /// <see cref="Render"/>ed into (a presenter passes pointer cells in its own zero-anchored local
    /// frame and renders at its zero-anchored bounds, so the frames coincide).</param>
    /// <param name="hitObject">Optional data to be provided to the hit tester</param>
    /// <returns><c>true</c> if the hit test was successful; otherwise, <c>false</c>.</returns>
    bool HitTest(CellPosition position, out object? hitObject)
    {
        hitObject = null;
        return false;
    }
}

/// <summary>
/// A chart capable of breaking up its series into multiple layers capable of being composited with alpha blending.
/// </summary>
public interface ILayeredChart : IChart
{
    /// <summary>
    /// Render each series into its OWN scene (a layer) sharing the resolved range, for the caller to
    /// composite with a <see cref="SceneCompositor"/>. Unlike <see cref="IChart.Render"/> (one surface, last-writer
    /// color at crossings), per-layer compositing lets <b>translucent area fills</b> alpha-blend where series
    /// overlap (red∩blue → purple — see §6), so this pays off when series set <see cref="ChartSeries.FillArea"/>
    /// with a translucent <see cref="ChartSeries.AreaBrush"/>. Each scene is sized to <paramref name="area"/>
    /// and drawn at its own origin; the caller offsets/clips via <c>CompositeParameters</c> and owns disposal.
    /// </summary>
    IReadOnlyList<Scene> ToLayers(in Rect area);
}