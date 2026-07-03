using Cursorial.Animation;

namespace Cursorial.Drawing.Media;

/// <summary>
/// An <see cref="IBrush"/> animation — <see cref="Animation{T}"/> with <see cref="BrushInterpolator"/>
/// baked in. Pair with <c>.Loop()</c> / <c>.AutoReverse()</c> for a perpetually scrolling or pulsing fill.
/// </summary>
public sealed class BrushAnimation : Animation<IBrush>
{
    /// <summary>Ease from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>.</summary>
    public BrushAnimation(IBrush from, IBrush to, TimeSpan duration, Easing? easing = null)
        : base(from, to, duration, BrushInterpolator.Instance, easing) { }
}
