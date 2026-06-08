namespace Cursorial.Animation;

/// <summary>
/// A two-point animation: eases from <see cref="From"/> to <see cref="To"/> over <see cref="Duration"/>,
/// shaping progress with an <see cref="Easing"/> and blending values with an <see cref="IInterpolator{T}"/>.
/// </summary>
/// <remarks>
/// This is the generic engine. Named conveniences (<see cref="DoubleAnimation"/>, …) derive from it with
/// the interpolator baked in, so callers get WPF-style ergonomics over a single implementation. A
/// zero-length <see cref="Duration"/> snaps straight to <see cref="To"/>.
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
public class Animation<T> : IAnimation<T>
{
    private readonly IInterpolator<T> _interpolator;
    private readonly Easing _easing;

    /// <summary>Create a <paramref name="from"/>→<paramref name="to"/> animation.</summary>
    /// <param name="from">The value at elapsed <see cref="TimeSpan.Zero"/>.</param>
    /// <param name="to">The value at <paramref name="duration"/>.</param>
    /// <param name="duration">The running length; must be non-negative.</param>
    /// <param name="interpolator">Blends the endpoints by eased progress.</param>
    /// <param name="easing">Progress shaping; defaults to <see cref="Easings.Linear"/>.</param>
    public Animation(T from, T to, TimeSpan duration, IInterpolator<T> interpolator, Easing? easing = null)
    {
        ArgumentNullException.ThrowIfNull(interpolator);
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be non-negative.");

        From = from;
        To = to;
        Duration = duration;
        _interpolator = interpolator;
        _easing = easing ?? Easings.Linear;
    }

    /// <summary>The value at the start of the animation.</summary>
    public T From { get; }

    /// <summary>The value at the end of the animation.</summary>
    public T To { get; }

    /// <summary>The progress-shaping curve.</summary>
    public Easing Easing => _easing;

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public T ValueAt(TimeSpan elapsed)
    {
        double progress = Duration <= TimeSpan.Zero ? 1.0 : Math.Clamp(elapsed / Duration, 0.0, 1.0);
        return _interpolator.Interpolate(From, To, _easing(progress));
    }
}
