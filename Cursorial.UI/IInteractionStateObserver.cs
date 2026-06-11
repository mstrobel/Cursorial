namespace Cursorial.UI;

/// <summary>
/// The per-application interaction-state change observer (matrix ND11) — the seam Fork B's styling
/// engine consumes at P3 (pseudo-class activation rides these notifications); tests install
/// recording sinks. Installed via <see cref="UIApplication.InteractionStateObserver"/>.
/// </summary>
/// <remarks>
/// Delivery contract: one notification per element per batch, <b>after</b> the state commit
/// (reading the element's current state inside the callback observes the new mask); immediate for
/// unbatched flips; nothing for net-zero flips inside a batch. UI thread only. Implementations must
/// be cheap and allocation-free — hover rides this path at any-event mouse-motion rates.
/// </remarks>
public interface IInteractionStateObserver
{
    /// <summary>Called once per element whose committed <see cref="InteractionState"/> changed.</summary>
    /// <param name="element">The element whose state changed.</param>
    /// <param name="oldState">The mask before the flip (for batches: at first flip inside the batch).</param>
    /// <param name="newState">The committed mask.</param>
    void OnInteractionStateChanged(UIElement element, InteractionState oldState, InteractionState newState);
}
