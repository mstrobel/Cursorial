// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// A frame-aligned timer (design doc §9.8): registered with the thread-ambient
/// <see cref="AnimationScheduler"/>, fired during the frame's tick at the <b>frozen</b>
/// <see cref="FrameClock"/> time (latency ≤ one paced frame period), and counted by the loop's
/// idle guard so a pending timer keeps frames coming. <c>FakeTimeProvider</c>-deterministic in
/// tests.
/// </summary>
/// <remarks>
/// Coalescing (ND20): a repeating timer fires at most once per frame regardless of how many
/// intervals elapsed, re-arming from the frozen clock. Timers are not element-scoped — owners stop
/// them on detach (S8's unhook convention). Callback exceptions surface through S6's guarded tick;
/// the timer's state is updated before the callback, so a thrower never re-fires. UI thread only.
/// </remarks>
public sealed class UITimer : IDisposable
{
    private readonly AnimationScheduler _scheduler;
    private readonly Action _callback;
    private readonly TimeSpan _dueTime;
    private readonly TimeSpan? _interval;
    private TimeSpan _due;
    private bool _isRunning;

    private UITimer(AnimationScheduler scheduler, TimeSpan dueTime, TimeSpan? interval, Action callback)
    {
        _scheduler = scheduler;
        _dueTime = dueTime;
        _interval = interval;
        _callback = callback;
    }

    /// <summary>Starts a one-shot timer firing once <paramref name="dueTime"/> after the current frame clock.</summary>
    /// <param name="dueTime">The delay, measured from the frozen frame clock.</param>
    /// <param name="callback">Invoked on the UI thread during the due frame's tick.</param>
    public static UITimer Start(TimeSpan dueTime, Action callback)
        => StartCore(dueTime, interval: null, callback);

    /// <summary>Starts a repeating timer: first fire after <paramref name="dueTime"/>, then every <paramref name="interval"/>.</summary>
    /// <param name="dueTime">The initial delay, measured from the frozen frame clock.</param>
    /// <param name="interval">The repeat period (coalesced to at most one fire per frame — ND20).</param>
    /// <param name="callback">Invoked on the UI thread during each due frame's tick.</param>
    public static UITimer Start(TimeSpan dueTime, TimeSpan interval, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return StartCore(dueTime, interval, callback);
    }

    private static UITimer StartCore(TimeSpan dueTime, TimeSpan? interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);

        var scheduler = AnimationScheduler.Current;
        var timer = new UITimer(scheduler, dueTime, interval, callback);
        timer._due = scheduler.Clock.Now + dueTime;
        timer._isRunning = scheduler.Arm(timer);
        return timer;
    }

    /// <summary>Whether the timer is armed (false after a one-shot fires, <see cref="Stop"/>, or scheduler shutdown).</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Re-arms the due time from the current frame clock (legal on running and stopped timers alike).</summary>
    public void Restart()
    {
        _due = _scheduler.Clock.Now + _dueTime;
        _isRunning = _scheduler.Arm(this);
    }

    /// <summary>Stops the timer; idempotent (equivalent to <see cref="Dispose"/>).</summary>
    public void Stop() => _isRunning = false;

    /// <inheritdoc cref="Stop"/>
    public void Dispose() => Stop();

    /// <summary>
    /// Scheduler-internal: fires when due at the frozen <paramref name="now"/>. State is updated
    /// <b>before</b> the callback (one-shot disarms; repeating re-arms from the frozen clock), so a
    /// throwing callback never re-fires and the registry stays consistent (N194).
    /// </summary>
    internal void TickIfDue(TimeSpan now)
    {
        if (!_isRunning || _due > now)
            return;

        if (_interval is { } interval)
            _due = now + interval; // coalesced re-arm from the frozen clock (ND20/N191)
        else
            _isRunning = false;    // one-shot (N190)

        _callback();
    }
}
