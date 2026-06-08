namespace Cursorial.Animation;

/// <summary>
/// A pure, time-free animation: a deterministic map from elapsed time to an immutable value.
/// </summary>
/// <remarks>
/// There is no clock, thread, or timer here. The consumer — a render loop today, the future
/// <c>Cursorial.UI</c> tomorrow — owns the clock and asks for the value at a given elapsed offset.
/// That keeps the whole layer deterministic and trivially testable: the same <c>elapsed</c>
/// always yields the same value. <see cref="ValueAt"/> clamps its <c>elapsed</c> argument to
/// <c>[TimeSpan.Zero, Duration]</c>, so querying before the start or after the end holds at the first
/// or last frame rather than extrapolating.
/// </remarks>
/// <typeparam name="T">The animated value type (covariant — an <c>IAnimation&lt;Derived&gt;</c> is an
/// <c>IAnimation&lt;Base&gt;</c>).</typeparam>
public interface IAnimation<out T>
{
    /// <summary>The animation's total running length. Never negative; may be <see cref="TimeSpan.Zero"/>.</summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// The value at <paramref name="elapsed"/>, with <paramref name="elapsed"/> clamped to
    /// <c>[TimeSpan.Zero, <see cref="Duration"/>]</c>.
    /// </summary>
    T ValueAt(TimeSpan elapsed);
}
