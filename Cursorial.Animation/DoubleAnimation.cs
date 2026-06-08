namespace Cursorial.Animation;

/// <summary>
/// A <see cref="double"/> animation — <see cref="Animation{T}"/> with <see cref="DoubleInterpolator"/>
/// baked in. The WPF-style convenience over the generic engine.
/// </summary>
public sealed class DoubleAnimation : Animation<double>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public DoubleAnimation(double from, double to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, DoubleInterpolator.Instance, easing) { }
}
