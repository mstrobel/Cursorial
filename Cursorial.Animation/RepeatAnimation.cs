namespace Cursorial.Animation;

/// <summary>
/// Repeats an inner animation — a fixed number of times or perpetually — optionally reversing on
/// alternate passes (ping-pong). Pure timeline arithmetic over the inner animation; no clock of its own.
/// </summary>
/// <remarks>
/// One iteration is the inner duration, or twice it when <see cref="AutoReverse"/> is set (a forward pass
/// then a backward pass). A finite repeat has <see cref="IAnimation{T}.Duration"/> = one iteration ×
/// <see cref="Count"/>. A perpetual repeat (<see cref="Count"/> is null) reports
/// <see cref="TimeSpan.MaxValue"/> and never settles — <see cref="ValueAt"/> wraps the elapsed time into
/// the current iteration forever. Prefer the <see cref="AnimationExtensions"/> sugar
/// (<c>.Repeat(n)</c> / <c>.PingPong(n)</c> / <c>.Loop()</c> / <c>.AutoReverse()</c>) over this directly.
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
public sealed class RepeatAnimation<T> : IAnimation<T>
{
    private readonly IAnimation<T> _inner;

    /// <summary>Wrap <paramref name="inner"/>, repeating it <paramref name="count"/> times (null ⇒ forever).</summary>
    /// <param name="inner">The animation to repeat.</param>
    /// <param name="count">Iteration count (at least 1), or null to repeat perpetually.</param>
    /// <param name="autoReverse">When true, each iteration plays forward then backward.</param>
    public RepeatAnimation(IAnimation<T> inner, int? count, bool autoReverse = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (count is < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Repeat count must be at least 1, or null for perpetual.");

        _inner = inner;
        Count = count;
        AutoReverse = autoReverse;

        TimeSpan iteration = autoReverse ? inner.Duration * 2 : inner.Duration;
        Duration = count is { } c ? iteration * c : TimeSpan.MaxValue;
    }

    /// <summary>The number of iterations, or null when this repeats forever.</summary>
    public int? Count { get; }

    /// <summary>True when this repeats forever (<see cref="Count"/> is null).</summary>
    public bool IsPerpetual => Count is null;

    /// <summary>Whether each iteration plays forward then backward.</summary>
    public bool AutoReverse { get; }

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public T ValueAt(TimeSpan elapsed)
    {
        TimeSpan innerDuration = _inner.Duration;
        if (innerDuration <= TimeSpan.Zero)
            return _inner.ValueAt(TimeSpan.Zero);

        if (elapsed <= TimeSpan.Zero)
            return _inner.ValueAt(TimeSpan.Zero);

        // Finite repeats hold the final frame at/after the end: an auto-reversing run finishes back at
        // the start (each iteration returns there), a forward run finishes at the inner end. Perpetual
        // repeats never reach this branch — they wrap forever.
        if (!IsPerpetual && elapsed >= Duration)
            return AutoReverse ? _inner.ValueAt(TimeSpan.Zero) : _inner.ValueAt(innerDuration);

        TimeSpan iteration = AutoReverse ? innerDuration * 2 : innerDuration;
        var within = new TimeSpan(elapsed.Ticks % iteration.Ticks);

        if (!AutoReverse)
            return _inner.ValueAt(within);

        return within <= innerDuration
            ? _inner.ValueAt(within)              // forward half
            : _inner.ValueAt(iteration - within); // backward half
    }
}
