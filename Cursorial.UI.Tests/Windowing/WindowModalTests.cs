// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using System.Threading;
using System.Threading.Tasks;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Testing;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Windowing;

/// <summary>
/// P7-W2 — modality + <see cref="Window.ShowDialogAsync(CancellationToken)"/>: a modal blocks every other
/// window (the <c>obscured</c> class + the input-enabled gate), the dialog task completes with the
/// <c>DialogResult</c> on close, activation hands off to the owner, and cancellation marshals a forced close
/// that cancels the task. (The input-driven <c>:modal-attention</c> pulse + capture release ride W3.)
/// </summary>
public sealed class WindowModalTests
{
    private static (UITestHost Host, WindowManager Wm) ShownRoot()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 20) });
        host.ShowRoot(new UIControls.StackPanel());
        Assert.True(host.RunUntilIdle());
        return (host, host.Application.WindowManager!);
    }

    [Fact]
    public void ShowDialog_BlocksOtherWindows_AndMarksThemObscured()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var owner = new Window { Width = 30, Height = 10 };
        owner.Show(wm);
        Assert.True(host.RunUntilIdle());
        Assert.True(owner.IsActive);

        var dialog = new Window { Owner = owner, Width = 20, Height = 6 };
        _ = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());

        Assert.True(dialog.IsModal);
        Assert.Same(dialog, wm.TopmostModal);
        Assert.True(dialog.IsActive);
        Assert.False(owner.IsActive);

        Assert.False(wm.IsInputEnabled(owner));      // the owner is blocked behind its modal
        Assert.True(owner.Classes.Contains("obscured"));
        Assert.True(wm.IsInputEnabled(dialog));      // the gate stays enabled
        Assert.False(dialog.Classes.Contains("obscured"));
    }

    [Fact]
    public void CloseDialog_CompletesTaskWithResult_UnblocksOwner_AndHandsBackActivation()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        var owner = new Window { Width = 30, Height = 10 };
        owner.Show(wm);
        Assert.True(host.RunUntilIdle());

        var dialog = new Window { Owner = owner, Width = 20, Height = 6 };
        var task = dialog.ShowDialogAsync();
        Assert.True(host.RunUntilIdle());
        Assert.False(task.IsCompleted);

        dialog.Close("ok");                          // sets DialogResult + closes
        Assert.True(host.RunUntilIdle());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal("ok", task.Result);
        Assert.False(dialog.IsShown);
        Assert.Null(wm.TopmostModal);
        Assert.True(wm.IsInputEnabled(owner));       // unblocked
        Assert.False(owner.Classes.Contains("obscured"));
        Assert.True(owner.IsActive);                 // handoff: owner re-activated (§8.6)
    }

    [Fact]
    public void ShowDialog_PreCanceledToken_ReturnsCanceledTask_WithoutShowing()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var dialog = new Window { Width = 20, Height = 6 };
        var task = dialog.ShowDialogAsync(cts.Token);

        Assert.True(task.IsCanceled);
        Assert.False(dialog.IsShown);
        Assert.Empty(wm.Windows);
    }

    [Fact]
    public void ShowDialog_Cancellation_MarshalsForcedClose_AndCancelsTask()
    {
        var (host, wm) = ShownRoot();
        using var hostScope = host;

        using var cts = new CancellationTokenSource();
        var dialog = new Window { Width = 20, Height = 6 };
        var task = dialog.ShowDialogAsync(cts.Token);
        Assert.True(host.RunUntilIdle());
        Assert.True(dialog.IsShown);

        cts.Cancel();                                 // posts the forced close to the UI dispatcher
        Assert.True(host.RunUntilIdle());

        Assert.True(task.IsCanceled);
        Assert.False(dialog.IsShown);
        Assert.Empty(wm.Windows);
    }
}
