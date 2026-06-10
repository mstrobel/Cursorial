using Cursorial.Animation;
using Cursorial.Rendering;

namespace Cursorial.Drawing;

/// <summary>
/// Linear interpolation between two <see cref="Rect"/>s — anchor (column/row) and extent (columns/rows)
/// each rounded to the nearest cell (ties away from zero) and clamped to ≥ 0. Animates a region sliding
/// and/or resizing in one shot (a panel expanding, a selection box gliding to its target). Clamping
/// matters: the <see cref="Rect"/> ctor rejects negative dimensions, so an overshooting easing would
/// otherwise throw. Lives in <c>Cursorial.Drawing</c> so <c>Cursorial.Animation</c> needn't depend on
/// Rendering. Stateless singleton.
/// </summary>
public sealed class RectInterpolator : IInterpolator<Rect>
{
    /// <summary>The shared instance.</summary>
    public static RectInterpolator Instance { get; } = new();

    private RectInterpolator() { }

    /// <inheritdoc/>
    public Rect Interpolate(Rect from, Rect to, double progress) =>
        new(SizeInterpolator.RoundClamp(from.Column, to.Column, progress),
            SizeInterpolator.RoundClamp(from.Row, to.Row, progress),
            SizeInterpolator.RoundClamp(from.Columns, to.Columns, progress),
            SizeInterpolator.RoundClamp(from.Rows, to.Rows, progress));
}
