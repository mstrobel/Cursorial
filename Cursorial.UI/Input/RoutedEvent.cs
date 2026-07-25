namespace Cursorial.UI.Input;

/// <summary>
/// A routed-event identity (design doc §7.2): name, routing strategy, owner type, args type, and a
/// dense <see cref="GlobalIndex"/> into per-element handler stores. Instances are created via
/// <see cref="RoutedEvent{TArgs}.Register"/> and compared by reference.
/// </summary>
public abstract class RoutedEvent
{
    private static int _nextGlobalIndex = -1;

    private protected RoutedEvent(string name, RoutingStrategy strategy, Type ownerType, Type argsType)
    {
        Name = name;
        Strategy = strategy;
        OwnerType = ownerType;
        ArgsType = argsType;
        GlobalIndex = Interlocked.Increment(ref _nextGlobalIndex);
    }

    /// <summary>The event name (diagnostics; not an identity — identity is the instance).</summary>
    public string Name { get; }

    /// <summary>The routing strategy.</summary>
    public RoutingStrategy Strategy { get; }

    /// <summary>The type that registered the event.</summary>
    public Type OwnerType { get; }

    /// <summary>The concrete <see cref="RoutedEventArgs"/> type this event dispatches.</summary>
    public Type ArgsType { get; }

    /// <summary>The dense process-wide registration index (per-element handler stores key on it).</summary>
    public int GlobalIndex { get; }

    /// <summary>
    /// Whether the route walker runs the per-node <c>InputBindings</c> sweep after instance
    /// handlers while unhandled (doc §7.5 step 4) — set only for <c>UIElement.KeyDownEvent</c>
    /// (KeyUp never drives framework activation, ND9).
    /// </summary>
    internal bool SweepsInputBindings;

    /// <summary>Whether this event's route is confined to the target's SURFACE (the pointer family —
    /// input-routing review Q1): the route walks <c>VisualParent</c> only and never crosses a popup seam.
    /// A pointer event is a statement about a screen cell the window manager already arbitrated to ONE
    /// surface; bridging afterward contradicts that arbitration. Tunnel/bubble pair members must agree
    /// (asserted in <c>EventRouting.RaisePair</c> — one shared route serves both legs).</summary>
    internal bool SurfaceScoped;

    /// <summary>
    /// Whether the per-node class stage (the <c>On*</c> virtual) runs even after a nearer node set
    /// <see cref="RoutedEventArgs.Handled"/> — set only for <c>UIElement.QueryCursorEvent</c>, whose
    /// <c>OnQueryCursor</c> must still run on ancestors so <c>ForceCursor</c> can override an already-claimed
    /// cursor (design doc §7.6, WPF parity). Instance handlers keep the normal handled-gating.
    /// </summary>
    internal bool ClassStageHandledEventsToo;

    /// <summary>
    /// Invokes the class-handler stage — the <c>On*</c> virtual — for this event at
    /// <paramref name="element"/> (ND1: the virtual runs at every route node before that node's
    /// instance handlers and is skipped once <see cref="RoutedEventArgs.Handled"/>). Null for
    /// events registered outside the framework vocabulary.
    /// </summary>
    internal abstract void InvokeClassStage(UIElement element, RoutedEventArgs args);

    /// <summary>Invokes one stored instance handler (typed dispatch through the generic subclass).</summary>
    internal abstract void InvokeHandler(Delegate handler, UIElement sender, RoutedEventArgs args);

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>The typed routed event; <typeparamref name="TArgs"/> is the args type handlers receive.</summary>
/// <typeparam name="TArgs">The concrete args type dispatched by this event.</typeparam>
public sealed class RoutedEvent<TArgs> : RoutedEvent
    where TArgs : RoutedEventArgs
{
    /// <summary>The class-handler stage (the owning element's <c>On*</c> virtual); framework-internal wiring.</summary>
    internal Action<UIElement, TArgs>? ClassStage;

    private RoutedEvent(string name, RoutingStrategy strategy, Type ownerType)
        : base(name, strategy, ownerType, typeof(TArgs))
    {
    }

    /// <summary>
    /// Registers a new routed event. Registration is identity-producing: two calls return two
    /// distinct events even with identical arguments.
    /// </summary>
    /// <param name="name">The event name (diagnostics).</param>
    /// <param name="strategy">The routing strategy.</param>
    /// <param name="ownerType">The registering type.</param>
    public static RoutedEvent<TArgs> Register(string name, RoutingStrategy strategy, Type ownerType)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(ownerType);
        return new RoutedEvent<TArgs>(name, strategy, ownerType);
    }

    internal override void InvokeClassStage(UIElement element, RoutedEventArgs args)
        => ClassStage?.Invoke(element, (TArgs)args);

    internal override void InvokeHandler(Delegate handler, UIElement sender, RoutedEventArgs args)
        => ((EventHandler<TArgs>)handler)(sender, (TArgs)args);
}
