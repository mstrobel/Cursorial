namespace Cursorial.UI.Data;

/// <summary>
/// A watch-only binding arming (design doc §6.2/§6.8; <c>BindingOperations.Watch</c>) — the styling
/// engine's <c>When</c>/<c>DataCondition</c> seam. Delivers values to a callback with no store
/// entry. <b>No <c>Pause</c>/<c>Resume</c></b> (ledger B16, doc over spec §3.9): watchers stay live
/// across styling <em>deactivation</em> edges — they are the re-activation predicate — and are
/// disposed at disarm/element-detach. The teardown sweep is the backstop.
/// </summary>
public interface IBindingWatch : IDisposable
{
    /// <summary>The last-delivered value; <see cref="UIProperty.UnsetValue"/> while unresolved.</summary>
    object? Value { get; }
}
