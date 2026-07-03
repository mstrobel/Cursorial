using Cursorial.Animation;

namespace Cursorial.Drawing.Media;

/// <summary>
/// A <see cref="Pen"/> animation — <see cref="Animation{T}"/> with <see cref="PenInterpolator"/>
/// baked in. The brush blends (via <see cref="BrushInterpolator"/>); the discrete stroke attributes
/// snap at the midpoint. Pair with <c>.Loop()</c> / <c>.AutoReverse()</c> for a pulsing border.
/// </summary>
public sealed class PenAnimation : Animation<Pen>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public PenAnimation(Pen from, Pen to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, PenInterpolator.Instance, easing) { }
}
