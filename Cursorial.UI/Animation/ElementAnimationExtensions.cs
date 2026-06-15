using System;

using Cursorial.Animation;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>The imperative animation surface (design doc §9.2): begin/stop an animation on a property.</summary>
public static class ElementAnimationExtensions
{
    /// <summary>
    /// Begins <paramref name="animation"/> on <paramref name="target"/>.<paramref name="property"/> through the
    /// thread-ambient scheduler. A running animation on the same (target, property) is retired first
    /// (SnapshotAndReplace — §9.4). Distinct from Fork A's raw
    /// <see cref="UIObject.BeginAnimation{T}(StyledProperty{T})"/> handle factory by arity (AD14).
    /// </summary>
    public static AnimationHandle BeginAnimation<T>(
        this UIObject target, StyledProperty<T> property, IAnimation<T> animation, AnimationStartOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(animation);
        target.VerifyAccess();

        return AnimationScheduler.Current.Begin(target, property, animation, options);
    }

    /// <summary>Stops any animation on <paramref name="target"/>.<paramref name="property"/> — the base resurfaces; no <c>Completed</c>.</summary>
    public static void StopAnimation(this UIObject target, UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);

        AnimationScheduler.CurrentOrNull?.StopAnimation(target, property);
    }
}
