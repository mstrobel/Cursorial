using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Terminal;

// ReSharper disable RedundantTypeArgumentsOfMethod

namespace Cursorial.UI.Input;

/// <summary>
/// S3's dispatch pipeline (design doc §7.4) — <b>the</b> entry point from S6's input drain.
/// <see cref="ProcessEvent"/> classifies one device event and routes it through the UI event
/// vocabulary, returning the three-state <see cref="InputDispatchResult"/> S6's default gestures
/// key on (<see cref="InputDispatchResult.DispatchedHandled"/> suppresses them).
/// </summary>
/// <remarks>
/// <para>
/// The full P2 pipeline: classification; the key dispatch order — access-key pre-stage (Alt
/// brackets, stale-Alt inference, sticky-cue Esc), tunnel/bubble with the per-node
/// <c>InputBindings</c> sweep, then the unhandled tails (access keys → navigation → TextInput
/// synthesis); mouse dispatch delegated to <c>RenderTree.HitTest</c> through the
/// <see cref="IWindowTopology"/> seam with the two-phase hover diff, capture, and the
/// per-rendered-frame <see cref="UpdateHover"/>; the terminal-focus cluster (ND24); and the
/// pressed-holder set (C8).
/// </para>
/// <para>
/// UI-thread only; everything is synchronous inside frame N's input drain (invariant 1). Handler
/// exceptions propagate to S6's funnel — pooled resources release along the unwind.
/// </para>
/// </remarks>
public sealed class InputDispatcher : IInputDispatchTarget
{
    /// <summary>Modifiers that suppress TextInput synthesis (doc §7.5 step 7 — Shift is allowed; Alt is reserved for access keys).</summary>
    private const KeyModifiers TextInputChordMask =
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super | KeyModifiers.Hyper | KeyModifiers.Meta;

    private readonly UIDispatcher _dispatcher;
    private readonly FocusManager _focus;
    private readonly AccessKeyManager _accessKeys;
    private readonly InteractionStateService _interactions;
    private IWindowTopology _topology;
    private TerminalCapabilities _capabilities = TerminalCapabilities.None;
    private UIElement? _captureTarget;

    // The pressed-holder set (ND12, styling contract C8): identity-keyed, entered/left through the
    // SetInteractionState(Pressed, …) fan-in — live membership, never deferred to a batch flush —
    // so the terminal-focus-out cluster can clear Pressed window-wide, including keyboard-held
    // press visuals that never took capture.
    private readonly HashSet<UIElement> _pressedHolders = [];
    private readonly List<UIElement> _pressedScratch = [];
    private CellPosition? _lastPointerPosition;
    private MouseButtons _buttonsHeld;
    private InputModality _lastModality = InputModality.Keyboard; // ND8: keyboard-first device
    private bool _inProcessEvent;

    // The hover state (doc §7.6): the retained pooled chain (root-first), the rebuild scratch, the
    // two pooled diff snapshots, and the retained device record that drives per-frame UpdateHover
    // re-diffs (immutable, always retainable; null until the first real mouse event and after a
    // terminal focus-out — a stale position from another window must not silently re-hover).
    private readonly HoverChainSnapshot _hoverRemoved = new();
    private readonly HoverChainSnapshot _hoverAdded = new();
    private UIElement[] _hoverChain = new UIElement[8];
    private UIElement[] _hoverScratch = new UIElement[8];
    private int _hoverCount;
    private MouseEvent? _lastMouseEvent;
    private bool _inHoverDiff; // the N206 guard (thrown in DEBUG only; the flag is kept in all builds)

    // The §7.6 pointer-shape state: the capability gate (OutputProtocolCapabilities.MouseCursorShape
    // — when off, resolution never runs: no emission, no tracking cost), the last shape pushed
    // through the S6 emission seam (null = the terminal default), and the seam itself (S6 wires it
    // to QueueControlSequence; emission is equality-gated here so the seam fires only on change).
    private bool _trackCursorShape;
    private MouseCursorShape? _effectiveCursorShape;

    /// <summary>S6's pointer-shape emission seam (doc §7.6): invoked only on effective-shape change; null = terminal default.</summary>
    internal Action<MouseCursorShape?>? CursorShapeChangedInternal;

    internal InputDispatcher(
        UIDispatcher dispatcher,
        FocusManager focus,
        AccessKeyManager accessKeys,
        InteractionStateService interactions,
        IWindowTopology topology)
    {
        _dispatcher = dispatcher;
        _focus = focus;
        _accessKeys = accessKeys;
        _interactions = interactions;
        _topology = topology;
    }

    /// <summary>
    /// Swaps the window-topology seam (matrix ND5): the P2 <see cref="SingleRootWindowTopology"/> is replaced
    /// by S4's <c>WindowManager</c> once it is composed (P7-W3), with no other dispatcher change.
    /// </summary>
    internal void SetWindowTopology(IWindowTopology topology) => _topology = topology;

    /// <summary>The element holding mouse capture, or null (capture is routing policy, not OS capture).</summary>
    public UIElement? MouseCaptureTarget => _captureTarget;

    /// <summary>The last observed pointer position in terminal cells; null until the first real mouse event.</summary>
    public CellPosition? LastPointerPosition => _lastPointerPosition;

    /// <summary>The dispatcher's held-button bookkeeping (mirrors the device mask across down/up).</summary>
    public MouseButtons ButtonsHeld => _buttonsHeld;

    /// <summary>The last input modality — feeds <c>:focus-visible</c>. Starts as <see cref="InputModality.Keyboard"/> (ND8).</summary>
    public InputModality LastModality => _lastModality;

    /// <summary>
    /// Raised when the terminal window gains/loses keyboard focus (from <c>FocusEvent</c>). A
    /// terminal focus change is <b>not</b> an element focus change — keyboard focus is retained.
    /// </summary>
    public event Action<bool>? TerminalFocusChanged;

    /// <summary>
    /// Raised on terminal focus-out with the still-focused element, <b>instead of</b> any
    /// <c>LostFocus</c> (focus is retained — doc §13.2): S2 flushes
    /// <c>UpdateSourceTrigger.LostFocus</c> edits on this.
    /// </summary>
    public event Action<UIElement>? EditCommitRequested;

    /// <summary>
    /// Raised once per hover-chain change, after the <c>MouseLeave</c>/<c>MouseEnter</c> raises
    /// (hover phase 2 — doc §7.4): ToolTipService's (S8) observation hook. The snapshots are
    /// pooled — copy elements out during the raise, never retain them.
    /// </summary>
    public event HoverChangedHandler? HoverChanged;

    /// <summary>The negotiated (undecorated) capability snapshot last fanned out (test observability).</summary>
    internal TerminalCapabilities NegotiatedCapabilities => _capabilities;

    /// <summary>
    /// Routes one device event (doc §7.5). Classification:
    /// <c>KeyEvent</c> → key dispatch (tunnel/bubble + the unhandled tails);
    /// <c>MouseEvent</c> → mouse dispatch (<c>Kind == Click</c> is a pipeline-contract violation —
    /// defensive no-op, ND4); <c>FocusEvent</c> → the terminal-focus cluster (ND24);
    /// <c>PasteEvent</c> → <c>TextInput { FromPaste = true }</c>;
    /// resize/device-response/unknown/pointer → <see cref="InputDispatchResult.NotUIInput"/> for
    /// S6 to route onward. Never re-entrant (DEBUG-asserted); nested <c>RaiseEvent</c> is legal.
    /// </summary>
    public InputDispatchResult ProcessEvent(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);

        _dispatcher.VerifyAccess();
        ThrowIfReentrant();
        _inProcessEvent = true;

        try
        {
            return inputEvent switch
                   {
                       KeyEvent key     => ProcessKeyEvent(key),
                       MouseEvent mouse => ProcessMouseEvent(mouse),
                       FocusEvent focus => ProcessFocusEvent(focus),
                       PasteEvent paste => ProcessPasteEvent(paste),
                       _                => InputDispatchResult.NotUIInput // Resize / DeviceResponse / Unknown / Pointer
                   };
        }
        finally
        {
            _inProcessEvent = false;
        }
    }

    /// <summary>
    /// Re-runs hover diffing without pointer movement — S6 calls this once per rendered frame
    /// after layout and composite parameters are final (doc §10.5 Phase 6, ND21), so hover stays
    /// correct under layout moves, composite slides, and scrolls, and detach-deferred hover work
    /// executes here. A no-op until the first real mouse event, and after a terminal focus-out
    /// (the last reported position belongs to another window until fresh motion arrives).
    /// </summary>
    public void UpdateHover()
    {
        _dispatcher.VerifyAccess();

        if (_lastMouseEvent is not {} device)
        {
            // N80: zero hit tests before the first real mouse event — but the §7.6 pointer-shape
            // resolution still re-runs per frame (a programmatic capture, or a Cursor property
            // change on the capture target, has no hover driver to ride).
            UpdateEffectiveCursorShape();
            return;
        }

        UpdateHoverChain(HitTestForEvent(device), device);
    }

    /// <summary>
    /// The capability fan-out leg (startup + after every renegotiation). Receives the
    /// <b>negotiated, undecorated</b> snapshot (ND23 — a decorator-claimed capability must never
    /// reach the access-key gate). The <c>AccessKeyManager</c> has its <b>own</b> explicit fan-out
    /// call (doc §10.5 — the four ordered calls); this one only records the dispatcher's view.
    /// A renegotiation that <b>loses</b> motion reporting clears the live hover chain through the
    /// ordinary diff and forgets the hover driver (N205) — capability honesty cuts both ways:
    /// <c>PointerOver</c> set under the old gate must not outlive it.
    /// </summary>
    public void OnCapabilitiesChanged(TerminalCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        _dispatcher.VerifyAccess();

        // §7.6 — forget the tracked pointer shape SILENTLY (and park the gate so the hover-clear
        // below cannot emit mid-method): across (re)negotiation the wire state is S6's to
        // re-baseline (it writes the OSC 22 reset on the renegotiate path). The re-resolution at
        // the end re-emits an active shape under the NEW gate — at most one emission per call.
        _effectiveCursorShape = null;
        _trackCursorShape = false;

        if (_capabilities.Input.Mouse.Motion && !capabilities.Input.Mouse.Motion)
        {
            // Clear under the OLD gate (the new one would short-circuit the diff), then forget the
            // driver so per-frame UpdateHover re-hovers nothing until fresh motion arrives.
            if (_lastMouseEvent is {} device)
                UpdateHoverChain(hit: null, device);

            _lastMouseEvent = null;
        }

        _capabilities = capabilities;
        _trackCursorShape = capabilities.Output.Protocol.MouseCursorShape;
        UpdateEffectiveCursorShape();
    }

    /// <summary>
    /// The S4 surface-change seam (doc §7.4/§7.11, ND5): called synchronously on every surface
    /// open / close / modal / z change — even mid-drain — to re-validate mouse capture, with a
    /// force-release (Direct <c>LostMouseCapture</c>) when the holder is no longer valid. P2
    /// semantics (N207): the holder must be attached and effectively visible; the surface-stack /
    /// modal-block validation joins at P7 with S4's window manager. The hover refresh is
    /// deliberately deferred — it rides the per-frame <see cref="UpdateHover"/> re-diff (ND21),
    /// which already observes the changed topology.
    /// </summary>
    public void OnSurfacesChanged()
    {
        _dispatcher.VerifyAccess();

        if (_captureTarget is {} holder && !(holder.IsAttachedToTree && holder.IsEffectivelyVisible))
        {
            ForceReleaseCapture();
            UpdateEffectiveCursorShape(); // §7.6 — back to the hover chain's resolution (or the default)
        }
    }

    /// <summary>
    /// The public position query (doc §7.4, N204): the element a mouse event at
    /// <paramref name="position"/> (terminal cells) would dispatch to — surface gating through
    /// the <see cref="IWindowTopology"/> seam, one <c>RenderTree.HitTest</c> descent, and the
    /// ND7 disabled hop — or <see langword="null"/> on a miss (out of viewport, no shown
    /// surface, no enabled ancestor). Pure: no routing, no hover change, no bookkeeping update
    /// (<see cref="LastPointerPosition"/> is untouched). This is S8's ToolTipService and the
    /// cursor-shape resolution seam; the surface out-parameter joins at P7 with S4's
    /// <c>TopLevelSurface</c> type (ND5).
    /// </summary>
    public UIElement? HitTest(CellPosition position)
    {
        _dispatcher.VerifyAccess();

        // A Move-shaped probe: the kind the P7 window manager never swallows (light dismiss and
        // activation are press-driven). Queries are off the hot path — the rental is fine.
        return HitTestForEvent(new MouseEvent
        {
            Kind = MouseEventKind.Move,
            Position = position,
            Button = MouseButton.None,
            ButtonsHeld = MouseButtons.None,
            Modifiers = KeyModifiers.None,
            Timestamp = DateTimeOffset.MinValue,
        });
    }

    /// <summary>
    /// Grants mouse capture to <paramref name="element"/> (attached + effectively visible only).
    /// An existing holder is force-released first (Direct <c>LostMouseCapture</c>).
    /// </summary>
    /// <returns>Whether capture is held by <paramref name="element"/> when the call returns.</returns>
    public bool CaptureMouse(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        _dispatcher.VerifyAccess();

        if (!element.IsAttachedToTree || !element.IsEffectivelyVisible)
            return false;

        if (ReferenceEquals(_captureTarget, element))
            return true;

        ForceReleaseCapture(); // transfer notifies the old holder
        _captureTarget = element;
        UpdateEffectiveCursorShape(); // §7.6 — the capture target's resolved cursor wins immediately
        return true;
    }

    /// <summary>Releases capture when <paramref name="element"/> holds it (only the holder releases; otherwise a no-op).</summary>
    public void ReleaseMouseCapture(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        _dispatcher.VerifyAccess();

        if (ReferenceEquals(_captureTarget, element))
        {
            ForceReleaseCapture();
            UpdateEffectiveCursorShape(); // §7.6 — back to the hover chain's resolution (or the default)
        }
    }

    /// <summary>S3's seam contract: <see cref="ProcessEvent"/> IS the Dispatch implementation (doc §10.9).</summary>
    InputDispatchResult IInputDispatchTarget.Dispatch(InputEvent inputEvent) => ProcessEvent(inputEvent);

    /// <summary>
    /// Detach hygiene fan-in (doc §7.10) — force-releases capture held by the detached element,
    /// truncates the hover chain at it (silent state clear; the per-frame
    /// <see cref="UpdateHover"/> re-diff against the live tree is the deferred refresh), and drops
    /// it from the pressed-holder set (ND12 — the Pressed bit clears with it).
    /// </summary>
    internal void OnElementDetached(UIElement element)
    {
        var releasedCapture = ReferenceEquals(_captureTarget, element);

        if (releasedCapture)
            ForceReleaseCapture();

        TruncateHoverChain(element);

        if (releasedCapture)
            UpdateEffectiveCursorShape(); // §7.6 — after the truncate, so the detached holder can't resolve

        if (_pressedHolders.Contains(element))
            element.SetInteractionStateInternal(InteractionState.Pressed, false); // fan-in removes it from the set
    }

    // ───────────────────────────── classification legs ─────────────────────────────

    private InputDispatchResult ProcessKeyEvent(KeyEvent key)
    {
        _lastModality = InputModality.Keyboard;

        var isDown = key.Kind == KeyEventKind.Down;

        // Step 1 — pre-stage (doc §7.5; skips Synthesized events — a stray KeyReleaseSynthesizer
        // must not corrupt the Alt bracket): Alt-side bracketing on both Down and Up; stale-Alt
        // inference + the sticky-cue Esc consume on Down only (KeyUp runs 1a alone). The
        // activation window is sampled FIRST — the stale inference may close the bracket on this
        // very key, which must still activate if the user typed it under visible cues (N182).
        var cueWindowWasOpen = _accessKeys.IsUnmodifiedActivationWindowOpen;
        if (!key.Synthesized)
        {
            if (key.Key is Key.LeftAlt or Key.RightAlt)
            {
                if (isDown)
                    _accessKeys.OnAltDown(key.Key, key.IsRepeat); // ND32: a repeat never re-arms the tap
                else
                    _accessKeys.OnAltUp(key.Key);
                // …and the event still routes normally below (N45).
            }
            else if (isDown && _accessKeys.OnPreStageKeyDown(key))
            {
                return InputDispatchResult.DispatchedHandled; // sticky-cue Esc consumed (N177)
            }
        }

        // Step 2 — target. Both null ⇒ dropped (DispatchedUnhandled, empty route; never "topmost").
        var target = _focus.FocusedElement ?? _focus.ActiveRoot;
        if (target is null)
            return InputDispatchResult.DispatchedUnhandled;

        RoutedEvent tunnelEvent = isDown ? UIElement.PreviewKeyDownEvent : UIElement.PreviewKeyUpEvent;
        RoutedEvent bubbleEvent = isDown ? UIElement.KeyDownEvent : UIElement.KeyUpEvent;

        // Steps 3–4 — tunnel + bubble over one route with one pooled args.
        var args = EventArgsPool<KeyEventArgs>.Rent();

        args.Initialize(tunnelEvent, target);
        args.InitializeKey(key);

        bool handled;

        try
        {
            EventRouting.RaisePair(target, tunnelEvent, bubbleEvent, args);
            handled = args.Handled;
        }
        finally
        {
            args.ReturnToPool();
        }

        // KeyUp is route-only (ND9): no tails of any kind ever run on Up.
        if (isDown && !handled)
        {
            // Step 5 — access keys (incl. F10 menu-mode entry); runs before navigation and
            // before TextInput (N48).
            handled = _accessKeys.ProcessKeyDown(key, cueWindowWasOpen);

            // Step 6 — Tab/directional navigation: marks handled only when focus actually moved.
            if (!handled)
                handled = TryNavigate(key);

            // Step 7 — TextInput synthesis: unhandled printable Down, Shift-only modifiers.
            if (!handled && key is { Key: Key.Character, Text.Length: > 0 } && (key.Modifiers & TextInputChordMask) == KeyModifiers.None)
                handled = RaiseTextInput(key.Text, fromPaste: false);
        }

        return handled ? InputDispatchResult.DispatchedHandled : InputDispatchResult.DispatchedUnhandled;
    }

    private InputDispatchResult ProcessMouseEvent(MouseEvent mouse)
    {
        if (mouse.Kind == MouseEventKind.Click)
        {
            // ND4: the mandated pipeline (SynthesizeClickEvents = false) never produces Click.
            // Defensive no-op — zero routing, zero state change, DEBUG diagnostic.
            InputDiagnostics.EmitClickContractViolation();
            return InputDispatchResult.DispatchedUnhandled;
        }

        _lastModality = InputModality.Pointer;
        _lastPointerPosition = mouse.Position;

        _buttonsHeld = mouse.Kind switch
                       {
                           MouseEventKind.ButtonDown => _buttonsHeld | ToButtonFlag(mouse.Button),
                           MouseEventKind.ButtonUp   => _buttonsHeld & ~ToButtonFlag(mouse.Button),
                           _                         => mouse.ButtonsHeld // Drag/Move/Wheel carry the interpreter-accumulated mask
                       };

        _lastMouseEvent = mouse; // the per-frame UpdateHover driver (immutable record, retainable)

        switch (mouse.Kind)
        {
            case MouseEventKind.Move:
            case MouseEventKind.Drag:
            {
                // ONE hit test per event (doc §13.2 — S3 re-derives nothing): hover and routing
                // share it. Hit testing runs even under capture so :pointerover stays honest
                // (doc §7.6); the route target is capture-first.
                var hit = HitTestForEvent(mouse);

                UpdateHoverChain(hit, mouse);

                return (_captureTarget ?? hit) is {} target
                    ? ToResult(RaiseMousePair<MouseEventArgs>(UIElement.PreviewMouseMoveEvent, UIElement.MouseMoveEvent, target, mouse))
                    : InputDispatchResult.DispatchedUnhandled;
            }

            case MouseEventKind.ButtonDown:
            case MouseEventKind.ButtonUp:
            {
                var isDown = mouse.Kind == MouseEventKind.ButtonDown;

                return (_captureTarget ?? HitTestForEvent(mouse)) is {} target
                    ? ToResult(RaiseMousePair<MouseButtonEventArgs>(
                        isDown ? UIElement.PreviewMouseDownEvent : UIElement.PreviewMouseUpEvent,
                        isDown ? UIElement.MouseDownEvent : UIElement.MouseUpEvent,
                        target,
                        mouse))
                    : InputDispatchResult.DispatchedUnhandled;
            }

            case MouseEventKind.Wheel:
                // The one mouse event capture never redirects (doc §7.6): wheel targets the HIT
                // element under the pointer, not the capture holder and not focus.
                return HitTestForEvent(mouse) is {} wheelTarget
                    ? ToResult(RaiseMousePair<MouseWheelEventArgs>(UIElement.PreviewMouseWheelEvent, UIElement.MouseWheelEvent, wheelTarget, mouse))
                    : InputDispatchResult.DispatchedUnhandled;

            default:
                return InputDispatchResult.DispatchedUnhandled;
        }
    }

    /// <summary>
    /// The unhandled navigation tail (doc §7.5 step 6): Tab/Shift+Tab walk the tab order (the
    /// Shift form also covers the wire's BackTab — <c>CSI Z</c> decodes as Tab+Shift); chordless
    /// arrows engage directional navigation, which is opt-in per container — elsewhere they fall
    /// through unhandled so controls keep their arrows.
    /// </summary>
    private bool TryNavigate(KeyEvent key)
        => key switch
           {
               { Key: Key.Tab, Modifiers: KeyModifiers.None }        => _focus.MoveFocus(FocusNavigationDirection.Next),
               { Key: Key.Tab, Modifiers: KeyModifiers.Shift }       => _focus.MoveFocus(FocusNavigationDirection.Previous),
               { Key: Key.UpArrow, Modifiers: KeyModifiers.None }    => _focus.MoveFocus(FocusNavigationDirection.Up),
               { Key: Key.DownArrow, Modifiers: KeyModifiers.None }  => _focus.MoveFocus(FocusNavigationDirection.Down),
               { Key: Key.LeftArrow, Modifiers: KeyModifiers.None }  => _focus.MoveFocus(FocusNavigationDirection.Left),
               { Key: Key.RightArrow, Modifiers: KeyModifiers.None } => _focus.MoveFocus(FocusNavigationDirection.Right),
               _                                                     => false
           };

    private static InputDispatchResult ToResult(bool handled)
        => handled ? InputDispatchResult.DispatchedHandled : InputDispatchResult.DispatchedUnhandled;

    /// <summary>
    /// The surface-level scan + intra-surface descent for one mouse event (doc §7.6): the
    /// <see cref="IWindowTopology"/> seam gates and picks the surface, S1's
    /// <c>RenderTree.HitTest</c> performs the one descent (composite order, boundary clips,
    /// <c>IsHitTestVisible</c>, <c>HitTestCore</c>), and ND7's disabled gating resolves the
    /// dispatch/hover target — disabled elements are hit-opaque but event-transparent, so the
    /// target is the deepest hit's nearest effectively-enabled self-or-ancestor. Null = miss
    /// (out of viewport, no shown surface, swallowed, or no enabled ancestor) — route nowhere,
    /// never throw (ND5/ND6).
    /// </summary>
    private UIElement? HitTestForEvent(MouseEvent mouse)
    {
        if (!_topology.FilterMouseEvent(mouse, out var surfaceRoot, out var column, out var row))
            return null; // swallowed by the window manager (P7 policy; never at P2)

        if (surfaceRoot?.RenderTreeHost is not {} tree)
            return null; // no surface under the point (ND5)

        var hit = tree.HitTest(column, row);

        while (hit is not null && !hit.IsEffectivelyEnabled)
            hit = hit.VisualParent ?? hit.LogicalParent; // ND7 — same logical hop as the route walk

        return hit;
    }

    /// <summary>Raises a <c>Preview*</c>/main mouse pair at <paramref name="target"/> with one pooled args.</summary>
    private static bool RaiseMousePair<TArgs>(RoutedEvent<TArgs> tunnelEvent, RoutedEvent<TArgs> bubbleEvent, UIElement target, MouseEvent device)
        where TArgs : MouseEventArgs, new()
    {
        var args = EventArgsPool<TArgs>.Rent();

        args.Initialize(tunnelEvent, target);
        args.InitializeMouse(device);

        try
        {
            EventRouting.RaisePair(target, tunnelEvent, bubbleEvent, args);
            return args.Handled;
        }
        finally
        {
            args.ReturnToPool();
        }
    }

    // ───────────────────────────── the hover chain (doc §7.6) ─────────────────────────────

    /// <summary>
    /// The two-phase hover diff. <b>Phase 1 (state):</b> build the new chain (root-first, the
    /// route walk's parent chain from the effective hit), find the common prefix with the retained
    /// chain, flip <see cref="InteractionState.PointerOver"/> off the removed suffix and onto the
    /// added suffix (equality-gated; the I3 stage wraps these writes in one
    /// <c>BeginInteractionUpdate</c> batch per ND11), snapshot both suffixes, and commit the
    /// retained chain. <b>Phase 2 (events):</b> <c>MouseLeave</c> deepest-first, <c>MouseEnter</c>
    /// outermost-first — over the snapshots, never the live chain, so a handler that detaches
    /// elements cannot mutate the iteration — then <see cref="HoverChanged"/>. Handlers observe
    /// post-restyle state. An unchanged chain is a complete no-op (N73).
    /// </summary>
    private void UpdateHoverChain(UIElement? hit, MouseEvent device)
    {
        ThrowIfHoverDiffReentrant(); // N206: a handler re-running the diff mid-diff is a programming error

        if (_capabilities.Input.Mouse.Motion) // capability-honest: PointerOver never set without real motion reporting (doc §7.6)
        {
            _inHoverDiff = true;
            try
            {
                UpdateHoverChainCore(hit, device);
            }
            finally
            {
                _inHoverDiff = false;
            }
        }

        // §7.6 — the pointer-shape resolution rides the hover machinery: re-resolve against the
        // committed (post-phase-2) chain and the capture holder. Equality-gated inside; runs even
        // without motion reporting so a capture-held cursor still resolves.
        UpdateEffectiveCursorShape();
    }

    private void UpdateHoverChainCore(UIElement? hit, MouseEvent device)
    {
        // Build the new chain leaf-first into the scratch, then reverse to root-first.
        var count = 0;

        for (var node = hit; node is not null; node = node.VisualParent ?? node.LogicalParent)
        {
            if (count == _hoverScratch.Length)
                Array.Resize(ref _hoverScratch, _hoverScratch.Length * 2);

            _hoverScratch[count++] = node;
        }

        Array.Reverse(_hoverScratch, 0, count);

        var prefix = 0;

        while (prefix < count && prefix < _hoverCount && ReferenceEquals(_hoverScratch[prefix], _hoverChain[prefix]))
            prefix++;

        if (prefix == count && prefix == _hoverCount)
        {
            Array.Clear(_hoverScratch, 0, count);
            return; // unchanged — no flips, no events, no HoverChanged (N73)
        }

        // ReSharper disable once UnusedVariable

        // PHASE 1 — state: snapshot the suffixes and flip PointerOver, removed (deepest-first)
        // then added (outermost-first), in ONE interaction batch (ND11 — one observer notification
        // per changed element), all committed before any raise (doc §7.6 two-phase; N75).
        using (var batch = _interactions.BeginUpdate())
        {
            _hoverRemoved.Begin();

            for (var i = _hoverCount - 1; i >= prefix; i--)
            {
                var element = _hoverChain[i];
                _hoverRemoved.Add(element);
                element.SetInteractionStateInternal(InteractionState.PointerOver, false);
            }

            _hoverAdded.Begin();

            for (var i = prefix; i < count; i++)
            {
                var element = _hoverScratch[i];
                _hoverAdded.Add(element);
                element.SetInteractionStateInternal(InteractionState.PointerOver, true);
            }

            // Commit the retained chain before the raises: handlers and detach truncation observe
            // the new chain, and snapshot iteration below is immune to their mutations (N82).
            if (_hoverChain.Length < count)
                Array.Resize(ref _hoverChain, _hoverScratch.Length);

            Array.Copy(_hoverScratch, _hoverChain, count);

            if (_hoverCount > count)
                Array.Clear(_hoverChain, count, _hoverCount - count);

            _hoverCount = count;
            Array.Clear(_hoverScratch, 0, count);
        }

        // PHASE 2 — events over the snapshots; the snapshots complete (stale-stamp + clear) on
        // every unwind path so a throwing handler cannot leak phase-stale state.
        try
        {
            var args = EventArgsPool<MouseEventArgs>.Rent();

            try
            {
                for (var i = 0; i < _hoverRemoved.CountUnchecked; i++)
                {
                    var element = _hoverRemoved.ItemUnchecked(i);
                    args.Initialize(UIElement.MouseLeaveEvent, element);
                    args.InitializeMouse(device);
                    EventRouting.Raise(element, args);
                }

                for (var i = 0; i < _hoverAdded.CountUnchecked; i++)
                {
                    var element = _hoverAdded.ItemUnchecked(i);
                    args.Initialize(UIElement.MouseEnterEvent, element);
                    args.InitializeMouse(device);
                    EventRouting.Raise(element, args);
                }
            }
            finally
            {
                args.ReturnToPool();
            }

            HoverChanged?.Invoke(_hoverRemoved, _hoverAdded);
        }
        finally
        {
            _hoverRemoved.Complete();
            _hoverAdded.Complete();
        }
    }

    /// <summary>
    /// Detach truncation (doc §7.10): drops <paramref name="element"/> and everything deeper (its
    /// detaching descendants) from the retained chain with a silent <c>PointerOver</c> clear — no
    /// <c>MouseLeave</c> is raised at a detached element; the per-frame <see cref="UpdateHover"/>
    /// re-diff against the live tree is the deferred refresh.
    /// </summary>
    private void TruncateHoverChain(UIElement element)
    {
        for (var i = 0; i < _hoverCount; i++)
        {
            if (!ReferenceEquals(_hoverChain[i], element))
                continue;

            for (var j = i; j < _hoverCount; j++)
            {
                _hoverChain[j].SetInteractionStateInternal(InteractionState.PointerOver, false);
                _hoverChain[j] = null!;
            }

            _hoverCount = i;
            return;
        }
    }

    // ───────────────────────────── the pointer shape (doc §7.6) ─────────────────────────────

    /// <summary>
    /// The §7.6 pointer-shape resolution: while mouse capture is held the capture target's
    /// resolved cursor wins (its self→root walk — capture redirects the pointer's meaning, so it
    /// owns the shape); otherwise the first non-null <see cref="UIElement.Cursor"/> walking the
    /// hover chain leaf→root; <see langword="null"/> = the terminal default. Equality-gated — the
    /// S6 emission seam fires only when the effective shape changes — and capability-gated on
    /// <c>OutputProtocolCapabilities.MouseCursorShape</c> (no resolution, no emission, no tracking
    /// when the terminal didn't negotiate OSC 22 — no polyfill). Allocation-free: it rides the
    /// existing hover diff / capture transitions / per-frame <see cref="UpdateHover"/>.
    /// </summary>
    private void UpdateEffectiveCursorShape()
    {
        if (!_trackCursorShape)
            return;

        var resolved = _captureTarget is {} holder
            ? ResolveCursor(holder)
            : ResolveCursorFromHoverChain();

        if (resolved == _effectiveCursorShape)
            return;

        _effectiveCursorShape = resolved;
        CursorShapeChangedInternal?.Invoke(resolved);
    }

    /// <summary>First non-null <see cref="UIElement.Cursor"/> on <paramref name="leaf"/>'s self→root chain (the route walk's parent hop).</summary>
    private static MouseCursorShape? ResolveCursor(UIElement leaf)
    {
        for (var node = (UIElement?)leaf; node is not null; node = node.VisualParent ?? node.LogicalParent)
        {
            if (node.GetValue(UIElement.CursorProperty) is {} cursor)
                return cursor;
        }

        return null;
    }

    /// <summary>First non-null <see cref="UIElement.Cursor"/> walking the retained hover chain leaf→root.</summary>
    private MouseCursorShape? ResolveCursorFromHoverChain()
    {
        for (var i = _hoverCount - 1; i >= 0; i--)
        {
            if (_hoverChain[i].GetValue(UIElement.CursorProperty) is {} cursor)
                return cursor;
        }

        return null;
    }

    /// <summary>The last shape pushed through the S6 seam (test observability); null = the terminal default.</summary>
    internal MouseCursorShape? EffectiveCursorShapeInternal => _effectiveCursorShape;

    private InputDispatchResult ProcessFocusEvent(FocusEvent focusEvent)
    {
        if (focusEvent.HasFocus)
        {
            TerminalFocusChanged?.Invoke(true);
            return InputDispatchResult.DispatchedHandled;
        }

        // The FocusEvent{HasFocus:false} cluster, in ND24 order. Keyboard focus is RETAINED
        // throughout — refocusing the terminal restores interaction state intact (doc §13.2).
        _accessKeys.OnTerminalFocusLost();                       // ① unconditional Alt/sticky/cue clears
        ForceReleaseCapture();                                   // ② capture force-release
        ClearHoverChainOnTerminalFocusLost();                    // ③ hover-chain clear
        UpdateEffectiveCursorShape();                            // §7.6 once, after ②+③ — usually a no-op
                                                                 // (③'s diff resolved when a hover driver
                                                                 // existed); covers programmatic capture
                                                                 // released with no mouse event ever seen
        ClearPressedHoldersOnTerminalFocusLost();                // ④ pressed-holder clears (C8)
        _buttonsHeld = MouseButtons.None;                        // ④ held-mask zeroed (N203 — the Ups go to another window)

        if (_focus.FocusedElement is {} focused)        // ⑤ exactly once, only with focus
            EditCommitRequested?.Invoke(focused);

        TerminalFocusChanged?.Invoke(false);                     // ⑥
        return InputDispatchResult.DispatchedHandled;
    }

    private InputDispatchResult ProcessPasteEvent(PasteEvent paste)
        => RaiseTextInput(paste.Text, fromPaste: true)
            ? InputDispatchResult.DispatchedHandled
            : InputDispatchResult.DispatchedUnhandled;

    /// <summary>Raises the <c>PreviewTextInput</c>/<c>TextInput</c> pair at the focused element (fallback: the active root).</summary>
    private bool RaiseTextInput(ReadOnlyMemory<char> text, bool fromPaste)
    {
        var target = _focus.FocusedElement ?? _focus.ActiveRoot;
        if (target is null)
            return false; // dropped (N22 parity for paste)

        var args = EventArgsPool<TextInputEventArgs>.Rent();

        args.Initialize(UIElement.PreviewTextInputEvent, target);
        args.InitializeText(text, fromPaste);

        try
        {
            EventRouting.RaisePair(target, UIElement.PreviewTextInputEvent, UIElement.TextInputEvent, args);
            return args.Handled;
        }
        finally
        {
            args.ReturnToPool();
        }
    }

    // §7.6 contract: this method does NOT re-resolve the pointer shape — each caller owns exactly
    // one UpdateEffectiveCursorShape() after its full transition settles (after the new capture
    // target installs, after the hover chain clears/truncates), so a capture transfer or focus-out
    // resolves once, never through an intermediate state with redundant OSC 22 bytes.
    private void ForceReleaseCapture()
    {
        if (_captureTarget is not {} target)
            return;

        _captureTarget = null; // cleared before the raise — handlers observe the post-release state

        var args = EventArgsPool<RoutedEventArgs>.Rent();
        args.Initialize(UIElement.LostMouseCaptureEvent, target);

        try
        {
            EventRouting.Raise(target, args);
        }
        finally
        {
            args.ReturnToPool();
        }
    }

    /// <summary>
    /// ND24 ③ — clears the hover chain (<c>MouseLeave</c> deepest-first + <c>PointerOver</c> off,
    /// via the ordinary diff against an empty chain) and forgets the hover driver: after a
    /// terminal focus-out the last reported cell belongs to another window, so the per-frame
    /// <see cref="UpdateHover"/> must not silently rebuild the chain under a stale position —
    /// hover stays empty until fresh motion arrives. <see cref="LastPointerPosition"/> (public
    /// bookkeeping) is retained.
    /// </summary>
    private void ClearHoverChainOnTerminalFocusLost()
    {
        if (_lastMouseEvent is {} device)
            UpdateHoverChain(hit: null, device);

        _lastMouseEvent = null;
    }

    /// <summary>
    /// ND24 ④ — clears <b>every</b> pressed holder in one interaction batch (C8: the set covers
    /// keyboard-held press visuals that never took capture). The flips route back through the
    /// Pressed fan-in, emptying the set; one observer notification per holder (N145).
    /// </summary>
    private void ClearPressedHoldersOnTerminalFocusLost()
    {
        if (_pressedHolders.Count == 0)
            return;

        _pressedScratch.Clear();
        _pressedScratch.AddRange(_pressedHolders); // the flips mutate the set — snapshot first

        // ReSharper disable once UnusedVariable
        using (var batch = _interactions.BeginUpdate())
        {
            for (var i = 0; i < _pressedScratch.Count; i++)
                _pressedScratch[i].SetInteractionStateInternal(InteractionState.Pressed, false);
        }

        _pressedScratch.Clear();
    }

    /// <summary>
    /// The <see cref="InteractionState.Pressed"/> fan-in (ND12): membership tracks every committed
    /// Pressed flip immediately, so the C8 clears always see an accurate set.
    /// </summary>
    internal void OnPressedChanged(UIElement element, bool pressed)
    {
        if (pressed)
            _pressedHolders.Add(element);
        else
            _pressedHolders.Remove(element);
    }

    /// <summary>Pressed-holder membership (test observability — N144).</summary>
    internal bool IsPressedHolderInternal(UIElement element) => _pressedHolders.Contains(element);

    /// <summary>Pressed-holder count (test observability).</summary>
    internal int PressedHolderCountInternal => _pressedHolders.Count;

    private static MouseButtons ToButtonFlag(MouseButton button)
        => button == MouseButton.None ? MouseButtons.None : (MouseButtons)(1u << ((int)button - 1));

    [System.Diagnostics.Conditional("DEBUG")]
    private void ThrowIfReentrant()
    {
        if (_inProcessEvent)
        {
            throw new InvalidOperationException(
                "InputDispatcher.ProcessEvent re-entered from inside a dispatch — a programming error. " +
                "S6 calls it one event at a time; handlers may use nested RaiseEvent, never ProcessEvent.");
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ThrowIfHoverDiffReentrant()
    {
        if (_inHoverDiff)
        {
            throw new InvalidOperationException(
                "The hover diff re-entered from inside a MouseEnter/MouseLeave/HoverChanged raise — a programming " +
                "error (N206): phase 2 is iterating the pooled snapshots. The per-frame UpdateHover re-diff already " +
                "covers post-handler refreshes; never call InputDispatcher.UpdateHover from a hover handler.");
        }
    }
}
