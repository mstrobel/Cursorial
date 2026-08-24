using System.Diagnostics.CodeAnalysis;
using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Input;

namespace Cursorial.UI.Interactivity;

/// <summary>
/// The core event trigger (design doc §4): hooks a ROUTED event on its host (or a retargeted
/// <see cref="SourceObject"/>) and fires its actions when the event raises, passing the
/// <see cref="RoutedEventArgs"/> as the firing parameter (so an action can read the event's source,
/// position, key, …). The event is named by <see cref="EventName"/>, resolved against the framework's
/// <c>{Name}Event</c> static-field convention on the source's type (the S3 registration idiom —
/// <c>ButtonBase.ClickEvent</c>, <c>UIElement.KeyDownEvent</c>) — or supplied directly via
/// <see cref="RoutedEvent"/> (the reflection-free path a generator can bake). An unresolvable event
/// throws at attach — never a silent no-op trigger.
/// </summary>
public class EventTrigger : TriggerBase<UIElement>
{
    private static readonly MethodInfo SubscribeMethod =
        typeof(EventTrigger).GetMethod(nameof(SubscribeCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    private Action? _unsubscribe;

    /// <summary>Creates an unconfigured trigger (set <see cref="EventName"/> or <see cref="RoutedEvent"/>).</summary>
    public EventTrigger()
    {
    }

    /// <summary>Creates a trigger for the named event.</summary>
    public EventTrigger(string eventName) => EventName = eventName;

    /// <summary>Creates a trigger for a known routed event (the reflection-free path).</summary>
    public EventTrigger(RoutedEvent routedEvent) => RoutedEvent = routedEvent;

    /// <summary>The event name, resolved via the <c>{Name}Event</c> static-field convention at attach.</summary>
    public string? EventName { get; set; }

    /// <summary>The routed event to hook (wins over <see cref="EventName"/> when both are set).</summary>
    public RoutedEvent? RoutedEvent { get; set; }

    /// <summary>Whether the handler runs even for already-handled raises (default false).</summary>
    public bool HandledEventsToo { get; set; }

    /// <summary>An alternate event source (must be a <see cref="UIElement"/>); default: the host.</summary>
    public object? SourceObject { get; set; }

    /// <inheritdoc/>
    protected override void OnAttached()
    {
        var source = ResolveSource();
        var routedEvent = ResolveEvent(source);
        _unsubscribe = (Action)SubscribeMethod
            .MakeGenericMethod(routedEvent.ArgsType) // TArgs : RoutedEventArgs — reference types, AOT-shared generics
            .Invoke(null, [this, source, routedEvent, HandledEventsToo])!;
    }

    /// <inheritdoc/>
    protected override void OnDetaching()
    {
        _unsubscribe?.Invoke();
        _unsubscribe = null;
    }

    private UIElement ResolveSource()
    {
        if (SourceObject is null)
            return AssociatedObject!;

        return SourceObject as UIElement
               ?? throw new InvalidOperationException(
                   $"EventTrigger.SourceObject must be a UIElement; got {SourceObject.GetType().Name}.");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The {Name}Event static-field convention is the runtime resolution path; a generator bakes RoutedEvent directly.")]
    private RoutedEvent ResolveEvent(UIElement source)
    {
        if (RoutedEvent is { } direct)
            return direct;

        if (string.IsNullOrEmpty(EventName))
            throw new InvalidOperationException("EventTrigger requires an EventName (or a RoutedEvent).");

        // The S3 registration idiom: a public static readonly RoutedEvent<TArgs> {Name}Event field on the
        // owner (searched over the SOURCE's type hierarchy, so a derived host finds inherited events).
        var field = source.GetType().GetField(
            EventName + "Event", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        if (field?.GetValue(null) is RoutedEvent resolved)
            return resolved;

        throw new InvalidOperationException(
            $"EventTrigger could not resolve the event '{EventName}' on {source.GetType().Name} — no public " +
            $"static '{EventName}Event' RoutedEvent field on its type hierarchy.");
    }

    private static Action SubscribeCore<TArgs>(EventTrigger self, UIElement source, RoutedEvent routedEvent, bool handledEventsToo)
        where TArgs : RoutedEventArgs
    {
        var typed = (RoutedEvent<TArgs>)routedEvent;
        EventHandler<TArgs> handler = (_, args) => self.Fire(args);
        source.AddHandler(typed, handler, handledEventsToo);
        return () => source.RemoveHandler(typed, handler);
    }
}
