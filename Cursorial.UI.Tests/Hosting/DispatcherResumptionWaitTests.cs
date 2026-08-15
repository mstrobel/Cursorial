// xUnit1031 (no blocking task ops) is deliberately disabled: these tests are ABOUT what a test does
// with an outstanding task while it owns the UI thread, so the blocking ops are the subject.
#pragma warning disable xUnit1031

using System.Diagnostics;

using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// The completion shape behind the dialog suites' most frequent CI failure, reproduced
/// deterministically with no dialog in sight: a task started ON the UI thread hops to the thread
/// pool and then resumes back ONTO the UI thread — <c>TaskDialog.ShowAsync</c>'s
/// <c>ConfigureAwait(false)</c> followed by <c>FileSaveDialog.ConfirmOverwriteAsync</c>'s
/// <c>ConfigureAwait(true)</c>.
/// <para>
/// While the work is in flight on the pool the dispatcher queue is legitimately empty, so
/// <see cref="UIHeadlessHost.RunUntilIdle"/> reports idle and returns; the resumption is posted a
/// moment later and can only run in a frame. A test that then BLOCKS the UI thread on the task is
/// the one thing that guarantees nobody ever pumps one — a hard deadlock that ends at the timeout
/// rather than at completion. Written against the pre-fix helper shape
/// (<c>RunUntilIdle</c> + <c>task.Wait(5s)</c>) this file failed at exactly 5.00s against a 400 ms
/// pool hop.
/// </para>
/// <para>
/// That is why the host's own <see cref="UIHeadlessHost.RunUntilCompleted"/> — which this file
/// guards, and which the dialog suites reach through <see cref="HeadlessPump"/> — pumps while it
/// polls instead of calling <see cref="Task.Wait(TimeSpan)"/>.
/// </para>
/// </summary>
public sealed class DispatcherResumptionWaitTests
{
    private const int Answer = 42;

    /// <summary>Long enough that the pool hop cannot land inside <c>RunUntilIdle</c>'s frames — the race, made a certainty.</summary>
    private static readonly TimeSpan PoolDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>The regression guard: the pumping wait runs the posted resumption and ends at completion, not at the deadline.</summary>
    [Fact]
    public void PumpingWait_RunsADispatcherPostedResumption_AndCompletesWithoutDeadlock()
    {
        using var host = CreateHost();
        var flow = StartUIBoundFlow(host);

        // The dialog helpers' prologue. The queue really is empty here — the continuation is still
        // parked on the pool — so idle is reported truthfully and tells us nothing about the task.
        Assert.True(host.RunUntilIdle());
        Assert.False(flow.IsCompleted);

        var clock = Stopwatch.StartNew();

        Assert.True(host.RunUntilCompleted(flow), "the UI-bound flow did not complete within the deadline");
        Assert.Equal(Answer, flow.Result);

        // The point of the fix: completion tracked the 400 ms hop instead of burning the deadline.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2),
                    $"the pumping wait should end with the resumption (~{PoolDelay.TotalMilliseconds:0} ms), not at a deadline; took {clock.Elapsed}");
    }

    /// <summary>
    /// The same flow resumed by plain frame stepping — the resumption is an ordinary dispatcher job,
    /// so nothing but the blocking wait was ever wrong.
    /// </summary>
    [Fact]
    public void FrameStepping_RunsADispatcherPostedResumption()
    {
        using var host = CreateHost();
        var flow = StartUIBoundFlow(host);

        Assert.True(host.RunUntilIdle());
        Assert.False(flow.IsCompleted);

        var clock = Stopwatch.StartNew();

        while (!flow.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
            host.RunFrame();

        Assert.True(flow.IsCompleted, "the resumption never ran");
        Assert.Equal(Answer, flow.Result);
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
    private static Task<int> StartUIBoundFlow(UIHeadlessHost host)
    {
        Task<int>? flow = null;
        host.Dispatcher.Post(() => flow = ResumeOnUIThreadAsync(host));
        host.RunFrame();

        return flow ?? throw new InvalidOperationException("the dispatcher job never ran");
    }

    /// <summary>UI thread → thread pool → UI thread, with a controllable stay on the pool.</summary>
    private static async Task<int> ResumeOnUIThreadAsync(UIHeadlessHost host)
    {
        // ConfigureAwait(true): the resumption is POSTED to the dispatcher and runs only in a frame.
        await Task.Run(() => Thread.Sleep(PoolDelay)).ConfigureAwait(true);

        Assert.True(host.Dispatcher.CheckAccess(), "the resumption did not land on the UI thread");
        return Answer;
    }
}
