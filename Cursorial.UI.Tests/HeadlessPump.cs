// xUnit1031 (no blocking task ops) is deliberately disabled: the bounded poll below IS the blocking
// op, and it is bounded precisely so it cannot become the deadlock it replaces.
#pragma warning disable xUnit1031

using System.Diagnostics;

using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// Waits for a task that may resume ON the UI thread, by PUMPING the headless host instead of
/// blocking it.
/// <para>
/// <see cref="UIHeadlessHost.Create"/> makes the calling (test) thread the UI thread, and a frame
/// installs the application's <c>SynchronizationContext</c> — so any <c>await</c> reached inside a
/// frame captures it and posts its resumption back to the dispatcher, where it runs only in a later
/// frame. Work that has meanwhile left for the thread pool is invisible to
/// <c>UIApplication.IsIdle</c> (it inspects the dispatcher-side queues only), so
/// <see cref="UIHeadlessHost.RunUntilIdle"/> can legitimately report idle in the window before the
/// pool thread posts. A test that then blocks the UI thread on the task — <c>task.Wait(…)</c> —
/// guarantees the queued resumption can never run: a hard deadlock that ends at the timeout instead
/// of at completion.
/// </para>
/// <para>
/// Pumping frames while polling closes that window: the resumption lands in a frame, the chain runs
/// on, and the wait ends when the task really completes. The deadline is preserved so a genuinely
/// hung flow still fails the test rather than hanging the run.
/// </para>
/// </summary>
internal static class HeadlessPump
{
    /// <summary>The default deadline — generous, because it is only ever reached by a real failure.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long each poll parks the UI thread before pumping another frame.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Pumps <paramref name="host"/> until <paramref name="task"/> completes, failing the test at
    /// <paramref name="timeout"/>. A task whose tail never touches the UI thread completes on the
    /// first poll, so the common path pumps no extra frames at all.
    /// </summary>
    internal static void WaitForCompletion(UIHeadlessHost host,
                                           Task task,
                                           string description = "the task",
                                           TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(task);

        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        var frames = 0;

        while (!task.Wait(PollInterval))
        {
            if (clock.Elapsed >= limit)
            {
                Assert.Fail($"{description} did not complete within {limit.TotalSeconds:0.##}s " +
                            $"({frames} frames pumped waiting for a UI-thread resumption).");
            }

            host.RunFrame();
            frames++;
        }
    }

    /// <summary>
    /// <see cref="WaitForCompletion"/>, then the result — faults and cancellation surface from the
    /// wait exactly as they did from the blocking wait this replaces.
    /// </summary>
    internal static TResult WaitForResult<TResult>(UIHeadlessHost host,
                                                   Task<TResult> task,
                                                   string description = "the task",
                                                   TimeSpan? timeout = null)
    {
        WaitForCompletion(host, task, description, timeout);
        return task.Result;
    }
}
