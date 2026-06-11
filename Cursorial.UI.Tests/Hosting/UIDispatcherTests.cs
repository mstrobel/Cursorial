// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// <see cref="UIDispatcher"/> semantics (design doc §10.3): affinity, Post/InvokeAsync queueing
/// (never inline), the Phase-2 snapshot drain, await resumption on the UI thread via the sync
/// context, and post-shutdown behavior.
/// </summary>
public sealed class UIDispatcherTests
{
    [Fact]
    public void CheckAccess_TrueOnOwnerThread_FalseElsewhere()
    {
        using var host = CreateHost(out var app);
        Assert.True(app.Dispatcher.CheckAccess());

        var fromOtherThread = WorkerThread.Run(() => app.Dispatcher.CheckAccess());
        Assert.False(fromOtherThread);
    }

    [Fact]
    public void VerifyAccess_ThrowsOffThread()
    {
        using var host = CreateHost(out var app);
        var exception = WorkerThread.Run(() => Record.Exception(() => app.Dispatcher.VerifyAccess()));
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Post_RunsInNextFrameDrain_NotInline()
    {
        using var host = CreateHost(out var app);
        var ran = false;
        app.Dispatcher.Post(() => ran = true);
        Assert.False(ran); // never inline

        host.RunFrame();
        Assert.True(ran);
    }

    [Fact]
    public void Post_Stateful_PassesState()
    {
        using var host = CreateHost(out var app);
        var seen = 0;
        app.Dispatcher.Post(42, state => seen = state);
        host.RunFrame();
        Assert.Equal(42, seen);
    }

    [Fact]
    public void Post_FromBackgroundThread_MarshalsToUIThread()
    {
        using var host = CreateHost(out var app);
        var uiThread = Environment.CurrentManagedThreadId;
        var ranOn = 0;
        WorkerThread.Run(() => app.Dispatcher.Post(() => ranOn = Environment.CurrentManagedThreadId));

        host.RunFrame();
        Assert.Equal(uiThread, ranOn);
    }

    [Fact]
    public void InvokeAsync_FromUIThread_QueuesNeverInline()
    {
        using var host = CreateHost(out var app);
        var task = app.Dispatcher.InvokeAsync(() => 7);
        Assert.False(task.IsCompleted); // queued for the next drain, even from the UI thread

        host.RunFrame();
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(7, task.Result);
    }

    [Fact]
    public void InvokeAsync_ExceptionGoesToTaskOnly()
    {
        using var host = CreateHost(out var app);
        var funneled = 0;
        app.DispatcherUnhandledException += (_, e) => { funneled++; e.Handled = true; };

        var task = app.Dispatcher.InvokeAsync(() => throw new InvalidOperationException("boom"));
        host.RunFrame();

        Assert.True(task.IsFaulted);
        Assert.Equal(0, funneled); // WPF semantics: the returned task owns it
    }

    [Fact]
    public void Post_Exception_RoutesToUnhandledFunnel()
    {
        using var host = CreateHost(out var app);
        Exception? seen = null;
        app.DispatcherUnhandledException += (_, e) => { seen = e.Exception; e.Handled = true; };

        app.Dispatcher.Post(() => throw new InvalidOperationException("posted"));
        host.RunFrame();

        Assert.IsType<InvalidOperationException>(seen);
        // Handled ⇒ the loop continues: another frame runs fine.
        host.RunFrame();
    }

    [Fact]
    public void JobsPostedDuringDrain_RunNextFrame()
    {
        using var host = CreateHost(out var app);
        var first = false;
        var second = false;
        app.Dispatcher.Post(() =>
        {
            first = true;
            app.Dispatcher.Post(() => second = true);
        });

        host.RunFrame();
        Assert.True(first);
        Assert.False(second); // snapshot count: enqueued mid-drain runs NEXT frame

        host.RunFrame();
        Assert.True(second);
    }

    [Fact]
    public void AwaitContinuation_ResumesOnUIThread_InLaterFrame()
    {
        using var host = CreateHost(out var app);
        var uiThread = Environment.CurrentManagedThreadId;
        var resumedOn = 0;
        var done = false;

        app.Dispatcher.Post(() => RunAsyncFlow());
        host.RunFrame();  // runs the post; the async flow awaits InvokeAsync (queued)
        host.RunFrame();  // runs the InvokeAsync job; continuation posts via the sync context
        host.RunFrame();  // runs the continuation
        Assert.True(done);
        Assert.Equal(uiThread, resumedOn);
        return;

        async void RunAsyncFlow()
        {
            await app.Dispatcher.InvokeAsync(() => 1);
            resumedOn = Environment.CurrentManagedThreadId; // landed via UISynchronizationContext
            done = true;
        }
    }

    [Fact]
    public void AfterShutdown_InvokeAsyncCanceled_PostDropped()
    {
        using var host = CreateHost(out var app);
        app.Shutdown();

        var invoke = app.Dispatcher.InvokeAsync(() => 1);
        Assert.True(invoke.IsCanceled);

        var ran = false;
        app.Dispatcher.Post(() => ran = true); // dropped
        Assert.Equal(0, app.Dispatcher.JobCount);
        Assert.False(ran);
    }

    [Fact]
    public void ShutdownToken_CanceledOnShutdown()
    {
        using var host = CreateHost(out var app);
        Assert.False(app.Dispatcher.ShutdownToken.IsCancellationRequested);
        app.Shutdown(3);
        Assert.True(app.Dispatcher.ShutdownToken.IsCancellationRequested);
    }

#if DEBUG
    [Fact]
    public void ElementAffinity_FollowsDispatcher_NotConstructionThreadAlone()
    {
        using var host = CreateHost(out _);

        // Constructed while UIApplication.Current is ambient ⇒ the element captured the
        // dispatcher (ledger A25); touching it from another thread trips the DEBUG assert.
        var element = new Cursorial.Tests.UI.LayoutMatrix.Probe(2, 1);
        Assert.True(element.CheckAccess());

        var exception = WorkerThread.Run(() => Record.Exception(() => element.VerifyAccess()));
        Assert.IsType<InvalidOperationException>(exception);
    }
#endif

    // ───────────────────────────── wake protocol (the Phase-7 clamp's substrate) ─────────────────────────────

    [Fact]
    public void WaitForWake_TimesOutFalse_WakeReturnsTrue_SignalIsSticky()
    {
        using var host = CreateHost(out var app);
        var dispatcher = app.Dispatcher;

        // No producer: the timed wait expires false (the clamp loop's budget-exhausted exit).
        Assert.False(dispatcher.WaitForWake(TimeSpan.FromMilliseconds(1)));

        // A Wake BEFORE the wait is not lost (the producer-first protocol): the wait returns
        // true immediately and consumes the signal.
        dispatcher.Wake();
        Assert.True(dispatcher.WaitForWake(TimeSpan.FromMilliseconds(1)));

        // Consumed: the next timed wait expires false again. This pair is what the Phase-7 rate
        // clamp leans on — each wake during the clamp is observed (true) without ending the clamp,
        // and the budget loop re-waits until the frame interval is spent.
        Assert.False(dispatcher.WaitForWake(TimeSpan.FromMilliseconds(1)));

        // Wake coalesces (at-most-once release): N producer wakes ⇒ ONE signaled wait.
        dispatcher.Wake();
        dispatcher.Wake();
        dispatcher.Wake();
        Assert.True(dispatcher.WaitForWake(TimeSpan.FromMilliseconds(1)));
        Assert.False(dispatcher.WaitForWake(TimeSpan.FromMilliseconds(1)));
    }

    private static UITestHost CreateHost(out UIApplication app)
    {
        var host = UITestHost.Create();
        app = host.Application;
        return host;
    }
}
