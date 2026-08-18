using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// Base view-model for a commandlet step. The VM owns the outcome: <see cref="Accept"/> retains the inline
/// receipt and shuts the step's app down accepted; <see cref="Cancel"/> clears the region and exits with the
/// canceled code. The app is injected (created per step by the <see cref="Runner"/>), keeping VMs
/// headless-testable without thread-local lookups.
/// </summary>
public abstract class CommandletViewModel(UIApplication app) : Cursorial.UI.Data.ObservableObject
{
    protected UIApplication App { get; } = app;

    /// <summary>The exit code this step completed with, or null while still interactive — the
    /// headless-observable seam (the runner reads the app's exit code instead).</summary>
    public int? CompletedCode { get; private set; }

    /// <summary>Retain the receipt and complete the step accepted.</summary>
    protected void Accept()
    {
        CompletedCode = ExitCodes.Accepted;
        App.InlineExitBehavior = InlineExitBehavior.Retain;
        App.Shutdown(ExitCodes.Accepted);
    }

    /// <summary>Clear the region and complete the step canceled (Esc / declined).</summary>
    public void Cancel()
    {
        CompletedCode = ExitCodes.Canceled;
        App.InlineExitBehavior = InlineExitBehavior.Clear;
        App.Shutdown(ExitCodes.Canceled);
    }

    /// <summary>The step's result as a wire variable (only consulted after an accepted run).</summary>
    public abstract Variable BuildResult(string name);
}
