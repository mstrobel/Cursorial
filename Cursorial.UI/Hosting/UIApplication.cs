using System.Buffers;
using System.Collections.Concurrent;

using Cursorial.Input;
using Cursorial.Input.Capabilities;
using Cursorial.Input.Events;
using Cursorial.Rendering;
using Cursorial.Terminal;

namespace Cursorial.UI;

/// <summary>
/// The composition root and the only owner of "time, thread, and the byte pipe" (design doc §10):
/// acquires the terminal host, runs the one dedicated UI thread, drives the single authoritative
/// frame loop, owns the screen <see cref="CellBuffer"/> + <see cref="FrameRenderer"/>, and tears
/// the terminal down on every exit path. Construct via <see cref="CreateBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>P1 shape.</b> The styling engine (P3), animation driver (P8), input router (P2), and window
/// system (P7) are documented no-op seams — the frame loop's phase slots exist and are exercised,
/// but the seam fields are null until their phases land. <see cref="RootElement"/> is the P1
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
    private SingleRootRenderSystem? _renderSystem;
    private SingleRootLayoutSystem? _layoutSystem;
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

    // The frame loop's cross-subsystem seams (design doc §10.9). Null until their phases land:
    // S3's router (P2), Fork B's styling hooks (P3), S5's animation driver (P8). Internal so the
    // landing phases — and phase-order tests — install implementations without public API churn.
    internal IInputDispatchTarget? InputDispatchTarget;
    internal IStyleFrameHooks? StyleHooks;
    internal IAnimationFrameDriver? AnimationDriver;

    internal UIApplication(UIApplicationOptions options)
    {
        _options = options;
        Dispatcher = new UIDispatcher();
        _syncContext = new UISynchronizationContext(Dispatcher);
        _guard = new UserCodeGuard(this);
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

            if (_systemsReady && _rootElement is { } old)
            {
                _renderSystem!.SetRoot(null); // RenderTree.Detach — scenes back to the pool
                old.DetachRoot();
                _layoutSystem!.SetRoot(null);
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

        _shutdownRequested = true;
        Dispatcher.BeginShutdown();
        Dispatcher.Wake();
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
            Synthesized = true,
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
        => _inputQueue.IsEmpty
           && Dispatcher.JobCount == 0
           && !(_layoutSystem?.HasPendingLayout ?? false)
           && !(_renderSystem?.HasDirtyVisuals ?? false)
           && !(StyleHooks?.HasPendingActivations ?? false)
           && !(AnimationDriver?.HasActiveAnimations ?? false)
           && Volatile.Read(ref _renderRequested) == 0
           && _controlSequences.IsEmpty;

    internal bool HasFatalException => _fatalException is not null;

    internal CellBuffer? FrameBufferInternal => _buffer;

    internal SingleRootRenderSystem? RenderSystem => _renderSystem;

    internal SingleRootLayoutSystem? LayoutSystem => _layoutSystem;

    internal TimeProvider TimeProviderInternal => _options.TimeProvider;

    private void WireRoot(UIElement root)
    {
        _layoutSystem!.SetRoot(root);  // attaches root under a fresh LayoutManager
        _renderSystem!.SetRoot(root);  // requires the root attached — ordering matters
    }

    private void DispatchDeviceResponse(DeviceResponseEvent response)
    {
        var sinks = Volatile.Read(ref _responseSinks); // snapshot-iterate
        for (var i = 0; i < sinks.Length; i++)
        {
            try
            {
                sinks[i](response);
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
        => key is { Kind: KeyEventKind.Down, Key: Key.Character, Text.Length: > 0 }
           && (key.Modifiers & KeyModifiers.Control) != 0
           && key.Text.Span[0] is 'c' or 'C';

    /// <summary>
    /// The funnel core (design doc §10.8). Returns <see langword="true"/> when the frame may
    /// continue (handled, or cooperative shutdown cancellation); <see langword="false"/> records
    /// the exception as fatal — the caller unwinds to teardown and <c>RunAsync</c> rethrows.
    /// </summary>
    internal bool RaiseUnhandled(Exception exception)
    {
        if (exception is OperationCanceledException oce && oce.CancellationToken == Dispatcher.ShutdownToken)
            return true; // cooperative shutdown cancellation bypasses the funnel entirely

        if (DispatcherUnhandledException is { } handler)
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
        _fatalException ??= exception;
        _shutdownRequested = true;
        Dispatcher.BeginShutdown();
        Dispatcher.Wake();
    }

    private static bool ComputeAltKeyTracking(InputCapabilities undecorated)
        => (undecorated.Keyboard.DistinguishesKeyUpDown && undecorated.Keyboard.ReportsRepeats)
           || undecorated.Protocol.Win32InputMode;

    /// <summary>Re-applies the recorded decoration projections to a fresh snapshot (renegotiation).</summary>
    private InputCapabilities ApplyDecorationProjections(InputCapabilities undecorated)
    {
        var capabilities = undecorated;
        if (_options.KeyReleaseSynthesis is not null)
        {
            capabilities = capabilities with
            {
                Keyboard = capabilities.Keyboard with { DistinguishesKeyUpDown = true, ReportsRepeats = true },
            };
        }

        return capabilities with
        {
            Mouse = capabilities.Mouse with
            {
                SynthesizesClickCounts = _options.ClickOptions.ClickCount != ClickCountTarget.None,
                SynthesizesClicks = _options.ClickOptions.SynthesizeClickEvents,
            },
        };
    }

    private sealed class ResponseSinkRegistration(UIApplication app, Action<DeviceResponseEvent> sink) : IDisposable
    {
        private Action<DeviceResponseEvent>? _sink = sink;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _sink, null) is not { } removed)
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
