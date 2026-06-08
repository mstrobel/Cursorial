namespace Cursorial.Animation;

/// <summary>
/// Blends two endpoint values by an already-eased factor. Pure and stateless — one shared instance
/// serves every animation (see the <c>Instance</c> singletons and <see cref="Interpolators"/>).
/// </summary>
/// <remarks>
/// The <c>progress</c> passed to <see cref="Interpolate"/> is <em>post-easing</em>: the
/// animation applies its <see cref="Easing"/> first, so an interpolator implements only the
/// value-space blend (typically linear) and need not know about timing or shaping. The factor is
/// normally in <c>[0, 1]</c> but may leave the range for overshooting easings, so implementations
/// should extrapolate or clamp deliberately rather than assume <c>[0, 1]</c>.
/// </remarks>
/// <typeparam name="T">The value type being blended.</typeparam>
public interface IInterpolator<T>
{
    /// <summary>The value <paramref name="progress"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    T Interpolate(T from, T to, double progress);
}
