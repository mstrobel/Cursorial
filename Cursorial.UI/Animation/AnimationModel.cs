

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>What a finite animation does with the property when it reaches its end (design doc §9.2).</summary>
public enum FillBehavior : byte
{
    /// <summary>Hold the end value at <see cref="BindingPriority.Animation"/> (the default); costs nothing per frame.</summary>
    HoldEnd = 0,

    /// <summary>Retract the animated value at the end — the base resurfaces (invariant 4).</summary>
    Stop = 1
}

/// <summary>How a new animation on an already-animated property combines with the running one (§9.4). <c>Compose</c> is deferred.</summary>
public enum HandoffBehavior : byte
{
    /// <summary>Retire the running animation and replace it (the only v1 policy).</summary>
    SnapshotAndReplace = 0
}

/// <summary>The lifecycle state of an animation (design doc §9.2).</summary>
public enum AnimationState : byte
{
    /// <summary>Waiting for <c>BeginTime</c> — the property is untouched (no handle yet).</summary>
    Delayed,

    /// <summary>Sampling each frame at the frozen clock.</summary>
    Running,

    /// <summary>Paused (A2): the value holds; excluded from the idle gate.</summary>
    Paused,

    /// <summary>Finite, reached its end with <see cref="FillBehavior.HoldEnd"/> — the end value holds statically.</summary>
    Holding,

    /// <summary>Finite, reached its end with <see cref="FillBehavior.Stop"/> — retracted, base resurfaced.</summary>
    Completed,

    /// <summary>Explicitly stopped / retired by handoff / detached — retracted, no <c>Completed</c> raised.</summary>
    Stopped
}

/// <summary>Options for <c>BeginAnimation</c> (design doc §9.2).</summary>
public readonly record struct AnimationStartOptions(
    TimeSpan BeginTime = default,
    FillBehavior Fill = FillBehavior.HoldEnd,
    HandoffBehavior Handoff = HandoffBehavior.SnapshotAndReplace);
