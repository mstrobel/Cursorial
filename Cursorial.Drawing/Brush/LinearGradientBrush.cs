using Cursorial.Output;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A gradient whose color varies along the line from a start point to an end point. Endpoints are
/// <see cref="RelativePoint"/>s relative to the paint bounds (so <c>(0, 0)</c> is the top-left corner
/// and <c>(1, 1)</c> the bottom-right; values outside <c>[0, 1]</c> are allowed). The default is a
/// horizontal left→right sweep (<see cref="RelativePoint.TopLeft"/> → <see cref="RelativePoint.TopRight"/>).
/// </summary>
public sealed class LinearGradientBrush : GradientBrush
{
    /// <summary>
    /// Create a linear gradient. <paramref name="startPoint"/> defaults to
    /// <see cref="RelativePoint.TopLeft"/> and <paramref name="endPoint"/> to
    /// <see cref="RelativePoint.TopRight"/> — a horizontal sweep.
    /// </summary>
    public LinearGradientBrush(IReadOnlyList<GradientStop> stops,
                               RelativePoint? startPoint = null, RelativePoint? endPoint = null,
                               GradientSpread spread = GradientSpread.Pad, double opacity = 1.0)
        : base(stops, spread, opacity)
    {
        StartPoint = RequireFinite(startPoint ?? RelativePoint.TopLeft, nameof(startPoint));
        EndPoint = RequireFinite(endPoint ?? RelativePoint.TopRight, nameof(endPoint));
    }

    /// <summary>
    /// Create a two-color linear gradient from <paramref name="start"/> (offset 0) to
    /// <paramref name="end"/> (offset 1) — the common case. Geometry defaults to a horizontal sweep.
    /// </summary>
    public LinearGradientBrush(Color start, Color end,
                               RelativePoint? startPoint = null, RelativePoint? endPoint = null,
                               GradientSpread spread = GradientSpread.Pad, double opacity = 1.0)
        : this([new(0.0, start), new(1.0, end)], startPoint, endPoint, spread, opacity) { }

    /// <summary>The gradient's start point, relative to the paint bounds (default: top-left).</summary>
    public RelativePoint StartPoint { get; }

    /// <summary>The gradient's end point, relative to the paint bounds (default: top-right).</summary>
    public RelativePoint EndPoint { get; }

    protected override double ComputeOffset(double px, double py, double width, double height)
    {
        double sx = StartPoint.X * width, sy = StartPoint.Y * height;
        double vx = EndPoint.X * width - sx, vy = EndPoint.Y * height - sy;
        double lengthSquared = vx * vx + vy * vy;
        if (lengthSquared <= Epsilon) return 0.0;

        return ((px - sx) * vx + (py - sy) * vy) / lengthSquared;
    }
}
