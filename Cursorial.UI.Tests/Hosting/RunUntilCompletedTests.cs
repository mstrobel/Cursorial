using System.Diagnostics;

using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The contract of <see cref="UIHeadlessHost.RunUntilCompleted"/>, the host's pumping wait: it
/// reports its outcome as a <see cref="bool"/> (like <see cref="UIHeadlessHost.RunUntilIdle"/>)
/// rather than throwing, and it runs a resumption that only a frame can run.
/// <para>
/// <see cref="DispatcherResumptionWaitTests"/> is the deterministic reproduction of the deadlock
/// this exists to prevent; these two rows pin the API shape that replaced it.
/// </para>
/// </summary>
public sealed class RunUntilCompletedTests
{
    /// <summary>Long enough that the pool hop cannot land inside <c>RunUntilIdle</c>'s frames.</summary>
    private static readonly TimeSpan PoolDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The timeout contract: a task that never completes ends the wait at the caller's deadline with
    /// <see langword="false"/> — no exception, and no hang.
    /// </summary>
    [Fact]
    public void RunUntilCompleted_ReturnsFalse_WhenTheTaskNeverCompletes()
    {
        using var host = CreateHost();

        var never = new TaskCompletionSource().Task;
        var deadline = TimeSpan.FromMilliseconds(250);
        var clock = Stopwatch.StartNew();

        var completed = host.RunUntilCompleted(never, deadline);
        var elapsed = clock.Elapsed;

        Assert.False(completed);
        Assert.False(never.IsCompleted);

        // It waited its deadline out — not less …
        Assert.True(elapsed >= deadline,
                    $"the wait returned before its own deadline ({elapsed.TotalMilliseconds:0} ms of {deadline.TotalMilliseconds:0} ms)");

        // … and came back on it, rather than running to the 5 s default or not at all.
        Assert.True(elapsed < TimeSpan.FromSeconds(2),
                    $"the wait overran the deadline it was given ({elapsed.TotalMilliseconds:0} ms of {deadline.TotalMilliseconds:0} ms)");
    }

    /// <summary>
    /// The completion contract: a resumption POSTED to the dispatcher (the shape
    /// <see cref="UIHeadlessHost.RunUntilIdle"/> plus a blocking wait deadlocks on) is pumped by the
    /// wait itself, which returns <see langword="true"/> with the task genuinely complete.
    /// </summary>
    [Fact]
    public void RunUntilCompleted_ReturnsTrue_WhenTheResumptionMustBePostedToTheDispatcher()
    {
        using var host = CreateHost();

        var resumedOnUIThread = false;
        var flow = StartUIBoundFlow(host, () => resumedOnUIThread = host.Dispatcher.CheckAccess());

        // The dispatcher queue is legitimately empty while the work sits on the pool, so idle is
        // reported truthfully and says nothing at all about the task.
        Assert.True(host.RunUntilIdle());
        Assert.False(flow.IsCompleted);

        Assert.True(host.RunUntilCompleted(flow), "the pumping wait hit its deadline");
        Assert.True(flow.IsCompleted);
        Assert.True(resumedOnUIThread, "the resumption did not land on the UI thread");
    }

    private static UIHeadlessHost CreateHost()
    {
        var host = UIHeadlessHost.Create();
        host.ShowRoot(new TextBlock { Text = "root stub" });
        Assert.True(host.RunUntilIdle());
        return host;
    }

    /// <summary>
    /// Starts <see cref="ResumeOnUIThreadAsync"/> from inside a frame — the only place the
    /// application's <c>SynchronizationContext</c> is installed, and therefore the only place an
    /// await can capture it.
    /// </summary>
    private static Task StartUIBoundFlow(UIHeadlessHost host, Action onResume)
    {
        Task? flow = null;
        host.Dispatcher.Post(() => flow = ResumeOnUIThreadAsync(onResume));
        host.RunFrame();

        return flow ?? throw new InvalidOperationException("the dispatcher job never ran");
    }

    /// <summary>UI thread → thread pool → UI thread, with a controllable stay on the pool.</summary>
    private static async Task ResumeOnUIThreadAsync(Action onResume)
    {
        // ConfigureAwait(true): the resumption is POSTED to the dispatcher and runs only in a frame.
        await Task.Run(() => Thread.Sleep(PoolDelay)).ConfigureAwait(true);
        onResume();
    }
}
