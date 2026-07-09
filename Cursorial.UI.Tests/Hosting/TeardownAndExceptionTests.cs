// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The canonical teardown order (design doc §10.7) against the synthetic host, the production
/// <c>RunAsync</c> path (dedicated thread, ownership hand-off, exit codes), and the exception
/// funnel policy (design doc §10.8).
/// </summary>
public sealed class TeardownAndExceptionTests
{
    [Fact]
    public void Teardown_EmitsCanonicalByteOrder()
    {
        var host = UITestHost.Create();
        host.ShowRoot(new Probe(4, 1) { FillGlyph = "X" });
        Assert.True(host.RunUntilIdle());

        host.Dispose();

        var bytes = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        var showCursor = bytes.IndexOf("\x1b[?25h", StringComparison.Ordinal);
        var sgrReset = bytes.IndexOf("\x1b[0m", StringComparison.Ordinal);
        var leaveAlt = bytes.IndexOf("\x1b[?1049l", StringComparison.Ordinal);

        Assert.True(showCursor >= 0, "show-cursor missing");
        Assert.True(sgrReset >= 0, "SGR reset missing");
        Assert.True(leaveAlt >= 0, "leave-alt-screen missing");
        Assert.True(showCursor < sgrReset, "show cursor must precede SGR reset");
        Assert.True(sgrReset < leaveAlt, "SGR reset must precede leaving the alt screen");
    }

    [Fact]
    public void Teardown_NoAltScreen_FallsBackToClear()
    {
        var host = UITestHost.Create(new UITestHostOptions { UseAlternateScreen = false });
        host.ShowRoot(new Probe(4, 1));
        Assert.True(host.RunUntilIdle());

        host.Dispose();

        var bytes = Encoding.ASCII.GetString(host.TeardownBytes.Span);
        Assert.DoesNotContain("\x1b[?1049l", bytes);
        Assert.Contains("\x1b[2J", bytes); // clear-screen fallback
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var host = UITestHost.Create();
        host.Dispose();
        host.Dispose();
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void Teardown_CancelsQueuedInvokeAsync_WithoutRunning()
    {
        var host = UITestHost.Create();
        var ran = false;
        var task = host.Application.Dispatcher.InvokeAsync(() => ran = true);

        host.Dispose();

        Assert.True(task.IsCanceled);
        Assert.False(ran);
    }

    [Fact]
    public void ProductionRunAsync_DedicatedThread_ExitCode_AndTeardown()
    {
        // The full production path over a synthetic host: real dedicated UI thread, ownership
        // hand-off, park/wake, Shutdown(exitCode), canonical teardown. Real wall clock.
        var terminal = new SyntheticTerminalHost(TestCapabilities.KittyTruecolor, new Size(80, 24));
        var app = UIApplication.CreateBuilder()
            .WithTerminalHost(terminal, disposeWithApp: true)
            .Build();

        var uiThreadId = 0;
        var run = app.RunAsync(() =>
        {
            uiThreadId = Environment.CurrentManagedThreadId; // the factory runs ON the UI thread
            return new Probe(4, 1) { FillGlyph = "Q" };
        });

        Assert.False(run.Wait(TimeSpan.FromMilliseconds(200))); // loop is alive, parked

        app.Shutdown(7);
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "the loop did not exit");
        Assert.Equal(7, run.Result);
        Assert.NotEqual(0, uiThreadId);
        Assert.NotEqual(Environment.CurrentManagedThreadId, uiThreadId);

        var teardown = Encoding.ASCII.GetString(terminal.FinalOutput.Span);
        Assert.Contains("\x1b[?25h", teardown);
        Assert.Contains("\x1b[?1049l", teardown);

        // Clear this thread's thread-local Current (teardown cleared the loop thread's only) —
        // xUnit reuses pool threads and a stale Current would poison later tests' captures.
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void ProductionRunAsync_PrebuiltRoot_HandsOffOwnership()
    {
        // The hand-off overload: the root is constructed on the Build thread (capturing the
        // dispatcher per ledger A25), ownership transfers to the loop thread, and the loop touches
        // the element without tripping the DEBUG affinity asserts.
        var terminal = new SyntheticTerminalHost(TestCapabilities.KittyTruecolor, new Size(80, 24));
        var app = UIApplication.CreateBuilder()
            .WithTerminalHost(terminal, disposeWithApp: true)
            .Build();
        var root = new Probe(4, 1) { FillGlyph = "H" }; // Build thread, post-Build: Current is set

        var run = app.RunAsync(root);
        var deadline = Environment.TickCount64 + 10_000;
        while (root.MeasureOverrideCount == 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(1); // wait for frame 0's layout before shutting the loop down

        app.Shutdown();
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)), "the loop did not exit");
        Assert.Equal(0, run.Result);
        Assert.True(root.MeasureOverrideCount > 0); // the loop laid it out on the UI thread

        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void ProductionRunAsync_IsSingleUse()
    {
        var terminal = new SyntheticTerminalHost(TestCapabilities.KittyTruecolor, new Size(80, 24));
        var app = UIApplication.CreateBuilder().WithTerminalHost(terminal, disposeWithApp: true).Build();
        var run = app.RunAsync(() => new Probe(1, 1));

        Assert.ThrowsAsync<InvalidOperationException>(() => app.RunAsync(() => new Probe(1, 1)))
            .GetAwaiter().GetResult();

        app.Shutdown();
        Assert.True(run.Wait(TimeSpan.FromSeconds(10)));
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public void HandledException_InMeasure_ContinuesRunning()
    {
        using var host = UITestHost.Create();
        var seen = 0;
        host.Application.DispatcherUnhandledException += (_, e) => { seen++; e.Handled = true; };

        var bomb = new Probe(4, 1) { MeasureResult = _ => throw new InvalidOperationException("measure bomb") };
        host.ShowRoot(bomb);
        host.RunFrame(); // throws inside layout → funneled → handled

        Assert.Equal(1, seen);
        host.RunFrame(); // the loop continues
        Assert.False(host.Application.HasFatalException);
    }

    [Fact]
    public void UnhandledException_IsFatal_RethrownFromFrame_TerminalStillRestored()
    {
        var host = UITestHost.Create();
        var bomb = new Probe(4, 1) { MeasureResult = _ => throw new InvalidOperationException("fatal bomb") };
        host.ShowRoot(bomb);

        var thrown = Assert.Throws<InvalidOperationException>(() => host.RunFrame());
        Assert.Equal("fatal bomb", thrown.Message);

        host.Dispose(); // the canonical teardown still runs — terminal restored on crash paths
        Assert.Contains("\x1b[?25h", Encoding.ASCII.GetString(host.TeardownBytes.Span));
    }

    [Fact]
    public void HandledDrawException_KeepsRunning_AndEmitsConservatively()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var seen = 0;
        host.Application.DispatcherUnhandledException += (_, e) => { seen++; e.Handled = true; };

        var bomb = new Probe(4, 1)
        {
            FillGlyph = "X",
            OnRender = (_, _) => throw new InvalidOperationException("draw bomb")
        };
        host.ShowRoot(bomb);
        host.RunFrame(); // the guard funnels the draw exception; the frame completes

        Assert.Equal(1, seen);
        Assert.True(host.LastFrameBytes.Length > 0); // conservative emit on a handled draw

        host.RunFrame(); // the dirty bit was cleared — the failing draw does NOT re-run every frame
        Assert.Equal(1, seen);
    }

    [Fact]
    public void UnhandledDrawException_IsFatal()
    {
        var host = UITestHost.Create();
        var bomb = new Probe(4, 1) { OnRender = (_, _) => throw new InvalidOperationException("fatal draw") };
        host.ShowRoot(bomb);

        Assert.Throws<InvalidOperationException>(() => host.RunFrame());
        host.Dispose();
    }

    [Fact]
    public void HandlerThrowing_IsFatalAggregate_NoReRaise()
    {
        var host = UITestHost.Create();
        var raises = 0;
        host.Application.DispatcherUnhandledException += (_, _) =>
        {
            raises++;
            throw new NotSupportedException("handler bomb");
        };

        host.Application.Dispatcher.Post(() => throw new InvalidOperationException("original"));
        var thrown = Assert.Throws<AggregateException>(() => host.RunFrame());

        Assert.Equal(1, raises); // NOT re-raised for the handler's own exception
        Assert.Collection(
            thrown.InnerExceptions,
            e => Assert.IsType<InvalidOperationException>(e),
            e => Assert.IsType<NotSupportedException>(e));
        host.Dispose();
    }

    // ───────────────────────────── ReportUnhandledException (the app-facing funnel seam) ─────────────────────────────

    [Fact] // On the dispatcher thread the seam funnels SYNCHRONOUSLY through the same core the frame loop uses —
           // a handler that marks Handled swallows it and the loop is unaffected (the fire-and-forget happy path).
    public void ReportUnhandledException_Handled_FunnelsImmediately_AndKeepsRunning()
    {
        using var host = UITestHost.Create();
        Exception? funneled = null;
        host.Application.DispatcherUnhandledException += (_, e) => { funneled = e.Exception; e.Handled = true; };

        var fault = new InvalidOperationException("fire-and-forget fault");
        host.Application.ReportUnhandledException(fault); // test thread owns the dispatcher ⇒ no pump needed

        Assert.Same(fault, funneled);                     // the exact reported exception reached the funnel
        Assert.False(host.Application.HasFatalException);  // handled ⇒ not fatal
        Assert.True(host.RunUntilIdle());                 // the loop runs on untouched
    }

    [Fact] // An unhandled report takes the same fatal path as an in-loop fault: recorded fatal, shutdown begun.
    public void ReportUnhandledException_Unhandled_RecordsFatal_AndBeginsShutdown()
    {
        var host = UITestHost.Create(); // NOT `using`: the fatal path is torn down explicitly, like the sibling fatal tests

        // No DispatcherUnhandledException handler ⇒ the fault is left unhandled.
        host.Application.ReportUnhandledException(new InvalidOperationException("fatal fire-and-forget"));

        Assert.True(host.Application.HasFatalException);                                // recorded fatal by the funnel core
        Assert.True(host.Application.Dispatcher.ShutdownToken.IsCancellationRequested); // …which began shutdown

        host.Dispose(); // canonical teardown still runs on the fatal path
        Assert.Contains("\x1b[?25h", Encoding.ASCII.GetString(host.TeardownBytes.Span));
    }

    [Fact] // Off the dispatcher thread the seam must MARSHAL onto it — never touch the funnel's UI-thread state
           // concurrently — so nothing funnels until the loop pumps, and it then runs ON the dispatcher thread.
    public void ReportUnhandledException_OffThread_IsMarshaledOntoTheDispatcher()
    {
        using var host = UITestHost.Create();
        var uiThreadId = Environment.CurrentManagedThreadId; // the test thread owns the dispatcher
        var seen = 0;
        var handlerThreadId = 0;
        host.Application.DispatcherUnhandledException += (_, e) =>
        {
            seen++;
            handlerThreadId = Environment.CurrentManagedThreadId;
            e.Handled = true;
        };

        // Report from a DEDICATED thread — not Task.Run, whose blocking wait can inline the body back onto the
        // owner thread and defeat the off-thread intent. Join keeps the blocked work off the UI thread.
        var reporterThreadId = 0;
        var reporter = new Thread(() =>
        {
            reporterThreadId = Environment.CurrentManagedThreadId;
            host.Application.ReportUnhandledException(new InvalidOperationException("off-thread fault"));
        });
        reporter.Start();
        reporter.Join();

        Assert.NotEqual(uiThreadId, reporterThreadId); // sanity: the report genuinely ran off the dispatcher thread
        Assert.Equal(0, seen); // POSTED, not run inline off-thread — nothing funnels until the loop pumps
        Assert.True(host.RunUntilIdle());
        Assert.Equal(1, seen);
        Assert.Equal(uiThreadId, handlerThreadId); // funneled ON the dispatcher thread, not the background one
    }
}
