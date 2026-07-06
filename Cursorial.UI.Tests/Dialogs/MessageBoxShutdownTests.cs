using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Dialogs;

/// <summary>
/// Wave1-3 regression (migrated from the editor's placeholder-port suite): the no-throw cancellation
/// contract must hold on the off-UI-thread path even when the dispatcher has shut down —
/// <c>UIDispatcher.InvokeAsync</c> then returns a canceled task without ever running the marshaled
/// delegate, and the <see cref="OperationCanceledException"/> must map to the dismissed result instead
/// of escaping <c>ShowAsync</c>. Wave 2 narrowed the companion WindowManager pre-check to the marshaled
/// path only: an on-UI-thread show against a built-but-not-run application is programmer error and keeps
/// failing loudly.
/// </summary>
public sealed class MessageBoxShutdownTests
{
    /// <summary>
    /// Runs <paramref name="show"/> on a dedicated (non-pool) thread. <c>Task.Run</c> is NOT a
    /// reliable off-UI-thread vehicle here: the pool may schedule the delegate on the very
    /// thread that created the host, where <c>Dispatcher.CheckAccess()</c> is still TRUE — and
    /// since wave 2 the on-UI-thread branch is deliberately unguarded (programmer error throws).
    /// A fresh thread always has a managed id distinct from the live host thread's, so
    /// <c>CheckAccess()</c> is deterministically false and the show takes the marshaled path
    /// these tests exist to pin.
    /// </summary>
    private static Task<TResult> ShowOffUIThread<TResult>(Func<Task<TResult>> show)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(show().GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        return completion.Task;
    }

    [Fact]
    public async Task ShowAsync_AfterDispatcherShutdown_ReturnsNoneInsteadOfThrowing()
    {
        var host = UITestHost.Create();
        host.ShowRoot(new TextBlock { Text = "stub" });
        Assert.True(host.RunUntilIdle());

        var application = host.Application;
        host.Dispose(); // canonical teardown — the dispatcher is shut down from here on

        // Off-thread: CheckAccess is false on the dedicated thread, forcing the marshaled path.
        var result = await ShowOffUIThread(() => MessageBox.ShowAsync(application, "unsaved changes"));

        Assert.Equal(MessageBoxResult.None, result);
    }

    [Fact]
    public async Task TaskDialogService_AfterDispatcherShutdown_ReportsDismissed()
    {
        var host = UITestHost.Create();
        host.ShowRoot(new TextBlock { Text = "stub" });
        Assert.True(host.RunUntilIdle());

        var application = host.Application;
        host.Dispose();

        var service = new MessageBoxTaskDialogService(application);
        var result = await ShowOffUIThread(() => service.ShowAsync(new TaskDialogRequest("Save changes?")
        {
            Buttons = TaskDialogButton.SaveTriad,
        }));

        Assert.True(result.IsDismissed);
    }

    [Fact]
    public async Task ShowAsync_OnUIThread_BeforeRunAsync_ThrowsInvalidOperationException()
    {
        // Build-without-run: Build binds the dispatcher to this thread and sets
        // UIApplication.Current, but the WindowManager is only composed during RunAsync startup.
        // An on-UI-thread show this early is programmer error — the wave-2 pre-check is scoped to
        // the MARSHALED path (the teardown race), so this must keep surfacing ShowDialogAsync's
        // "No window manager is available" InvalidOperationException instead of silently
        // reporting a dismissal.
        var host = new SyntheticTerminalHost(TestCapabilities.KittyTruecolor, new Size(80, 24));
        var application = UIApplication.CreateBuilder()
                                       .WithTerminalHost(host, disposeWithApp: true)
                                       .Build();

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => MessageBox.ShowAsync(application, "too early"));
        }
        finally
        {
            await application.DisposeAsync();
        }
    }
}
