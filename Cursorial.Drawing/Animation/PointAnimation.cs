using Cursorial.Animation;
using Cursorial.Drawing.Charts;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// A <see cref="PointD"/> animation — <see cref="Animation{T}"/> with <see cref="PointInterpolator"/>
/// baked in (continuous, unrounded blending). Move a point through value space — a tracking marker, a
/// playhead sweeping a plot.
/// </summary>
public sealed class PointAnimation : Animation<PointD>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public PointAnimation(PointD from, PointD to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, PointInterpolator.Instance, easing) { }
}
