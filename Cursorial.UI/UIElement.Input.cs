using Cursorial.Media;
using Cursorial.UI.Input;

// ReSharper disable EventNeverSubscribedTo.Global

namespace Cursorial.UI;

public abstract partial class UIElement : IInteractionStateSink
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // S3's slice of UIElement (design doc §7.3): the routed-event vocabulary, the handler store,
    // RaiseEvent/RentEvent, the On* class-handler stage, the interaction-state sink, input
    // bindings, and the focus/capture entry points.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private EventHandlerStore? _eventHandlers;
    private InputBindingCollection? _inputBindings;

    // ───────────────────────────── the UI event vocabulary (doc §7) ─────────────────────────────

    /// <summary>The tunneling half of the key-down pair.</summary>
    public static readonly RoutedEvent<KeyEventArgs> PreviewKeyDownEvent =
        RegisterClassEvent<KeyEventArgs>("PreviewKeyDown", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewKeyDown(a));

    /// <summary>
    /// The bubbling key-down event (class stage: <see cref="OnKeyDown"/>). The
    /// <see cref="InputBindings"/> sweep runs per node on this event's bubble — after the virtual
    /// and instance handlers, while unhandled (doc §7.5 step 4).
    /// </summary>
    public static readonly RoutedEvent<KeyEventArgs> KeyDownEvent =
        RegisterClassEvent<KeyEventArgs>("KeyDown", RoutingStrategy.Bubble, static (e, a) => e.OnKeyDown(a), sweepsInputBindings: true);

    /// <summary>The tunneling half of the key-up pair (Kitty/Win32 terminals only — never gate activation on it).</summary>
    public static readonly RoutedEvent<KeyEventArgs> PreviewKeyUpEvent =
        RegisterClassEvent<KeyEventArgs>("PreviewKeyUp", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewKeyUp(a));

    /// <summary>The bubbling key-up event. Route-only (ND9): no framework tail ever runs on key-up.</summary>
    public static readonly RoutedEvent<KeyEventArgs> KeyUpEvent =
        RegisterClassEvent<KeyEventArgs>("KeyUp", RoutingStrategy.Bubble, static (e, a) => e.OnKeyUp(a));

    /// <summary>The tunneling half of the text-input pair (synthesized keys + bracketed paste).</summary>
    public static readonly RoutedEvent<TextInputEventArgs> PreviewTextInputEvent =
        RegisterClassEvent<TextInputEventArgs>("PreviewTextInput", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewTextInput(a));

    /// <summary>The bubbling text-input event (class stage: <see cref="OnTextInput"/>).</summary>
    public static readonly RoutedEvent<TextInputEventArgs> TextInputEvent =
        RegisterClassEvent<TextInputEventArgs>("TextInput", RoutingStrategy.Bubble, static (e, a) => e.OnTextInput(a));

    /// <summary>The tunneling half of the mouse-button-down pair.</summary>
    public static readonly RoutedEvent<MouseButtonEventArgs> PreviewMouseDownEvent =
        RegisterClassEvent<MouseButtonEventArgs>("PreviewMouseDown", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewMouseDown(a), surfaceScoped: true);

    /// <summary>The bubbling mouse-button-down event (multi-click counts ride this one — <see cref="MouseButtonEventArgs.ClickCount"/>).</summary>
    public static readonly RoutedEvent<MouseButtonEventArgs> MouseDownEvent =
        RegisterClassEvent<MouseButtonEventArgs>("MouseDown", RoutingStrategy.Bubble, static (e, a) => e.OnMouseDown(a), surfaceScoped: true);

    /// <summary>The tunneling half of the mouse-button-up pair.</summary>
    public static readonly RoutedEvent<MouseButtonEventArgs> PreviewMouseUpEvent =
        RegisterClassEvent<MouseButtonEventArgs>("PreviewMouseUp", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewMouseUp(a), surfaceScoped: true);

    /// <summary>The bubbling mouse-button-up event.</summary>
    public static readonly RoutedEvent<MouseButtonEventArgs> MouseUpEvent =
        RegisterClassEvent<MouseButtonEventArgs>("MouseUp", RoutingStrategy.Bubble, static (e, a) => e.OnMouseUp(a), surfaceScoped: true);

    /// <summary>The tunneling half of the mouse-move pair (any-event motion is on by default — keep handlers cheap).</summary>
    public static readonly RoutedEvent<MouseEventArgs> PreviewMouseMoveEvent =
        RegisterClassEvent<MouseEventArgs>("PreviewMouseMove", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewMouseMove(a), surfaceScoped: true);

    /// <summary>The bubbling mouse-move event.</summary>
    public static readonly RoutedEvent<MouseEventArgs> MouseMoveEvent =
        RegisterClassEvent<MouseEventArgs>("MouseMove", RoutingStrategy.Bubble, static (e, a) => e.OnMouseMove(a), surfaceScoped: true);

    /// <summary>The tunneling half of the mouse-wheel pair.</summary>
    public static readonly RoutedEvent<MouseWheelEventArgs> PreviewMouseWheelEvent =
        RegisterClassEvent<MouseWheelEventArgs>("PreviewMouseWheel", RoutingStrategy.Tunnel, static (e, a) => e.OnPreviewMouseWheel(a), surfaceScoped: true);

    /// <summary>The bubbling mouse-wheel event (always targets the hit element, never focus — doc §7.6).</summary>
    public static readonly RoutedEvent<MouseWheelEventArgs> MouseWheelEvent =
        RegisterClassEvent<MouseWheelEventArgs>("MouseWheel", RoutingStrategy.Bubble, static (e, a) => e.OnMouseWheel(a), surfaceScoped: true);

    /// <summary>The pointer entered this element's hover chain (Direct, non-bubbling — WPF semantics).</summary>
    public static readonly RoutedEvent<MouseEventArgs> MouseEnterEvent =
        RegisterClassEvent<MouseEventArgs>("MouseEnter", RoutingStrategy.Direct, static (e, a) => e.OnMouseEnter(a));

    /// <summary>The pointer left this element's hover chain (Direct, non-bubbling).</summary>
    public static readonly RoutedEvent<MouseEventArgs> MouseLeaveEvent =
        RegisterClassEvent<MouseEventArgs>("MouseLeave", RoutingStrategy.Direct, static (e, a) => e.OnMouseLeave(a));

    /// <summary>Keyboard focus arrived (Bubble; state committed before the raise — doc §7.7).</summary>
    public static readonly RoutedEvent<FocusChangedEventArgs> GotFocusEvent =
        RegisterClassEvent<FocusChangedEventArgs>("GotFocus", RoutingStrategy.Bubble, static (e, a) => e.OnGotFocus(a));

    /// <summary>
    /// Keyboard focus left (Bubble). NOT raised on terminal focus-out — keyboard focus is retained
    /// and <c>InputDispatcher.EditCommitRequested</c> fires instead (doc §13.2).
    /// </summary>
    public static readonly RoutedEvent<FocusChangedEventArgs> LostFocusEvent =
        RegisterClassEvent<FocusChangedEventArgs>("LostFocus", RoutingStrategy.Bubble, static (e, a) => e.OnLostFocus(a));

    /// <summary>Mouse capture was released (Direct) — explicit release, transfer, detach, surface close, or terminal focus-out.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> LostMouseCaptureEvent =
        RegisterClassEvent<RoutedEventArgs>("LostMouseCapture", RoutingStrategy.Direct, static (e, a) => e.OnLostMouseCapture(a));

    /// <summary>
    /// Raised (Bubble) when the framework resolves the pointer shape over an element (design doc §7.6; WPF
    /// parity). Bubbles from the element directly under the pointer to the root; the class stage
    /// (<see cref="OnQueryCursor"/>) fills <see cref="QueryCursorEventArgs.Cursor"/> from
    /// <see cref="Cursor"/> honoring <see cref="ForceCursor"/>. A handler may override the result. The
    /// framework raises it on the pooled per-frame hover/capture re-resolution path, so handlers must be cheap.
    /// </summary>
    public static readonly RoutedEvent<QueryCursorEventArgs> QueryCursorEvent =
        RegisterClassEvent<QueryCursorEventArgs>("QueryCursor", RoutingStrategy.Bubble, static (e, a) => e.OnQueryCursor(a),
            classStageHandledEventsToo: true,  // OnQueryCursor runs on ancestors even after a descendant claimed it, so ForceCursor can override
            surfaceScoped: true);              // rides the (surface-scoped) hover machinery — an owner's ForceCursor never crosses a popup seam

    // ───────────────────────────── focus properties (doc §7.3) ─────────────────────────────

    /// <summary>
    /// Whether the element can receive keyboard focus (default <see langword="false"/> at the
    /// <see cref="UIElement"/> tier — interactive controls opt in via metadata override).
    /// </summary>
    public static readonly StyledProperty<bool> FocusableProperty =
        UIProperty.Register<UIElement, bool>(nameof(Focusable));

    /// <inheritdoc cref="FocusableProperty"/>
    public bool Focusable { get => GetValue(FocusableProperty); set => SetValue(FocusableProperty, value); }

    /// <summary>
    /// Whether the element participates in Tab navigation (default <see langword="true"/>; a
    /// non-stop element remains programmatically focusable — doc §7.3).
    /// </summary>
    public static readonly StyledProperty<bool> IsTabStopProperty =
        UIProperty.Register<UIElement, bool>(nameof(IsTabStop), defaultValue: true);

    /// <inheritdoc cref="IsTabStopProperty"/>
    public bool IsTabStop { get => GetValue(IsTabStopProperty); set => SetValue(IsTabStopProperty, value); }

    /// <summary>
    /// The element's tab-order rank (default <see cref="int.MaxValue"/> — ties resolve to document
    /// order via a stable sort, so untouched trees tab in document order).
    /// </summary>
    public static readonly StyledProperty<int> TabIndexProperty =
        UIProperty.Register<UIElement, int>(nameof(TabIndex), defaultValue: int.MaxValue);

    /// <inheritdoc cref="TabIndexProperty"/>
    public int TabIndex { get => GetValue(TabIndexProperty); set => SetValue(TabIndexProperty, value); }

    // ───────────────────────────── mouse cursor (doc §7.6) ─────────────────────────────

    /// <summary>
    /// The host mouse-pointer shape requested while the pointer is over this element (doc §7.6) —
    /// Core's <see cref="MouseCursorShape"/> directly, no UI wrapper type. <see langword="null"/>
    /// (the default) means "no preference": resolution walks the hover chain leaf→root to the
    /// first non-null <c>Cursor</c>, falling back to the terminal default at the root; while mouse
    /// capture is held the capture target's resolved cursor wins. Honored only on terminals
    /// reporting <c>OutputProtocolCapabilities.MouseCursorShape</c> (OSC 22 — Kitty, Ghostty,
    /// Foot); silently inert otherwise, no polyfill. <b>[no invalidation]</b> — the cursor is not
    /// cell content; a change takes effect at the next hover/capture re-resolution — at the latest,
    /// the next rendered frame's per-frame re-resolution (doc §7.6). No <c>RequestRender</c> is
    /// needed when anything else renders; an otherwise-idle tree picks the change up on its next
    /// frame.
    /// </summary>
    public static readonly StyledProperty<MouseCursorShape?> CursorProperty =
        UIProperty.Register<UIElement, MouseCursorShape?>(nameof(Cursor), changed: OnCursorChanged);

    /// <inheritdoc cref="CursorProperty"/>
    public MouseCursorShape? Cursor { get => GetValue(CursorProperty); set => SetValue(CursorProperty, value); }

    /// <summary>
    /// When <see langword="true"/>, this element's <see cref="Cursor"/> is forced over the pointer even when a
    /// nearer element (a descendant under the pointer) already set one (design doc §7.6; WPF parity). The
    /// <c>QueryCursor</c> route is leaf→root, so by default the deepest element with a <see cref="Cursor"/>
    /// wins; an ancestor that <see cref="ForceCursor"/>s overrides it. Has no effect unless <see cref="Cursor"/>
    /// is also set. Honored only on terminals reporting OSC 22 (like <see cref="Cursor"/>).
    /// </summary>
    public static readonly StyledProperty<bool> ForceCursorProperty =
        UIProperty.Register<UIElement, bool>(nameof(ForceCursor), changed: OnForceCursorChanged);

    /// <inheritdoc cref="ForceCursorProperty"/>
    public bool ForceCursor { get => GetValue(ForceCursorProperty); set => SetValue(ForceCursorProperty, value); }

    private static readonly UIPropertyKey<bool> IsFocusedPropertyKey =
        UIProperty.RegisterReadOnly<UIElement, bool>(nameof(IsFocused));

    /// <summary>
    /// Whether this element holds keyboard focus — the structurally read-only mirror of
    /// <c>FocusManager.FocusedElement</c> (the internal <see cref="UIPropertyKey{T}"/> is the write
    /// right; keyless writes throw — PD14). Exists for <c>When</c> data-conditions and code; the
    /// styling path is the <see cref="InteractionState.Focused"/> bit.
    /// </summary>
    public static readonly StyledProperty<bool> IsFocusedProperty = IsFocusedPropertyKey.Property;

    /// <inheritdoc cref="IsFocusedProperty"/>
    public bool IsFocused => GetValue(IsFocusedProperty);

    private static readonly UIPropertyKey<bool> IsKeyboardFocusWithinPropertyKey =
        UIProperty.RegisterReadOnly<UIElement, bool>(nameof(IsKeyboardFocusWithin));

    /// <summary>
    /// Whether keyboard focus is on this element or inside its subtree (structurally read-only —
    /// PD14). Maintained by the focus manager along the diverging ancestor chains only, so common
    /// ancestors of a focus move see zero change notifications.
    /// </summary>
    public static readonly StyledProperty<bool> IsKeyboardFocusWithinProperty = IsKeyboardFocusWithinPropertyKey.Property;

    /// <inheritdoc cref="IsKeyboardFocusWithinProperty"/>
    public bool IsKeyboardFocusWithin => GetValue(IsKeyboardFocusWithinProperty);

    /// <summary>Focus-manager write surface for the <see cref="IsFocusedProperty"/> mirror (the key stays private).</summary>
    internal void SetIsFocusedInternal(bool value) => SetValue(IsFocusedPropertyKey, value);

    /// <summary>Focus-manager write surface for the <see cref="IsKeyboardFocusWithinProperty"/> mirror.</summary>
    internal void SetIsKeyboardFocusWithinInternal(bool value) => SetValue(IsKeyboardFocusWithinPropertyKey, value);

    private static void OnCursorChanged(UIObject sender, MouseCursorShape? oldValue, MouseCursorShape? newValue)
    {
        if (sender is UIElement { IsPointerOver: true })
            UIApplication.Current?.InputDispatcher.UpdateCursor();
    }

    // A ForceCursor flip on an element in the hover chain (every ancestor of the hovered leaf has
    // IsPointerOver) re-runs the QueryCursor resolution — the ancestor may now win or yield.
    private static void OnForceCursorChanged(UIObject sender, bool oldValue, bool newValue)
    {
        if (sender is UIElement { IsPointerOver: true })
            UIApplication.Current?.InputDispatcher.UpdateCursor();
    }

    // ───────────────────────────── interaction state (doc §3.2) ─────────────────────────────

    private InteractionState _interactionState;
    private bool _inInteractionBatch;

    /// <summary>
    /// Whether the pointer is over this element or a descendant — the
    /// <see cref="InteractionState.PointerOver"/> bit the hover chain maintains (doc §7.6).
    /// Capability-honest: never set when the terminal doesn't report motion. Not a styled
    /// property — the styling path is the interaction bit itself.
    /// </summary>
    public bool IsPointerOver => (_interactionState & InteractionState.PointerOver) != 0;

    /// <summary>The current interaction bitmask (the matrix's <c>state(e)</c> read surface).</summary>
    internal InteractionState InteractionStateInternal => _interactionState;

    /// <summary>
    /// The control-author interaction-state write (design doc §7.3 / §3.2) — equality-gated,
    /// committed before any notification. <see cref="InteractionState.Pressed"/> flips additionally
    /// fan into the dispatcher's pressed-holder set (styling contract C8): <c>:pressed</c> MUST
    /// flow through here so terminal focus-out can clear it window-wide.
    /// </summary>
    /// <param name="state">The flag(s) to flip.</param>
    /// <param name="active">Whether to set or clear.</param>
    protected void SetInteractionState(InteractionState state, bool active)
    {
        VerifyAccess();
        SetInteractionStateInternal(state, active);
    }

    /// <inheritdoc cref="SetInteractionState"/>
    void IInteractionStateSink.SetInteractionState(InteractionState state, bool active)
        => SetInteractionState(state, active);

    /// <summary>
    /// Opens an application-wide interaction-state batch (ND11): flips inside the scope coalesce
    /// into one observer notification per element at the outermost dispose; net-zero flips are
    /// silent. Returns a no-op scope outside a running application.
    /// </summary>
    protected InteractionUpdateScope BeginInteractionUpdate()
        => UIApplication.Current is { } app ? app.InteractionStates.BeginUpdate() : default;

    /// <inheritdoc cref="BeginInteractionUpdate"/>
    InteractionUpdateScope IInteractionStateSink.BeginInteractionUpdate() => BeginInteractionUpdate();

    /// <summary>
    /// The framework-internal equality-gated write behind <see cref="SetInteractionState"/>:
    /// commits the flip, then routes it through the application's
    /// <see cref="InteractionStateService"/> (observer delivery + Pressed fan-in).
    /// </summary>
    /// <returns>Whether the stored mask changed.</returns>
    internal bool SetInteractionStateInternal(InteractionState state, bool active)
    {
        var old = _interactionState;
        var next = active ? old | state : old & ~state;
        if (next == old)
            return false;

        _interactionState = next; // commit BEFORE notification — observers read the new mask (N136)
        OnInteractionStateChangedCore(old, next);
        UIApplication.Current?.InteractionStates.OnStateCommitted(this, old, next);
        return true;
    }

    /// <summary>
    /// Called after the interaction-state mask is committed, before observer delivery (N136). The
    /// control-author seam for read-only mirrors that must track a bit the framework can flip
    /// window-wide (a <c>ButtonBase</c>'s <c>IsPressed</c> after a pressed-holder clear, CD24).
    /// </summary>
    private protected virtual void OnInteractionStateChangedCore(InteractionState oldState, InteractionState newState)
    {
    }

    /// <summary>Marks this element as recorded in the open batch. Returns false when already recorded.</summary>
    internal bool EnterInteractionBatch()
    {
        if (_inInteractionBatch)
            return false;

        _inInteractionBatch = true;
        return true;
    }

    /// <summary>Clears the batch-membership flag (flush-time bookkeeping).</summary>
    internal void ExitInteractionBatch() => _inInteractionBatch = false;

    // ───────────────────────────── input bindings (doc §7.9) ─────────────────────────────

    /// <summary>
    /// The element's input bindings, swept during the <c>KeyDown</c> bubble at this node — after
    /// the <see cref="OnKeyDown"/> virtual and instance handlers, only while unhandled (doc §7.5
    /// step 4). The collection is <b>ordered</b>: ordering is the priority mechanism (the first
    /// matching gesture whose command can execute wins). Lazily allocated on first access.
    /// </summary>
    public InputBindingCollection InputBindings
    {
        get
        {
            VerifyAccess();
            return _inputBindings ??= new InputBindingCollection(this);
        }
    }

    /// <summary>
    /// The per-node bindings sweep (doc §7.5 step 4): first gesture matching <paramref name="e"/>
    /// whose command's <c>CanExecute</c> is true executes and handles the event; a false
    /// <c>CanExecute</c> is skipped <b>without consuming</b> (ND15 — later bindings and later route
    /// nodes still run). Effectively-disabled nodes never execute bindings (N165).
    /// </summary>
    internal void SweepInputBindings(KeyEventArgs e)
    {
        if (_inputBindings is not { Count: > 0 } bindings || !IsEffectivelyEnabled)
            return;

        for (var i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (binding.Gesture is not { } gesture || binding.Command is not { } command || !gesture.Matches(e))
                continue;

            var parameter = binding.CommandParameter;
            if (!command.CanExecute(parameter))
                continue; // ND15: skipped without consuming

            command.Execute(parameter);
            e.Handled = true;
            return; // first match wins — later bindings never consulted (N160)
        }
    }

    // ───────────────────────────── handler store + raising ─────────────────────────────

    /// <summary>
    /// Adds an instance handler for <paramref name="routedEvent"/>. Registration is a list, not a
    /// set — the same delegate added twice is invoked twice. With
    /// <paramref name="handledEventsToo"/> the handler also runs for already-handled events (and
    /// may set <see cref="RoutedEventArgs.Handled"/> back to <see langword="false"/>).
    /// </summary>
    public void AddHandler<TArgs>(RoutedEvent<TArgs> routedEvent, EventHandler<TArgs> handler, bool handledEventsToo = false)
        where TArgs : RoutedEventArgs
    {
        ArgumentNullException.ThrowIfNull(routedEvent);
        ArgumentNullException.ThrowIfNull(handler);
        VerifyAccess();
        (_eventHandlers ??= new EventHandlerStore()).Add(routedEvent, handler, handledEventsToo);
    }

    /// <summary>Removes one registration of <paramref name="handler"/> for <paramref name="routedEvent"/> (no-op when absent).</summary>
    public void RemoveHandler<TArgs>(RoutedEvent<TArgs> routedEvent, EventHandler<TArgs> handler)
        where TArgs : RoutedEventArgs
    {
        ArgumentNullException.ThrowIfNull(routedEvent);
        ArgumentNullException.ThrowIfNull(handler);
        VerifyAccess();
        _eventHandlers?.Remove(routedEvent, handler);
    }

    /// <summary>
    /// Builds and walks the route for <paramref name="args"/> per its event's strategy, with this
    /// element as the target. Caller-constructed args are caller-owned and remain valid after the
    /// call; a <see cref="RentEvent{TArgs}"/> rental is returned to its pool when the raise
    /// completes (one rental → exactly one raise). Nested raises from handlers are legal.
    /// </summary>
    public void RaiseEvent(RoutedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        VerifyAccess();

        var routedEvent = args.RoutedEvent
            ?? throw new InvalidOperationException("RoutedEventArgs.RoutedEvent must be set before raising (use the (routedEvent, source) constructor or RentEvent).");
        if (!routedEvent.ArgsType.IsInstanceOfType(args))
        {
            throw new ArgumentException(
                $"Args type mismatch: event '{routedEvent.Name}' dispatches {routedEvent.ArgsType.Name}, got {args.GetType().Name}.",
                nameof(args));
        }

        args.InitializeSourceIfUnset(this);
        try
        {
            EventRouting.Raise(this, args);
        }
        finally
        {
            if (args.ReturnOnRaiseCompletion)
                args.ReturnToPool();
        }
    }

    /// <summary>
    /// Rents a pooled args instance for a control-author raise (design doc §7.2). The rental must
    /// be passed to exactly one <see cref="RaiseEvent"/>, which returns it to the pool on
    /// completion; raising it twice throws in DEBUG builds. Handlers must copy values out rather
    /// than retain the instance.
    /// </summary>
    protected TArgs RentEvent<TArgs>(RoutedEvent<TArgs> routedEvent)
        where TArgs : RoutedEventArgs, new()
    {
        ArgumentNullException.ThrowIfNull(routedEvent);
        VerifyAccess();
        var args = EventArgsPool<TArgs>.Rent();
        args.PrepareRental(routedEvent);
        return args;
    }

    /// <summary>Invokes this element's instance handlers for one route node (route-engine internal).</summary>
    internal void InvokeInstanceHandlers(RoutedEvent routedEvent, RoutedEventArgs args)
        => _eventHandlers?.Invoke(this, routedEvent, args);

    /// <summary>The lazily allocated handler store, exposed for the matrix's lazy-allocation row (N2).</summary>
    internal EventHandlerStore? EventHandlerStoreForDebug => _eventHandlers;

    // ───────────────────────────── focus / capture entry points ─────────────────────────────

    /// <summary>
    /// Attempts to move keyboard focus to this element (validation: attached, focusable,
    /// effectively enabled, effectively visible — no ancestor fallback). Returns
    /// <see langword="false"/> outside a running application.
    /// </summary>
    /// <param name="method">How focus is arriving (feeds the <c>:focus-visible</c> policy — doc §7.7).</param>
    public bool Focus(FocusNavigationMethod method = FocusNavigationMethod.Programmatic)
        => UIApplication.Current?.FocusManager.SetFocus(this, method) ?? false;

    /// <summary>
    /// Requests mouse capture in <paramref name="mode"/> (routing policy, not OS capture — doc §7.6;
    /// default <see cref="CaptureMode.Element"/>). Granted only to attached, effectively visible
    /// elements. THE capture surface — there is no separate capture interface.
    /// </summary>
    public bool CaptureMouse(CaptureMode mode = CaptureMode.Element)
        => UIApplication.Current?.InputDispatcher.CaptureMouse(this, mode) ?? false;

    /// <summary>Releases mouse capture when this element holds it (only the holder releases).</summary>
    public void ReleaseMouseCapture()
        => UIApplication.Current?.InputDispatcher.ReleaseMouseCapture(this);

    // ───────────────────────────── the class-handler stage (ND1) ─────────────────────────────
    // The On* virtuals ARE the class-handler stage: invoked at every route node before that node's
    // instance handlers, skipped once Handled. There is no open class-handler registry in v1.
    // The CLR events add or remove routed event handlers to the element's registration list.

    /// <inheritdoc cref="PreviewKeyDownEvent"/>
    public event EventHandler<KeyEventArgs> PreviewKeyDown
    {
        add => AddHandler(PreviewKeyDownEvent, value);
        remove => RemoveHandler(PreviewKeyDownEvent, value);
    }

    /// <inheritdoc cref="KeyDownEvent"/>
    public event EventHandler<KeyEventArgs> KeyDown
    {
        add => AddHandler(KeyDownEvent, value);
        remove => RemoveHandler(KeyDownEvent, value);
    }

    /// <inheritdoc cref="PreviewKeyUpEvent"/>
    public event EventHandler<KeyEventArgs> PreviewKeyUp
    {
        add => AddHandler(PreviewKeyUpEvent, value);
        remove => RemoveHandler(PreviewKeyUpEvent, value);
    }

    /// <inheritdoc cref="KeyUpEvent"/>
    public event EventHandler<KeyEventArgs> KeyUp
    {
        add => AddHandler(KeyUpEvent, value);
        remove => RemoveHandler(KeyUpEvent, value);
    }

    /// <inheritdoc cref="PreviewTextInputEvent"/>
    public event EventHandler<TextInputEventArgs> PreviewTextInput
    {
        add => AddHandler(PreviewTextInputEvent, value);
        remove => RemoveHandler(PreviewTextInputEvent, value);
    }

    /// <inheritdoc cref="TextInputEvent"/>
    public event EventHandler<TextInputEventArgs> TextInput
    {
        add => AddHandler(TextInputEvent, value);
        remove => RemoveHandler(TextInputEvent, value);
    }

    /// <inheritdoc cref="PreviewMouseDownEvent"/>
    public event EventHandler<MouseButtonEventArgs> PreviewMouseDown
    {
        add => AddHandler(PreviewMouseDownEvent, value);
        remove => RemoveHandler(PreviewMouseDownEvent, value);
    }

    /// <inheritdoc cref="MouseDownEvent"/>
    public event EventHandler<MouseButtonEventArgs> MouseDown
    {
        add => AddHandler(MouseDownEvent, value);
        remove => RemoveHandler(MouseDownEvent, value);
    }

    /// <inheritdoc cref="PreviewMouseUpEvent"/>
    public event EventHandler<MouseButtonEventArgs> PreviewMouseUp
    {
        add => AddHandler(PreviewMouseUpEvent, value);
        remove => RemoveHandler(PreviewMouseUpEvent, value);
    }

    /// <inheritdoc cref="MouseUpEvent"/>
    public event EventHandler<MouseButtonEventArgs> MouseUp
    {
        add => AddHandler(MouseUpEvent, value);
        remove => RemoveHandler(MouseUpEvent, value);
    }

    /// <inheritdoc cref="PreviewMouseMoveEvent"/>
    public event EventHandler<MouseEventArgs> PreviewMouseMove
    {
        add => AddHandler(PreviewMouseMoveEvent, value);
        remove => RemoveHandler(PreviewMouseMoveEvent, value);
    }

    /// <inheritdoc cref="MouseMoveEvent"/>
    public event EventHandler<MouseEventArgs> MouseMove
    {
        add => AddHandler(MouseMoveEvent, value);
        remove => RemoveHandler(MouseMoveEvent, value);
    }

    /// <inheritdoc cref="PreviewMouseWheelEvent"/>
    public event EventHandler<MouseWheelEventArgs> PreviewMouseWheel
    {
        add => AddHandler(PreviewMouseWheelEvent, value);
        remove => RemoveHandler(PreviewMouseWheelEvent, value);
    }

    /// <inheritdoc cref="MouseWheelEvent"/>
    public event EventHandler<MouseWheelEventArgs> MouseWheel
    {
        add => AddHandler(MouseWheelEvent, value);
        remove => RemoveHandler(MouseWheelEvent, value);
    }

    /// <inheritdoc cref="MouseEnterEvent"/>
    public event EventHandler<MouseEventArgs> MouseEnter
    {
        add => AddHandler(MouseEnterEvent, value);
        remove => RemoveHandler(MouseEnterEvent, value);
    }

    /// <inheritdoc cref="MouseLeaveEvent"/>
    public event EventHandler<MouseEventArgs> MouseLeave
    {
        add => AddHandler(MouseLeaveEvent, value);
        remove => RemoveHandler(MouseLeaveEvent, value);
    }

    /// <inheritdoc cref="GotFocusEvent"/>
    public event EventHandler<FocusChangedEventArgs> GotFocus
    {
        add => AddHandler(GotFocusEvent, value);
        remove => RemoveHandler(GotFocusEvent, value);
    }

    /// <inheritdoc cref="LostFocusEvent"/>
    public event EventHandler<FocusChangedEventArgs> LostFocus
    {
        add => AddHandler(LostFocusEvent, value);
        remove => RemoveHandler(LostFocusEvent, value);
    }

    /// <inheritdoc cref="LostMouseCaptureEvent"/>
    public event EventHandler<RoutedEventArgs> LostMouseCapture
    {
        add => AddHandler(LostMouseCaptureEvent, value);
        remove => RemoveHandler(LostMouseCaptureEvent, value);
    }

    /// <inheritdoc cref="QueryCursorEvent"/>
    public event EventHandler<QueryCursorEventArgs> QueryCursor
    {
        add => AddHandler(QueryCursorEvent, value);
        remove => RemoveHandler(QueryCursorEvent, value);
    }

    /// <summary>Class stage for <see cref="PreviewKeyDownEvent"/> at every tunnel node.</summary>
    protected virtual void OnPreviewKeyDown(KeyEventArgs e) {}

    /// <summary>Class stage for <see cref="KeyDownEvent"/> at every bubble node. Controls activate on Down (doc §13.2).</summary>
    protected virtual void OnKeyDown(KeyEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewKeyUpEvent"/>.</summary>
    protected virtual void OnPreviewKeyUp(KeyEventArgs e) {}

    /// <summary>Class stage for <see cref="KeyUpEvent"/>. Framework code never activates on key-up.</summary>
    protected virtual void OnKeyUp(KeyEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewTextInputEvent"/>.</summary>
    protected virtual void OnPreviewTextInput(TextInputEventArgs e) {}

    /// <summary>Class stage for <see cref="TextInputEvent"/>.</summary>
    protected virtual void OnTextInput(TextInputEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewMouseDownEvent"/>.</summary>
    protected virtual void OnPreviewMouseDown(MouseButtonEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseDownEvent"/> (double-click logic belongs here — counts ride MouseDown).</summary>
    protected virtual void OnMouseDown(MouseButtonEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewMouseUpEvent"/>.</summary>
    protected virtual void OnPreviewMouseUp(MouseButtonEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseUpEvent"/>.</summary>
    protected virtual void OnMouseUp(MouseButtonEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewMouseMoveEvent"/> (any-event motion — keep it allocation-free).</summary>
    protected virtual void OnPreviewMouseMove(MouseEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseMoveEvent"/>.</summary>
    protected virtual void OnMouseMove(MouseEventArgs e) {}

    /// <summary>Class stage for <see cref="PreviewMouseWheelEvent"/>.</summary>
    protected virtual void OnPreviewMouseWheel(MouseWheelEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseWheelEvent"/>.</summary>
    protected virtual void OnMouseWheel(MouseWheelEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseEnterEvent"/> (Direct).</summary>
    protected virtual void OnMouseEnter(MouseEventArgs e) {}

    /// <summary>Class stage for <see cref="MouseLeaveEvent"/> (Direct).</summary>
    protected virtual void OnMouseLeave(MouseEventArgs e) {}

    /// <summary>Class stage for <see cref="GotFocusEvent"/>.</summary>
    protected virtual void OnGotFocus(FocusChangedEventArgs e) {}

    /// <summary>Class stage for <see cref="LostFocusEvent"/>.</summary>
    protected virtual void OnLostFocus(FocusChangedEventArgs e) {}

    /// <summary>Class stage for <see cref="LostMouseCaptureEvent"/> (Direct) — release pressed visuals here.</summary>
    protected virtual void OnLostMouseCapture(RoutedEventArgs e) {}

    /// <summary>
    /// Class stage for <see cref="QueryCursorEvent"/> (Bubble; design doc §7.6, WPF parity). Contributes this
    /// element's <see cref="Cursor"/> to the resolution: it claims the cursor when it has one and either the
    /// route hasn't settled yet (no nearer element set it) or this element <see cref="ForceCursor"/>s — so the
    /// deepest cursor wins unless an ancestor forces its own. Override to compute a cursor dynamically.
    /// </summary>
    protected virtual void OnQueryCursor(QueryCursorEventArgs e)
    {
        if (Cursor is { } cursor && (!e.Handled || ForceCursor))
        {
            e.Cursor = cursor;
            e.Handled = true;
        }
    }

    private static RoutedEvent<TArgs> RegisterClassEvent<TArgs>(
        string name,
        RoutingStrategy strategy,
        Action<UIElement, TArgs> classStage,
        bool sweepsInputBindings = false,
        bool classStageHandledEventsToo = false,
        bool surfaceScoped = false)
        where TArgs : RoutedEventArgs
    {
        var routedEvent = RoutedEvent<TArgs>.Register(name, strategy, typeof(UIElement));
        routedEvent.ClassStage = classStage;
        routedEvent.SweepsInputBindings = sweepsInputBindings;
        routedEvent.ClassStageHandledEventsToo = classStageHandledEventsToo;
        routedEvent.SurfaceScoped = surfaceScoped;
        return routedEvent;
    }

    /// <summary>
    /// The ND28 fan-in: after an enabled/visibility producer's cascade settles, repair focus when
    /// the focused element is no longer a valid target (disabled or hidden <b>in place</b> — a
    /// disabled element must never keep receiving key routes; WPF oracle). A cheap no-op while
    /// focus is empty or still valid. <c>Focusable</c> flips are deliberately not watched at P2
    /// (no property hook; recorded deferral — ND28).
    /// </summary>
    private static void RepairFocusAfterStateInvalidation()
        => UIApplication.Current?.FocusManager.RepairFocusIfInvalid();

    /// <summary>
    /// S3 detach fan-in (doc §7.10): capture force-release, hover truncation, pressed-holder
    /// removal, focus repair + scope-memory hygiene (skipping the doomed
    /// <paramref name="detachingRoot"/> subtree — ND30), and the access-key backstop unregister —
    /// app-resolved per element.
    /// </summary>
    private static void NotifyInputServicesDetached(UIElement element, UIElement detachingRoot)
    {
        if (UIApplication.Current is { } app)
        {
            app.InputDispatcher.OnElementDetached(element);
            app.FocusManager.OnElementDetached(element, detachingRoot);
            app.AccessKeys.OnElementDetached(element);
        }
    }
}
