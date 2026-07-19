namespace Cursorial.UI.Input;

/// <summary>
/// The pooled route scratch + walker (design doc §7.5). A route is the visual-parent walk from the
/// target to its surface root, continuing through the <b>logical parent at surface roots</b> (the
/// popup seam — Esc inside a menu must bubble to the popup's host chain). Built once per raise
/// (ND3: tree mutation during dispatch does not alter the in-flight route); rented from a
/// per-thread free-list so nested dispatch never reuses an in-flight scratch.
/// </summary>
internal sealed class EventRoute
{
    private const int MaxPooled = 8;

    [ThreadStatic]
    private static Stack<EventRoute>? _pool;

    private UIElement[] _nodes = new UIElement[16];
    private int _count;

    private EventRoute()
    {
    }

    /// <summary>The number of nodes (target first).</summary>
    internal int Count => _count;

    /// <summary>The node at <paramref name="index"/> (0 = target, last = outermost root).</summary>
    internal UIElement this[int index] => _nodes[index];

    /// <summary>Rents a cleared route from the per-thread free-list.</summary>
    internal static EventRoute Rent()
    {
        var pool = _pool ??= new Stack<EventRoute>();
        return pool.Count > 0 ? pool.Pop() : new EventRoute();
    }

    /// <summary>Returns a route to the free-list, clearing element references so nothing is pinned.</summary>
    internal static void Return(EventRoute route)
    {
        Array.Clear(route._nodes, 0, route._count);
        route._count = 0;
        var pool = _pool ??= new Stack<EventRoute>();
        if (pool.Count < MaxPooled)
            pool.Push(route);
    }

    /// <summary>Builds the route: target → visual parents → (the <see cref="UIElement.UIParent"/> bridge hop at
    /// surface roots — logical/templated parent, or a Popup's placement-target owner) → outermost root. Uses the
    /// same <c>VisualParent ?? UIParent</c> walk as S3's hit-test/hover/capture so routing honors the same
    /// tooltip/popup→owner bridge they do (a PlacementTarget-only popup's Escape reaches its owner).</summary>
    internal void Build(UIElement target)
    {
        // TODO: Certain classes of events--specifically input events, and mouse events in particular--should
        //       probably not jump outside the visual tree. This theory should be adversarially reviewed. If
        //       put into effect, there is additional tree-walking code that should maintain parity with this,
        //       e.g., `InputDispatcher.HitTestForEvent`.
        for (var node = target; node is not null; node = node.VisualParent ?? node.UIParent)
            Add(node);
    }

    private void Add(UIElement node)
    {
        if (_count == _nodes.Length)
            Array.Resize(ref _nodes, _nodes.Length * 2);

        _nodes[_count++] = node;
    }
}

/// <summary>
/// The route-walk engine (design doc §7.5). Per node, in both phases: the <c>On*</c> class virtual
/// (skipped once <see cref="RoutedEventArgs.Handled"/>) then instance handlers in registration
/// order (normal handlers skipped while handled; <c>handledEventsToo</c> handlers always run and
/// may un-handle — ND1/ND2). Handler exceptions propagate (fail fast to S6's funnel); pooled
/// scratch is released along the unwind.
/// </summary>
internal static class EventRouting
{
    /// <summary>Raises one event per its strategy (the public <see cref="UIElement.RaiseEvent"/> core).</summary>
    internal static void Raise(UIElement target, RoutedEventArgs args)
    {
        var routedEvent = args.RoutedEventUnchecked!;
        if (routedEvent.Strategy == RoutingStrategy.Direct)
        {
            InvokeNode(target, routedEvent, args);
            return;
        }

        var route = EventRoute.Rent();
        try
        {
            route.Build(target);
            if (routedEvent.Strategy == RoutingStrategy.Tunnel)
            {
                for (var i = route.Count - 1; i >= 0; i--)
                    InvokeNode(route[i], routedEvent, args);
            }
            else
            {
                for (var i = 0; i < route.Count; i++)
                    InvokeNode(route[i], routedEvent, args);
            }
        }
        finally
        {
            EventRoute.Return(route);
        }
    }

    /// <summary>
    /// Raises a <c>Preview*</c>/main pair over one route with one shared args instance (doc §7.5):
    /// tunnel root → target with <paramref name="tunnelEvent"/>, then bubble target → root with
    /// <paramref name="bubbleEvent"/>. <see cref="RoutedEventArgs.Handled"/> is pair-scoped (ND2).
    /// </summary>
    internal static void RaisePair(UIElement target, RoutedEvent tunnelEvent, RoutedEvent bubbleEvent, RoutedEventArgs args)
    {
        var route = EventRoute.Rent();
        try
        {
            route.Build(target);

            args.SetRoutedEvent(tunnelEvent);
            for (var i = route.Count - 1; i >= 0; i--)
                InvokeNode(route[i], tunnelEvent, args);

            args.SetRoutedEvent(bubbleEvent);
            for (var i = 0; i < route.Count; i++)
                InvokeNode(route[i], bubbleEvent, args);
        }
        finally
        {
            EventRoute.Return(route);
        }
    }

    private static void InvokeNode(UIElement node, RoutedEvent routedEvent, RoutedEventArgs args)
    {
        if (!args.Handled || routedEvent.ClassStageHandledEventsToo)
            routedEvent.InvokeClassStage(node, args);

        node.InvokeInstanceHandlers(routedEvent, args);

        // The per-node InputBindings sweep (doc §7.5 step 4; KeyDown bubble only): virtual →
        // instance handlers → bindings, still-unhandled gate per node (N159/N163).
        if (!args.Handled && routedEvent.SweepsInputBindings)
        {
            System.Diagnostics.Debug.Assert(
                args is KeyEventArgs,
                "SweepsInputBindings is only ever set on KeyDownEvent (RegisterClassEvent invariant).");
            node.SweepInputBindings((KeyEventArgs)args);
        }
    }
}
