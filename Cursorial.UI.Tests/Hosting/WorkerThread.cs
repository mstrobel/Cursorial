namespace Cursorial.Tests.UI.Hosting;

/// <summary>
/// Runs a delegate on a guaranteed-distinct OS thread. <c>Task.Run(...).GetResult()</c> is NOT
/// safe for cross-thread affinity tests: xUnit executes tests on thread-pool threads, and a
/// blocking wait can <b>inline</b> the queued task onto the waiting (same) thread, silently
/// passing a "different thread" check on the same thread id.
/// </summary>
internal static class WorkerThread
{
    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException("Worker-thread delegate failed.", failure);

        return result;
    }

    public static void Run(Action action)
        => Run<object?>(() =>
        {
            action();
            return null;
        });
}
