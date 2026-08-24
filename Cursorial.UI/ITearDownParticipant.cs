namespace Cursorial.UI;

/// <summary>
/// A participant in an element's <see cref="UIElement.TearDown"/> sweep — the generalization of the
/// <c>InputBindings</c> special case: non-child <see cref="UIObject"/>s anchored on an element (attached
/// behaviors/triggers/actions, input bindings) are reachable by neither the visual/logical child sweep nor
/// the element's own binding registry, so anything holding external subscriptions on their behalf must
/// release them when the element's life ends. Register via
/// <see cref="UIElement.RegisterTearDownParticipant"/> (unregister when the association ends); the
/// callback runs once, on the element's UI thread, during <see cref="UIElement.TearDown"/> — after the
/// child sweep and the element's own binding teardown.
/// </summary>
public interface ITearDownParticipant
{
    /// <summary>Releases everything held on behalf of <paramref name="host"/> (idempotent).</summary>
    void OnTearDown(UIElement host);
}
