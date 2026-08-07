using Cursorial.UI.Hosting.Headless;

namespace Cursorial.Tests.UI;

/// <summary>
/// The dialog suites' assertion around <see cref="UIHeadlessHost.RunUntilCompleted"/> — the pumping
/// wait for a task that may resume ON the UI thread. The wait itself is the host's now (see that
/// method for why blocking this thread on the task instead is a deadlock, and why the bound is
/// time rather than frames); all that is left here is turning its <see langword="false"/> — the
/// deadline hit — into a test failure that says what was waited for and how far it got.
/// </summary>
internal static class HeadlessPump
{
    /// <summary>
    /// Pumps <paramref name="host"/> until <paramref name="task"/> completes, failing the test at
    /// <paramref name="timeout"/> (default <see cref="UIHeadlessHost.DefaultCompletionTimeout"/>).
    /// </summary>
    internal static void WaitForCompletion(UIHeadlessHost host,
                                           Task task,
                                           string description = "the task",
                                           TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);

        var limit = timeout ?? UIHeadlessHost.DefaultCompletionTimeout;
        var startFrame = host.Application.CurrentFrameTime.FrameNumber;

        if (!host.RunUntilCompleted(task, limit))
        {
            var frames = host.Application.CurrentFrameTime.FrameNumber - startFrame;
            Assert.Fail($"{description} did not complete within {limit.TotalSeconds:0.##}s " +
                        $"({frames} frames pumped waiting for a UI-thread resumption).");
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

        // No blocking wait remains to suppress (the old file-level xUnit1031 disable is gone with it):
        // the wait above either completed the task or failed the test, so this only unwraps.
        return task.Result;
    }
}
