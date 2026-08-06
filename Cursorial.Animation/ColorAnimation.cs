using Cursorial.Media;

namespace Cursorial.Animation;

/// <summary>
/// A <see cref="Color"/> animation — <see cref="Animation{T}"/> with <see cref="ColorInterpolator"/>
/// baked in (premultiplied-sRGB blending). The WPF-style convenience over the generic engine.
/// </summary>
public sealed class ColorAnimation : Animation<Color>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public ColorAnimation(Color from, Color to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, ColorInterpolator.Instance, easing) { }
}
