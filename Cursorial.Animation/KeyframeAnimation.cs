namespace Cursorial.Animation;

/// <summary>
/// A multi-stop animation: walks a sorted list of <see cref="Keyframe{T}"/> and, within each segment,
/// eases from the previous keyframe's value to the next using the next keyframe's easing.
/// </summary>
/// <remarks>
/// <see cref="IAnimation{T}.Duration"/> is the last keyframe's time. Before the first keyframe the value
/// holds at the first; after the last it holds at the last. Keyframes are sorted by time on construction,
/// so callers may pass them in any order. Coincident keyframes (equal times) form a zero-length segment
/// that snaps to the later value.
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
public class KeyframeAnimation<T> : IAnimation<T>
{
    private readonly Keyframe<T>[] _frames;
    private readonly IInterpolator<T> _interpolator;

    /// <summary>Create a keyframe animation over <paramref name="keyframes"/> (sorted by time internally).</summary>
    /// <param name="keyframes">At least one keyframe; all times must be non-negative.</param>
    /// <param name="interpolator">Blends adjacent keyframe values by eased segment progress.</param>
    public KeyframeAnimation(IEnumerable<Keyframe<T>> keyframes, IInterpolator<T> interpolator)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(interpolator);

        // OrderBy is a stable sort, so coincident keyframes (equal times) keep their input order —
        // the later one wins at that instant, giving deterministic step semantics. Array.Sort would not.
        _frames = [.. keyframes.OrderBy(static k => k.Time)];
        if (_frames.Length == 0)
            throw new ArgumentException("A keyframe animation needs at least one keyframe.", nameof(keyframes));

        if (_frames[0].Time < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keyframes), "Keyframe times must be non-negative.");

        _interpolator = interpolator;
        Duration = _frames[^1].Time;
    }

    /// <inheritdoc/>
    public TimeSpan Duration { get; }

    /// <inheritdoc/>
    public T ValueAt(TimeSpan elapsed)
    {
        if (elapsed <= _frames[0].Time) return _frames[0].Value;
        if (elapsed >= Duration) return _frames[^1].Value;

        // Find the segment [i, i+1] containing `elapsed`: the last keyframe whose time is <= elapsed.
        int i = 0;
        while (i < _frames.Length - 1 && _frames[i + 1].Time <= elapsed)
            i++;

        Keyframe<T> a = _frames[i];
        Keyframe<T> b = _frames[i + 1];
        TimeSpan span = b.Time - a.Time;
        double local = span <= TimeSpan.Zero ? 1.0 : (elapsed - a.Time) / span;
        Easing easing = b.Easing ?? Easings.Linear;
        return _interpolator.Interpolate(a.Value, b.Value, easing(local));
    }
}
