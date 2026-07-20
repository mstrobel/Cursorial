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

    /// <summary>
    /// The route's parent hop — the SINGLE source of truth for the event-route chain, shared by
    /// <see cref="Build"/>, <see cref="RouteEnd"/> (the gesture tail's continuation point), and the
    /// dispatcher's disabled-hit fallback parity (ND7). Visual first; past a surface root the route
    /// continues via the LOGICAL parent <b>only</b> (the input-routing review's narrowing — WPF's
    /// <c>IgnoreModelParentBuildRoute</c> shape): the <c>PlacementTarget</c> leg is a hierarchy/lookup
    /// bridge, never a route (the standalone-popup key/semantic leak), and the <c>TemplatedParent</c> leg
    /// is provably dead on live routes (a template part reachable mid-route always has a visual parent) —
    /// asserted, not traversed. Ownership-chain walks (focus, access keys, capture, dismissal ancestry,
    /// <c>IsAncestorOf</c>) deliberately keep the wider <c>VisualParent ?? UIParent</c> chain.
    /// </summary>
    internal static UIElement? NextOnRoute(UIElement node)
    {
        if (node.VisualParent is { } visual)
            return visual;

        // The dead-leg proof covers LIVE routes only: a DETACHED template subtree is legitimately stamped
        // with a TemplatedParent before visual attach (ControlTemplate.Instantiate stamps pre-order; a
        // stamp-time {TemplateBinding} push can raise routed events from the unattached parts), and a
        // discarded fragment keeps its stamp forever (there is no template unstamp) while public RaiseEvent
        // on detached elements stays documented-legal — both end their route at the fragment root.
        System.Diagnostics.Debug.Assert(
            !node.IsAttachedToTree || node.LogicalParent is not null || node.TemplatedParent is null,
            "A live route reached a node whose only continuation is a TemplatedParent — the dead-leg assumption is violated.");

        return node.LogicalParent;
    }

    /// <summary>The last node the route walk reaches from <paramref name="target"/> — where the gesture
    /// tail's ownership-chain continuation picks up (design review Q2).</summary>
    internal static UIElement RouteEnd(UIElement target)
    {
        var node = target;
        while (NextOnRoute(node) is { } next)
            node = next;
        return node;
    }

    /// <summary>Builds the route: target → visual parents → (for non-surface-scoped events) the LOGICAL hop at
    /// surface roots → outermost root. A surface-scoped (pointer-family) route never leaves the target's
    /// surface: it is <c>VisualParent</c>-only (input-routing review Q1 — completing the 13b34bb hover
    /// confinement at the route level). Key/semantic routes take the logical seam hop, which is what keeps a
    /// popup's Escape reaching the <c>Popup</c> element and menu Clicks reaching the menu bar.</summary>
    internal void Build(UIElement target, bool surfaceScoped)
    {
        if (surfaceScoped)
        {
            for (var node = target; node is not null; node = node.VisualParent)
                Add(node);
        }
        else
        {
            for (var node = target; node is not null; node = NextOnRoute(node))
                Add(node);
        }
    }

    /// <summary>Builds the OWNERSHIP chain (<c>VisualParent ?? UIParent</c> — the focus pair's raise walk),
    /// snapshotted into the pooled scratch like every route (ND3), with a hop guard.</summary>
    internal void BuildOwnership(UIElement target)
    {
        var guard = 0;
        for (var node = (UIElement?)target; node is not null && guard++ < 256; node = node.VisualParent ?? node.UIParent)
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
    /// <summary>
    /// Raises a bubble along the OWNERSHIP chain (<c>VisualParent ?? UIParent</c>, the focus chain's walk)
    /// rather than the event route — the focus pair's raise path (input-routing review Q2 ruling 4):
    /// GotFocus/LostFocus are focus-STATE notifications, formally coupled to the same chain their
    /// <c>IsKeyboardFocusWithin</c> gates ride (placement leg included — ComboBox/menu close-on-focus-leave
    /// and the LostFocus binding flush depend on the full reach), so narrowing the event route never
    /// narrows them.
    /// </summary>
    internal static void RaiseAlongOwnershipChain(UIElement target, RoutedEventArgs args)
    {
        var routedEvent = args.RoutedEventUnchecked!;

        // Snapshot the chain before dispatch (ND3 parity with Raise): a handler that reparents or closes a
        // popup must not alter the in-flight chain. The guard bounds the ownership walk (it can carry a
        // placement hop; the chain is cycle-free in practice, but the route scratch never was unbounded).
        var route = EventRoute.Rent();
        try
        {
            route.BuildOwnership(target);
            for (var i = 0; i < route.Count; i++)
                InvokeNode(route[i], routedEvent, args);
        }
        finally
        {
            EventRoute.Return(route);
        }
    }

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
            route.Build(target, routedEvent.SurfaceScoped);
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
        System.Diagnostics.Debug.Assert(
            tunnelEvent.SurfaceScoped == bubbleEvent.SurfaceScoped,
            "A Preview/main pair shares ONE route — its members must agree on SurfaceScoped.");

        var route = EventRoute.Rent();
        try
        {
            route.Build(target, tunnelEvent.SurfaceScoped);

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
