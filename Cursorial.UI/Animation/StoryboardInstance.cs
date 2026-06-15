// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// One running storyboard, keyed <c>(igniter, scope)</c> (design doc §9.3) — the group its per-track
/// <see cref="AnimationInstance"/> children roll their completion up to. Completes once when every finite
/// child has completed and no child is perpetual; a force-retired child (handoff / detach / stop) makes the
/// group ineligible to complete (no <c>Completed</c>).
/// </summary>
internal sealed class StoryboardInstance : IAnimationCompletion
{
    private readonly AnimationScheduler _scheduler;
    private readonly List<AnimationInstance> _children = []; // for forwarding the timeline ops (Pause/Seek/SkipToEnd)
    private int _childCount;
    private int _finitePending;
    private bool _anyPerpetual;
    private bool _terminated;        // force-retired ⇒ ineligible to complete
    private bool _completionPending;
    private bool _completed;

    internal StoryboardInstance(AnimationScheduler scheduler, object igniter, UIElement scope, Storyboard storyboard)
    {
        _scheduler = scheduler;
        Igniter = igniter;
        Scope = scope;
        Storyboard = storyboard;
        Handle = new StoryboardHandle(this);
    }

    /// <summary>The ignition identity (a <c>BeginStoryboard</c> action for edge ignitions, the <see cref="Storyboard"/> for imperative <c>Begin</c>).</summary>
    internal object Igniter { get; }

    /// <summary>The scope element the storyboard was begun on.</summary>
    internal UIElement Scope { get; }

    /// <summary>The shared storyboard description.</summary>
    internal Storyboard Storyboard { get; }

    /// <summary>The caller-facing handle.</summary>
    internal StoryboardHandle Handle { get; }

    /// <summary>True once the group has completed naturally.</summary>
    internal bool IsCompleted => _completed;

    /// <summary>Registers a started child (call before the child's first sample so a zero-duration child can't underflow).</summary>
    internal void AddChild(AnimationInstance child)
    {
        _children.Add(child);
        _childCount++;
        if (child.IsPerpetual)
            _anyPerpetual = true;
        else
            _finitePending++;
    }

    // ── Storyboard-timeline ops (A2; design doc §9.3) ──────────────────────────────────────────────────

    /// <summary>Pauses every child (the storyboard timeline holds).</summary>
    internal void Pause()
    {
        for (var i = 0; i < _children.Count; i++)
            _children[i].Pause();
    }

    /// <summary>Resumes every child from where it paused.</summary>
    internal void Resume()
    {
        for (var i = 0; i < _children.Count; i++)
            _children[i].Resume();
    }

    /// <summary>
    /// Seeks the storyboard timeline to <paramref name="offset"/>: each child seeks to
    /// <c>offset − BeginTime</c>; a child whose start lies past the seek point rewinds to Delayed
    /// (its property returns to base, timeline retained).
    /// </summary>
    internal void Seek(TimeSpan offset)
    {
        for (var i = 0; i < _children.Count; i++)
        {
            var child = _children[i];
            var childElapsed = offset - child.BeginTimeOffset;
            if (childElapsed >= TimeSpan.Zero)
                child.Seek(childElapsed);
            else
                child.RewindToDelayed(child.BeginTimeOffset - offset);
        }
    }

    /// <summary>Skips the whole storyboard to its end — validates all-finite UP FRONT (any perpetual track throws first — AD6).</summary>
    internal void SkipToEnd()
    {
        if (_anyPerpetual)
            throw new InvalidOperationException("Cannot SkipToEnd a storyboard with a perpetual track (AD6).");

        for (var i = 0; i < _children.Count; i++)
            _children[i].SkipToEnd();
    }

    /// <summary>Called after all tracks have been started — completes immediately if there were no children at all.</summary>
    internal void OnAllChildrenStarted()
    {
        if (_childCount == 0 && !_terminated && !_completed)
        {
            _completionPending = true;
            _scheduler.EnqueueCompleted(this);
        }
    }

    /// <summary>A finite child completed naturally — the group completes when the last one does (no perpetual child).</summary>
    internal void OnChildCompleted()
    {
        if (_terminated || _completed)
            return;

        if (--_finitePending <= 0 && !_anyPerpetual)
        {
            _completionPending = true;
            _scheduler.EnqueueCompleted(this); // raised after the whole sampling pass (AD3)
        }
    }

    /// <summary>A child was force-retired (handoff / detach / stop) — the group can no longer complete naturally.</summary>
    internal void OnChildRetired()
    {
        if (_completed)
            return;          // already completed — a later detach-retire of a Holding child is moot

        _terminated = true;
        _completionPending = false;
    }

    /// <summary>Marks the group terminated (the scheduler retired its children) so a queued completion can't fire.</summary>
    internal void MarkTerminated()
    {
        _terminated = true;
        _completionPending = false;
    }

    /// <inheritdoc/>
    public void RaiseCompleted()
    {
        if (!_completionPending || _completed)
            return;

        _completed = true;
        _completionPending = false;
        Handle.RaiseCompleted();
    }

    /// <summary>The public <c>StoryboardHandle.Stop()</c> path.</summary>
    internal void StopFromHandle() => _scheduler.StopStoryboard(this);
}

/// <summary>
/// The caller's handle to a running <see cref="Storyboard"/> (design doc §9.3). A1 shipped
/// <see cref="IsCompleted"/>/<see cref="Stop"/>/<see cref="Completed"/>; the storyboard-timeline ops
/// (<see cref="Pause"/>/<see cref="Resume"/>/<see cref="Seek"/>/<see cref="SkipToEnd"/>) landed in A2.
/// </summary>
public sealed class StoryboardHandle
{
    private readonly StoryboardInstance _instance;
    private bool _completedRaised;

    internal StoryboardHandle(StoryboardInstance instance) => _instance = instance;

    /// <summary>True once every finite track has completed (a perpetual track ⇒ never).</summary>
    public bool IsCompleted => _instance.IsCompleted;

    /// <summary>Stops the whole storyboard: every child retracts (bases resurface); no <c>Completed</c>.</summary>
    public void Stop() => _instance.StopFromHandle();

    /// <summary>Pauses every track — the storyboard timeline holds (A2).</summary>
    public void Pause() => _instance.Pause();

    /// <summary>Resumes every track from where it paused (A2).</summary>
    public void Resume() => _instance.Resume();

    /// <summary>Seeks the storyboard timeline to <paramref name="offset"/> (per-track <c>offset − BeginTime</c>; A2).</summary>
    public void Seek(TimeSpan offset) => _instance.Seek(offset);

    /// <summary>Snaps the whole storyboard to its end; throws if any track is perpetual (validated up front — A2/AD6).</summary>
    public void SkipToEnd() => _instance.SkipToEnd();

    /// <summary>Raised once per lifetime, after the sampling pass — never on <c>Stop</c>, detach, or a perpetual track (AD3).</summary>
    public event Action<StoryboardHandle>? Completed;

    internal void RaiseCompleted()
    {
        if (_completedRaised)
            return;

        _completedRaised = true;
        Completed?.Invoke(this);
    }
}
