using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>
/// A gradient whose color varies with (elliptical) distance from a center toward an optional focal
/// point — the SVG two-point radial. <see cref="Center"/> and <see cref="GradientOrigin"/> are
/// <see cref="RelativePoint"/>s, and <see cref="RadiusX"/> / <see cref="RadiusY"/> are fractions, all
/// relative to the paint bounds; the default is centered, filling the bounds as an ellipse
/// (focus = center).
/// </summary>
public sealed class RadialGradientBrush : GradientBrush
{
    /// <summary>
    /// Create a radial gradient. <paramref name="center"/> defaults to <see cref="RelativePoint.Center"/>,
    /// the radii to half the bounds (a centered ellipse that fills them), and
    /// <paramref name="gradientOrigin"/> (the focal point) to the center.
    /// </summary>
    public RadialGradientBrush(IReadOnlyList<GradientStop> stops,
                               RelativePoint? center = null,
                               double radiusX = 0.5, double radiusY = 0.5,
                               RelativePoint? gradientOrigin = null,
                               GradientSpread spread = GradientSpread.Pad, double opacity = 1.0)
        : base(stops, spread, opacity)
    {
        Center = RequireFinite(center ?? RelativePoint.Center, nameof(center));
        RadiusX = RequireFinite(radiusX, nameof(radiusX));
        RadiusY = RequireFinite(radiusY, nameof(radiusY));
        GradientOrigin = RequireFinite(gradientOrigin ?? Center, nameof(gradientOrigin));
    }

    /// <summary>
    /// Create a two-color radial gradient from <paramref name="centerColor"/> (offset 0, at the focal
    /// point) out to <paramref name="edgeColor"/> (offset 1, at the ellipse edge) — the common case.
    /// </summary>
    public RadialGradientBrush(Color centerColor, Color edgeColor,
                               RelativePoint? center = null,
                               double radiusX = 0.5, double radiusY = 0.5,
                               RelativePoint? gradientOrigin = null,
                               GradientSpread spread = GradientSpread.Pad, double opacity = 1.0)
        : this([new(0.0, centerColor), new(1.0, edgeColor)], center, radiusX, radiusY, gradientOrigin, spread, opacity) { }

    /// <summary>The ellipse center, relative to the paint bounds (default: centered).</summary>
    public RelativePoint Center { get; }

    /// <summary>The horizontal radius as a fraction of the bounds width (default: 0.5).</summary>
    public double RadiusX { get; }

    /// <summary>The vertical radius as a fraction of the bounds height (default: 0.5).</summary>
    public double RadiusY { get; }

    /// <summary>The focal point the gradient emanates from, relative to the bounds (default: the center).</summary>
    public RelativePoint GradientOrigin { get; }

    protected override double ComputeOffset(double px, double py, double width, double height)
    {
        double cx = Center.X * width, cy = Center.Y * height;
        double rx = RadiusX * width, ry = RadiusY * height;
        if (rx <= Epsilon || ry <= Epsilon) return 0.0;

        // Cast a ray from the focal point through the sample point to the unit circle (unit-ellipse
        // space); the gradient value is 1/s.
        double fx = (GradientOrigin.X * width - cx) / rx;
        double fy = (GradientOrigin.Y * height - cy) / ry;
        // A focal point outside the unit ellipse makes the ray cast degenerate — it can miss the circle
        // (disc < 0 everywhere → flat outer color) or yield a negative solution. Project it just inside,
        // per the SVG radial-gradient rule, so the gradient stays well-defined for any focal placement.
        double focalDist2 = fx * fx + fy * fy;
        const double focalLimit = 0.999;
        if (focalDist2 > focalLimit * focalLimit)
        {
            double scale = focalLimit / Math.Sqrt(focalDist2);
            fx *= scale;
            fy *= scale;
        }
        double pnx = (px - cx) / rx;
        double pny = (py - cy) / ry;
        double dx = pnx - fx, dy = pny - fy;

        double a = dx * dx + dy * dy;
        if (a <= Epsilon) return 0.0;                 // sample sits on the focal point

        double b = 2.0 * (fx * dx + fy * dy);
        double c = fx * fx + fy * fy - 1.0;
        double disc = b * b - 4.0 * a * c;
        if (disc < 0.0) return 1.0;                   // outside → outer color

        double s = (-b + Math.Sqrt(disc)) / (2.0 * a);
        return s <= Epsilon ? 1.0 : 1.0 / s;
    }
}
