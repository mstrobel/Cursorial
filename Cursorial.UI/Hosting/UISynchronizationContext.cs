// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The dispatcher-backed <see cref="SynchronizationContext"/> (design doc §10.3): installed on the
/// UI thread at loop start (and around each <c>UITestHost</c> frame) so <c>await</c> continuations
/// in app code land in the dispatcher queue and run in a later frame's Phase 2 — frame-coherent by
/// construction. Uninstalled as the first statement of teardown so teardown awaits cannot deadlock
/// on a dispatcher nobody drains again.
/// </summary>
internal sealed class UISynchronizationContext(UIDispatcher dispatcher) : SynchronizationContext
{
    /// <inheritdoc/>
    public override void Post(SendOrPostCallback d, object? state)
        => dispatcher.Post((d, state), static s => s.d(s.state)); // order-preserving, never blocks the caller

    /// <inheritdoc/>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (dispatcher.CheckAccess())
        {
            d(state); // inline on the UI thread
            return;
        }

        // Blocking invoke from a foreign thread — the documented deadlock hazard: if the UI thread
        // is itself blocked on this caller, neither makes progress. Send is discouraged; Post is
        // the supported shape.
        dispatcher.InvokeAsync(() => d(state)).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override SynchronizationContext CreateCopy() => this;
}
