namespace Cursorial.Animation;

/// <summary>
/// Holds an inner animation's first frame for a fixed delay, then plays it. Pure timeline arithmetic over
/// the inner animation; no clock of its own (the UI's <c>BeginTime</c> stagger and the <c>.Delay</c> sugar
/// both build on this).
/// </summary>
/// <remarks>
/// During <c>[0, delay]</c> the value holds at <c>inner.ValueAt(0)</c>; thereafter the inner plays over
/// <c>[delay, delay + inner.Duration]</c>. <see cref="IAnimation{T}.Duration"/> is
/// <c>checked(delay + inner.Duration)</c> — except a <b>perpetual</b> inner
/// (<see cref="TimeSpan.MaxValue"/>) is passed through unchanged so the addition never overflows.
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
public sealed class DelayAnimation<T> : IAnimation<T>
{
    private readonly IAnimation<T> _inner;
    private readonly TimeSpan _delay;

    /// <summary>Hold <paramref name="inner"/>'s first frame for <paramref name="delay"/>, then play it.</summary>
    /// <param name="inner">The animation to delay.</param>
    /// <param name="delay">The hold before <paramref name="inner"/> starts; must be ≥ <see cref="TimeSpan.Zero"/>.</param>
    public DelayAnimation(IAnimation<T> inner, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must not be negative.");

        _inner = inner;
        _delay = delay;
        // A perpetual inner stays perpetual — adding to MaxValue would overflow; finite sums are checked.
        Duration = inner.Duration == TimeSpan.MaxValue ? TimeSpan.MaxValue : checked(delay + inner.Duration);
    }

    /// <summary>The delay before the inner animation starts.</summary>
    public TimeSpan Delay => _delay;

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public T ValueAt(TimeSpan elapsed)
        // Hold the inner's first frame through the delay window; afterward the inner plays (and clamps to
        // its own Duration for elapsed past the end), so this needs no separate end-clamp.
        => elapsed <= _delay ? _inner.ValueAt(TimeSpan.Zero) : _inner.ValueAt(elapsed - _delay);
}
