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

    // The root awaiting a deferred first-focus: set by OnWindowActivated when activation could not
    // place focus yet (no scope memory and no tab stop — the visual subtree behind templates /
    // content presenters is not built until the first layout pass), retried at the post-layout
    // boundary by CompletePendingActivationFocus. One-shot per activation.
    private UIElement? _pendingActivationRoot;

    /// <summary>The modality source for the <c>:focus-visible</c> policy (assigned by the application after dispatcher construction).</summary>
    internal InputDispatcher? InputDispatcherInternal;

    /// <summary>The access-key manager (assigned by the application): pointer-driven focus changes exit menu mode.</summary>
    internal AccessKeyManager? AccessKeysInternal;
    
    /// <summary>Internal shortcut to the current application's <see cref="FocusManager"/>.</summary>
    internal static FocusManager? Current => UIApplication.Current?.FocusManager;

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

            if ((node.VisualParent ?? node.UIParent) is not {} parent)
                return node; // root fallback

            node = parent;
        }
    }

    // ───────────────────────────── focus retention (the toolbar focus-scope behavior, doc §7.7) ─────────────────────────────

    /// <summary>
    /// Whether focus that enters this scope <b>stays</b> until explicitly moved (the default, <see langword="true"/>
    /// — ordinary focus behavior). Set <see langword="false"/> to make the scope <b>return</b> focus to wherever it
    /// came from — the WPF focus-scope-for-toolbars behavior, terminal-adapted: a <b>pointer</b> or <b>access-key</b>
    /// invocation within a non-retaining scope returns focus to the pre-entry element (click Bold, keep typing),
    /// while tabbing IN holds focus there for arrow navigation until <c>Escape</c> returns it. The bars <c>Toolbar</c>
    /// sets this <see langword="false"/>. Orthogonal to <see cref="IsFocusScopeProperty"/> (activation memory).
    /// </summary>
    public static readonly AttachedProperty<bool> RetainsFocusProperty =
        UIProperty.RegisterAttached<FocusManager, UIElement, bool>("RetainsFocus", defaultValue: true);

    // Whether an INVOKE within the (non-retaining) scope auto-returns focus: true when the scope was ENTERED by
    // Pointer or AccessKey (a one-shot interaction — click Bold, keep typing), false when entered by Tab or
    // Directional navigation (the user is deliberately in the bar; only Escape returns). Set at entry; Escape's
    // explicit restore ignores this gate. The return SCOPE (not a fixed element) is captured alongside, see below.
    internal static readonly AttachedProperty<bool> RetainedReturnAutoProperty =
        UIProperty.RegisterAttached<FocusManager, UIElement, bool>("RetainedReturnAuto");

    // The focus scope that held focus immediately BEFORE entry — captured so the return resolves the logical focus of
    // the scope that ACTUALLY had focus, which (with nested scopes such as a ListBox items host that records its own
    // memory) is NOT necessarily the scope enclosing this bar. Stored as a SCOPE, not a fixed element:
    // GetFocusedElement(scope) is read at return time, so a since-detached previous element falls back to that scope's
    // current memory. Captured on the first genuine entry, preserved across intra-bar moves, consumed on return.
    internal static readonly AttachedProperty<UIElement?> RetainedReturnScopeProperty =
        UIProperty.RegisterAttached<FocusManager, UIElement, UIElement?>("RetainedReturnScope");

    /// <summary>Reads <see cref="RetainsFocusProperty"/> from <paramref name="element"/>.</summary>
    public static bool GetRetainsFocus(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(RetainsFocusProperty);
    }

    /// <summary>Sets <see cref="RetainsFocusProperty"/> on <paramref name="element"/>.</summary>
    public static void SetRetainsFocus(UIElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RetainsFocusProperty, value);
    }

    /// <summary>
    /// The nearest self-or-ancestor <b>non-retaining</b> scope of <paramref name="element"/>
    /// (<see cref="RetainsFocusProperty"/> = <see langword="false"/>), or <see langword="null"/> when every
    /// ancestor retains focus. Walks the LOGICAL parent first (then visual): a generated bar item is a logical
    /// child of its owning items control, so this reaches the owning toolbar in one hop whether the item is on the
    /// row or in the overflow popup band.
    /// </summary>
    internal static UIElement? FindReturningScope(UIElement element)
    {
        for (UIElement? node = element; node is not null; node = node.UIParent ?? node.VisualParent)
        {
            if (!node.GetValue(RetainsFocusProperty))
                return node;                            // a non-retaining scope — returns focus
            if (node.GetValue(IsFocusScopeProperty))
                return null;                            // a RETAINING focus scope is a barrier: focus inside it stays
                                                        // (a drop-opener like the overflow chevron opts out this way)
        }
        return null;
    }

    // When focus ENTERS a non-retaining scope from outside it, record whether the entry method auto-returns on
    // invoke (Pointer/AccessKey ⇒ yes; Tab/Directional ⇒ no, only Escape returns). Restore + Programmatic entries are
    // SKIPPED — a popup-close Restore or a programmatic move must not arm an auto-return (the opener keeps focus).
    // Moving WITHIN the scope preserves the entry record (so arrowing across the bar still returns to the original
    // element). The return TARGET is not recorded — it is resolved at return time as the OUTER scope's logical focus.
    private static void MarkReturnableEntry(UIElement target, UIElement? oldFocus, FocusNavigationMethod method)
    {
        if (method is FocusNavigationMethod.Restore or FocusNavigationMethod.Programmatic)
            return;
        if (FindReturningScope(target) is not {} scope)
            return;
        if (oldFocus is not null && ReferenceEquals(FindReturningScope(oldFocus), scope))
            return; // intra-scope move — keep the entry record (and the captured return scope)

        scope.SetValue(RetainedReturnAutoProperty,
            method is FocusNavigationMethod.Pointer or FocusNavigationMethod.AccessKey);

        // Capture the scope that held focus before entry — the return resolves ITS logical focus. With nested scopes
        // (a ListBox items host records the focused item on the panel, not the surface root), the pre-entry scope is
        // not the one enclosing this bar, so the enclosing scope's memory would be stale.
        scope.SetValue(RetainedReturnScopeProperty, oldFocus is null ? null : GetFocusScope(oldFocus));
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
                   FocusNavigationDirection.Next     => MoveTab(forward: true),
                   FocusNavigationDirection.Previous => MoveTab(forward: false),
                   _                                 => MoveDirectional(direction)
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

    /// <summary>
    /// The element focus lands on when navigation is DIRECTED AT <paramref name="element"/> (e.g. an access-key
    /// proxy's declared target): <paramref name="element"/> itself when it is a valid focus target; otherwise,
    /// treating it as a scope/container, its remembered focus → first tab-ordered eligible descendant (the ND33
    /// entry ladder); <see langword="null"/> when it holds no focus target (the caller then forwards via
    /// <see cref="FindNext"/>). Pure query — never moves focus.
    /// </summary>
    public UIElement? ResolveFocusEntry(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _dispatcher.VerifyAccess();
        return IsValidFocusTarget(element) ? element : FocusNavigator.ResolveEntryInto(element);
    }

    /// <summary>
    /// Explicitly returns focus to the logical focus of the OUTER focus scope enclosing the nearest
    /// <b>non-retaining</b> scope (<see cref="RetainsFocusProperty"/> = <see langword="false"/>) of
    /// <paramref name="elementInScope"/> — i.e. where focus was before it entered the bar. The bar surface calls this
    /// on an unhandled <c>Escape</c>; a drop-down opener can call it when its popup closes. Unlike the implicit
    /// invoke return it ignores the entry modality — even a Tab/Directional entry returns. A no-op
    /// (<see langword="false"/>) when there is no non-retaining scope, the outer scope has no remembered focus, or
    /// that element is no longer a valid focus target.
    /// </summary>
    /// <param name="elementInScope">An element within the non-retaining scope (typically the just-invoked control).</param>
    public bool RestoreRetainedFocus(UIElement elementInScope)
    {
        ArgumentNullException.ThrowIfNull(elementInScope);
        _dispatcher.VerifyAccess();
        return ReturnRetainedFocus(elementInScope, requireAuto: false);
    }

    // The implicit INVOKE return (ButtonBase calls this after a click): returns focus only when the scope was
    // ENTERED by a one-shot interaction (Pointer/AccessKey ⇒ RetainedReturnAuto). A Tab/Directional entry keeps
    // focus AND its return target (only the explicit Escape path returns from there).
    internal bool TryAutoReturnFocus(UIElement elementInScope)
    {
        _dispatcher.VerifyAccess();
        return ReturnRetainedFocus(elementInScope, requireAuto: true);
    }

    private bool ReturnRetainedFocus(UIElement elementInScope, bool requireAuto)
    {
        if (FindReturningScope(elementInScope) is not {} scope)
            return false;

        if (requireAuto && !scope.GetValue(RetainedReturnAutoProperty))
            return false; // Tab/Directional entry — keep focus (only the explicit Escape path returns)

        scope.SetValue(RetainedReturnAutoProperty, false); // consume the one-shot auto-arm

        // The return target is the logical focus of the scope that held focus before entry. Prefer the scope CAPTURED
        // at entry (MarkReturnableEntry): with nested scopes (a ListBox items host) the pre-entry focus lives in a
        // scope that is NOT the one enclosing this bar, so resolving the enclosing scope's memory would return to the
        // wrong element. Fall back to the enclosing scope when nothing was captured (e.g. an explicit restore with no
        // prior entry record). The captured value is a SCOPE — its CURRENT FocusedElement memory is read here, so the
        // original element is found even after arrowing across the bar, and a detached one falls back to scope memory.
        var returnScope = scope.GetValue(RetainedReturnScopeProperty);
        scope.SetValue(RetainedReturnScopeProperty, null); // consume

        if (returnScope is null)
        {
            if ((scope.UIParent ?? scope.VisualParent) is not {} parent)
                return false;
            returnScope = GetFocusScope(parent);
        }

        var target = GetFocusedElement(returnScope);
        if (target is null || !IsValidFocusTarget(target) || ReferenceEquals(target, FocusedElement))
            return false; // nothing valid to return to, or focus is already there (don't report a no-op as a return)

        // A return is a focus RESTORE — always show the focus-visible ring (the pinned Restore policy), regardless of
        // how the bar was invoked. Resolving by the invoke modality leaked Pointer through and dropped the ring on a
        // pointer-invoked return while keyboard/access-key kept it (an inconsistency); Restore makes all paths equal.
        return SetFocus(target, FocusNavigationMethod.Restore);
    }

    // ───────────────────────────── activation / restore (doc §7.7) ─────────────────────────────

    /// <summary>
    /// Records the active surface root and restores focus into it: the root's scope memory when
    /// still valid, else its first tab-ordered focusable, else none (keys then target the root) —
    /// always with <see cref="FocusNavigationMethod.Restore"/>. At P2 the single application root
    /// is the only surface; S4's window manager takes over these calls at P7.
    /// </summary>
    /// <remarks>
    /// Activation runs synchronously when a root is shown — <b>before the first layout pass</b>, so a
    /// just-attached root's controls behind a template / content-presenter boundary
    /// (<c>ContentControl.Content</c>, <c>ScrollViewer</c>) are not yet in the visual tree the tab-order
    /// walk descends. When neither scope memory nor a first tab stop is reachable, the activation is
    /// <em>parked</em> (<see cref="_pendingActivationRoot"/>) and the spine retries it through
    /// <see cref="CompletePendingActivationFocus"/> after the first layout pass builds the subtree.
    /// </remarks>
    public void OnWindowActivated(UIElement windowRoot)
    {
        ArgumentNullException.ThrowIfNull(windowRoot);
        _dispatcher.VerifyAccess();

        ActiveRoot = windowRoot;
        _pendingActivationRoot = null;

        if (GetFocusedElement(windowRoot) is {} memory && IsValidFocusTarget(memory))
        {
            SetFocus(memory, FocusNavigationMethod.Restore);
            return;
        }

        if (_navigator.FirstOrLastTabStop(windowRoot, forward: true) is {} first)
        {
            SetFocus(first, FocusNavigationMethod.Restore);
            return;
        }

        // No focus target reachable yet — park the activation. The first tab stop typically appears
        // once the first layout pass applies templates and realizes content (the demo's whole panel
        // sits behind a ScrollViewer's banded SCP + a ContentControl boundary); the post-layout retry
        // places focus then. Until it resolves, keys/paste fall back to the active root (N115).
        _pendingActivationRoot = windowRoot;
    }

    /// <summary>
    /// Retries a <em>parked</em> activation focus (see <see cref="OnWindowActivated"/>): the spine calls
    /// this at the post-layout boundary so a root whose first tab stop only materializes after the first
    /// measure/arrange (templates applied, content realized) auto-focuses its first focusable. A no-op
    /// once focus has landed anywhere, once the user moved focus, or once the active root changed — the
    /// retry never overrides a genuine focus the application or the user already established.
    /// </summary>
    internal void CompletePendingActivationFocus()
    {
        if (_pendingActivationRoot is not {} root)
            return;

        // Stale-park hygiene: only retry while this is still the active root and focus is still empty.
        if (!ReferenceEquals(ActiveRoot, root) || FocusedElement is not null)
        {
            _pendingActivationRoot = null;
            return;
        }

        // Scope memory may have been recorded since activation (e.g. window-activation memory) — honor
        // it first, exactly as OnWindowActivated does.
        if (GetFocusedElement(root) is {} memory && IsValidFocusTarget(memory))
        {
            _pendingActivationRoot = null;
            SetFocus(memory, FocusNavigationMethod.Restore);
            return;
        }

        if (_navigator.FirstOrLastTabStop(root, forward: true) is {} first)
        {
            _pendingActivationRoot = null;
            SetFocus(first, FocusNavigationMethod.Restore);
        }
        // else: still nothing reachable — leave it parked; a later layout (content arriving async) retries.
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

        if (ReferenceEquals(_pendingActivationRoot, windowRoot))
            _pendingActivationRoot = null; // a deactivated root never auto-focuses
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
                node.SetValue(FocusedElementProperty, (UIElement?) null);
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
        if (FocusedElement is {} focused && !IsValidFocusTarget(focused))
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
            && _navigator.FirstOrLastTabStop(scopeRoot, forward: true) is {} first)
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

        if (FocusedElement is {} current)
        {
            target = _navigator.NextTabStop(current, forward);
        }
        else if (ActiveRoot is {} root)
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
        if (FocusedElement is not {} current)
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
                                                                                                    or FocusNavigationMethod.AccessKey or
                                                                                                    FocusNavigationMethod.Restore => true,
                                                                          FocusNavigationMethod.Programmatic =>
                                                                              (InputDispatcherInternal?.LastModality ?? InputModality.Keyboard) ==
                                                                              InputModality.Keyboard,
                                                                          _ => false // Pointer
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
            {
                GetFocusScope(target).SetValue(FocusedElementProperty, target); // nearest scope only (N113)
                MarkReturnableEntry(target, oldFocus, method); // arm the auto-return for a non-retaining scope entry
            }

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