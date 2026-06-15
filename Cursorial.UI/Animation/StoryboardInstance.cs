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
    internal void AddChild(bool perpetual)
    {
        _childCount++;
        if (perpetual)
            _anyPerpetual = true;
        else
            _finitePending++;
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
/// The caller's handle to a running <see cref="Storyboard"/> (design doc §9.3). A1 ships
/// <see cref="IsCompleted"/>, <see cref="Stop"/>, and <see cref="Completed"/>; the storyboard-timeline ops
/// (<c>Pause</c>/<c>Resume</c>/<c>Seek</c>/<c>SkipToEnd</c>) land in A2.
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
