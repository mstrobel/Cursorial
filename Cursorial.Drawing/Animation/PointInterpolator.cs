using Cursorial.Animation;
using Cursorial.Drawing.Charts;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Linear interpolation between two <see cref="PointD"/>s (each axis independently) in continuous
/// chart/value space — no rounding or clamping, unlike the cell-quantized <see cref="Size"/> /
/// <see cref="Rect"/> interpolators. Animates a point through a plot (a moving marker, a sweeping
/// cursor in value space). Stateless singleton.
/// </summary>
public sealed class PointInterpolator : IInterpolator<PointD>
{
    /// <summary>The shared instance.</summary>
    public static PointInterpolator Instance { get; } = new();

    private PointInterpolator() { }

    /// <inheritdoc/>
    public PointD Interpolate(PointD from, PointD to, double progress) =>
        new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);
}
