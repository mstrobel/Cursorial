namespace Cursorial.Animation;

/// <summary>
/// Fluent repeat combinators over <see cref="IAnimation{T}"/>, spanning two axes — direction (forward
/// vs reversing) and length (a fixed count vs perpetual):
/// <list type="table">
///   <item><term><c>Repeat(n)</c></term><description>forward, <c>n</c> times</description></item>
///   <item><term><c>PingPong(n)</c></term><description>forward-then-back, <c>n</c> cycles</description></item>
///   <item><term><c>Loop()</c></term><description>forward, forever (a perpetual sawtooth)</description></item>
///   <item><term><c>AutoReverse()</c></term><description>forward-then-back, forever (bounces)</description></item>
/// </list>
/// </summary>
public static class AnimationExtensions
{
    /// <summary>Play <paramref name="animation"/> forward <paramref name="count"/> times.</summary>
    public static IAnimation<T> Repeat<T>(this IAnimation<T> animation, int count) =>
        new RepeatAnimation<T>(animation, count);

    /// <summary>Play <paramref name="animation"/> forward-then-back <paramref name="count"/> times.</summary>
    public static IAnimation<T> PingPong<T>(this IAnimation<T> animation, int count) =>
        new RepeatAnimation<T>(animation, count, autoReverse: true);

    /// <summary>Repeat <paramref name="animation"/> forward forever (a perpetual sawtooth).</summary>
    public static IAnimation<T> Loop<T>(this IAnimation<T> animation) =>
        new RepeatAnimation<T>(animation, count: null);

    /// <summary>Bounce <paramref name="animation"/> forward-and-back forever (a perpetual ping-pong).</summary>
    public static IAnimation<T> AutoReverse<T>(this IAnimation<T> animation) =>
        new RepeatAnimation<T>(animation, count: null, autoReverse: true);

    /// <summary>Hold <paramref name="animation"/>'s first frame for <paramref name="delay"/>, then play it.</summary>
    public static IAnimation<T> Delay<T>(this IAnimation<T> animation, TimeSpan delay) =>
        new DelayAnimation<T>(animation, delay);

    /// <summary>Play <paramref name="next"/> after <paramref name="first"/> finishes (a two-step sequence).</summary>
    public static IAnimation<T> Then<T>(this IAnimation<T> first, IAnimation<T> next) =>
        new SequenceAnimation<T>(first, next);
}
