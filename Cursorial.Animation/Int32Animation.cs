namespace Cursorial.Animation;

/// <summary>
/// An <see cref="int"/> animation — <see cref="Animation{T}"/> with <see cref="Int32Interpolator"/>
/// baked in (rounded blending). Handy for integer targets like cell offsets.
/// </summary>
public sealed class Int32Animation : Animation<int>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public Int32Animation(int from, int to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, Int32Interpolator.Instance, easing) { }
}
