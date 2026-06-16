

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// The caller's handle to a running animation (design doc §9.2). <c>Stop()</c> and <c>Completed</c> shipped in
/// A0; <c>Pause</c>/<c>Resume</c>/<c>Seek</c>/<c>SkipToEnd</c> in A2.
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

    /// <summary>Pauses the animation — legal from Delayed/Running; the value holds; it drops out of the idle gate (A2).</summary>
    public void Pause() => _instance.Pause();

    /// <summary>Resumes a paused animation from where it paused (no time jump — A2).</summary>
    public void Resume() => _instance.Resume();

    /// <summary>Seeks to <paramref name="offset"/> on the animation's own timeline (clamped); works while paused (A2).</summary>
    public void Seek(TimeSpan offset) => _instance.Seek(offset);

    /// <summary>Snaps to the end value and completes; throws for a perpetual animation (A2).</summary>
    public void SkipToEnd() => _instance.SkipToEnd();

    /// <summary>Raises <see cref="Completed"/> at most once (the scheduler's post-sampling drain calls this).</summary>
    internal void RaiseCompleted()
    {
        if (_completedRaised)
            return;

        _completedRaised = true;
        Completed?.Invoke(this);
    }
}
