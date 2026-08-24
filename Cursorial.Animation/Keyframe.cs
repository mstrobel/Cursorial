namespace Cursorial.Animation;

/// <summary>
/// One stop in a <see cref="KeyframeAnimation{T}"/>: the <see cref="Value"/> reached at <see cref="Time"/>,
/// and the <see cref="Easing"/> shaping the <em>segment leading into</em> this keyframe from the previous one.
/// </summary>
/// <remarks>
/// The easing belongs to the incoming segment, mirroring WPF's per-keyframe easing model — the first
/// keyframe's easing is therefore unused (nothing precedes it). A null <see cref="Easing"/> means
/// <see cref="Easings.Linear"/> for that segment.
/// </remarks>
/// <typeparam name="T">The animated value type.</typeparam>
/// <param name="Time">The elapsed offset at which <paramref name="Value"/> is reached; non-negative.</param>
/// <param name="Value">The value at <paramref name="Time"/>.</param>
/// <param name="Easing">The easing for the segment ending at this keyframe (null ⇒ linear).</param>
public readonly record struct Keyframe<T>(TimeSpan Time, T Value, Easing? Easing = null)
{
    /// <summary>
    /// The markup construction path (W3 — <c>&lt;Keyframe x:TypeArguments="x:Double" Time="0:0:0.1"
    /// Value="0.5"/&gt;</c>): members arrive via init; the positional ctor remains the code-first form.
    /// </summary>
    public Keyframe() : this(TimeSpan.Zero, default!)
    {
    }
}
