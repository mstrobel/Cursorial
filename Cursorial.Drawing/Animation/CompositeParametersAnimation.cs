using Cursorial.Animation;

namespace Cursorial.Drawing;

/// <summary>
/// A <see cref="CompositeParameters"/> animation — <see cref="Animation{T}"/> with
/// <see cref="CompositeParametersInterpolator"/> baked in. Drives a cached scene's slide/fade per frame
/// without re-rasterizing the scene.
/// </summary>
public sealed class CompositeParametersAnimation : Animation<CompositeParameters>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public CompositeParametersAnimation(CompositeParameters from, CompositeParameters to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, CompositeParametersInterpolator.Instance, easing) { }
}
