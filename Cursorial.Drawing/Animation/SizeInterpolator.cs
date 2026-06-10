using Cursorial.Animation;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// Linear interpolation between two <see cref="Size"/>s — each dimension rounded to the nearest cell
/// (ties away from zero) and clamped to ≥ 0, since sizes are non-negative cell counts and an
/// overshooting easing could otherwise drive a dimension below zero. Lives in <c>Cursorial.Drawing</c>
/// (which bridges Rendering and the animation layer) so <c>Cursorial.Animation</c> needn't depend on
/// Rendering. Stateless singleton.
/// </summary>
public sealed class SizeInterpolator : IInterpolator<Size>
{
    /// <summary>The shared instance.</summary>
    public static SizeInterpolator Instance { get; } = new();

    private SizeInterpolator() { }

    /// <inheritdoc/>
    public Size Interpolate(Size from, Size to, double progress) =>
        new(RoundClamp(from.Columns, to.Columns, progress), RoundClamp(from.Rows, to.Rows, progress));

    // Rounded (ties away from zero) and clamped ≥ 0 — shared by the cell-quantized geometry interpolators.
    internal static int RoundClamp(int from, int to, double progress) =>
        Math.Max(0, (int) Math.Round(from + (to - from) * progress, MidpointRounding.AwayFromZero));
}
