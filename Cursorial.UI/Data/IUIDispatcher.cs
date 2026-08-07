namespace Cursorial.UI.Data;

/// <summary>
/// The minimal dispatcher seam the binding engine consumes for cross-thread INPC marshaling
/// (design doc §6.9). S6 owns the concrete <c>UIDispatcher</c>, which implements this interface
/// <em>directly</em> (same assembly, identical member shapes) so the ambient lookup never allocates
/// an adapter; the engine needs only <see cref="CheckAccess"/> and <see cref="Post"/>. Keep any new
/// member on this seam implementable by <c>UIDispatcher</c> as-is. <b>Pinned</b>: posted work runs in the next
/// frame's dispatch drain <em>before layout</em>, and <see cref="Post"/> MUST wake the event-driven
/// frame loop when no drain is pending (the <c>Invalidate()</c> pattern) — a background VM update
/// must not wait for unrelated input.
/// </summary>
public interface IUIDispatcher
{
    /// <summary>Whether the calling thread is the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="callback"/> for the next dispatch drain (before layout); wakes an idle loop.</summary>
    void Post(Action callback);
}

/// <summary>
/// Resolves the ambient <see cref="IUIDispatcher"/> for the binding engine. By default it bridges to
/// <c>UIApplication.Current?.Dispatcher</c>; tests install a fake via <see cref="Install"/>.
/// </summary>
internal static class BindingDispatcher
{
    // A process-wide override (not thread-static): a cross-thread INPC handler running on a foreign
    // thread must still see the UI dispatcher to marshal back. Tests install a fake here; production
    // falls back to the ambient application's dispatcher.
    private static volatile IUIDispatcher? _override;

    /// <summary>
    /// The current dispatcher — the test override, else the ambient application's dispatcher.
    /// <b>Allocation-free</b>: <c>UIDispatcher</c> implements <see cref="IUIDispatcher"/> itself, so
    /// the ambient arm is a plain reference read, not a wrapper. This is read <b>once per binding
    /// push</b> (<c>BindingExpressionCore.DispatchSourceChange</c>) and discarded unused on the UI
    /// thread, so anything manufactured here is per-push garbage in every app — keep it a read.
    /// </summary>
    public static IUIDispatcher? Current => _override ?? UIApplication.Current?.Dispatcher;

    /// <summary>
    /// Installs a process-wide dispatcher override (tests / hosting); returns a scope that restores
    /// the prior value. <b>Not safe for concurrent calls</b>: <c>Install</c>/<c>Dispose</c> is a
    /// non-atomic swap of the single process-global slot, so two tests that install concurrently race
    /// (last writer wins for the life of both). The override is intentionally process-global — a
    /// cross-thread INPC handler on a pool thread must still see the UI dispatcher to marshal back —
    /// which makes it single-UIApplication scoped; run binding tests that install a dispatcher
    /// non-concurrently.
    /// </summary>
    public static IDisposable Install(IUIDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var prior = _override;
        _override = dispatcher;
        return new Scope(prior);
    }

    private sealed class Scope(IUIDispatcher? prior) : IDisposable
    {
        public void Dispose() => _override = prior;
    }
}
