using System.Collections.Concurrent;

using Cursorial.UI.Data;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The single UI thread's marshal point (design doc §10.3): <see cref="CheckAccess"/> /
/// <see cref="VerifyAccess"/> affinity, fire-and-forget <see cref="Post(Action)"/>, frame-coherent
/// <see cref="InvokeAsync(Action)"/>, and the loop's wake protocol. <b>No priority tiers exist</b>
/// (invariant 1) and there are no nested dispatcher loops — ordering is the frame-phase sequence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership model.</b> At <see cref="UIApplication.CreateBuilder"/>'s <c>Build()</c> the owner
/// is the building thread (pre-run <see cref="UIObject"/> construction is legal there). The
/// production loop's first act is the one-shot <see cref="TransferOwnershipToCurrentThread"/>,
/// which pins the dedicated UI thread; <see cref="VerifyAccess"/> from the old thread throws after
/// it. <c>UITestHost</c> never transfers — the creating thread stays owner and steps frames.
/// </para>
/// <para>
/// <b><see cref="InvokeAsync(Action)"/> never runs inline</b>, even from the UI thread — inline
/// execution would break frame-phase ordering. Callers wanting inline semantics check
/// <see cref="CheckAccess"/> and call the delegate directly. Jobs run in Phase 2 of the frame loop
/// with a snapshot count: jobs posted during the drain run the <em>next</em> frame (no live-lock).
/// After shutdown, <see cref="InvokeAsync(Action)"/> returns a canceled task and
/// <see cref="Post(Action)"/> is dropped — nothing enqueues into the void.
/// </para>
/// <para>
/// <b>It <em>is</em> the binding engine's seam.</b> <see cref="IUIDispatcher"/> (design doc §6.9)
/// is exactly <see cref="CheckAccess"/> + <see cref="Post(Action)"/>, both of which this type
/// already exposes, so it implements the interface directly rather than being wrapped. That is
/// load-bearing for allocation, not tidiness: <c>BindingExpressionCore</c> resolves the ambient
/// dispatcher <b>once per binding push</b> and, on the UI thread, discards it immediately
/// (<see cref="CheckAccess"/> is true) — an adapter object there would be 24 B of garbage per push
/// in every app.
/// </para>
/// </remarks>
public sealed class UIDispatcher : IUIDispatcher
{
    private readonly ConcurrentQueue<DispatcherJob> _jobs = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _wakeSignaled;
    private int _ownerThreadId;
    private bool _ownershipTransferred;

    internal UIDispatcher() => _ownerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>Whether the calling thread is the dispatcher's owner (the UI thread).</summary>
    public bool CheckAccess() => Volatile.Read(ref _ownerThreadId) == Environment.CurrentManagedThreadId;

    /// <summary>Throws <see cref="InvalidOperationException"/> when called off the UI thread.</summary>
    public void VerifyAccess()
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException(
                $"Cross-thread access: this call must be made from the UI thread (managed thread " +
                $"{Volatile.Read(ref _ownerThreadId)}), not thread {Environment.CurrentManagedThreadId}. " +
                "Marshal with UIDispatcher.Post or InvokeAsync.");
        }
    }

    /// <summary>Canceled when shutdown is requested — the cooperative-cancellation token for app code.</summary>
    public CancellationToken ShutdownToken => _shutdownCts.Token;

    /// <summary>
    /// Queues <paramref name="action"/> for the next dispatcher drain (Phase 2). Thread-safe;
    /// wakes an idle loop. Dropped after shutdown. Exceptions route to
    /// <see cref="UIApplication.DispatcherUnhandledException"/>.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_shutdownCts.IsCancellationRequested)
            return; // documented: dropped after shutdown

        _jobs.Enqueue(new DispatcherJob(action, null, null, null, null));
        Wake();
    }

    /// <summary>
    /// The allocation-light stateful overload: no closure — <paramref name="state"/> rides the job
    /// (value types box once; the invoker delegate is cached per <typeparamref name="TState"/>).
    /// </summary>
    public void Post<TState>(TState state, Action<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_shutdownCts.IsCancellationRequested)
            return;

        _jobs.Enqueue(new DispatcherJob(null, action, state, StatefulInvoker<TState>.Instance, null));
        Wake();
    }

    /// <summary>Queues <paramref name="action"/> and returns a task that completes after it runs (a later frame's Phase 2).</summary>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return EnqueueInvoke(new InvokeActionJob(action));
    }

    /// <summary>Queues <paramref name="callback"/> and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var job = new InvokeFuncJob<T>(callback);
        EnqueueInvoke(job);
        return job.Task;
    }

    /// <summary>Queues an async callback; the returned task completes when the inner task does (unwrapped).</summary>
    public Task InvokeAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return EnqueueInvoke(new InvokeAsyncJob(callback));
    }

    /// <summary>Queues an async callback and unwraps its result.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var job = new InvokeAsyncFuncJob<T>(callback);
        EnqueueInvoke(job);
        return job.Task;
    }

    // ───────────────────────────── frame-loop internals ─────────────────────────────

    /// <summary>
    /// The one-shot Build-thread → UI-thread ownership hand-off (design doc §10.3). Throws on a
    /// second transfer. <c>UITestHost</c> never calls this.
    /// </summary>
    internal void TransferOwnershipToCurrentThread()
    {
        if (_ownershipTransferred)
            throw new InvalidOperationException("Dispatcher ownership has already been transferred once.");

        _ownershipTransferred = true;
        Volatile.Write(ref _ownerThreadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// The producer half of the normative wake protocol (design doc §10.5): callers enqueue their
    /// item <b>first</b>, then this CASes the signaled flag 0→1 and releases the semaphore only on
    /// CAS success (at-most-once release).
    /// </summary>
    internal void Wake()
    {
        if (Interlocked.CompareExchange(ref _wakeSignaled, 1, 0) == 0)
            _wake.Release();
    }

    /// <summary>
    /// The consumer half: waits up to <paramref name="timeout"/>; on a signaled return, clears the
    /// flag <b>before</b> the caller drains (the load-bearing ordering — clearing after the drain
    /// loses the race where a post-drain producer skips the release and the loop parks on a
    /// non-empty queue).
    /// </summary>
    internal bool WaitForWake(TimeSpan timeout)
    {
        if (!_wake.Wait(timeout))
            return false;

        Interlocked.Exchange(ref _wakeSignaled, 0);
        return true;
    }

    /// <summary>The parking variant — blocks until a producer wakes the loop.</summary>
    internal void WaitForWake()
    {
        _wake.Wait();
        Interlocked.Exchange(ref _wakeSignaled, 0);
    }

    /// <summary>The Phase-2 snapshot bound.</summary>
    internal int JobCount => _jobs.Count;

    /// <summary>Dequeues one job for Phase 2.</summary>
    internal bool TryDequeueJob(out DispatcherJob job) => _jobs.TryDequeue(out job);

    /// <summary>Cancels <see cref="ShutdownToken"/>; later posts drop and invokes cancel.</summary>
    internal void BeginShutdown()
    {
        if (!_shutdownCts.IsCancellationRequested)
            _shutdownCts.Cancel();
    }

    /// <summary>
    /// Teardown step 0 (design doc §10.7): drains the queue to empty, completing every
    /// <see cref="InvokeAsync(Action)"/> completion as canceled <b>without running the actions</b> —
    /// no caller awaits into the void.
    /// </summary>
    internal void DrainJobsCanceled()
    {
        while (_jobs.TryDequeue(out var job))
            job.Cancel();
    }

    private Task EnqueueInvoke(InvokeJob job)
    {
        if (_shutdownCts.IsCancellationRequested)
        {
            job.TrySetCanceled();
            return job.CompletionTask;
        }

        _jobs.Enqueue(new DispatcherJob(null, null, null, null, job));
        Wake();
        return job.CompletionTask;
    }

    /// <summary>Caches the unboxing invoker per closed state type (one delegate per TState, ever).</summary>
    private static class StatefulInvoker<TState>
    {
        public static readonly Action<object?, object?> Instance =
            static (action, state) => ((Action<TState>)action!)((TState)state!);
    }
}

/// <summary>One queued dispatcher work item (design doc §10.3 / spec §3.8).</summary>
internal readonly struct DispatcherJob(
    Action? plain,
    object? statefulAction,
    object? state,
    Action<object?, object?>? statefulInvoker,
    InvokeJob? invoke)
{
    /// <summary>
    /// Runs the job. <c>Post</c>-shaped jobs let exceptions propagate to the caller (the frame
    /// loop's funnel); <c>InvokeAsync</c>-shaped jobs capture exceptions into their task and never
    /// throw (WPF semantics — the returned task is the caller's responsibility).
    /// </summary>
    public void Run()
    {
        if (plain is not null)
            plain();
        else if (statefulInvoker is not null)
            statefulInvoker(statefulAction, state);
        else
            invoke!.Run();
    }

    /// <summary>Teardown: cancels an <c>InvokeAsync</c> completion without running; drops posts.</summary>
    public void Cancel() => invoke?.TrySetCanceled();
}

/// <summary>The <c>InvokeAsync</c> job family — exceptions go to the returned task only.</summary>
internal abstract class InvokeJob
{
    public abstract Task CompletionTask { get; }

    public abstract void Run();

    public abstract void TrySetCanceled();
}

internal sealed class InvokeActionJob(Action action) : InvokeJob
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task CompletionTask => _tcs.Task;

    public override void Run()
    {
        try
        {
            action();
            _tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    public override void TrySetCanceled() => _tcs.TrySetCanceled();
}

internal sealed class InvokeFuncJob<T>(Func<T> callback) : InvokeJob
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _tcs.Task;

    public override Task CompletionTask => _tcs.Task;

    public override void Run()
    {
        try
        {
            _tcs.TrySetResult(callback());
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    public override void TrySetCanceled() => _tcs.TrySetCanceled();
}

internal sealed class InvokeAsyncJob(Func<Task> callback) : InvokeJob
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task CompletionTask => _tcs.Task;

    public override void Run()
    {
        try
        {
            var task = callback();
            task.ContinueWith(
                static (t, s) =>
                {
                    var tcs = (TaskCompletionSource)s!;
                    if (t.IsCanceled)
                        tcs.TrySetCanceled();
                    else if (t.Exception is { } ex)
                        tcs.TrySetException(ex.InnerExceptions);
                    else
                        tcs.TrySetResult();
                },
                _tcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    public override void TrySetCanceled() => _tcs.TrySetCanceled();
}

internal sealed class InvokeAsyncFuncJob<T>(Func<Task<T>> callback) : InvokeJob
{
    private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Task => _tcs.Task;

    public override Task CompletionTask => _tcs.Task;

    public override void Run()
    {
        try
        {
            var task = callback();
            task.ContinueWith(
                static (t, s) =>
                {
                    var tcs = (TaskCompletionSource<T>)s!;
                    if (t.IsCanceled)
                        tcs.TrySetCanceled();
                    else if (t.Exception is { } ex)
                        tcs.TrySetException(ex.InnerExceptions);
                    else
                        tcs.TrySetResult(t.Result);
                },
                _tcs,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }

    public override void TrySetCanceled() => _tcs.TrySetCanceled();
}
