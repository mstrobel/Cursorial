// ReSharper disable RedundantCast

namespace Cursorial.UI.Input;

/// <summary>
/// Keyboard-focus state for one application (design doc §7.7): the physical-focus singleton, the
/// <see cref="ActiveRoot"/> record (the load-bearing key/paste fallback target), logical focus
/// scopes with per-scope memory, Tab / directional navigation, and the focus-repair / detach
/// hygiene chain. State commits before the <c>LostFocus</c>/<c>GotFocus</c> raises; the
/// <see cref="InteractionState.Focused"/> / <see cref="InteractionState.FocusWithin"/> /
/// <see cref="InteractionState.FocusVisible"/> bits and the read-only
/// <see cref="UIElement.IsFocusedProperty"/> / <see cref="UIElement.IsKeyboardFocusWithinProperty"/>
/// mirrors are maintained along the diverging ancestor chains only.
/// </summary>
/// <remarks>
/// Terminal-level focus (<c>FocusEvent</c>) never moves keyboard focus — the dispatcher's
/// terminal-focus cluster retains <see cref="FocusedElement"/> and raises
/// <c>EditCommitRequested</c> instead (doc §13.2). UI-thread only.
/// </remarks>
public sealed class FocusManager
{
    private const int MaxTransitionDepth = 8;

    private readonly UIDispatcher _dispatcher;
    private readonly InteractionStateService _interactions;
    private readonly FocusNavigator _navigator = new();
    private readonly List<UIElement> _oldChainScratch = [];
    private readonly List<UIElement> _newChainScratch = [];
    private int _transitionDepth;

    /// <summary>The modality source for the <c>:focus-visible</c> policy (assigned by the application after dispatcher construction).</summary>
    internal InputDispatcher? InputDispatcherInternal;

    /// <summary>The access-key manager (assigned by the application): pointer-driven focus changes exit menu mode.</summary>
    internal AccessKeyManager? AccessKeysInternal;

    internal FocusManager(UIDispatcher dispatcher, InteractionStateService interactions)
    {
        _dispatcher = dispatcher;
        _interactions = interactions;
    }

    // ───────────────────────────── logical focus scopes (doc §7.7) ─────────────────────────────

    /// <summary>
    /// Marks an element as a logical focus scope (default <see langword="false"/>; window roots are
    /// set <see langword="true"/> by the host convention — the P2 single-root harness at show, S4's
    /// window manager at P7; menu/popup/toolbar roots follow at their phases).
    /// </summary>
    public static readonly AttachedProperty<bool> IsFocusScopeProperty =
        UIProperty.RegisterAttached<FocusManager, UIElement, bool>("IsFocusScope");

    /// <summary>
    /// The scope's focus memory — the element focus returns to on window re-activation
    /// (framework-written on every focus change, on the <b>nearest</b> scope only, so an outer
    /// window's memory survives an inner-scope excursion). Cleared eagerly when the remembered
    /// element detaches (no pinned subtrees).
    /// </summary>
    public static readonly AttachedProperty<UIElement?> FocusedElementProperty =
        UIProperty.RegisterAttached<FocusManager, UIElement, UIElement?>("FocusedElement");

    /// <summary>Reads <see cref="IsFocusScopeProperty"/> from <paramref name="element"/>.</summary>
    public static bool GetIsFocusScope(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsFocusScopeProperty);
    }

    /// <summary>Sets <see cref="IsFocusScopeProperty"/> on <paramref name="element"/>.</summary>
    public static void SetIsFocusScope(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsFocusScopeProperty, value);
    }

    /// <summary>The scope's remembered element (<see cref="FocusedElementProperty"/>), or null.</summary>
    public static UIElement? GetFocusedElement(UIElement scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.GetValue(FocusedElementProperty);
    }

    /// <summary>Sets the scope's focus memory (framework-written on focus changes; settable to prime <c>Once</c>-container entry).</summary>
    public static void SetFocusedElement(UIElement scope, UIElement? element)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.SetValue(FocusedElementProperty, element);
    }

    /// <summary>
    /// The nearest <b>self-or-ancestor</b> focus scope of <paramref name="element"/> (WPF
    /// self-inclusive semantics); the tree root when no explicit scope is marked.
    /// </summary>
    public static UIElement GetFocusScope(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var node = element;
        while (true)
        {
            if (node.GetValue(IsFocusScopeProperty))
                return node;

            if ((node.VisualParent ?? node.LogicalParent) is not { } parent)
                return node; // root fallback

            node = parent;
        }
    }

    // ───────────────────────────── physical focus ─────────────────────────────

    /// <summary>The physically focused element — one per application; null when focus is empty.</summary>
    public UIElement? FocusedElement { get; private set; }

    /// <summary>
    /// The active surface root, recorded from <see cref="OnWindowActivated"/> /
    /// <see cref="OnWindowDeactivated"/> — the key/paste dispatch fallback and the navigation start
    /// point with empty focus. With both this and <see cref="FocusedElement"/> null, key and paste
    /// events are dropped (never routed to "topmost" — topmost is not activation).
    /// </summary>
    public UIElement? ActiveRoot { get; private set; }

    /// <summary>
    /// Moves keyboard focus to <paramref name="target"/> after validation (attached, focusable,
    /// effectively enabled, effectively visible — no ancestor fallback). State commits before the
    /// <c>LostFocus</c>-then-<c>GotFocus</c> raises; the nearest scope records memory;
    /// <see cref="InteractionState.FocusVisible"/> follows the doc §7.7 policy (set for Tab /
    /// Directional / AccessKey / Restore — Restore <b>always</b>, the recorded divergence from
    /// Chrome heuristics — and for Programmatic under keyboard modality; never for Pointer).
    /// Re-entrant calls from focus handlers are last-wins, depth-capped at 8.
    /// </summary>
    /// <returns>Whether focus is on <paramref name="target"/> when the call returns.</returns>
    public bool SetFocus(UIElement target, FocusNavigationMethod method = FocusNavigationMethod.Programmatic)
    {
        ArgumentNullException.ThrowIfNull(target);
        _dispatcher.VerifyAccess();

        if (!IsValidFocusTarget(target))
            return false;

        if (ReferenceEquals(FocusedElement, target))
        {
            // Same element: no Lost/GotFocus re-raise; FocusVisible still updates per method
            // (a Tab landing on the focused element shows the visual; Restore always does — N106).
            target.SetInteractionStateInternal(InteractionState.FocusVisible, ComputeFocusVisible(method));
            AccessKeysInternal?.OnFocusChanged(method); // Pointer clears the sticky cue (N106/N179)
            return true;
        }

        MoveFocusCore(target, method, ComputeFocusVisible(method));
        return ReferenceEquals(FocusedElement, target);
    }

    /// <summary>Clears keyboard focus (rare); key/paste events then target <see cref="ActiveRoot"/>.</summary>
    public void ClearFocus()
    {
        _dispatcher.VerifyAccess();
        if (FocusedElement is null)
            return;

        MoveFocusCore(target: null, FocusNavigationMethod.Programmatic, focusVisible: false);
    }

    /// <summary>
    /// Moves focus in <paramref name="direction"/> from <see cref="FocusedElement"/>:
    /// <see cref="FocusNavigationDirection.Next"/>/<see cref="FocusNavigationDirection.Previous"/>
    /// walk the tab order (with empty focus they start at <see cref="ActiveRoot"/>'s first/last tab
    /// stop); the four directional values engage only inside a
    /// <see cref="KeyboardNavigation.DirectionalNavigationProperty"/> container (and return
    /// <see langword="false"/> with empty focus).
    /// </summary>
    /// <returns>Whether focus actually moved — the dispatcher's navigation tail marks events handled on this.</returns>
    public bool MoveFocus(FocusNavigationDirection direction)
    {
        _dispatcher.VerifyAccess();
        return direction switch
        {
            FocusNavigationDirection.Next => MoveTab(forward: true),
            FocusNavigationDirection.Previous => MoveTab(forward: false),
            _ => MoveDirectional(direction),
        };
    }

    /// <summary>
    /// The element Tab would move to from <paramref name="from"/> — a pure query over the tab-order
    /// collection (design-doc punch 23; Label targeting): <paramref name="from"/> need not be
    /// focusable, its document position anchors the search; <c>Once</c> containers entry-resolve;
    /// wraps within <paramref name="from"/>'s tab container. Null when nothing is reachable. Never
    /// moves focus.
    /// </summary>
    public UIElement? FindNext(UIElement from)
    {
        ArgumentNullException.ThrowIfNull(from);
        _dispatcher.VerifyAccess();
        return _navigator.NextTabStop(from, forward: true);
    }

    // ───────────────────────────── activation / restore (doc §7.7) ─────────────────────────────

    /// <summary>
    /// Records the active surface root and restores focus into it: the root's scope memory when
    /// still valid, else its first tab-ordered focusable, else none (keys then target the root) —
    /// always with <see cref="FocusNavigationMethod.Restore"/>. At P2 the single application root
    /// is the only surface; S4's window manager takes over these calls at P7.
    /// </summary>
    public void OnWindowActivated(UIElement windowRoot)
    {
        ArgumentNullException.ThrowIfNull(windowRoot);
        _dispatcher.VerifyAccess();
        ActiveRoot = windowRoot;

        if (GetFocusedElement(windowRoot) is { } memory && IsValidFocusTarget(memory))
        {
            SetFocus(memory, FocusNavigationMethod.Restore);
            return;
        }

        if (_navigator.FirstOrLastTabStop(windowRoot, forward: true) is { } first)
            SetFocus(first, FocusNavigationMethod.Restore);
        // else: no focusables — keys/paste fall back to the active root (N115).
    }

    /// <summary>
    /// Clears <see cref="ActiveRoot"/> when it matches <paramref name="windowRoot"/>. Physical
    /// focus is left in place (it moves when the next window activates — S4 policy at P7); the
    /// logical scope memory is the whole point of deactivation surviving.
    /// </summary>
    public void OnWindowDeactivated(UIElement windowRoot)
    {
        ArgumentNullException.ThrowIfNull(windowRoot);
        _dispatcher.VerifyAccess();
        if (ReferenceEquals(ActiveRoot, windowRoot))
            ActiveRoot = null;
    }

    // ───────────────────────────── detach hygiene (doc §7.10) ─────────────────────────────

    /// <summary>
    /// Detach fan-in: eagerly clears scope memories pointing at the detached element (no pinned
    /// subtrees — N114), drops a detached <see cref="ActiveRoot"/>, and repairs physical focus
    /// (nearest focusable ancestor → scope root's first tab stop → clear; repair never sets
    /// <see cref="InteractionState.FocusVisible"/>). <paramref name="detachingRoot"/> is the root
    /// of the subtree being removed: repair candidates inside it are skipped (ND30 — the bottom-up
    /// walk leaves doomed ancestors momentarily attached-looking; they must never receive focus).
    /// </summary>
    internal void OnElementDetached(UIElement element, UIElement detachingRoot)
    {
        for (var node = element; node is not null; node = node.VisualParent ?? node.LogicalParent)
        {
            if (ReferenceEquals(node.GetValue(FocusedElementProperty), element))
                node.SetValue(FocusedElementProperty, (UIElement?)null);
        }

        if (ReferenceEquals(ActiveRoot, element))
            ActiveRoot = null;

        if (ReferenceEquals(FocusedElement, element))
            RepairFocus(element, detachingRoot);
    }

    /// <summary>
    /// The in-place repair leg (ND28): when the focused element stops being a valid target without
    /// detaching — disabled, hidden, or an ancestor of it — focus repairs exactly as on detach.
    /// Called by S1's enabled/visibility producers after their cascades settle; a cheap no-op while
    /// focus is empty or still valid.
    /// </summary>
    internal void RepairFocusIfInvalid()
    {
        if (FocusedElement is { } focused && !IsValidFocusTarget(focused))
            RepairFocus(focused, detachingRoot: null);
    }

    /// <param name="invalidated">The element focus is being repaired away from.</param>
    /// <param name="detachingRoot">
    /// The root of the detaching subtree (every element within is excluded as a repair candidate —
    /// ND30), or <see langword="null"/> for in-place repair (ND28 — nothing is detaching; the walk
    /// starts at <paramref name="invalidated"/>'s parent and ordinary validation excludes the
    /// disabled/hidden region).
    /// </param>
    private void RepairFocus(UIElement invalidated, UIElement? detachingRoot)
    {
        // ① Nearest still-attached focusable ancestor (N109) outside the doomed subtree (ND30).
        // The detach walk is bottom-up, so the parent chain above the detaching root is still
        // navigable when the notification fires; everything at or below it is skipped.
        var start = detachingRoot ?? invalidated;
        for (var node = start.VisualParent ?? start.LogicalParent; node is not null; node = node.VisualParent ?? node.LogicalParent)
        {
            if (IsValidFocusTarget(node))
            {
                MoveFocusCore(node, FocusNavigationMethod.Programmatic, focusVisible: false);
                return;
            }
        }

        // ② The scope root's first tab-ordered focusable (N110); the active root stands in when
        // the scope itself is gone or doomed (ND30).
        var scopeRoot = GetFocusScope(invalidated);
        if (!scopeRoot.IsAttachedToTree || (detachingRoot is not null && IsWithinSubtree(scopeRoot, detachingRoot)))
            scopeRoot = ActiveRoot!;

        if (scopeRoot is { IsAttachedToTree: true }
            && (detachingRoot is null || !IsWithinSubtree(scopeRoot, detachingRoot))
            && _navigator.FirstOrLastTabStop(scopeRoot, forward: true) is { } first)
        {
            MoveFocusCore(first, FocusNavigationMethod.Programmatic, focusVisible: false);
            return;
        }

        // ③ Nothing focusable remains (N111): clear; keys fall back to the active root.
        MoveFocusCore(target: null, FocusNavigationMethod.Programmatic, focusVisible: false);
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="root"/> or inside its subtree (route-walk chain).</summary>
    private static bool IsWithinSubtree(UIElement node, UIElement root)
    {
        for (var n = node; n is not null; n = n.VisualParent ?? n.LogicalParent)
        {
            if (ReferenceEquals(n, root))
                return true;
        }

        return false;
    }

    // ───────────────────────────── navigation legs ─────────────────────────────

    private bool MoveTab(bool forward)
    {
        UIElement? target;
        if (FocusedElement is { } current)
        {
            target = _navigator.NextTabStop(current, forward);
        }
        else if (ActiveRoot is { } root)
        {
            target = _navigator.FirstOrLastTabStop(root, forward); // N122: first/last with empty focus
        }
        else
        {
            return false;
        }

        return target is not null && MoveFocusForNavigation(target, FocusNavigationMethod.Tab);
    }

    private bool MoveDirectional(FocusNavigationDirection direction)
    {
        if (FocusedElement is not { } current)
            return false;

        var target = _navigator.NextDirectional(current, direction);
        return target is not null && MoveFocusForNavigation(target, FocusNavigationMethod.Directional);
    }

    /// <summary>Navigation marks handled only when focus actually moved (doc §7.5 step 6, N130).</summary>
    private bool MoveFocusForNavigation(UIElement target, FocusNavigationMethod method)
    {
        var before = FocusedElement;
        SetFocus(target, method);
        return !ReferenceEquals(FocusedElement, before);
    }

    // ───────────────────────────── the transition core ─────────────────────────────

    /// <summary>Validation shared with the navigator: attached, focusable, effectively enabled, effectively visible.</summary>
    internal static bool IsValidFocusTarget(UIElement element)
        => element.IsAttachedToTree &&
           element.GetValue(UIElement.FocusableProperty) &&
           element is { IsEffectivelyEnabled: true, IsEffectivelyVisible: true };

    private bool ComputeFocusVisible(FocusNavigationMethod method) => method switch
    {
        FocusNavigationMethod.Tab or FocusNavigationMethod.Directional
            or FocusNavigationMethod.AccessKey or FocusNavigationMethod.Restore => true,
        FocusNavigationMethod.Programmatic =>
            (InputDispatcherInternal?.LastModality ?? InputModality.Keyboard) == InputModality.Keyboard,
        _ => false, // Pointer
    };

    /// <summary>
    /// One focus transition: commit state (doc §7.7 — old element clears
    /// <c>Focused|FocusVisible</c>, the diverging chains swap <c>FocusWithin</c> + the read-only
    /// property mirrors, the new element sets), record scope memory, then raise <c>LostFocus</c>
    /// from the old and <c>GotFocus</c> from the new. Re-entrant transitions (handlers refocusing)
    /// are last-wins, capped at depth 8 with a DEBUG diagnostic.
    /// </summary>
    private void MoveFocusCore(UIElement? target, FocusNavigationMethod method, bool focusVisible)
    {
        if (_transitionDepth >= MaxTransitionDepth)
        {
            InputDiagnostics.EmitFocusTransitionDepthExceeded();
            return;
        }

        _transitionDepth++;
        try
        {
            var oldFocus = FocusedElement;
            FocusedElement = target; // state before events (doc §7.7)

            // ReSharper disable once UnusedVariable

            // One interaction batch (ND11): the diverging-chain flips coalesce into one observer
            // notification per element, delivered post-commit and before the raises (N143).
            using (var batch = _interactions.BeginUpdate())
            {
                CommitFocusState(oldFocus, target, focusVisible);
            }

            if (target is not null)
                GetFocusScope(target).SetValue(FocusedElementProperty, target); // nearest scope only (N113)

            AccessKeysInternal?.OnFocusChanged(method); // Pointer clears the sticky cue (doc §7.7; N179)

            if (oldFocus is not null)
                RaiseFocusChanged(UIElement.LostFocusEvent, oldFocus, oldFocus, target, method);

            // ND31: a LostFocus handler may have completed a nested transition (last-wins) — the
            // outer GotFocus is then stale and is skipped: an element never observes GotFocus
            // while IsFocused == false (N210).
            if (target is not null && ReferenceEquals(FocusedElement, target))
                RaiseFocusChanged(UIElement.GotFocusEvent, target, oldFocus, target, method);
        }
        finally
        {
            _transitionDepth--;
        }
    }

    private void CommitFocusState(UIElement? oldFocus, UIElement? newFocus, bool focusVisible)
    {
        BuildFocusChain(oldFocus, _oldChainScratch);
        BuildFocusChain(newFocus, _newChainScratch);

        // Common suffix from the root side: shared ancestors keep FocusWithin with zero writes
        // (the common-prefix diff + equality gate — N102).
        var oldCount = _oldChainScratch.Count;
        var newCount = _newChainScratch.Count;
        var common = 0;
        while (common < oldCount && common < newCount
               && ReferenceEquals(_oldChainScratch[oldCount - 1 - common], _newChainScratch[newCount - 1 - common]))
        {
            common++;
        }

        if (oldFocus is not null)
        {
            oldFocus.SetInteractionStateInternal(InteractionState.Focused | InteractionState.FocusVisible, false);
            oldFocus.SetIsFocusedInternal(false);
        }

        for (var i = 0; i < oldCount - common; i++) // leaf-first off the old-only segment
        {
            var element = _oldChainScratch[i];
            element.SetInteractionStateInternal(InteractionState.FocusWithin, false);
            element.SetIsKeyboardFocusWithinInternal(false);
        }

        for (var i = newCount - common - 1; i >= 0; i--) // root-first onto the new-only segment
        {
            var element = _newChainScratch[i];
            element.SetInteractionStateInternal(InteractionState.FocusWithin, true);
            element.SetIsKeyboardFocusWithinInternal(true);
        }

        if (newFocus is not null)
        {
            newFocus.SetInteractionStateInternal(InteractionState.Focused, true);
            newFocus.SetInteractionStateInternal(InteractionState.FocusVisible, focusVisible);
            newFocus.SetIsFocusedInternal(true);
        }

        _oldChainScratch.Clear();
        _newChainScratch.Clear();
    }

    /// <summary>Leaf-first self-plus-ancestors chain over the route walk (<c>VisualParent ?? LogicalParent</c>).</summary>
    private static void BuildFocusChain(UIElement? leaf, List<UIElement> chain)
    {
        for (var node = leaf; node is not null; node = node.VisualParent ?? node.LogicalParent)
            chain.Add(node);
    }

    private static void RaiseFocusChanged(
        RoutedEvent<FocusChangedEventArgs> routedEvent,
        UIElement target,
        UIElement? oldFocus,
        UIElement? newFocus,
        FocusNavigationMethod method)
    {
        var args = EventArgsPool<FocusChangedEventArgs>.Rent();
        args.Initialize(routedEvent, target);
        args.InitializeFocus(oldFocus, newFocus, method);
        try
        {
            EventRouting.Raise(target, args);
        }
        finally
        {
            args.ReturnToPool();
        }
    }
}
