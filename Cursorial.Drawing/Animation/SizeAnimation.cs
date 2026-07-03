using Cursorial.Animation;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A <see cref="Size"/> animation — <see cref="Animation{T}"/> with <see cref="SizeInterpolator"/>
/// baked in (rounded, non-negative blending). Animate a panel or fragment growing/shrinking; pair
/// with <c>.AutoReverse()</c> for a breathing effect.
/// </summary>
public sealed class SizeAnimation : Animation<Size>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public SizeAnimation(Size from, Size to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, SizeInterpolator.Instance, easing) { }
}
