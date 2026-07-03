namespace Cursorial.Animation;

/// <summary>
/// Plays a series of inner animations back to back on one timeline. Pure timeline arithmetic; no clock of
/// its own (the <c>.Then</c> sugar builds on this; cross-property parallelism is a UI <c>Storyboard</c>,
/// not this).
/// </summary>
/// <remarks>
/// Each child owns a half-open interval <c>[start_k, start_k+1)</c>, so at a shared boundary the <b>next</b>
/// child wins; a non-final zero-duration child therefore owns an empty interval and is never sampled.
/// <see cref="IAnimation{T}.Duration"/> is the <c>checked</c> sum of the children's durations. A
/// <b>perpetual</b> child is legal only in the last position (it would otherwise never yield to a
/// successor); the total is then <see cref="TimeSpan.MaxValue"/>. Before the start the first child's first
/// frame shows; at/after the total the last child's last frame holds (the inner clamps).
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
public sealed class SequenceAnimation<T> : IAnimation<T>
{
    private readonly IAnimation<T>[] _children;
    private readonly TimeSpan[] _starts; // cumulative start offset of each child

    /// <summary>Play <paramref name="children"/> one after another. At least one child is required.</summary>
    /// <exception cref="ArgumentException">No children, or a non-final child is perpetual.</exception>
    public SequenceAnimation(params IAnimation<T>[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0)
            throw new ArgumentException("A sequence needs at least one child.", nameof(children));

        for (var i = 0; i < children.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(children[i]);
            if (i < children.Length - 1 && children[i].Duration == TimeSpan.MaxValue)
                throw new ArgumentException("Only the last child of a sequence may be perpetual.", nameof(children));
        }

        _children = (IAnimation<T>[])children.Clone();
        _starts = new TimeSpan[children.Length];

        var accumulated = TimeSpan.Zero;
        for (var i = 0; i < _children.Length; i++)
        {
            _starts[i] = accumulated;
            var duration = _children[i].Duration;
            accumulated = duration == TimeSpan.MaxValue ? TimeSpan.MaxValue : checked(accumulated + duration);
        }

        Duration = accumulated;
    }

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public T ValueAt(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            return _children[0].ValueAt(TimeSpan.Zero);

        // The active child is the LAST whose start ≤ elapsed: at a shared boundary (a zero-duration child)
        // the later child wins, so the half-open intervals and the "next child wins" rule fall out for free.
        // The inner clamps for elapsed past the child's own end (and past the sequence total).
        for (var i = _children.Length - 1; i >= 0; i--)
            if (elapsed >= _starts[i])
                return _children[i].ValueAt(elapsed - _starts[i]);

        return _children[0].ValueAt(TimeSpan.Zero);
    }
}
