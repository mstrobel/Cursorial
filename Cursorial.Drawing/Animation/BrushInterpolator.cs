using Cursorial.Animation;
using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Interpolates between two brushes of the <b>same shape</b>, producing a fresh immutable brush per call
/// — the per-frame target for an animated fill (e.g. a scrolling or pulsing gradient).
/// </summary>
/// <remarks>
/// Same-type pairs blend their parameters: <see cref="SolidColorBrush"/> blends its color (via
/// <see cref="Color.Lerp(Color, Color, double)"/>); gradients blend endpoints / center / radii / angle
/// (via <see cref="RelativePointInterpolator"/> and scalar lerp), opacity, and — when the stop <em>counts</em>
/// match — each stop's offset and color pairwise. The discrete <c>GradientSpread</c> snaps at the midpoint
/// (it can't be interpolated). Disparate shapes or mismatched stop counts can't blend meaningfully, so they
/// <b>snap</b> to the nearer endpoint (<c>from</c> when <c>progress &lt; 0.5</c>) — matching
/// <see cref="Color.Lerp(Color, Color, double)"/>'s non-RGB behavior. Stateless singleton.
/// </remarks>
public sealed class BrushInterpolator : IInterpolator<IBrush>
{
    /// <summary>The shared instance.</summary>
    public static BrushInterpolator Instance { get; } = new();

    private BrushInterpolator() { }

    /// <inheritdoc/>
    public IBrush Interpolate(IBrush from, IBrush to, double progress)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return (from, to) switch
               {
                   (SolidColorBrush a, SolidColorBrush b) =>
                       new SolidColorBrush(Color.Lerp(a.Color, b.Color, progress)),

                   (LinearGradientBrush a, LinearGradientBrush b) when a.Stops.Count == b.Stops.Count =>
                       new LinearGradientBrush(
                           LerpStops(a.Stops, b.Stops, progress),
                           LerpPoint(a.StartPoint, b.StartPoint, progress),
                           LerpPoint(a.EndPoint, b.EndPoint, progress),
                           progress < 0.5 ? a.Spread : b.Spread,
                           Lerp(a.Opacity, b.Opacity, progress)),

                   (RadialGradientBrush a, RadialGradientBrush b) when a.Stops.Count == b.Stops.Count =>
                       new RadialGradientBrush(
                           LerpStops(a.Stops, b.Stops, progress),
                           LerpPoint(a.Center, b.Center, progress),
                           Lerp(a.RadiusX, b.RadiusX, progress),
                           Lerp(a.RadiusY, b.RadiusY, progress),
                           LerpPoint(a.GradientOrigin, b.GradientOrigin, progress),
                           progress < 0.5 ? a.Spread : b.Spread,
                           Lerp(a.Opacity, b.Opacity, progress)),

                   (ConicGradientBrush a, ConicGradientBrush b) when a.Stops.Count == b.Stops.Count =>
                       new ConicGradientBrush(
                           LerpStops(a.Stops, b.Stops, progress),
                           LerpPoint(a.Center, b.Center, progress),
                           Lerp(a.AngleDegrees, b.AngleDegrees, progress),
                           Lerp(a.Opacity, b.Opacity, progress)),

                   // Disparate shapes / mismatched stop counts: nothing meaningful to blend — snap to the nearer.
                   _ => progress < 0.5 ? from : to
               };
    }

    private static GradientStop[] LerpStops(IReadOnlyList<GradientStop> a, IReadOnlyList<GradientStop> b, double t)
    {
        var stops = new GradientStop[a.Count];
        for (int i = 0; i < a.Count; i++)
            stops[i] = new GradientStop(Lerp(a[i].Offset, b[i].Offset, t), Color.Lerp(a[i].Color, b[i].Color, t));
        return stops;
    }

    private static RelativePoint LerpPoint(RelativePoint a, RelativePoint b, double t) =>
        RelativePointInterpolator.Instance.Interpolate(a, b, t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
