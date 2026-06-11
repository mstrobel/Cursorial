using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A gradient whose color sweeps around a center — a full 360° sweep clockwise from 12 o'clock,
/// rotated by an angle. <see cref="Center"/> is a <see cref="RelativePoint"/> relative to the paint
/// bounds (default: centered). Conic gradients always wrap, so <see cref="GradientSpread"/> does not
/// apply to them.
/// </summary>
public sealed class ConicGradientBrush : GradientBrush
{
    /// <summary>
    /// Create a conic gradient. <paramref name="center"/> defaults to <see cref="RelativePoint.Center"/>;
    /// <paramref name="angleDegrees"/> rotates the sweep clockwise from 12 o'clock.
    /// </summary>
    public ConicGradientBrush(IReadOnlyList<GradientStop> stops,
                              RelativePoint? center = null,
                              double angleDegrees = 0.0, double opacity = 1.0)
        : base(stops, GradientSpread.Pad, opacity)
    {
        Center = RequireFinite(center ?? RelativePoint.Center, nameof(center));
        AngleDegrees = RequireFinite(angleDegrees, nameof(angleDegrees));
    }

    /// <summary>
    /// Create a two-color conic gradient sweeping from <paramref name="start"/> (offset 0) around to
    /// <paramref name="end"/> (offset 1) — the common case.
    /// </summary>
    public ConicGradientBrush(Color start, Color end,
                              RelativePoint? center = null,
                              double angleDegrees = 0.0, double opacity = 1.0)
        : this([new(0.0, start), new(1.0, end)], center, angleDegrees, opacity) { }

    /// <summary>The sweep center, relative to the paint bounds (default: centered).</summary>
    public RelativePoint Center { get; }

    /// <summary>The rotation of the sweep, in degrees clockwise from 12 o'clock (default: 0).</summary>
    public double AngleDegrees { get; }

    protected override double ComputeOffset(double px, double py, double width, double height)
    {
        double dx = px - Center.X * width;
        double dy = py - Center.Y * height;
        double angle = Math.Atan2(dx, -dy) * (180.0 / Math.PI);   // clockwise from 12 o'clock
        double t = (angle - AngleDegrees) / 360.0;
        return t - Math.Floor(t);                                 // wrap into [0, 1)
    }

    // ComputeOffset already wraps; conic is always a full sweep, so spread is the identity.
    protected override double ApplySpread(double t) => t;
}
