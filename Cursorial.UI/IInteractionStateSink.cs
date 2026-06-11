namespace Cursorial.UI;

/// <summary>
/// The framework write surface for <see cref="InteractionState"/> (design doc §3.2), implemented by
/// <see cref="UIElement"/>. Callers are framework services (S3's dispatcher and focus manager, S4's
/// window manager, S1's effective-enabled plumbing) and control authors — never styles.
/// </summary>
/// <remarks>
/// Flips are equality-gated; each committed flip reaches the application's installed
/// <see cref="IInteractionStateObserver"/> (one notification per element per batch — matrix ND11).
/// <see cref="InteractionState.Pressed"/> flips additionally fan into the dispatcher's
/// pressed-holder set so terminal focus-out can clear <c>Pressed</c> window-wide (styling
/// contract C8) — which is why <c>:pressed</c> must flow through this sink and never around it.
/// </remarks>
public interface IInteractionStateSink
{
    /// <summary>Sets or clears <paramref name="state"/> (equality-gated; commits before any notification).</summary>
    /// <param name="state">The flag(s) to flip.</param>
    /// <param name="active">Whether to set (<see langword="true"/>) or clear the flag(s).</param>
    void SetInteractionState(InteractionState state, bool active);

    /// <summary>
    /// Opens a coalescing batch: flips inside the scope produce <b>one</b> observer notification per
    /// element at disposal (net-zero flips produce none; nested scopes flush at the outermost
    /// dispose). The batch is application-wide — the returned scope is shared state, not
    /// element-local.
    /// </summary>
    InteractionUpdateScope BeginInteractionUpdate();
}
