namespace Cursorial.UI.Data;

/// <summary>
/// The minimal dispatcher seam the binding engine consumes for cross-thread INPC marshaling
/// (design doc §6.9). S6 owns the concrete <c>UIDispatcher</c>; the engine needs only
/// <see cref="CheckAccess"/> and <see cref="Post"/>. <b>Pinned</b>: posted work runs in the next
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

    /// <summary>The current dispatcher — the test override, else the ambient application's dispatcher.</summary>
    public static IUIDispatcher? Current => _override ?? AmbientDispatcher;

    private static IUIDispatcher? AmbientDispatcher
        => UIApplication.Current?.Dispatcher is { } dispatcher ? new UIDispatcherAdapter(dispatcher) : null;

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

    private sealed class UIDispatcherAdapter(UIDispatcher dispatcher) : IUIDispatcher
    {
        public bool CheckAccess() => dispatcher.CheckAccess();

        public void Post(Action callback) => dispatcher.Post(callback);
    }
}
