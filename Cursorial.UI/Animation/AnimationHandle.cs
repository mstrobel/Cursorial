using System;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The caller's handle to a running animation (design doc §9.2). <c>Stop()</c> and <c>Completed</c> ship in
/// A0; <c>Pause</c>/<c>Resume</c>/<c>Seek</c>/<c>SkipToEnd</c> land in A2.
/// </summary>
public sealed class AnimationHandle
{
    private readonly AnimationInstance _instance;
    private bool _completedRaised;

    internal AnimationHandle(AnimationInstance instance, UIObject target, UIProperty property)
    {
        _instance = instance;
        Target = target;
        Property = property;
    }

    /// <summary>The current lifecycle state (§9.2).</summary>
    public AnimationState State => _instance.State;

    /// <summary>The animated object.</summary>
    public UIObject Target { get; }

    /// <summary>The animated property.</summary>
    public UIProperty Property { get; }

    /// <summary>
    /// Raised once per lifetime, on the UI thread, after the frame's sampling pass — never on
    /// <c>Stop()</c>, detach-stop, or <c>Shutdown</c> (design doc §9.2/AD3).
    /// </summary>
    public event Action<AnimationHandle>? Completed;

    /// <summary>Stops the animation: the store handle is disposed ⇒ the base resurfaces (invariant 4); no <c>Completed</c>.</summary>
    public void Stop() => _instance.StopFromHandle();

    /// <summary>Raises <see cref="Completed"/> at most once (the scheduler's post-sampling drain calls this).</summary>
    internal void RaiseCompleted()
    {
        if (_completedRaised)
            return;

        _completedRaised = true;
        Completed?.Invoke(this);
    }
}
