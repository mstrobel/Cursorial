using Cursorial.Animation;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The thread-ambient animation orchestrator (design doc §9.1) — the
/// <see cref="IAnimationFrameDriver"/> S6's frame loop drives. <b>P2 ships the early-S5 slice</b>
/// (inversion 1): the frozen <see cref="Clock"/> and the <see cref="UITimer"/> registry, in the
/// shape S5's full scheduler absorbs unchanged at P8 (active animation instances, storyboards, and
/// <c>TickNewlyStarted</c> work join then).
/// </summary>
/// <remarks>
/// Thread-ambient by decision (doc §9.1): invariant 6 means one UI thread, so one clock and one
/// idle signal serve every window. <see cref="UIApplication"/> constructs, installs (on both the
/// build thread and the UI thread, mirroring <c>UIApplication.Current</c>), and drives one
/// instance per application.
/// </remarks>
public sealed class AnimationScheduler : IAnimationFrameDriver
{
    [ThreadStatic]
    private static AnimationScheduler? _current;

    private readonly List<UITimer> _timers = [];
    private readonly List<AnimationInstance> _instances = []; // active + holding animation instances (P8 A0)
    private readonly List<StoryboardInstance> _storyboards = []; // running storyboard groups (P8 A1)
    private readonly List<IAnimationCompletion> _completed = []; // completions to raise after the sampling pass (AD3)
    private bool _isShutdown;
    private bool _animationsEnabledLast = true; // tracks the AnimationsEnabled edge for the §9.7 flip

    /// <summary>
    /// The scheduler installed on the current thread. Throws off the UI/build threads — the
    /// thread-ambient contract is what lets <see cref="UITimer.Start(TimeSpan, Action)"/> be
    /// static.
    /// </summary>
    public static AnimationScheduler Current
        => _current ?? throw new InvalidOperationException(
            "No AnimationScheduler is installed on this thread — UITimer.Start and animation APIs are " +
            "available only on a UIApplication's UI thread (or its build thread before the run).");

    /// <summary>The installed scheduler, or null (framework-internal probing).</summary>
    internal static AnimationScheduler? CurrentOrNull => _current;

    /// <summary>Installs <paramref name="scheduler"/> as the current thread's ambient scheduler.</summary>
    public static void Install(AnimationScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        _current = scheduler;
    }

    /// <summary>Clears the ambient slot when it holds <paramref name="scheduler"/> (teardown hygiene).</summary>
    internal static void Uninstall(AnimationScheduler scheduler)
    {
        if (ReferenceEquals(_current, scheduler))
            _current = null;
    }

    /// <summary>The frame-frozen clock — the single time source for timers (and animations at P8).</summary>
    public FrameClock Clock { get; } = new();

    /// <summary>The reduced-motion switch (doc §9.7; consumed by the animation surface at P8 — timers ignore it).</summary>
    public bool AnimationsEnabled { get; set; } = true;

    /// <inheritdoc/>
    public void BeginFrame(in FrameTime time) => Clock.Now = time.Elapsed;

    /// <summary>
    /// Phase 4: fires due timers at the frozen clock (ND20 — a repeating timer fires at most once
    /// per frame and re-arms from the frozen time). Iterates a count snapshot, so a timer started
    /// from a callback is armed at the frozen clock but never fires in the same frame (N195).
    /// Callback exceptions propagate to S6's guarded tick; each timer's state is updated
    /// <b>before</b> its callback runs, so a thrower never re-fires and the registry stays
    /// consistent (flag-then-sweep — N194).
    /// </summary>
    public void Tick()
    {
        var now = Clock.Now;

        // §9.7/AD15: a true→false AnimationsEnabled flip collapses motion at the next Tick — finite instances
        // (incl. Delayed/Paused) snap to their end through the completion branch, perpetual instances retract;
        // Holding is untouched. false→true is prospective (handled at Begin). Snapshot the count first.
        if (_animationsEnabledLast && !AnimationsEnabled)
        {
            var live = _instances.Count;
            for (var i = 0; i < live; i++)
            {
                var state = _instances[i].State;
                if (state is AnimationState.Delayed or AnimationState.Running or AnimationState.Paused)
                    _instances[i].ApplyReducedMotion();
            }
        }
        _animationsEnabledLast = AnimationsEnabled;

        var timerCount = _timers.Count; // snapshot — callbacks may Start new timers (appended, skipped this frame)

        try
        {
            for (var i = 0; i < timerCount; i++)
                _timers[i].TickIfDue(now);
        }
        finally
        {
            for (var i = _timers.Count - 1; i >= 0; i--)
            {
                if (!_timers[i].IsRunning)
                    _timers.RemoveAt(i);
            }
        }

        // Sample the active animation instances at the frozen clock (§9.1). Count snapshot: an instance begun
        // inside a sample callback appends and is skipped this frame (it self-sampled at Begin — AD2/N4).
        var count = _instances.Count;
        for (var i = 0; i < count; i++)
            if (_instances[i].IsActive)
                _instances[i].Sample(now);

        DrainCompleted();   // raise completions AFTER the whole pass — frame-N values are coherent first (AD3)
        SweepFinished();
#if DEBUG
        TrackLeaks();
#endif
    }

    /// <summary>
    /// After the post-Tick styling flush (§9.1): completion processing for instances ignited on that edge
    /// (e.g. animation-driven pseudo flips → storyboards begun after Tick's snapshot). They self-sampled at
    /// Begin, so this only drains their pending completions. Cheap no-op when nothing started.
    /// </summary>
    public void TickNewlyStarted()
    {
        if (_completed.Count > 0)
            DrainCompleted();
        SweepFinished();
    }

    /// <summary>The idle gate (doc §10.5 Phase 7 / §9.6): running timers + Delayed/Running instances count — S6
    /// never parks while one is pending. Paused/Holding/Completed/Stopped do not (Holding costs nothing).</summary>
    public bool HasActiveAnimations
    {
        get
        {
            for (var i = 0; i < _timers.Count; i++)
                if (_timers[i].IsRunning)
                    return true;

            for (var i = 0; i < _instances.Count; i++)
                if (_instances[i].IsActive)
                    return true;

            return false;
        }
    }

    /// <summary>Teardown (doc §9.6): stops every timer and retracts every live animation handle (bases resurface
    /// for one final restore frame) with no <c>Completed</c>; idempotent, then inert (subsequent Begin throws).</summary>
    public void Shutdown()
    {
        _isShutdown = true;
        for (var i = 0; i < _timers.Count; i++)
            _timers[i].Stop();
        _timers.Clear();

        for (var i = 0; i < _storyboards.Count; i++)
            _storyboards[i].MarkTerminated();
        _storyboards.Clear();

        for (var i = 0; i < _instances.Count; i++)
            _instances[i].Retire();
        _instances.Clear();
        _completed.Clear();
    }

    // ── Animation instances (P8 A0) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins an animation on <paramref name="target"/>.<paramref name="property"/> (design doc §9.2). Handoff
    /// (SnapshotAndReplace): any live instance on the same (target, property) is retired silently first; the new
    /// instance self-samples now when its <c>BeginTime</c> is zero (frame coherence — AD2).
    /// </summary>
    internal AnimationHandle Begin<T>(UIObject target, StyledProperty<T> property, IAnimation<T> animation, in AnimationStartOptions options)
    {
        if (_isShutdown)
            throw new InvalidOperationException("The animation scheduler is shut down; no new animations can begin.");

        RetireMatching(target, property);   // SnapshotAndReplace — retire the running one (no Completed, §9.4)

        var instance = new AnimationInstance<T>(this, target, property, animation, Clock.Now + options.BeginTime, options);
        var handle = new AnimationHandle(instance, target, property);
        instance.BindPublicHandle(handle);
        _instances.Add(instance);
#if DEBUG
        WarnIfPerpetualLayout(target, property, animation);
#endif
        StartInstance(instance);
        return handle;
    }

    /// <summary>Stops any animation on (<paramref name="target"/>, <paramref name="property"/>) — no <c>Completed</c> (§9.2).</summary>
    internal void StopAnimation(UIObject target, UIProperty property) => RetireMatching(target, property);

    /// <summary>Detach-stop (§9.6): retires + evicts every storyboard scoped on <paramref name="element"/> and every
    /// instance targeting it; no <c>Completed</c>; idempotent against Fork B's own retraction on the same detach.</summary>
    internal void OnElementDetached(UIObject element)
    {
        // Storyboard groups scoped on this element (retires all their children, whatever they target).
        for (var i = _storyboards.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_storyboards[i].Scope, element))
                RetireStoryboard(_storyboards[i]);

        // Standalone instances (and any surviving storyboard child) targeting this element directly.
        for (var i = _instances.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_instances[i].TargetObject, element))
            {
                _instances[i].Retire();
                _instances.RemoveAt(i);
            }
    }

    internal void EnqueueCompleted(IAnimationCompletion completion) => _completed.Add(completion);

    internal void Remove(AnimationInstance instance) => _instances.Remove(instance);

    // ── Storyboards (P8 A1) ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins <paramref name="storyboard"/> on <paramref name="scope"/> keyed <c>(igniter, scope)</c> (design doc
    /// §9.3). Handoff retires any live instance on the same key first. Each track resolves + starts a child;
    /// on a track failure the imperative path throws (after rolling the partial group back), the edge-ignited
    /// path routes to <see cref="AnimationDiagnostics"/> and skips the track (siblings proceed).
    /// </summary>
    internal StoryboardHandle BeginStoryboard(object igniter, UIElement scope, Storyboard storyboard, HandoffBehavior handoff, bool throwOnFailure)
    {
        if (_isShutdown)
            throw new InvalidOperationException("The animation scheduler is shut down; no new storyboards can begin.");

        RetireStoryboardByKey(igniter, scope); // SnapshotAndReplace at the (igniter, scope) level (§9.4)

        var instance = new StoryboardInstance(this, igniter, scope, storyboard);
        _storyboards.Add(instance);

        foreach (var track in storyboard.Tracks)
        {
            if (track.BeginOn(this, instance, scope, out var failure))
                continue;

            if (throwOnFailure)
            {
                RetireStoryboard(instance); // roll the partial group back before throwing
                throw new InvalidOperationException(failure);
            }

            AnimationDiagnostics.RaiseTrackError(new StoryboardTrackError(storyboard, track, scope, failure!));
        }

        instance.OnAllChildrenStarted();
        return instance.Handle;
    }

    /// <summary>Starts one storyboard track's child instance under <paramref name="owner"/> (property-level last-started-wins).</summary>
    internal void BeginStoryboardChild<T>(StoryboardInstance owner, UIObject target, StyledProperty<T> property, IAnimation<T> animation, in AnimationStartOptions options)
    {
        RetireMatching(target, property); // a new animation on the property retires the old (storyboard or standalone)

        var instance = new AnimationInstance<T>(this, target, property, animation, Clock.Now + options.BeginTime, options)
        {
            Owner = owner,
        };
        _instances.Add(instance);
        owner.AddChild(instance); // register BEFORE the first sample (a zero-duration child can't underflow the count)
#if DEBUG
        WarnIfPerpetualLayout(target, property, animation);
#endif
        StartInstance(instance);
    }

    /// <summary>The §9.7 Begin branch: a normal self-sample when motion is enabled, else the reduced-motion collapse (AD15).</summary>
    private void StartInstance(AnimationInstance instance)
    {
        if (AnimationsEnabled)
            instance.BeginNow(Clock.Now);
        else
            instance.ApplyReducedMotion();
    }

    /// <summary>The public <c>StoryboardHandle.Stop()</c> path — retires this group silently.</summary>
    internal void StopStoryboard(StoryboardInstance instance) => RetireStoryboard(instance);

    /// <summary>Stops the instance keyed (<paramref name="igniter"/>, <paramref name="scope"/>) — <c>Storyboard.Stop</c> (igniter = the storyboard).</summary>
    internal void StopStoryboardByKey(object igniter, UIElement scope) => RetireStoryboardByKey(igniter, scope);

    /// <summary>Stops every live instance of <paramref name="storyboard"/> on <paramref name="scope"/> across igniters — <c>StopStoryboard</c> (§9.3).</summary>
    internal void StopStoryboardOnScope(Storyboard storyboard, UIElement scope)
    {
        for (var i = _storyboards.Count - 1; i >= 0; i--)
        {
            var sb = _storyboards[i];
            if (ReferenceEquals(sb.Storyboard, storyboard) && ReferenceEquals(sb.Scope, scope))
                RetireStoryboard(sb);
        }
    }

    private void RetireStoryboardByKey(object igniter, UIElement scope)
    {
        for (var i = _storyboards.Count - 1; i >= 0; i--)
        {
            var sb = _storyboards[i];
            if (ReferenceEquals(sb.Igniter, igniter) && ReferenceEquals(sb.Scope, scope))
                RetireStoryboard(sb);
        }
    }

    private void RetireStoryboard(StoryboardInstance instance)
    {
        instance.MarkTerminated(); // before retiring children so their OnChildRetired is a no-op past completion
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_instances[i].Owner, instance))
                continue;

            _instances[i].Retire();
            _instances.RemoveAt(i);
        }
        _storyboards.Remove(instance);
    }

    private void RetireMatching(UIObject target, UIProperty property)
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
        {
            var instance = _instances[i];
            if (ReferenceEquals(instance.TargetObject, target) && ReferenceEquals(instance.TargetPropertyObject, property))
            {
                instance.Retire();
                _instances.RemoveAt(i);
            }
        }
    }

    private void DrainCompleted()
    {
        // index-over-live-Count: a Completed handler may Begin a zero-duration animation that self-completes and
        // enqueues here mid-drain — same-frame delivery (AD7).
        for (var i = 0; i < _completed.Count; i++)
            _completed[i].RaiseCompleted();
        _completed.Clear();
    }

    private void SweepFinished()
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
            if (_instances[i].IsFinished)   // Stopped/Completed — Holding/Delayed/Running/Paused stay registered
                _instances.RemoveAt(i);
    }

#if DEBUG
    private const int LeakWarnFrames = 120; // ~2s at 60fps of a never-attached target before we suspect a leak (§9.6)

    /// <summary>Warns once if a perpetual animation targets a layout-affecting property — it forces measure/arrange forever (§9.9).</summary>
    private static void WarnIfPerpetualLayout<T>(UIObject target, StyledProperty<T> property, IAnimation<T> animation)
    {
        if (animation.Duration != TimeSpan.MaxValue)
            return;
        if ((property.GetEffects(target.GetType()) & PropertyEffects.AffectsMeasure) == 0)
            return;

        AnimationDiagnostics.RaiseWarning(
            $"Perpetual animation on layout-affecting property '{property}' of {target.GetType().Name} forces a " +
            "measure/arrange every frame forever (design doc §9.9 — prefer a composite-shaped target).");
    }

    /// <summary>The §9.6 leak tracker: an animation whose target never enters the tree for many frames is a probable leak.</summary>
    private void TrackLeaks()
    {
        for (var i = 0; i < _instances.Count; i++)
        {
            var instance = _instances[i];
            if (instance.TargetObject is UIElement { IsAttachedToTree: true })
            {
                instance.EverAttached = true;
                instance.UnattachedFrames = 0;
                continue;
            }

            if (instance.EverAttached)
                continue; // attached then detached ⇒ the detach-stop path owns it, not a leak

            if (++instance.UnattachedFrames == LeakWarnFrames)
                AnimationDiagnostics.RaiseWarning(
                    $"Animation on a never-attached {instance.TargetObject.GetType().Name} has run {LeakWarnFrames} " +
                    "frames without its target entering the tree (probable leak — design doc §9.6).");
        }
    }
#endif

    /// <summary>Arms <paramref name="timer"/> into the registry (no-op after <see cref="Shutdown"/>).</summary>
    /// <returns>Whether the timer was armed.</returns>
    internal bool Arm(UITimer timer)
    {
        if (_isShutdown)
            return false;

        if (!_timers.Contains(timer))
            _timers.Add(timer);

        return true;
    }
}
