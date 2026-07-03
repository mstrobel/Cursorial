using System.Buffers;
using System.Collections.Concurrent;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;
using Cursorial.UI.Input;

// ReSharper disable CheckNamespace

namespace Cursorial.UI;

/// <summary>
/// The composition root and the only owner of "time, thread, and the byte pipe" (design doc §10):
/// acquires the terminal host, runs the one dedicated UI thread, drives the single authoritative
/// frame loop, owns the screen <see cref="CellBuffer"/> + <see cref="FrameRenderer"/>, and tears
/// the terminal down on every exit path. Construct via <see cref="CreateBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase shape.</b> S3's input router (P2) is installed — <see cref="InputDispatcher"/> is the
/// default Phase-1 dispatch seam. The styling engine (P3), animation driver (P8), and window
/// system (P7) remain documented no-op seams — the frame loop's phase slots exist and are
/// exercised, but those seam fields are null until their phases land. <see cref="RootElement"/> is the P1
/// stand-in for window content: at P7, S4's <c>Window</c>/<c>WindowManager</c> replace the
/// single-root wiring (and the doc's <c>RunAsync(Func&lt;Window&gt;)</c> shape replaces the
/// <see cref="UIElement"/>-typed overloads) with no frame-loop change. The S7 theme surface
/// (Resources/Styles/Theme and the <c>UIObject</c>/<c>IResourceHost</c> base) merges into this
/// type at P5.
/// </para>
/// <para>
/// <b>No nested dispatcher loops exist</b> (normative, loop-forced): modal results are async-only;
/// a blocking <c>ShowDialog</c> would deadlock by construction and is not provided anywhere in the
/// stack.
/// </para>
/// </remarks>
public sealed partial class UIApplication : IAsyncDisposable
{
    [ThreadStatic]
    private static UIApplication? _current;

    private readonly UIApplicationOptions _options;
    private readonly UISynchronizationContext _syncContext;
    private readonly UserCodeGuard _guard;
    private readonly TerminalCaretService _caretService = new();
    private readonly ConcurrentQueue<InputEvent> _inputQueue = new();
    private readonly ConcurrentQueue<Action<IBufferWriter<byte>>> _controlSequences = new();
    private readonly ArrayBufferWriter<byte> _scratch = new(16 * 1024); // pooled across frames
    private readonly object _responseSinkLock = new();
    private Action<DeviceResponseEvent>[] _responseSinks = [];

    private ITerminalHost? _host;
    private bool _ownsHost;
    private TerminalCapabilities _capabilities = TerminalCapabilities.None;
    private InputCapabilities _effectiveInputCapabilities = InputCapabilities.None;
    private bool _supportsAltKeyTracking;
    private CellBuffer? _buffer;
    private FrameRenderer? _renderer;
    private WindowManager? _windowManager;
    private IAsyncInputDevice? _device; // decorated; NEVER disposed by S6 — the host owns transport lifecycle
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;
    private Exception? _pumpFault; // Interlocked slot, exchanged-to-null on raise (fires once)
    private volatile bool _streamEnded;
    private int _renderRequested;
    private bool _renegotiating; // UI-thread written, loop-read
    private volatile bool _shutdownRequested;
    private int _exitCode;
    private int _exitCodeSet;
    private Exception? _fatalException;
    private long _appStartTimestamp;
    private long _frame;
    private TimeSpan _lastElapsed;
    private FrameTime _currentFrameTime;
    private UIElement? _rootElement;
    private bool _systemsReady;
    private bool _enteredAltScreen;
    private int _runCalled;
    private int _tornDown;
    private Task<int>? _runTask;

    private readonly FocusManager _focusManager;
    private readonly AccessKeyManager _accessKeys;
    private readonly InputDispatcher _inputDispatcher;
    private readonly AnimationScheduler _animationScheduler;
    private Styles? _styles;

    // The frame loop's cross-subsystem seams (design doc §10.9). S3's router (P2), Fork B's
    // styling engine (P3), and the early-S5 scheduler slice (frozen clock + UITimer registry; the
    // full animation surface joins at P8) are installed by default. Internal so phase-order tests
    // can install probe implementations.
    internal IInputDispatchTarget? InputDispatchTarget;
    internal IStyleFrameHooks? StyleHooks;
    internal IAnimationFrameDriver? AnimationDriver;

    // The Cursorial.UI.Bars KeyTip overlay's post-layout hook (keytips-design §9) — null unless a bars app called
    // UIApplication.EnableKeyTips(). The frame loop calls CompletePendingLayout() after layout so badges re-anchor
    // and any parked next level builds. Kept as the narrow IKeyTipLayoutHook interface so Cursorial.UI needn't
    // reference Bars.
    internal IKeyTipLayoutHook? _keyTipController;

    /// <summary>Fork B's matcher + activation engine (P3) — the production styling instance.</summary>
    internal StyleEngine StyleEngineInternal { get; }

    /// <summary>
    /// Whether the frame loop is inside its layout/render window (Phases 5–6): pseudo flips raised
    /// here queue and surface via <see cref="IStyleFrameHooks.HasPendingActivations"/> instead of
    /// applying synchronously (ledger B1 / matrix SD12).
    /// </summary>
    internal bool InDeferredStylingPhase;

    internal UIApplication(UIApplicationOptions options)
    {
        _options = options;
        Dispatcher = new UIDispatcher();
        _syncContext = new UISynchronizationContext(Dispatcher);
        _guard = new UserCodeGuard(this);
        InteractionStates = new InteractionStateService();
        StyleEngineInternal = new StyleEngine(this);
        StyleHooks = StyleEngineInternal;            // B1: the Phase-3 flush slot
        InteractionStates.Observer = StyleEngineInternal; // SD22: the production observer (slot stays assignable)
        _focusManager = new FocusManager(Dispatcher, InteractionStates);
        _accessKeys = new AccessKeyManager(Dispatcher, _focusManager, InteractionStates);
        // The P2 topology: one implicit surface (the shown root). S4's WindowManager substitutes
        // the real IWindowTopology at P7 with no dispatcher rewrite (matrix ND5).
        _inputDispatcher = new InputDispatcher(Dispatcher, _focusManager, _accessKeys, InteractionStates, new SingleRootWindowTopology(this));
        _focusManager.InputDispatcherInternal = _inputDispatcher; // the :focus-visible modality source (doc §7.7)
        _focusManager.AccessKeysInternal = _accessKeys;           // pointer-driven focus exits menu mode (doc §7.7)
        InteractionStates.PressedSink = _inputDispatcher;         // the Pressed fan-in (ND12 / C8)
        _inputDispatcher.CursorShapeChangedInternal = OnEffectiveCursorShapeChanged; // §7.6 — S3 resolves, S6 emits
        InputDispatchTarget = _inputDispatcher; // S3's router IS the default Phase-1 dispatch seam
        _animationScheduler = new AnimationScheduler();
        AnimationDriver = _animationScheduler;
        AnimationScheduler.Install(_animationScheduler); // build/headless thread; the UI thread re-installs at loop start
        _caretService.StateChanged = RequestRender; // a pure caret move must arm Phase 6 (§5.9)
        _current = this; // Build-thread Current: pre-run UIObject construction is legal from here
    }

    /// <summary>Creates the application builder.</summary>
    public static UIApplicationBuilder CreateBuilder() => new();

    /// <summary>
    /// The thread-local current application: the Build thread pre-run, the UI thread after; null
    /// elsewhere. Fork A's per-element dispatcher capture reads this at construction (ledger A25),
    /// so it is non-null exactly on the threads where element construction is legal — and two
    /// parallel <c>UITestHost</c>s never cross-wire.
    /// </summary>
    public static UIApplication? Current => _current;

    /// <summary>The single UI thread's dispatcher.</summary>
    public UIDispatcher Dispatcher { get; }

    /// <summary>
    /// S3's input dispatcher (design doc §7.4) — the application's routing surface: capture,
    /// pointer-state bookkeeping, <c>TerminalFocusChanged</c>/<c>EditCommitRequested</c>, and the
    /// <c>ProcessEvent</c> entry the frame loop drives in Phase 1.
    /// </summary>
    public InputDispatcher InputDispatcher => _inputDispatcher;

    /// <summary>
    /// The keyboard-focus manager (design doc §7.7): the physical-focus singleton, logical focus
    /// scopes with memory, Tab / directional navigation, and the detach repair chain.
    /// </summary>
    public FocusManager FocusManager => _focusManager;

    /// <summary>
    /// The access-key engine (design doc §7.8): the flat registry, the capability-gated cue
    /// machine, scopes, and the menu-mode entry hooks. Receives its own capability fan-out call
    /// with the <b>negotiated</b> snapshot (ND23).
    /// </summary>
    public AccessKeyManager AccessKeys => _accessKeys;

    /// <summary>
    /// The thread-ambient animation scheduler (design doc §9.1) — at P2 the early-S5 slice: the
    /// frame-frozen <see cref="FrameClock"/> and the <see cref="UITimer"/> registry.
    /// </summary>
    public AnimationScheduler AnimationScheduler => _animationScheduler;

    /// <summary>
    /// The one installable per-application interaction-state observer (matrix ND11) — Fork B's
    /// styling engine becomes the production instance at P3; tests install recording sinks.
    /// Receives one <c>(element, oldState, newState)</c> notification per element per batch,
    /// delivered after the state commit.
    /// </summary>
    public IInteractionStateObserver? InteractionStateObserver
    {
        get => InteractionStates.Observer;
        set
        {
            Dispatcher.VerifyAccess();
            InteractionStates.Observer = value;
        }
    }

    /// <summary>The interaction-state coordinator (batching + observer delivery + Pressed fan-in).</summary>
    internal InteractionStateService InteractionStates { get; }

    /// <summary>
    /// The application-level style collection — Fork B's <see cref="StyleLayer.App"/> channel
    /// (design doc §3.5): its rules apply to every shown tree, below element-scoped
    /// <see cref="UIElement.Styles"/> and explicit <see cref="UIElement.Style"/> attachments.
    /// Lazily allocated; styles added here seal on add (doc §3.3).
    /// </summary>
    public Styles Styles
    {
        get
        {
            if (_styles is null)
            {
                Dispatcher.VerifyAccess();
                _styles = new Styles();
                _styles.AttachTo(this);
            }

            return _styles;
        }
    }

    /// <summary>The App-channel styles without lazy allocation (the engine's scope-gathering read).</summary>
    internal Styles? StylesOrNull => _styles;

    /// <summary>
    /// The SD21 invalidation hook for <see cref="Styles"/> mutation — the styling engine's
    /// scope-wide re-match entry (coarse re-match with rule-identity diff).
    /// </summary>
    internal void OnStylesInvalidated(Styles styles) => StyleEngineInternal.OnAppStylesInvalidated();

    /// <summary>The undecorated negotiated snapshot; replaced by <see cref="RenegotiateAsync"/>.</summary>
    public TerminalCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Post-decoration input capabilities (click synthesis, opt-in key-release synthesis applied) —
    /// what the assembled pipeline actually delivers; S3 introspects clicks/gestures here.
    /// Recomputed on renegotiation by re-applying the recorded decoration projections.
    /// </summary>
    public InputCapabilities EffectiveInputCapabilities => _effectiveInputCapabilities;

    /// <summary>
    /// The access-key gate (pinned formula, design doc §10.2), computed from the <b>undecorated</b>
    /// snapshot so <c>KeyReleaseSynthesizer</c> can never make it lie:
    /// <c>(Keyboard.DistinguishesKeyUpDown &amp;&amp; Keyboard.ReportsRepeats) || Protocol.Win32InputMode</c>.
    /// </summary>
    public bool SupportsAltKeyTracking => _supportsAltKeyTracking;

    private bool _nerdFontAvailable;

    /// <summary>
    /// Whether the terminal is rendering with a Nerd Font (the <c>caps-nerdfont</c> root class — CD-P2J-1). There is
    /// no probe (no terminal advertises Nerd Font glyph coverage), so this is an explicit opt-in for a future
    /// user-options infrastructure to set; it defaults to <see langword="false"/>. Setting it re-stamps the
    /// capability classes on every surface root live, and — being application state, not a negotiated capability —
    /// it survives renegotiation.
    /// </summary>
    public bool NerdFontAvailable
    {
        get => _nerdFontAvailable;
        set
        {
            Dispatcher.VerifyAccess();
            if (_nerdFontAvailable == value)
                return;
            _nerdFontAvailable = value;
            StyleEngineInternal.RestampCapabilityClasses();
            NerdFontAvailableChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when <see cref="NerdFontAvailable"/> flips (so capability-tiered visuals
    /// such as <see cref="Controls.Icon"/> can re-resolve their rendered tier live).</summary>
    public event EventHandler? NerdFontAvailableChanged;

    /// <summary>The last frame's timestamp (UI-thread read).</summary>
    public FrameTime CurrentFrameTime => _currentFrameTime;

    /// <summary>Raised on the UI thread after <see cref="RenegotiateAsync"/> swaps the snapshot.</summary>
    public event EventHandler<CapabilitiesChangedEventArgs>? CapabilitiesChanged;

    /// <summary>
    /// The exception funnel (design doc §10.8): every user-code entry point in the loop routes
    /// escaping exceptions here. <c>Handled = true</c> continues the frame; unhandled records
    /// fatal, runs the full canonical teardown, and rethrows from <c>RunAsync</c> — the terminal
    /// is always restored before an exception escapes.
    /// </summary>
    public event EventHandler<DispatcherUnhandledExceptionEventArgs>? DispatcherUnhandledException;

    /// <summary>
    /// Window-close shutdown policy (design doc §10.7). <b>P1 note:</b> windowing does not exist
    /// yet, so neither window-driven mode has a trigger; S4's <c>WindowClosed</c> applies it at P7.
    /// </summary>
    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnMainWindowClose;

    /// <summary>The caret registry (S1 §5.9) the render system consumes at frame assembly.</summary>
    public TerminalCaretService CaretService => _caretService;

    private IClipboardService? _clipboardService;

    /// <summary>
    /// The OSC 52 clipboard service (S1 §5.9 / punch 30): a focused <see cref="Controls.TextBox"/>'s
    /// Copy/Cut writes through it. Write-gated on the negotiated capability; reads are not yet supported
    /// (<see cref="IClipboardService.CanRead"/> is false — the terminal's own paste is the inbound path).
    /// </summary>
    public IClipboardService Clipboard => _clipboardService ??= new TerminalClipboardService(this);

    /// <summary>
    /// The root element tree rendered on the single full-screen surface — <b>the P1 stand-in for
    /// window content</b> (S4's <c>Window.Content</c> replaces it at P7). UI-thread only; setting
    /// it detaches the previous root (returning its scenes to the pool) and wires the new one
    /// under a fresh <see cref="LayoutManager"/> + <c>RenderTree</c>.
    /// </summary>
    public UIElement? RootElement
    {
        get => _rootElement;
        set
        {
            Dispatcher.VerifyAccess();

            if (ReferenceEquals(_rootElement, value))
                return;

            if (_systemsReady && _rootElement is {} old)
            {
                _focusManager.OnWindowDeactivated(old); // the single-root deactivation (per-window at W2)
                _windowManager!.SetRootSurface(null); // detaches the root surface — scenes to the pool AND DetachRoot
                ReleaseRegistryForRoot(old); // S7: drop the root's subscription registry (design doc §11.6)
            }

            _rootElement = value;

            if (_systemsReady && value is not null)
                WireRoot(value);

            RequestRender();
        }
    }

    // ───────────────────────────── thread-safe surface ─────────────────────────────

    /// <summary>Requests shutdown. Thread-safe; idempotent — the first exit code wins.</summary>
    public void Shutdown(int exitCode = 0)
    {
        if (Interlocked.CompareExchange(ref _exitCodeSet, 1, 0) == 0)
            Volatile.Write(ref _exitCode, exitCode);

        var raiseBeginShutdown = _shutdownRequested is false;

        _shutdownRequested = true;

        try
        {
            if (raiseBeginShutdown)
                RaiseBeginShutdown();
        }
        finally
        {
            Dispatcher.BeginShutdown();
            Dispatcher.Wake();
        }
    }

    private void RaiseBeginShutdown()
    {
        if (Dispatcher.CheckAccess() is false)
        {
            Dispatcher.Post(RaiseBeginShutdown);
            return;
        }

        // @formatter:off
        try { BeginShutdown?.Invoke(this, EventArgs.Empty); }
        catch { /* Best effort */}
        // @formatter:on
    }

    /// <summary>Thread-safe redraw request (Interlocked flag + wake) — the cross-thread invalidate.</summary>
    public void RequestRender()
    {
        Interlocked.Exchange(ref _renderRequested, 1);
        Dispatcher.Wake();
    }

    /// <summary>
    /// The sanctioned out-of-band byte channel (design doc §10.2): the payload is appended to the
    /// frame's scratch buffer in Phase 6 <b>after</b> the renderer's delta (forcing a flush even on
    /// an empty delta). OSC-class sequences only (title, clipboard, pointer shape, palette) —
    /// SGR / CUP / ED / scroll are FORBIDDEN: <see cref="FrameRenderer"/> is the sole owner of that
    /// terminal state and they desync the next delta. Thread-safe; wakes the loop.
    /// </summary>
    public void QueueControlSequence(Action<IBufferWriter<byte>> writePayload)
    {
        ArgumentNullException.ThrowIfNull(writePayload);
        _controlSequences.Enqueue(writePayload);
        Dispatcher.Wake();
    }

    /// <summary>
    /// BYO/embedder resize injection (BYO hosts receive no <see cref="ResizeEvent"/>s): enqueues a
    /// synthesized one into the same coalescing path as everything else. Thread-safe.
    /// </summary>
    public void NotifyResized(int columns, int rows)
    {
        _inputQueue.Enqueue(new ResizeEvent
                            {
                                Columns = columns,
                                Rows = rows,
                                Timestamp = _options.TimeProvider.GetUtcNow(),
                                Synthesized = true
                            });
        Dispatcher.Wake();
    }

    /// <summary>
    /// Registers a pass-through sink for <see cref="DeviceResponseEvent"/>s (palette queries,
    /// graphics) — S6's response router keeps protocol traffic out of S3's input routing.
    /// Dispose the returned registration to unregister. Thread-safe.
    /// </summary>
    public IDisposable RegisterDeviceResponseSink(Action<DeviceResponseEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_responseSinkLock)
        {
            _responseSinks = [.. _responseSinks, sink];
        }

        return new ResponseSinkRegistration(this, sink);
    }

    // ───────────────────────────── internal plumbing ─────────────────────────────

    /// <summary>Direct input injection (<c>UITestHost.SendInput</c> — no pump hop).</summary>
    internal void EnqueueInput(InputEvent inputEvent)
    {
        _inputQueue.Enqueue(inputEvent);
        Dispatcher.Wake();
    }

    /// <summary>The idle predicate behind <c>UITestHost.RunUntilIdle</c> and the Phase-7 guard inputs.</summary>
    internal bool IsIdle
        => _inputQueue.IsEmpty &&
           Dispatcher.JobCount == 0 &&
           _windowManager is not { HasPendingLayout: true } &&
           _windowManager is not { HasDirtyVisuals: true } &&
           _windowManager is not { HasDeferredTopology: true } && // a mid-layout close drains next frame (§8.8)
           StyleHooks is not { HasPendingActivations: true } &&
           AnimationDriver is not { HasActiveAnimations: true } &&
           Volatile.Read(ref _renderRequested) == 0 &&
           _controlSequences.IsEmpty;

    internal bool HasFatalException => _fatalException is not null;

    internal CellBuffer? FrameBufferInternal => _buffer;

    /// <summary>S4's window manager (design doc §8.5) — the window/popup system, available while the application
    /// is running. Apps usually drive windows through <see cref="Window.Show()"/> / <see cref="Window.ShowDialogAsync(System.Threading.CancellationToken)"/>
    /// and <see cref="Popup"/>; this exposes the manager-level surface (the window list, active window, fit
    /// affordance, shutdown). <see langword="null"/> before the systems are composed.</summary>
    public WindowManager? WindowManager => _windowManager;

    internal TimeProvider TimeProviderInternal => _options.TimeProvider;

    // The focus/access-key "active root" currently activated (the app root, or the active Window). S4 moves
    // this per window via OnActiveWindowFocusChanged when the WindowManager's active window changes.
    private UIElement? _lastActiveFocusRoot;

    private void WireRoot(UIElement root)
    {
        _windowManager!.SetRootSurface(root);  // attaches the root under a fresh LayoutManager + RenderTree

        // The window-root focus convention (doc §7.7, matrix N117): the shown root is a focus
        // scope, set by the harness — S4's window manager owns this per window at P7. Activation
        // then restores focus: scope memory if valid, else the first tab-ordered focusable (N115).
        FocusManager.SetIsFocusScope(root, true);
        _focusManager.OnWindowActivated(root);
        _accessKeys.OnWindowActivated(root); // cue stamping (permanent in AlwaysVisible mode — doc §7.8)
        _lastActiveFocusRoot = root;
    }

    /// <summary>
    /// S4 per-window focus activation (doc §7.7): when the <see cref="WindowManager"/>'s active window changes,
    /// move the focus + access-key "active root" to that window (or back to the app root when no window is active).
    /// Marks the new root a focus scope, restores/auto-focuses its first tab stop, re-stamps the access-key cue,
    /// and deactivates the previous root. WITHOUT this a freshly-activated window sits with no focus, its content
    /// never auto-focuses, and Enter never reaches its <see cref="Controls.Button.IsDefault"/> button (its
    /// surface-root binding is unreachable from the stale/host active root).
    /// </summary>
    private void OnActiveWindowFocusChanged()
    {
        var newRoot = (UIElement?) _windowManager?.ActiveWindow ?? _rootElement;
        if (newRoot is null || ReferenceEquals(newRoot, _lastActiveFocusRoot))
            return;

        if (_lastActiveFocusRoot is { } old)
            _focusManager.OnWindowDeactivated(old);

        _lastActiveFocusRoot = newRoot;
        FocusManager.SetIsFocusScope(newRoot, true);
        _focusManager.OnWindowActivated(newRoot);
        _accessKeys.OnWindowActivated(newRoot);
    }

    /// <summary>
    /// The §7.6 pointer-shape emission leg: S3's dispatcher resolves and equality-gates (the seam
    /// fires only on effective-shape change, and never when the terminal didn't negotiate
    /// <c>MouseCursorShape</c>); S6 turns each change into one queued OSC 22 sequence — drained in
    /// Phase 6 after the frame's delta, like every other out-of-band control sequence. "Back to
    /// the terminal default" is an explicit <c>WriteSet(Default)</c>, never the empty-payload
    /// <c>WriteReset</c>: kitty honors empty-as-reset, but Ghostty ignores it and strands the
    /// previous shape (observed live, 2026-06-11); the named <c>default</c> shape restores on both.
    /// </summary>
    private void OnEffectiveCursorShapeChanged(MouseCursorShape? shape)
    {
        // null = "back to the terminal default" → the named Default shape (slot 0), per the
        // Ghostty rationale above.
        var resolved = shape ?? MouseCursorShape.Default;
        var index = (int)resolved;
        QueueControlSequence((uint)index < (uint)CursorShapeWriters.Length
            ? CursorShapeWriters[index] ??= writer => MouseCursorWriter.WriteSet(writer, resolved)
            : writer => MouseCursorWriter.WriteSet(writer, resolved)); // out-of-range enum value: uncached — the writer throws at drain, same as before
    }

    // One cached writer delegate per shape, lazily filled (index = the enum value). Shape changes
    // are user-rate (hover crossings), not motion-rate, but the emission leg stays allocation-free
    // like the S3 resolution leg feeding it. Static: shared across application instances (benign
    // race — identical delegates, last write wins).
    private static readonly Action<IBufferWriter<byte>>?[] CursorShapeWriters =
        new Action<IBufferWriter<byte>>?[(int)MouseCursorShape.ZoomOut + 1];

    private void DispatchDeviceResponse(DeviceResponseEvent response)
    {
        var sinks = Volatile.Read(ref _responseSinks); // snapshot-iterate

        foreach (var sink in sinks)
        {
            try
            {
                sink(response);
            }
            catch (Exception ex)
            {
                if (!RaiseUnhandled(ex))
                    return;
            }
        }
    }

    private void ApplyDefaultGestures(InputEvent inputEvent)
    {
        // Raw mode inverts signal semantics: Ctrl+C is a KeyEvent, not SIGINT (design doc §10.7).
        if (_options.ExitOnUnhandledCtrlC && inputEvent is KeyEvent key && IsCtrlC(key))
            Shutdown(0);
    }

    private static bool IsCtrlC(KeyEvent key)
        => key is { Kind: KeyEventKind.Down, Key: Key.Character, Text.Length: > 0 } &&
           key.Modifiers.HasFlag(KeyModifiers.Control) &&
           key.Text.Span[0] is 'c' or 'C';

    /// <summary>
    /// The funnel core (design doc §10.8). Returns <see langword="true"/> when the frame may
    /// continue (handled, or cooperative shutdown cancellation); <see langword="false"/> records
    /// the exception as fatal — the caller unwinds to teardown and <c>RunAsync</c> rethrows.
    /// </summary>
    internal bool RaiseUnhandled(Exception exception)
    {
        if (exception is OperationCanceledException oce && oce.CancellationToken == Dispatcher.ShutdownToken)
            return true; // cooperative shutdown cancellation bypasses the funnel entirely

        if (DispatcherUnhandledException is {} handler)
        {
            var args = new DispatcherUnhandledExceptionEventArgs { Exception = exception };

            try
            {
                handler(this, args);
            }
            catch (Exception handlerException)
            {
                // A handler throwing is fatal immediately: record both, do NOT re-raise (no recursion).
                RecordFatal(new AggregateException(exception, handlerException));
                return false;
            }

            if (args.Handled)
                return true;
        }

        RecordFatal(exception);
        return false;
    }

    private void RecordFatal(Exception exception)
    {
        var raiseBeginShutdown = _shutdownRequested is false;

        _fatalException ??= exception;
        _shutdownRequested = true;

        if (raiseBeginShutdown)
            RaiseBeginShutdown();

        Dispatcher.BeginShutdown();
        Dispatcher.Wake();
    }

    private static bool ComputeAltKeyTracking(InputCapabilities undecorated)
        => undecorated.Keyboard is { DistinguishesKeyUpDown: true, ReportsRepeats: true } ||
           undecorated.Protocol.Win32InputMode;

    /// <summary>Re-applies the recorded decoration projections to a fresh snapshot (renegotiation).</summary>
    private InputCapabilities ApplyDecorationProjections(InputCapabilities undecorated)
    {
        var capabilities = undecorated;

        if (_options.KeyReleaseSynthesis is not null)
        {
            capabilities = capabilities with
                           {
                               Keyboard = capabilities.Keyboard with { DistinguishesKeyUpDown = true, ReportsRepeats = true }
                           };
        }

        return capabilities with
               {
                   Mouse = capabilities.Mouse with
                           {
                               SynthesizesClickCounts = _options.ClickOptions.ClickCount != ClickCountTarget.None,
                               SynthesizesClicks = _options.ClickOptions.SynthesizeClickEvents
                           }
               };
    }

    private sealed class ResponseSinkRegistration(UIApplication app, Action<DeviceResponseEvent> sink) : IDisposable
    {
        private Action<DeviceResponseEvent>? _sink = sink;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _sink, null) is not {} removed)
                return;

            lock (app._responseSinkLock)
            {
                var sinks = app._responseSinks;

                var index = Array.IndexOf(sinks, removed);
                if (index < 0)
                    return;

                var next = new Action<DeviceResponseEvent>[sinks.Length - 1];
                Array.Copy(sinks, 0, next, 0, index);
                Array.Copy(sinks, index + 1, next, index, sinks.Length - index - 1);
                app._responseSinks = next;
            }
        }
    }

    /// <summary>The concrete <see cref="IUserCodeGuard"/> — wires the funnel into S1's draw path.</summary>
    internal sealed class UserCodeGuard(UIApplication app) : IUserCodeGuard
    {
        private bool _handled;

        public bool IsFatal => app.HasFatalException;

        public bool Run<TState>(TState state, Action<TState> userCode)
        {
            try
            {
                userCode(state);
                return true;
            }
            catch (Exception ex)
            {
                if (app.RaiseUnhandled(ex))
                {
                    _handled = true;
                    return true;
                }

                return false;
            }
        }

        /// <summary>Reads-and-clears the handled-draw flag — Phase 6's conservative-emit input.</summary>
        public bool ConsumeHandledFlag()
        {
            var handled = _handled;
            _handled = false;
            return handled;
        }
    }
}
