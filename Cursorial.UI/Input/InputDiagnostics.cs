using System.Diagnostics;

namespace Cursorial.UI.Input;

/// <summary>
/// DEBUG-only diagnostics channel for input-pipeline contract violations (e.g. a synthesized
/// <c>MouseEventKind.Click</c> reaching the dispatcher — ND4). Emission compiles out of release
/// builds; the violations are defensively no-op'd either way.
/// </summary>
internal static class InputDiagnostics
{
    /// <summary>Raised (DEBUG builds only) when a pipeline-contract violation is observed.</summary>
    internal static event Action<string>? ContractViolation;

    /// <summary>Reports the ND4 violation: a synthesized Click implies S6 enabled <c>SynthesizeClickEvents</c>.</summary>
    [Conditional("DEBUG")]
    internal static void EmitClickContractViolation()
        => ContractViolation?.Invoke(
            "A MouseEventKind.Click event reached InputDispatcher.ProcessEvent. The mandated S6 pipeline " +
            "(WithClickSynthesis with SynthesizeClickEvents = false) never produces Click events; the event " +
            "was ignored without routing. Fix the pipeline assembly rather than handling Click.");

    /// <summary>
    /// Reports a pathological focus loop: re-entrant <c>SetFocus</c> chains (GotFocus/LostFocus
    /// handlers refocusing each other) exceeded the depth cap of 8 (doc §7.7); the deepest request
    /// was refused and focus stays where the last committed transition put it.
    /// </summary>
    [Conditional("DEBUG")]
    internal static void EmitFocusTransitionDepthExceeded()
        => ContractViolation?.Invoke(
            "Re-entrant FocusManager.SetFocus exceeded the depth cap of 8 — GotFocus/LostFocus handlers are " +
            "refocusing each other in a loop. The deepest request was refused (last committed transition wins). " +
            "Break the cycle in the handlers rather than relying on the cap.");
}
