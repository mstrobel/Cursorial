using Cursorial.Animation;

namespace Cursorial.Drawing;

/// <summary>
/// Linear interpolation between two <see cref="RelativePoint"/>s (each axis independently). Lives in
/// <c>Cursorial.Drawing</c> because <see cref="RelativePoint"/> does — the animation layer stays
/// dependency-free of Drawing. Stateless singleton.
/// </summary>
public sealed class RelativePointInterpolator : IInterpolator<RelativePoint>
{
    /// <summary>The shared instance.</summary>
    public static RelativePointInterpolator Instance { get; } = new();

    private RelativePointInterpolator() { }

    /// <inheritdoc/>
    public RelativePoint Interpolate(RelativePoint from, RelativePoint to, double progress) =>
        new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);
}
