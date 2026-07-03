using Cursorial.Animation;
using Cursorial.Rendering;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A <see cref="Rect"/> animation — <see cref="Animation{T}"/> with <see cref="RectInterpolator"/>
/// baked in (rounded, non-negative blending). Slide and/or resize a region in one shot — a panel
/// expanding, a focus/selection box gliding to its target.
/// </summary>
public sealed class RectAnimation : Animation<Rect>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public RectAnimation(Rect from, Rect to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, RectInterpolator.Instance, easing) { }
}
