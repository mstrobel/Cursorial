using Cursorial.CLI.Wire;
using Cursorial.UI;

namespace Cursorial.CLI.Commandlets;

/// <summary>
/// Base view-model for a commandlet step. The VM owns the outcome CODES: <see cref="Accept"/> completes
/// the step accepted, <see cref="Cancel"/> completes it canceled. Whether an accepted step's inline
/// receipt stays on screen is the RUNNER's call — the <c>--retain</c> policy is baked into each step's
/// app at build time; only Cancel overrides it (a canceled step always clears). The app is injected
/// (created per step by the <see cref="Runner"/>), keeping VMs headless-testable without thread-local
/// lookups.
/// </summary>
public abstract class CommandletViewModel(UIApplication app) : UI.Data.ObservableObject
{
    protected UIApplication App { get; } = app;

    /// <summary>The exit code this step completed with, or null while still interactive — the
    /// headless-observable seam (the runner reads the app's exit code instead).</summary>
    public int? CompletedCode { get; private set; }

    /// <summary>Complete the step accepted; the app's baked-in <c>--retain</c> policy decides the receipt.</summary>
    protected void Accept()
    {
        CompletedCode = ExitCodes.Accepted;
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
