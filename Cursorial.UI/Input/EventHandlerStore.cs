namespace Cursorial.UI.Input;

/// <summary>
/// The per-element instance-handler store (design doc §7.2): a flat registration list keyed by the
/// event identity. Lazily allocated by the first <see cref="UIElement.AddHandler{TArgs}"/> — most
/// elements never pay for one. Registration is a list, not a set: adding the same delegate twice
/// invokes it twice; one remove removes one registration.
/// </summary>
/// <remarks>
/// Storage is copy-on-write (mutation allocates a fresh array; registration is setup-time, raising
/// is the hot path), so an in-flight <see cref="Invoke"/> iterates the registration list as of the
/// moment the node's sweep began (ND27): a handler removing itself — the one-shot pattern — never
/// skips the next handler, and a handler added during a raise is not invoked by that raise.
/// </remarks>
internal sealed class EventHandlerStore
{
    private Entry[] _entries = [];

    private readonly record struct Entry(RoutedEvent Event, Delegate Handler, bool HandledEventsToo);

    /// <summary>The live registration count (functional-empty observability for tests).</summary>
    internal int Count => _entries.Length;

    /// <summary>Appends a registration (copy-on-write — in-flight raises keep the prior snapshot).</summary>
    internal void Add(RoutedEvent routedEvent, Delegate handler, bool handledEventsToo)
    {
        var entries = _entries;
        var next = new Entry[entries.Length + 1];
        Array.Copy(entries, next, entries.Length);
        next[entries.Length] = new Entry(routedEvent, handler, handledEventsToo);
        _entries = next;
    }

    /// <summary>Removes the first registration matching the event + delegate (no-op when absent; copy-on-write).</summary>
    internal void Remove(RoutedEvent routedEvent, Delegate handler)
    {
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (!ReferenceEquals(entry.Event, routedEvent) || !entry.Handler.Equals(handler))
                continue;

            var next = new Entry[entries.Length - 1];
            Array.Copy(entries, next, i);
            Array.Copy(entries, i + 1, next, i, entries.Length - i - 1);
            _entries = next;
            return;
        }
    }

    /// <summary>
    /// Invokes this element's handlers for <paramref name="routedEvent"/> in registration order:
    /// normal handlers are skipped while <see cref="RoutedEventArgs.Handled"/>;
    /// <c>handledEventsToo</c> handlers always run (and may un-handle — the check is live per
    /// entry, ND2). The handler set is the registration list as of this call's start (ND27):
    /// handler-driven Add/Remove affects later nodes and later raises, never this sweep.
    /// </summary>
    internal void Invoke(UIElement sender, RoutedEvent routedEvent, RoutedEventArgs args)
    {
        var entries = _entries; // the ND27 snapshot — mutation swaps the array, never edits it

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (!ReferenceEquals(entry.Event, routedEvent))
                continue;
            if (args.Handled && !entry.HandledEventsToo)
                continue;

            routedEvent.InvokeHandler(entry.Handler, sender, args);
        }
    }
}
