using System.Diagnostics;

namespace Cursorial.UI.Input;

/// <summary>
/// The base routed-event args (design doc §7.2): the in-flight <see cref="RoutedEvent"/>, the
/// dispatch target (<see cref="Source"/> / <see cref="OriginalSource"/> — always equal in v1,
/// ND22), and the route-scoped <see cref="Handled"/> flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership (pooling contract).</b> Framework dispatch rents instances from a per-concrete-type
/// free-list; rented args are valid only for the duration of their dispatch and are debug-stamped —
/// touching a captured pooled args after the dispatch completes throws in DEBUG builds. Handlers
/// that need to retain data copy it out (the wrapped device records — <c>KeyEvent</c>,
/// <c>MouseEvent</c> — are immutable and always retainable). Caller-constructed args
/// (<see langword="new"/>) are caller-owned: never pooled, never stamped, valid after
/// <see cref="UIElement.RaiseEvent"/> returns.
/// </para>
/// <para>
/// A rented args (<see cref="UIElement.RentEvent{TArgs}"/>) must be passed to exactly one
/// <see cref="UIElement.RaiseEvent"/> call, which returns it to the pool on completion; raising the
/// same rented instance twice throws in DEBUG builds.
/// </para>
/// </remarks>
public class RoutedEventArgs
{
    private RoutedEvent? _routedEvent;
    private UIElement? _originalSource;
    private UIElement? _source;
    private bool _handled;
    private Action<RoutedEventArgs>? _poolReturn; // non-null while rented from a free-list
    private bool _returnOnRaiseCompletion;        // RentEvent rentals: RaiseEvent owns the return
#if DEBUG
    private bool _stale;                          // returned to the pool — touching throws (N10)
#endif

    /// <summary>Creates an empty caller-owned args (also the pooled-instance construction path).</summary>
    public RoutedEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to pass to <see cref="UIElement.RaiseEvent"/>.</summary>
    /// <param name="routedEvent">The event to raise.</param>
    /// <param name="source">The dispatch target (becomes <see cref="Source"/> and <see cref="OriginalSource"/>).</param>
    public RoutedEventArgs(RoutedEvent routedEvent, UIElement source)
    {
        ArgumentNullException.ThrowIfNull(routedEvent);
        ArgumentNullException.ThrowIfNull(source);
        _routedEvent = routedEvent;
        _originalSource = source;
        _source = source;
    }

    /// <summary>
    /// The event currently being raised. During a <c>Preview*</c>/main pair the same args instance
    /// carries the tunnel event, then the bubble event.
    /// </summary>
    public RoutedEvent? RoutedEvent
    {
        get
        {
            ThrowIfStale();
            return _routedEvent;
        }
    }

    /// <summary>The original dispatch target. v1: always equals <see cref="Source"/> (ND22 — template source adjustment deferred).</summary>
    public UIElement? OriginalSource
    {
        get
        {
            ThrowIfStale();
            return _originalSource;
        }
    }

    /// <summary>The dispatch target as seen by handlers. v1: always equals <see cref="OriginalSource"/>.</summary>
    public UIElement? Source
    {
        get
        {
            ThrowIfStale();
            return _source;
        }
    }

    /// <summary>
    /// Whether the event has been claimed. Mutable and pair-scoped (ND2): handling the tunnel
    /// suppresses the rest of the tunnel <b>and</b> the whole main phase down to
    /// <c>handledEventsToo</c> subscribers; a <c>handledEventsToo</c> handler may set this back to
    /// <see langword="false"/>, resuming normal invocation downstream.
    /// </summary>
    public bool Handled
    {
        get
        {
            ThrowIfStale();
            return _handled;
        }
        set
        {
            ThrowIfStale();
            _handled = value;
        }
    }

    // ───────────────────────────── pooling plumbing (framework-internal) ─────────────────────────────

    /// <summary>Called by the pool on rent: re-arms the instance and clears all per-dispatch state.</summary>
    internal void OnRented(Action<RoutedEventArgs> poolReturn)
    {
        _poolReturn = poolReturn;
        _returnOnRaiseCompletion = false;
        _routedEvent = null;
        _originalSource = null;
        _source = null;
        _handled = false;
#if DEBUG
        _stale = false;
#endif
    }

    /// <summary>
    /// Returns a rented instance to its free-list (idempotent; no-op for caller-owned args).
    /// Element/device references are cleared so a pooled instance never pins a detached subtree.
    /// </summary>
    internal void ReturnToPool()
    {
        if (_poolReturn is not { } poolReturn)
            return;

        _poolReturn = null;
        _returnOnRaiseCompletion = false;
        _routedEvent = null;
        _originalSource = null;
        _source = null;
        _handled = false;
        ResetPooledState();
#if DEBUG
        _stale = true;
#endif
        poolReturn(this);
    }

    /// <summary>Clears subclass payload (device records, text) when the instance returns to the pool.</summary>
    internal virtual void ResetPooledState()
    {
    }

    /// <summary>Whether <see cref="UIElement.RaiseEvent"/> owns the pool return (the <c>RentEvent</c> contract).</summary>
    internal bool ReturnOnRaiseCompletion => _returnOnRaiseCompletion;

    /// <summary>Stamps a <c>RentEvent</c> rental: event identity now, source at raise, pool return at raise completion.</summary>
    internal void PrepareRental(RoutedEvent routedEvent)
    {
        _routedEvent = routedEvent;
        _returnOnRaiseCompletion = true;
    }

    /// <summary>Framework-dispatch initialization: event + target (Source == OriginalSource, ND22).</summary>
    internal void Initialize(RoutedEvent routedEvent, UIElement source)
    {
        _routedEvent = routedEvent;
        _originalSource = source;
        _source = source;
        _handled = false;
    }

    /// <summary>Sets the sources when unset (the public <see cref="UIElement.RaiseEvent"/> path; sender = the raising element).</summary>
    internal void InitializeSourceIfUnset(UIElement source)
    {
        _originalSource ??= source;
        _source ??= source;
    }

    /// <summary>Switches the in-flight event between the tunnel and bubble legs of a paired raise.</summary>
    internal void SetRoutedEvent(RoutedEvent routedEvent) => _routedEvent = routedEvent;

    /// <summary>The unguarded event identity (route-engine internal; bypasses the DEBUG stale stamp).</summary>
    internal RoutedEvent? RoutedEventUnchecked => _routedEvent;

    /// <summary>
    /// DEBUG stale-stamp check (doc §7.2): a pooled args captured past its dispatch throws here.
    /// Compiled out of release builds — copy values during the dispatch, never retain the args.
    /// </summary>
    [Conditional("DEBUG")]
    private protected void ThrowIfStale()
    {
#if DEBUG
        if (_stale)
        {
            throw new InvalidOperationException(
                "This pooled RoutedEventArgs has been returned to the free-list — it was valid only for the " +
                "duration of its dispatch. Copy values out during the dispatch (the wrapped device record is " +
                "immutable and always retainable); never retain the args instance itself.");
        }
#endif
    }
}
