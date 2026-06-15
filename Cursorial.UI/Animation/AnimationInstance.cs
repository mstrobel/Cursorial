using System;

using Cursorial.Animation;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>Something the scheduler's post-sampling drain raises a <c>Completed</c> on (an animation or a storyboard group).</summary>
internal interface IAnimationCompletion
{
    /// <summary>Raises the public handle's <c>Completed</c> (at most once) — the post-sampling drain calls this (AD3).</summary>
    void RaiseCompleted();
}

/// <summary>
/// The non-generic base the scheduler holds in one list and samples polymorphically (design doc §9.2).
/// One instance owns one (target, property) animation's lifecycle.
/// </summary>
internal abstract class AnimationInstance : IAnimationCompletion
{
    /// <summary>The lifecycle state (§9.2).</summary>
    internal AnimationState State { get; private protected set; }

    /// <summary>The storyboard group this instance belongs to (null ⇒ a standalone <c>BeginAnimation</c> — §9.3).</summary>
    internal StoryboardInstance? Owner { get; set; }

    /// <summary>True while the instance must be sampled / pins the idle gate (Delayed or Running — §9.6).</summary>
    internal bool IsActive => State is AnimationState.Delayed or AnimationState.Running;

    /// <summary>True once the instance no longer participates (Stopped/Completed) — swept from the registry.</summary>
    internal bool IsFinished => State is AnimationState.Stopped or AnimationState.Completed;

    internal abstract UIObject TargetObject { get; }
    internal abstract UIProperty TargetPropertyObject { get; }

    /// <summary>Samples at the frozen clock; finite ends enqueue a completion (raised after the pass — §9.1/AD3).</summary>
    internal abstract void Sample(TimeSpan now);

    /// <summary>Writes the first sample synchronously at <c>Begin</c> (BeginTime == 0 → self-sample, AD2).</summary>
    internal abstract void BeginNow(TimeSpan now);

    /// <summary>Retires the instance silently (Stopped, retracted, NO <c>Completed</c>) — handoff / detach / Shutdown.</summary>
    internal abstract void Retire();

    /// <summary>The public <c>AnimationHandle.Stop()</c> path: retire + drop from the scheduler registry.</summary>
    internal abstract void StopFromHandle();

    /// <summary>Raises the public handle's <c>Completed</c> (at most once) — the post-sampling drain.</summary>
    public abstract void RaiseCompleted();
}

/// <summary>
/// The typed animation state machine (design doc §9.2): samples an <see cref="IAnimation{T}"/> onto a
/// Fork-A <see cref="AnimatedValueHandle{T}"/> at the frozen frame clock, with completion at most once
/// (after the sampling pass), perpetual/overshoot guards, and store-owned retraction (invariant 4).
/// </summary>
internal sealed class AnimationInstance<T> : AnimationInstance
{
    private readonly AnimationScheduler _scheduler;
    private readonly UIObject _target;
    private readonly StyledProperty<T> _property;
    private readonly IAnimation<T> _animation;
    private readonly TimeSpan _startTime;   // T_N + BeginTime (the animation's own t=0 lands here)
    private readonly TimeSpan _duration;
    private readonly bool _perpetual;        // captured once — keeps TimeSpan.MaxValue out of arithmetic (AD6)
    private readonly FillBehavior _fill;
    private AnimatedValueHandle<T>? _handle;
    private AnimationHandle? _publicHandle;
    private bool _completionPending;

    internal AnimationInstance(
        AnimationScheduler scheduler, UIObject target, StyledProperty<T> property,
        IAnimation<T> animation, TimeSpan startTime, in AnimationStartOptions options)
    {
        _scheduler = scheduler;
        _target = target;
        _property = property;
        _animation = animation;
        _startTime = startTime;
        _duration = animation.Duration;
        _perpetual = _duration == TimeSpan.MaxValue;
        _fill = options.Fill;
        State = options.BeginTime > TimeSpan.Zero ? AnimationState.Delayed : AnimationState.Running;
    }

    internal override UIObject TargetObject => _target;
    internal override UIProperty TargetPropertyObject => _property;

    /// <summary>The public handle is wired right after construction (it needs <c>this</c>).</summary>
    internal void BindPublicHandle(AnimationHandle handle) => _publicHandle = handle;

    internal override void BeginNow(TimeSpan now)
    {
        // BeginTime == 0 ⇒ Running already: attach the store handle and write the first sample synchronously
        // (frame coherence — AD2). Delayed instances attach when the clock crosses _startTime in Sample.
        if (State == AnimationState.Running)
            Sample(now);
    }

    internal override void Sample(TimeSpan now)
    {
        if (State == AnimationState.Delayed)
        {
            if (now < _startTime)
                return;           // still within the BeginTime window — property untouched
            State = AnimationState.Running;
        }

        if (State != AnimationState.Running)
            return;               // Paused / Holding / Completed / Stopped — nothing to sample

        var elapsed = now - _startTime;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        if (!_perpetual && elapsed >= _duration)
        {
            Write(_duration);     // the final write is always ValueAt(Duration) (AD5)
            if (State != AnimationState.Running)
                return;           // a reentrant Stop from the write's own notification skips completion (AD7)
            Complete();
        }
        else
        {
            Write(elapsed);
        }
    }

    private void Write(TimeSpan elapsed)
    {
        _handle ??= _target.BeginAnimation(_property);   // Fork A: last-started-wins (detaches any prior handle)
        _handle.SetValue(_animation.ValueAt(elapsed));   // store equality gate is the cadence valve (AD9)
    }

    private void Complete()
    {
        if (_fill == FillBehavior.HoldEnd)
        {
            State = AnimationState.Holding;   // keep the handle: the Animation slot holds ValueAt(Duration)
        }
        else
        {
            _handle?.Dispose();               // Stop: retract → base resurfaces (invariant 4)
            _handle = null;
            State = AnimationState.Completed;
        }

        if (Owner is not null)
        {
            Owner.OnChildCompleted();         // a storyboard child rolls its completion up to the group (§9.3)
        }
        else
        {
            _completionPending = true;
            _scheduler.EnqueueCompleted(this); // raised after the whole sampling pass (AD3)
        }
    }

    internal override void Retire()
    {
        if (IsFinished)
            return;               // already Stopped/Completed — idempotent (AD7)

        _handle?.Dispose();       // store-owned retraction; idempotent
        _handle = null;
        State = AnimationState.Stopped;
        _completionPending = false;   // a retired instance never raises Completed
        Owner?.OnChildRetired();      // a force-retired child makes its group ineligible to complete (§9.3)
    }

    internal override void StopFromHandle()
    {
        if (IsFinished)
            return;

        Retire();
        _scheduler.Remove(this);
    }

    public override void RaiseCompleted()
    {
        if (!_completionPending)
            return;               // at most once per lifetime (AD3)

        _completionPending = false;
        _publicHandle?.RaiseCompleted();
    }
}
