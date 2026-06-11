namespace Cursorial.UI;

/// <summary>
/// The binding-death notification seam (taken at install, design doc §2.4). The store invokes
/// <see cref="OnEvicted"/> exactly once when it <em>store-initiatedly</em> evicts an entry —
/// <c>ClearValue</c> on a local-priority entry (ledger A9), frame removal for frame-hosted entries
/// (A5), displacement by a newly installed local entry (matrix PD12), and the
/// <c>ValueStore.TearDown()</c> sweep (A13). Self-initiated <c>Dispose</c> never fires it (PD12).
/// </summary>
public interface IValueEvictionListener
{
    /// <summary>
    /// Called when the store evicts <paramref name="entry"/>, <em>before</em> the resulting
    /// promotion's change notification (matrix PD2) — tear down expression machinery here so a dead
    /// binding can't echo. Calling <c>entry.Dispose()</c> from inside this callback is legal and a
    /// no-op (ledger A7).
    /// </summary>
    void OnEvicted(BindingEntryBase entry);
}
