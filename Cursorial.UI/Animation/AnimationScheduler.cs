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
    private bool _isShutdown;

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
        var count = _timers.Count; // snapshot — callbacks may Start new timers (appended, skipped this frame)

        try
        {
            for (var i = 0; i < count; i++)
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
    }

    /// <summary>Cheap no-op until storyboards land at P8.</summary>
    public void TickNewlyStarted()
    {
    }

    /// <summary>The idle gate (doc §10.5 Phase 7): running timers count — S6 never parks while one is pending.</summary>
    public bool HasActiveAnimations
    {
        get
        {
            for (var i = 0; i < _timers.Count; i++)
            {
                if (_timers[i].IsRunning)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Teardown (doc §9.6): stops every timer; idempotent, then inert (new timers never arm).</summary>
    public void Shutdown()
    {
        _isShutdown = true;
        for (var i = 0; i < _timers.Count; i++)
            _timers[i].Stop();

        _timers.Clear();
    }

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
