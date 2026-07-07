using Cursorial.UI;

namespace Cursorial.Tests.UI;

// DelegateCommand — the plain delegate-backed ICommand / IPreviewableCommand building block (what BarCommand's
// delegate constructors wrap). Pins: CanPreview answers honestly from the supplied delegates (the structural gate),
// Preview is self-gated by CanExecute (the runtime gate — no NEW tentative state on a gated command), CancelPreview
// is NEVER gated (the cleanup obligation of the atomicity contract), Execute is self-gated and — deliberately — does
// NOT auto-raise CanExecuteChanged (the courtesy re-raise is the BarCommand wrapper's job).
public sealed class DelegateCommandTests
{
    [Fact] // CanPreview is the structural gate: true exactly when the preview delegate pair was supplied
    public void CanPreview_ReportsSuppliedDelegates()
    {
        Assert.False(new DelegateCommand(() => { }).CanPreview);
        Assert.False(new DelegateCommand(_ => { }, canExecute: _ => true).CanPreview);
        Assert.True(new DelegateCommand(_ => { }, null, previewExecute: _ => { }, cancelPreviewExecute: _ => { }).CanPreview);
    }

    [Fact] // Execute and Preview are self-gated by CanExecute; CancelPreview is NEVER gated (atomicity contract)
    public void Gates_ExecuteAndPreviewGated_CancelNever()
    {
        var log = new List<string>();
        var gated = false;
        var command = new DelegateCommand(
            _ => log.Add("execute"),
            _ => !gated,
            previewExecute: _ => log.Add("preview"),
            cancelPreviewExecute: _ => log.Add("cancel"));

        command.Preview(null);
        command.Execute(null);
        gated = true;
        command.Preview(null);       // refused: no NEW tentative state on a gated command
        command.Execute(null);       // refused
        command.CancelPreview(null); // ALWAYS delivered — the cleanup obligation outlives the gate

        Assert.Equal(new[] { "preview", "execute", "cancel" }, log);
    }

    [Fact] // Execute does NOT auto-raise CanExecuteChanged (plain building block; the wrapper adds the courtesy raise)
    public void Execute_DoesNotAutoRaise()
    {
        var command = new DelegateCommand(() => { });
        var raised = 0;
        command.CanExecuteChanged += (_, _) => raised++;

        command.Execute(null);
        Assert.Equal(0, raised);

        command.RaiseCanExecuteChanged(); // the manual channel still works
        Assert.Equal(1, raised);
    }
}
