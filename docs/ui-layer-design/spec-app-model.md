All prep read (DECISIONS.md, design-doc.md, rendering-session.md, input.md, animation.md, styling proposal §3.5 batching/fixpoint contract verified). Final spec follows.

---

# S6 — Application model, dispatcher, and the frame loop

**Subsystem spec for `Cursorial.UI` (namespace `Cursorial.UI`; test host in `Cursorial.UI.Testing`).**
Conforms to DECISIONS.md invariants 1–7; orchestrates against rendering-session.md §7 (the demo loop is the validated template), input.md (single-shot device, pump threading), animation.md (time-free mechanism — the clock lives here).

---

## 1. Scope

**S6 owns:**

- `UIApplication` — composition root (see §3.0 for the concrete wiring order): terminal-host acquisition (happy-path session, BYO session, or synthetic host), capability exposure, `TerminalPalette` theming with the capability-rewrite pattern, alt-screen entry/exit, the canonical teardown order, signal-net interplay, exit codes.
- **The frame loop** — the single authoritative per-frame phase ordering (input drain → dispatch → dispatcher jobs → styling flush → animation tick → layout → render → idle), frame pacing (including the event-driven rate clamp), and the event-driven vs. animated idle policy.
- `ITerminalHost` — the S6-owned abstraction over "a thing that gives us capabilities, an input device, and an output sink" (invariant-7-clean: no Core change). `TerminalSessionHost` adapts `TerminalSession` for both production paths; the test host implements it synthetically.
- The **input pump** — the one-and-only enumeration of `host.Input.ReadAllAsync` (single-shot per session!), decorator assembly (`WithClickSynthesis`; opt-in `KeyReleaseSynthesizer`), the thread-safe input queue, `ResizeEvent` coalescing/application, `DeviceResponseEvent` routing.
- `UIDispatcher` + `UISynchronizationContext` — cross-thread `Post`/`InvokeAsync`, `CheckAccess`/`VerifyAccess` with the construction-thread→UI-thread ownership hand-off, async/await resumption on the UI thread. **No priority tiers exist** (invariant 1).
- The **out-of-band control-sequence channel** (`QueueControlSequence`) — the sanctioned route for OSC-class writes (window title, clipboard, pointer shape, post-startup palette changes) that keeps "nobody else writes raw bytes" structurally true.
- Exception policy (`DispatcherUnhandledException`, the `IUserCodeGuard` funnel passed down to S1/S4 draw delegates), shutdown (`ShutdownMode`, `Shutdown(int)`, window-close-driven exit), `RenegotiateAsync` orchestration (capability re-stamp, renderer/buffer rebuild, output-pipe gating).
- The **headless test host** (`UITestHost`): synthetic `ITerminalHost`, fake `TimeProvider`, manual frame stepping.

**S6 explicitly does NOT own:** input *semantics* — hit testing, routing, focus, access-key logic, interaction pseudo-class flips (S3; S6 hands S3 events one at a time and guarantees ordering); scene assembly, z-order, window-manager layer policy, what gets re-rastered vs. re-composited (S1/S4; S6 only *calls* them in the right phase); styling activation algorithms (Fork B; S6 only calls the flush hook); animation value application (S5; S6 only supplies the timestamp); the property system (Fork A).

**Normative cross-subsystem constraint stated here because S6's loop design forces it:** there are **no nested dispatcher loops** (no WPF `PushFrame`). A blocking `ShowDialog()` would deadlock by construction and is not provided anywhere in the stack. Modal results are async-only — S4's dialog API shape is `Task<TResult> ShowDialogAsync(...)`, completed on close, frame-coherent via `UISynchronizationContext`. (See §4, §5 req-5.)

---

## 2. Public API sketch

### 2.1 `UIApplication` and builder

```csharp
namespace Cursorial.UI;

public sealed class UIApplication : IAsyncDisposable
{
    public static UIApplicationBuilder CreateBuilder() => new();

    /// Thread-local. Set on the building thread by Build() (enables pre-run UIObject construction)
    /// and on the dedicated UI thread at loop start; cleared per-thread at loop exit / DisposeAsync.
    /// Reads from any other thread return null — Fork A's per-element dispatcher capture is therefore
    /// correct on exactly the threads where element construction is legal, and two parallel UITestHosts
    /// never cross-wire.
    public static UIApplication? Current { get; private set; }        // [ThreadStatic] backing field

    public UIDispatcher Dispatcher { get; }
    public TerminalCapabilities Capabilities { get; }                  // volatile snapshot; re-read after renegotiate
    /// Post-decoration input capabilities (click synthesis, opt-in key-release synthesis applied) —
    /// what the assembled pipeline actually delivers; S3 introspects Mouse.SynthesizesClicks etc. here.
    /// Recomputed on renegotiation by re-applying the recorded decoration projections to the new snapshot.
    public InputCapabilities EffectiveInputCapabilities { get; }
    public event EventHandler<CapabilitiesChangedEventArgs>? CapabilitiesChanged;   // fired on UI thread
    public event EventHandler<DispatcherUnhandledExceptionEventArgs>? DispatcherUnhandledException;

    public ResourceDictionary Resources { get; }                       // app-level resource scope (Fork B walk terminus)
    public Styles Styles { get; }                                      // app-level styles
    public ThemeVariant ThemeVariant { get; }                          // recomputed on renegotiate (Fork C/X contract)

    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnMainWindowClose;
    /// input.md §7 gate, computed from the HOST's UNDECORATED Capabilities.Input:
    ///   Keyboard.DistinguishesKeyUpDown && (Keyboard.ReportsRepeats || Protocol.Win32InputMode).
    /// Decorators never influence this value — KeyReleaseSynthesizer's timer-derived claims cannot
    /// make it lie (it can never produce Alt events).
    public bool SupportsAltKeyTracking { get; }
    public FrameTime CurrentFrameTime { get; }     // last frame's timestamp (UI thread read)

    /// PREFERRED: the factory runs on the dedicated UI thread after startup, before the first frame —
    /// no construction-thread hand-off involved.
    public Task<int> RunAsync(Func<Window> mainWindowFactory, CancellationToken cancellationToken = default);
    /// Hand-off sugar: the Window must have been constructed on the same thread that called Build()
    /// (the dispatcher's pre-run owner); ownership transfers to the UI thread at loop start and the
    /// builder thread must not touch UI objects afterwards (debug-asserted — see §3.1).
    public Task<int> RunAsync(Window mainWindow, CancellationToken cancellationToken = default);
    // Both overloads: single-use — a second RunAsync throws InvalidOperationException.
    // Happy path: TerminalSession.OpenAsync throws InvalidOperationException when stdio isn't a TTY
    // (CI/pipes) — surfaced as-is; use WithSession/WithTerminalHost or UITestHost there.

    public void Shutdown(int exitCode = 0);        // thread-safe; idempotent (first code wins)
    public void RequestRender();                   // thread-safe redraw request (Interlocked flag + wake)

    /// Thread-safe out-of-band byte channel: payload is appended to the frame's scratch buffer in
    /// Phase 6 AFTER the renderer's delta (forces a flush even when the cell delta is empty; wakes the
    /// loop). OSC-class sequences only (title, clipboard, pointer shape, palette); SGR / CUP / ED /
    /// scroll are FORBIDDEN — the FrameRenderer is the sole owner of that state and they desync the
    /// next delta. WindowWriter/ClipboardWriter/MouseCursorWriter/post-startup TerminalPalette calls
    /// all route through here.
    public void QueueControlSequence(Action<IBufferWriter<byte>> writePayload);

    /// BYO/embedder resize injection (BYO sessions receive no ResizeEvents — input.md): enqueues a
    /// synthesized ResizeEvent into the frame loop's input queue. Thread-safe.
    public void NotifyResized(int columns, int rows);

    public ValueTask RenegotiateAsync(CancellationToken ct = default);  // UI thread only (VerifyAccess)

    /// Pass-through of DeviceResponseEvents to interested parties (TerminalPalette queries, graphics).
    public IDisposable RegisterDeviceResponseSink(Action<DeviceResponseEvent> sink);

    public ValueTask DisposeAsync();               // idempotent; runs teardown if loop never ran/already exited
}

public sealed class UIApplicationBuilder
{
    public UIApplicationBuilder WithSessionOptions(TerminalSessionOptions options);
    public UIApplicationBuilder WithSession(TerminalSession session, bool disposeWithApp = false); // BYO sugar → TerminalSessionHost
    public UIApplicationBuilder WithTerminalHost(ITerminalHost host, bool disposeWithApp = false); // full BYO (tests/embedding)
    public UIApplicationBuilder WithFrameRate(int framesPerSecond);    // clamped to [1,120]; default 30
    public UIApplicationBuilder WithTimeProvider(TimeProvider timeProvider);     // default TimeProvider.System
    public UIApplicationBuilder WithPalette(Action<TerminalThemeBuilder> configure); // OSC 4/10/11/12 theme
    public UIApplicationBuilder WithClickOptions(MouseClickOptions options);     // default: SynthesizeClickEvents=true
    public UIApplicationBuilder WithKeyReleaseSynthesis(TimeSpan? upTimeout = null, TimeSpan? repeatTimeout = null); // OPT-IN
    public UIApplicationBuilder UseAlternateScreen(bool enabled = true);          // default true
    public UIApplicationBuilder WithRendererOptions(bool orderedDither = false);  // RestrictToDirtyRegions deferred (§7)
    public UIApplicationBuilder ExitOnUnhandledCtrlC(bool enabled = true);        // default true
    /// No I/O here; the host opens inside RunAsync. Constructs the UIDispatcher (owner = this thread)
    /// and sets the thread-local UIApplication.Current — pre-run UIObject construction is legal on this
    /// thread from this point. Single-use: a second Build() throws InvalidOperationException.
    public UIApplication Build();
}

public enum ShutdownMode { OnMainWindowClose, OnLastWindowClose, OnExplicitShutdown }

public sealed class DispatcherUnhandledExceptionEventArgs : EventArgs
{
    public required Exception Exception { get; init; }
    public bool Handled { get; set; }              // true ⇒ loop continues; false ⇒ teardown + rethrow from RunAsync
}

public sealed class CapabilitiesChangedEventArgs : EventArgs
{
    public required TerminalCapabilities OldCapabilities { get; init; }
    public required TerminalCapabilities NewCapabilities { get; init; }
}

/// One timestamp per frame — the single time truth for S5 ticks, gestures, and diagnostics (invariant: one
/// frame, one clock reading; mid-frame code never re-samples the clock).
public readonly record struct FrameTime(long FrameNumber, TimeSpan Elapsed, TimeSpan Delta);

public sealed record TerminalTheme           // built by TerminalThemeBuilder
{
    public Color? Foreground { get; init; }
    public Color? Background { get; init; }
    public Color? CursorColor { get; init; }
    public IReadOnlyDictionary<byte, Color>? PaletteOverrides { get; init; }   // OSC 4 indices
}
```

### 2.2 `UIDispatcher`

```csharp
public sealed class UIDispatcher
{
    public bool CheckAccess();                     // Environment.CurrentManagedThreadId == _ownerThreadId
    public void VerifyAccess();                    // throws InvalidOperationException("call from the UI thread…")
    // Ownership model: at Build() the owner is the building thread (pre-run construction is legal there).
    // RunLoop's first act is TransferOwnershipToCurrentThread() — a one-shot internal hand-off that pins
    // the dedicated UI thread as owner; after transfer, VerifyAccess on the old thread throws. UITestHost
    // never transfers (the creating thread stays owner). Transfer twice ⇒ InvalidOperationException.

    public void Post(Action action);                                   // fire-and-forget; runs next dispatcher drain
    public void Post<TState>(TState state, Action<TState> action);     // allocation-light stateful overload
    public Task InvokeAsync(Action action);
    public Task<T> InvokeAsync<T>(Func<T> callback);
    public Task InvokeAsync(Func<Task> callback);                      // async callbacks unwrapped
    public Task<T> InvokeAsync<T>(Func<Task<T>> callback);
    // Calling InvokeAsync FROM the UI thread does not run inline — it queues for the current/next drain
    // (preserves frame-phase ordering); await it and the continuation resumes via the sync context.
    // AFTER SHUTDOWN: InvokeAsync returns a canceled Task; Post is dropped (documented; nothing enqueues
    // into the void — see §3.6 job-drain).

    public CancellationToken ShutdownToken { get; }   // canceled when Shutdown is requested
    internal void Wake();                              // auto-reset wake used by pump/Post/RequestRender/Shutdown
}

internal sealed class UISynchronizationContext : SynchronizationContext
{
    // Post(d, s)  → dispatcher.Post(s, st => d(st))           [order-preserving, never blocks the caller]
    // Send(d, s)  → inline when CheckAccess; otherwise blocking InvokeAsync(...).GetAwaiter().GetResult()
    //               (documented deadlock hazard if the UI thread is itself blocked on the caller)
    // CreateCopy() → this
}
```

`UIObject`'s debug thread-affinity assert (invariant 6) is `Dispatcher.VerifyAccess()` — Fork A consumes the dispatcher reference via the thread-local `UIApplication.Current!.Dispatcher` captured at element construction. `Current` is non-null exactly on the threads where construction is legal (the Build thread pre-run; the UI thread after), so the capture is sound on the framework's own happy path, including `RunAsync(new MainWindow())`.

### 2.3 Headless test host (`Cursorial.UI.Testing`)

```csharp
public sealed class UITestHost : IAsyncDisposable
{
    /// Synchronous, no probes, no TTY, no negotiation: a SyntheticTerminalHost (an ITerminalHost
    /// implementation — see §4) with scripted capabilities over an in-memory sink. The CALLING thread
    /// becomes the UI thread (dispatcher owner; thread-local Current set here); frames run only when
    /// you step them. A UITestHost is single-thread-affine; parallel test collections each create their
    /// own and never interact (Current is thread-local).
    public static UITestHost Create(UITestHostOptions? options = null);

    public UIApplication Application { get; }
    public UIDispatcher Dispatcher { get; }
    public FakeTimeProvider Time { get; }             // manual clock; animations/gestures sample it
    /// LIVE accessor — returns the application's CURRENT back buffer; the underlying instance is
    /// replaced by SendResize-driven rebuilds and renegotiation. Do not cache across frames.
    public CellBuffer FrameBuffer { get; }
    public ReadOnlyMemory<byte> LastFrameBytes { get; }  // emitted wire bytes (when CaptureFrameBytes)

    public void ShowWindow(Window window);            // routes through S4 exactly as production
    public void RunFrame();                           // ONE full frame, synchronously, on this thread
    public int  RunFrames(int count);                 // returns frames actually run (early-exits on shutdown)
    /// Idle predicate (all must hold): input queue empty ∧ dispatcher jobs empty ∧
    /// !layout.HasPendingLayout ∧ !renderSystem.HasDirtyVisuals ∧ !styling.HasPendingActivations ∧
    /// !animation.HasActiveAnimations. Returns false when maxFrames is hit first.
    public bool RunUntilIdle(int maxFrames = 100);
    /// Advances Time in FrameInterval steps, one RunFrame per step; a non-multiple remainder is applied
    /// as one final partial step with a closing RunFrame.
    public void AdvanceTime(TimeSpan delta);

    // Deterministic input: enqueued DIRECTLY into the frame loop's queue (no pump hop, no race).
    public void SendInput(InputEvent inputEvent);     // Timestamp defaulted from Time if unset
    public void SendKey(Key key, KeyModifiers modifiers = default, string? text = null, bool withRelease = false);
    public void SendText(string text);
    public void SendMouseMove(int column, int row);
    public void SendClick(int column, int row, MouseButton button = MouseButton.Left, int clickCount = 1);
    public void SendResize(int columns, int rows);

    // Parser-inclusive path: raw bytes through a REAL VtInputDevice constructed by the host itself —
    // VtInputDevice(source, caps, mode, timeProvider: Time, escapeAmbiguityTimeout) — so interpreter
    // event Timestamps and the bare-ESC ambiguity timer live on the SAME fake clock as SendInput and
    // the gesture/animation machinery (multi-click threshold tests are deterministic). A trailing lone
    // ESC commits only after AdvanceTime crosses the ambiguity window — it will NOT auto-commit on the
    // wall clock.
    public void SendBytes(ReadOnlySpan<byte> rawBytes);
    public Task DrainParsedInputAsync(TimeSpan? timeout = null);   // await pump catch-up before RunFrame

    public string GetRowText(int row);
    public Cell GetCell(int column, int row);
    public ValueTask DisposeAsync();                  // full canonical teardown into the captured sink
}

public sealed record UITestHostOptions
{
    public Size InitialSize { get; init; } = new(80, 24);
    public TerminalCapabilities Capabilities { get; init; } = TestCapabilities.KittyTruecolor; // presets incl. Ansi16Legacy, NoMotion
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(33);
    public bool CaptureFrameBytes { get; init; } = false;
}
```

### 2.4 Consumer example

```csharp
public static async Task<int> Main()
{
    var app = UIApplication.CreateBuilder()
        .WithFrameRate(30)
        .WithPalette(theme =>
        {
            theme.Background = Color.FromHex("#1e1e2e");   // OSC 11 + capability-rewrite so alpha blending
            theme.Foreground = Color.FromHex("#cdd6f4");   // composites against the REAL themed colors
        })
        .WithClickOptions(new MouseClickOptions { SynthesizeClickEvents = true })
        .Build();                                          // Current + Dispatcher exist from here

    app.DispatcherUnhandledException += (_, e) =>
    {
        Log.Error(e.Exception);
        e.Handled = e.Exception is not OutOfMemoryException;   // keep running on app-level errors
    };

    // Preferred: factory overload — MainWindow is constructed ON the UI thread.
    return await app.RunAsync(() => new MainWindow());   // terminal restored before this returns — safe to print after
    // Also legal: await app.RunAsync(new MainWindow()) — constructed on this (Build) thread, handed off.
}

// From a background data feed:
app.Dispatcher.Post(model, m => m.RefreshTicker());   // marshals; wakes the idle loop; visible same frame it runs

// In an async event handler (resumes on the UI thread via UISynchronizationContext):
async void OnSavePressed(object? s, RoutedEventArgs e)
{
    SaveButton.IsEnabled = false;          // frame N
    await _store.SaveAsync();              // continuation posted to dispatcher
    SaveButton.IsEnabled = true;           // frame N+k, still UI thread
}
```

---

## 3. Mechanics

### 3.0 Composition

`UIApplication` is the composition root in the plain sense: it **news up the default subsystem implementations in a fixed order** inside `RunAsync` (no DI container in v1; the seams in §4 exist so tests and future hosts can substitute):

1. *(at `Build()`)* `UIDispatcher` (owner = building thread), `UISynchronizationContext`, app `Resources`/`Styles` instances, thread-local `Current`.
2. *(in `RunAsync`, caller's context)* `ITerminalHost` open (§3.5 startup steps 1–7): host → capabilities → palette + capability rewrite → `ThemeVariant` → `CellBuffer` + `FrameRenderer` → UI-mode entry bytes.
3. *(on the UI thread, before the first frame)* in dependency order: **styling engine** (Fork B; receives app `Resources`/`Styles` + the capability snapshot) → **window system / render system** (S4 over S1; one object typically implements both `IWindowSystem` and `IRenderSystem`; receives buffer dimensions and the `IUserCodeGuard`) → **layout system** (S2; receives the window system's root enumeration) → **input router** (S3 `IInputDispatchTarget`; receives the window system for hit testing/z-order, the styling engine's interaction-state sink, and the dispatcher) → **animation driver** (S5; receives nothing from S6 — values flow through `AnimatedValueHandle<T>`).
4. The five seam references are captured into the internal `FrameLoop` core (`internal sealed class FrameLoop` with `RunFrameOnce(in FrameTime)`), which backs both the production loop and `UITestHost` stepping.

### 3.1 Threading model: one dedicated UI thread

`RunAsync` performs async startup (host open, negotiation, palette) on the caller, then spawns **one dedicated thread** (`Thread { Name = "Cursorial UI", IsBackground = false }`) that runs the loop synchronously to completion; `RunAsync` returns a `Task<int>` completed by that thread. Rationale: a true OS-thread identity makes `CheckAccess`, `UIObject` affinity asserts, and frame coherence trivially sound — no async-loop/sync-context re-entrancy hazards.

**Ownership hand-off.** The dispatcher's owner is the `Build()` thread until the loop thread's first act, `TransferOwnershipToCurrentThread()`, pins the dedicated thread (and sets the thread-local `Current` there). Contract: all pre-run `UIObject` construction happens on the Build thread; once `RunAsync` is called, the Build thread must not touch UI objects (the transfer is debug-observable — `VerifyAccess` from the old thread throws after it). The `Func<Window>` overload sidesteps the hand-off entirely (factory runs post-transfer). In `UITestHost`, no thread is spawned and no transfer occurs — the creating thread is the UI thread and steps frames manually.

The loop's two async touchpoints are handled by **bounded blocking**: `Output.Writer.Write(...)` (buffer copy) and `FlushAsync().GetAwaiter().GetResult()` (one flush per frame; the pipe's drain side runs on the thread pool, so this cannot deadlock against the UI thread; library internals are `ConfigureAwait(false)` throughout).

`UISynchronizationContext` is installed via `SynchronizationContext.SetSynchronizationContext` at loop start (test host: set/restored around each `RunFrame`). Async event-handler continuations therefore land in the dispatcher queue and execute in a subsequent frame's dispatcher-drain phase — frame-coherent by construction. **It is uninstalled (`SetSynchronizationContext(null)`) as the first statement of `RunTeardown`** — see §3.6.

### 3.2 Input pump

```csharp
// Assembly (startup, once — single-shot contract honored: exactly one ReadAllAsync per session):
IAsyncInputDevice device = host.Input;
if (options.KeyReleaseSynthesis is { } krs)            // OPT-IN ONLY, see §6
    device = new KeyReleaseSynthesizer(device, krs.UpTimeout, krs.RepeatTimeout, options.TimeProvider);
device = device.WithClickSynthesis(options.ClickOptions);   // default on: ClickCount + Click events
EffectiveInputCapabilities = device.Capabilities;            // post-decoration surface for S3 (§2.1)
// SupportsAltKeyTracking is computed from host.Capabilities.Input — the UNDECORATED snapshot — and is
// therefore immune to KeyReleaseSynthesizer's unconditional DistinguishesKeyUpDown/ReportsRepeats claims.

_pumpTask = Task.Run(async () =>
{
    try
    {
        await foreach (var e in device.ReadAllAsync(_pumpCts.Token).ConfigureAwait(false))
        { _inputQueue.Enqueue(e); _dispatcher.Wake(); }
        _streamEnded = true; _dispatcher.Wake();        // EOF (terminal closed) ⇒ loop initiates shutdown
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Interlocked.Exchange(ref _pumpFault, ex); _dispatcher.Wake(); }   // surfaced on UI thread, §3.7
});
```

- `_inputQueue` is a `ConcurrentQueue<InputEvent>`; the channel under `VtInputDevice` is unbounded, so the pump never back-pressures the terminal; the loop drains promptly each frame.
- **We use the pull surface, never `EventInputDevice`** — `EventInputDevice` swallows handler exceptions silently (input.md); the pull pump gives us full exception visibility, which S6 owns.
- **The decorated device is never disposed by S6.** `TransformingInputDevice`/`KeyReleaseSynthesizer` dispose their inner device, and `host.Input` must stay alive through negotiator restore (the session's disposal drains trailing reports through it). Teardown cancels `_pumpCts`, awaits `_pumpTask` (blocking — §3.6), and lets `host.DisposeAsync()` own device lifecycle.
- **`KeyReleaseSynthesizer` stance: OFF by default.** Its "held" view flickers at OS auto-repeat granularity, its `DistinguishesKeyUpDown=true` claim is timer-derived best-effort, and it can never produce modifier-key events. Opting in does **not** change `SupportsAltKeyTracking` (undecorated provenance, above) — requirement 6's gate stays honest even with the synthesizer on; it *does* surface in `EffectiveInputCapabilities`, which is the surface S3 reads for everything except the Alt gate. Wrap order is synthesizer innermost, click transform outermost.
- `RenegotiateAsync` parks the *session's* internal pump; our enumeration simply sees no events during the ~500 ms window — no pump action required (output-side gating is §3.5).
- BYO sessions/hosts receive no `ResizeEvent`s (input.md); embedders call `UIApplication.NotifyResized(cols, rows)`, which enqueues a synthesized `ResizeEvent` into `_inputQueue` + `Wake()` — same coalescing path as everything else.

### 3.3 The frame loop (normative phase order)

`Guarded(...)` below is the exception funnel of §3.7 — **a pattern, not a delegate-taking wrapper**: each phase wraps its user-code calls in an inline `try/catch` (or a cached delegate where one is structurally required). Phase 1 in particular allocates **no closures per event** — at any-event motion rates that would be a closure per cell crossed.

```
RunLoop():                                       // dedicated UI thread (FrameLoop core shared with UITestHost)
  dispatcher.TransferOwnershipToCurrentThread()  // pin affinity; set thread-local Current
  SetSynchronizationContext(uiSyncContext)
  styling.OnCapabilitiesChanged(Capabilities)    // RECORDS capabilities + ThemeVariant; no roots exist yet —
                                                 // stamping happens at root attachment (hook contract, §4)
  windowSystem.Show(ResolveMainWindow())         // factory overload: invoke factory HERE, on the UI thread
  while (!_shutdownRequested):
    ts      = timeProvider.GetTimestamp()
    elapsed = timeProvider.GetElapsedTime(_appStartTimestamp)
    time    = new FrameTime(_frame, elapsed, elapsed - _lastElapsed)          // PHASE 0 — one timestamp
    bool resizedThisFrame = false                                             // frame-local, never sticks

    // PHASE 1 — input drain (drain to empty: producer is the pump only; no self-feeding; inline try/catch)
    ResizeEvent? resize = null
    while (_inputQueue.TryDequeue(out var e)):
      switch e:
        ResizeEvent r          → resize = r                                   // coalesce: last one wins
        DeviceResponseEvent d  → foreach sink in snapshot: try sink(d) catch ex → RaiseUnhandled(ex)
        FocusEvent / other     → try { if (!inputTarget.Dispatch(e)) ApplyDefaultGestures(e) }
                                 catch ex → RaiseUnhandled(ex)
    if (resize is { Columns: >0, Rows: >0 }): ApplyResize(resize); resizedThisFrame = true   // §3.4
    var fault = Interlocked.Exchange(ref _pumpFault, null)                    // raised ONCE, then cleared
    if (fault != null): RaiseUnhandled(fault)     // fatal unless Handled; Handled ⇒ app runs on with input
                                                  // PERMANENTLY DEAD (single-shot device — no restart exists)
    if (_streamEnded): Shutdown(0)

    // PHASE 2 — dispatcher drain (SNAPSHOT count: jobs posted during the drain run NEXT frame — no live-lock)
    int n = _jobs.Count
    while (n-- > 0 && _jobs.TryDequeue(out var job)): try job.Run() catch ex → RaiseUnhandled(ex)

    // PHASE 3 — styling activation flush (frame-coherence: all pseudo/class flips from phases 1–2 reach
    // fixpoint BEFORE animation/layout/render; per-event BeginInteractionUpdate scopes are S3's — S6
    // debug-asserts none remain open, then drains the queued-flip fixpoint loop)
    Guarded: styling.FlushPendingActivations()

    // PHASE 4 — animation tick (S5; ONE timestamp; values land at BindingPriority.Animation via
    // AnimatedValueHandle; completion callbacks run inline here). Then a second cheap styling flush
    // catches animation-driven pseudo flips, and TickNewlyStarted samples any storyboard ignited THIS
    // frame (including by that second flush) at elapsed-zero — no one-frame From-snap.
    Guarded: animation.Tick(in time)
    Guarded: styling.FlushPendingActivations()
    Guarded: animation.TickNewlyStarted(in time)

    // PHASE 5 — layout (dirty-measure/arrange lists, top-down; layout may invalidate layout → iterate)
    bool layoutRan = false
    for (int pass = 0; layout.HasPendingLayout && pass < 8; pass++):
      Guarded: layout.RunLayoutPass(); layoutRan = true
    if (layout.HasPendingLayout):
      layout.AbandonPendingLayout()               // GIVE UP until the next explicit invalidation — a
      DiagnosticWarnRateLimited("layout did not converge in 8 passes")   // non-converging layout must not
                                                  // pin HasPendingLayout=true and hot-loop the idle phase

    // PHASE 6 — render (GATED on !_renegotiating: the negotiator owns the output pipe during its window)
    bool rendered = false
    if (!_renegotiating):
      renderNeeded = renderSystem.HasDirtyVisuals || layoutRan || resizedThisFrame
                     || Interlocked.Exchange(ref _renderRequested, 0) != 0
      if (renderNeeded):
        bool changed = GuardedRender()            // renderSystem.RenderFrame(_buffer, in time) under the
                                                  // funnel; HANDLED exception ⇒ changed = true (conservative
                                                  // emit — compositing invariant makes the buffer safe);
                                                  // unhandled ⇒ guard.IsFatal, break to teardown
        if (changed || resizedThisFrame):
          _scratch.ResetWrittenCount()                                        // pooled ArrayBufferWriter<byte>
          _renderer.Render(_buffer, _scratch)     // diff (sync-output brackets emitted BY the renderer)
          rendered = true
      if (!_controlSequences.IsEmpty):            // out-of-band OSC channel (§2.1) — AFTER the delta
        if (!rendered): _scratch.ResetWrittenCount()
        while (_controlSequences.TryDequeue(out var payload)): try payload(_scratch) catch ex → RaiseUnhandled(ex)
        rendered = true
      if (rendered && _scratch.WrittenCount > 0):
        Output.Writer.Write(_scratch.WrittenSpan)                             // ONE write…
        Output.Writer.FlushAsync().GetAwaiter().GetResult()                   // …ONE flush = one frame

    // PHASE 7 — idle (every path below is paced: there is NO unpaced re-entry into the loop)
    if (_shutdownRequested) break
    remaining   = FrameInterval - timeProvider.GetElapsedTime(ts)
    workPending = renderSystem.HasDirtyVisuals || layout.HasPendingLayout || styling.HasPendingActivations
    if (workPending):                             // late invalidation (incl. styling flips queued in
      if (remaining > 0) _wake.Wait(remaining)    // phases 5–6) — run another frame, but at frame pace
    else if (animation.HasActiveAnimations):
      if (remaining > 0) _wake.Wait(remaining)    // paced mode; early wake on input/post is fine, we just loop
    else:
      // EVENT-DRIVEN with rate clamp: when this frame rendered, first wait out the remaining frame budget —
      // events arriving meanwhile (mouse-move storms under any-event tracking) coalesce into ONE next frame,
      // so a pointer sweep renders at most 1/FrameInterval, never hundreds of fps. Then park for free.
      if (rendered && remaining > 0 && _wake.Wait(remaining)):
        { }                                       // signaled during the clamp ⇒ proceed to next frame
      else:
        _wake.Wait()                              // park until input/Post/RequestRender/QueueControlSequence/Shutdown
    _frame++; _lastElapsed = elapsed              // SINGLE advancement point — every path updates BOTH
  RunTeardown()                                   // §3.6 — also runs from finally on fatal exception
```

- **Wake protocol (normative — the flag-clear ordering is load-bearing).** `_wake` is `SemaphoreSlim(0,1)` + an `Interlocked` signaled flag. *Producer:* enqueue the item **first**, then CAS the flag 0→1, and `Release()` **only on CAS success** (at-most-once release; no `SemaphoreFullException`). *Consumer:* on return from `Wait`, **clear the flag first**, then drain the queues. A wake arriving between Wait-return and flag-clear is absorbed by the imminent drain; a wake arriving after flag-clear re-signals and re-releases. The wrong order (clear after drain) loses the race where a producer enqueues post-drain, sees the flag still set, skips `Release`, and the loop parks on a non-empty queue — a frozen app.
- Wake sources: input enqueue, `Post`/`InvokeAsync`, `RequestRender`, `QueueControlSequence`, `NotifyResized`, `Shutdown`, pump fault/EOF. Animations starting never need a wake — they can only start from UI-thread code inside a frame, so the same frame's Phase-7 check sees `HasActiveAnimations`. Styling activations *do* participate in the Phase-7 guard (`HasPendingActivations`) because layout/render phases can queue flips after both flush points — without the guard a flip would sit unapplied until the next input event (a stale-UI liveness hole).
- **FrameInterval policy:** `1000 / framesPerSecond` ms, default 30 fps (between the demos' 25–50 fps band). All waits measure from frame *start*, so heavy frames self-throttle toward the budget; when idle, zero CPU. Input latency while animating or during the event-driven clamp ≤ one FrameInterval (acceptable; documented).

### 3.4 Resize

`ApplyResize(r)`: `_buffer.Resize(r.Columns, r.Rows)` (contents discarded — by contract the renderer full-redraws on dimension change), `renderSystem.OnViewportResized(new Size(r.Columns, r.Rows))` (S4 re-arranges windows, invalidates affected scenes; window roots `InvalidateMeasure` ⇒ full relayout lands in Phase 5 of the **same frame**). Coalescing means a drag-resize storm costs one relayout per frame, not per event. Coalescing also means Phase 1 dispatches the frame's other events *before* applying the (last) resize — a deliberate, documented reorder of the in-band stream (full fidelity is unachievable in one frame anyway since layout runs in Phase 5); the S3 contract therefore requires position clamping (§4). Initial size comes from `host.QuerySizeAsync()` at startup (fallback `Console.WindowWidth/Height` in try/catch, then 80×24); the happy-path session's startup `ResizeEvent` corrects it in frame 0/1.

### 3.5 Startup and `RenegotiateAsync`

**Startup (inside `RunAsync`, before the UI thread starts):**
1. `host = builderHost ?? new TerminalSessionHost(await TerminalSession.OpenAsync(sessionOptionsWithEmergencyBytes, ct))` — `WithSession` wraps the supplied session in a `TerminalSessionHost`; `WithTerminalHost` uses the host directly.
2. Initial size query (above).
3. `caps = host.Capabilities`.
4. **Palette theming + capability rewrite** (the demos' sanctioned pattern): if a theme is configured, `palette = new TerminalPalette(host.Output, caps.Output)`; when `palette.IsSupported`, apply `Set`/`SetForeground`/`SetBackground`/`SetCursor`, then `caps = caps with { Output = caps.Output with { Color = caps.Output.Color with { DefaultForeground = theme.Foreground ?? …, DefaultBackground = theme.Background ?? … } } }` — so `CellBuffer` alpha-composites against the real themed RGB. Register `palette.OnInputEvent` as a device-response sink (its queries need responses routed back). Post-startup palette mutations route through `QueueControlSequence` (the startup writes happen before the loop owns the pipe, so direct sink writes are safe only here).
5. `ThemeVariant = ThemeVariant.FromCapabilities(caps)` (post-rewrite, so luminance reflects the themed background).
6. `new CellBuffer(cols, rows, caps) { CursorVisible = false }`; `new FrameRenderer(caps.Output, new FrameRendererOptions(OrderedDither: options.OrderedDither))` — **always the negotiated capabilities** (quantization protection).
7. Enter UI mode: if `UseAlternateScreen && caps.Output.Window.AlternateScreenBuffer` → `ScreenWriter.WriteEnterAlternateScreen`; else `ScreenWriter.WriteClearScreen` fallback; then `SgrEncoder.WriteReset`; flush. Cursor hiding is left to the buffer (`CursorVisible = false` ⇒ DECRST 25 on frame 0).
8. Start pump; start UI thread (which performs §3.0 step 3 composition, then the loop).

**`RenegotiateAsync` (UI thread, rare, "don't call mid-interaction"):** set `_renegotiating = true` **before** the await — Phase 6 (renderer emission *and* the control-sequence drain) is gated on it, because the session's negotiator writes probe sequences to the same `PipeWriter` from thread-pool continuations during the ~500 ms window; `PipeWriter` is not thread-safe and even serialized interleaving would split frame escape sequences around probe traffic. Then `await host.RenegotiateAsync()` (continuation resumes via the dispatcher); on success: `old = Capabilities; new = host.Capabilities` (+ palette capability-rewrite re-applied if themed); `_renderer.Close(_scratch)` → flush (erases overlay fragments, restores autowrap — correct mid-session because the next frame's renderer re-disables autowrap anyway); rebuild `FrameRenderer` and `CellBuffer` with the new capabilities; recompute `ThemeVariant` and `EffectiveInputCapabilities` (re-apply recorded decoration projections); `styling.OnCapabilitiesChanged(new)` (re-stamps `caps-truecolor|ansi256|ansi16|nocolor`, `caps-motion`, `caps-kitty-keyboard`, `caps-unicode|ascii` on attached visual roots and pulses `ResourceDictionary.Changed` for theme re-resolution); raise `CapabilitiesChanged`; clear `_renegotiating`; force full relayout + redraw (`RequestRender` stays set across the window, so the first post-renegotiate frame repaints). On failure the session guarantees the old negotiator stays — S6 clears the flag and changes nothing else.

### 3.6 Shutdown and teardown

`Shutdown(int)` is thread-safe: CAS the exit code, set `_shutdownRequested`, cancel `ShutdownToken`, `Wake()`. Window-close policy: S4 raises `WindowClosed`; S6 applies `ShutdownMode` (`OnMainWindowClose`: main window closed ⇒ `Shutdown(0)`; `OnLastWindowClose`: `OpenWindowCount == 0` ⇒ `Shutdown(0)`; `OnExplicitShutdown`: never automatic). Default gesture: an *unhandled* Ctrl+C `KeyEvent` (raw mode — Ctrl+C is input, not SIGINT) triggers `Shutdown(0)` when `ExitOnUnhandledCtrlC` (routed through S3 first so a text box can claim it for copy).

**Teardown — the canonical order, verbatim from rendering-session.md §7** (runs in `finally`, so fatal exceptions still restore the terminal; every step best-effort/idempotent):

0. **`SynchronizationContext.SetSynchronizationContext(null)`** — first statement. Every await below is a blocking `GetAwaiter().GetResult()` (safe: library internals are `ConfigureAwait(false)` throughout). Without this, any captured continuation would post to a dispatcher nobody drains again and `RunAsync` would never complete — the exact failure §3.7 exists to prevent. **Then drain `_jobs` to empty, completing every `InvokeAsync` `Completion` as canceled** (the job actions are NOT run); from this point `InvokeAsync` returns a canceled `Task` and `Post` is dropped — no caller awaits into the void.
1. Cancel pump (`_pumpCts.Cancel()`, blocking-wait `_pumpTask`).
2. `_renderer.Close(scratch)` — fragment erases + re-enable autowrap (**must run before leaving the session** or the shell inherits a no-wrap terminal).
3. `CursorWriter.WriteShow(scratch)`.
4. `SgrEncoder.WriteReset(scratch)`.
5. `ScreenWriter.WriteLeaveAlternateScreen(scratch)` (or `WriteClearScreen` on the no-alt-screen fallback).
6. Write + blocking flush (one shot).
7. `palette?.Dispose()` — OSC resets while the sink is still open.
8. `host.DisposeAsync()` blocking-wait (skipped for BYO host/session unless `disposeWithApp`) — opt-in disables, cooked-mode restore.
9. Only now is `Console.WriteLine` safe (raw-mode LF caveat); `RunAsync` completes with the exit code (or rethrows the fatal exception). Thread-local `Current` is cleared on the UI thread here and on the disposing thread in `DisposeAsync`.

**Signal-net interplay:** the happy-path session already restores opt-ins + termios and `Environment.Exit(128+sig)` on SIGINT/SIGTERM/SIGHUP/SIGQUIT — but it knows nothing of our alt screen / hidden cursor. **S6 depends on the additive Core seam `TerminalSessionOptions.EmergencyRestoreBytes`** (`ReadOnlyMemory<byte>`, written via the existing `IStdioTransports.WriteBytesSync` — a direct, signal-safe syscall — at the top of the session's signal path, before termios restore). S6 supplies a conservative, unconditional byte string cached at startup: show cursor + SGR reset + leave-alt-screen + enable-autowrap (each is an idempotent no-op when not applicable, so the bytes can be fixed before negotiation completes). This is invariant-7-clean (opaque, additive, the session never interprets the bytes). **A PipeWriter-based fallback is explicitly NOT viable** — writing/flushing the sink from a signal handler races a mid-frame UI-thread write, and the flush may never drain before `Environment.Exit`; if the seam cannot land, the documented fallback is S6's own `PosixSignalRegistration` performing a direct `write(2)` to fd 1 (P/Invoke; ugly but honest). **Scope: owned happy-path sessions only.** BYO sessions and BYO hosts get neither the seam bytes nor any S6 signal registration — BYO embedders explicitly own their signal strategy (CLAUDE.md contract), and S6 must not hijack process exit there (`ITerminalHost.OwnsSignalHandling` gates this).

### 3.7 Exception policy

Every user-code entry point in the loop (`Dispatch`, dispatcher jobs, styling flush, animation tick incl. completion callbacks, measure/arrange, draw delegates inside `RenderFrame`) runs through the **funnel**: catch → raise `DispatcherUnhandledException` on the UI thread → if `Handled`, continue the frame (the phase's remaining work proceeds; a failed draw leaves that scene's previous raster — safe under the compositing model); if not `Handled`, record as fatal, run **full canonical teardown**, then rethrow from `RunAsync`. The terminal is *always* restored before the exception escapes — a TUI crash that leaves the user's shell in raw alt-screen is the one unforgivable failure.

Funnel mechanics, made precise:
- The funnel is a **pattern** (inline try/catch per phase; cached delegates where a delegate shape is required) — no per-event closure allocations in Phase 1's hot path.
- **Draw delegates:** S6 cannot guard code it never calls — so the funnel is **passed down**: `IRenderSystem` receives an `IUserCodeGuard` at composition (§3.0) and must route every app draw delegate through `guard.Run(...)`. A handled draw exception leaves that scene's previous raster and `RenderFrame` continues with the remaining scenes; **`RenderFrame`'s result under a handled exception is defined as `changed = true`** (conservatively emit — the compositing invariant makes the buffer safe-by-construction; skipping emission could freeze a half-updated screen). On an unhandled exception the guard records fatal (`IsFatal`), `RenderFrame` returns immediately, and the loop unwinds to teardown.
- **The handler itself throwing** is fatal immediately: the handler's exception is recorded (as an `AggregateException` with the original), the event is NOT re-raised (no recursion), teardown runs, `RunAsync` rethrows.
- **`OperationCanceledException` whose token is `ShutdownToken`** bypasses the funnel entirely — it is cooperative shutdown cancellation, not an app error.
- `InvokeAsync` exceptions go to the returned `Task` only (caller's responsibility, WPF semantics); `Post` exceptions go to `DispatcherUnhandledException`. Pump faults surface at the top of the next frame (Phase 1) through the same event, **exactly once** (the fault slot is exchanged to null on raise); setting `Handled = true` on a pump fault means the app continues with **input permanently dead** — the device is single-shot and there is no restart; the typical handled response is a graceful save-and-`Shutdown`. Because S6 uses the pull input surface, **no exception is ever silently swallowed** (the `EventInputDevice` swallowing behavior is structurally avoided).

### 3.8 Data structures (complete list)

- `ConcurrentQueue<InputEvent> _inputQueue` (pump → loop; also fed by `NotifyResized` and `UITestHost.SendInput`).
- `ConcurrentQueue<DispatcherJob> _jobs`; `DispatcherJob` is a `readonly struct { Action? Plain; Action<object?>? Stateful; object? State; TaskCompletionSource<object?>? Completion }`.
- `ConcurrentQueue<Action<IBufferWriter<byte>>> _controlSequences` (out-of-band OSC channel, drained Phase 6).
- Auto-reset wake: `SemaphoreSlim(0,1)` + `int _wakeSignaled` (Interlocked) — protocol normative in §3.3.
- `ArrayBufferWriter<byte> _scratch` — **pooled across frames**, `ResetWrittenCount()` per frame (the maps call out the demos' per-frame allocation as the thing a framework should fix).
- `int _renderRequested` (Interlocked, the sanctioned cross-thread invalidate, mirroring `InteractiveDemo.Invalidate`).
- `Exception? _pumpFault` (Interlocked slot, exchanged-to-null on raise); `bool _renegotiating` (UI-thread written, loop-read).
- Cached emergency-restore byte array (show cursor + SGR reset + leave alt + autowrap) — handed to the Core seam.
- `List<Action<DeviceResponseEvent>> _responseSinks` (UI-thread mutated; snapshot-iterated).
- `long _appStartTimestamp` (`TimeProvider.GetTimestamp()`); all frame time derives from `GetElapsedTime` — fake-clock friendly.

---

## 4. Cross-subsystem contracts

All interfaces below are `Cursorial.UI` internal-ish seams (public where other assemblies need them). All calls happen **on the UI thread** unless noted.

**OWNED BY S6 — the terminal host seam (invariant-7-clean; no Core change):**
```csharp
public interface ITerminalHost : IAsyncDisposable
{
    TerminalCapabilities Capabilities { get; }     // volatile snapshot; replaced by RenegotiateAsync
    IAsyncInputDevice Input { get; }               // single-shot per host lifetime
    IOutputByteSink Output { get; }
    bool OwnsSignalHandling { get; }               // true ⇒ host registered a signal net (happy-path session);
                                                   // false ⇒ embedder owns signals — S6 registers nothing
    ValueTask<(int Columns, int Rows)?> QuerySizeAsync(CancellationToken ct = default);
    ValueTask RenegotiateAsync(CancellationToken ct = default);   // hosts that can't: no-op
}
```
`TerminalSessionHost` adapts the sealed `TerminalSession` (both `OpenAsync` factories) — this is the only path that runs real negotiation/probes. `SyntheticTerminalHost` (in `Cursorial.UI.Testing`) implements it directly: scripted `TerminalCapabilities`, an in-memory `IOutputByteSink` (`Pipe`-backed; byte capture for `LastFrameBytes`), and a directly-constructed `VtInputDevice(source, caps, mode, timeProvider: Time, escapeAmbiguityTimeout)` over an in-memory byte source for the `SendBytes` parser path — synchronous to create, no probes, no TTY, single fake clock domain.

**OWNED BY S6 — the user-code funnel handed to S1/S4:**
```csharp
public interface IUserCodeGuard
{
    /// Runs app code under §3.7's policy. Returns true when the code completed or its exception was
    /// Handled; false when an unhandled exception was recorded as fatal — the caller must unwind promptly.
    bool Run<TState>(TState state, Action<TState> userCode);
    bool IsFatal { get; }
}
```

**REQUIRES from S3 (input routing):**
```csharp
public interface IInputDispatchTarget
{
    /// Phase 1, one event at a time, in arrival order. Returns true when handled (suppresses default gestures).
    /// S3 owns hit testing (cheap! Move fires per cell crossed under any-event tracking), routing, focus,
    /// access keys, and opening/closing per-event InteractionUpdateScope batches (styling §3.5).
    /// COORDINATE CAVEAT: resize coalescing (§3.4) means Dispatch may receive positions outside the
    /// current viewport bounds (pre-resize events, post-resize hit state, or negative drag coords) —
    /// S3 must clamp, never throw.
    bool Dispatch(InputEvent inputEvent);
}
```
S6 guarantees: events arrive only between frames' Phase 1 boundaries, never re-entrantly; `FocusEvent`, `PasteEvent`, `UnknownEvent` all flow through `Dispatch`; `ResizeEvent`/`DeviceResponseEvent` never do.

**REQUIRES from Fork B (styling engine):**
```csharp
public interface IStyleFrameHooks
{
    void FlushPendingActivations();      // drain queued pseudo-flip fixpoint (generation-capped per proposal §3.5);
                                         // called at Phase 3 and after the animation tick; MUST be cheap when empty.
    bool HasPendingActivations { get; }  // true when the flip queue is non-empty; participates in the Phase 7
                                         // idle guard and RunUntilIdle — flips queued during layout/render
                                         // (scrollbar-visibility-style PseudoClassMappings) must trigger
                                         // another frame, not sit until the next input event. O(1).
    void OnCapabilitiesChanged(TerminalCapabilities capabilities);
                                         // CONTRACT: record the capability snapshot (+ ThemeVariant inputs).
                                         // Stamping of caps-* classes happens at visual-root ATTACHMENT for
                                         // roots that don't exist yet, and immediately for already-attached
                                         // roots (the renegotiate re-stamp path). The startup call (§3.3,
                                         // pre-Show) therefore stamps nothing — by design.
}
```

**REQUIRES from S5 (animation/storyboards):**
```csharp
public interface IAnimationFrameDriver
{
    void Tick(in FrameTime time);        // sample every active storyboard ONCE at time.Elapsed; apply via
                                         // AnimatedValueHandle<T>.SetValue; fire completion callbacks inline.
    void TickNewlyStarted(in FrameTime time);
                                         // sample storyboards registered AFTER this frame's Tick (e.g. ignited
                                         // by the post-tick styling flush) at their elapsed-zero value so the
                                         // frame renders From, not the pre-animation value (no one-frame snap).
                                         // MUST be a cheap no-op when none started.
    bool HasActiveAnimations { get; }    // drives paced-vs-event-driven idle (Phase 7); perpetual repeats count.
}
```

**REQUIRES from S2/tree (layout):**
```csharp
public interface ILayoutSystem
{
    bool HasPendingLayout { get; }
    void RunLayoutPass();                // one top-down dirty-measure + dirty-arrange sweep across all windows
    void AbandonPendingLayout();         // clear all dirty-layout state after the per-frame pass cap (8) — a
                                         // non-converging layout must not pin HasPendingLayout and spin the
                                         // loop; the next explicit invalidation re-arms normally.
}
```

**REQUIRES from S4 (window manager) / S1 (visual/scene):**
```csharp
public interface IRenderSystem
{
    bool HasDirtyVisuals { get; }
    /// Re-Draw invalidated scenes only (re-raster); refresh CompositeParameters (re-composite); assemble the
    /// SceneLayer z-stack; SceneCompositor.Composite(layers, target) — target is RETAINED, never cleared;
    /// set buffer cursor state (caret). Returns true when the target changed (compositor bool ∥ cursor ∥
    /// fragments). EXCEPTIONS: every app draw delegate runs through the IUserCodeGuard supplied at
    /// composition; a handled draw leaves that scene's previous raster and rendering continues; on
    /// guard.IsFatal return immediately. S6 treats a handled-exception frame as changed = true.
    bool RenderFrame(CellBuffer target, in FrameTime time);
    void OnViewportResized(Size newSize);
}

public interface IWindowSystem            // merged lifecycle + showing seam (one S4 object typically
{                                         // implements IWindowSystem AND IRenderSystem)
    void Show(Window window);             // called by S6 for the main window pre-frame-0; by app code after
    event Action<Window> WindowClosed;    // S6 applies ShutdownMode
    int OpenWindowCount { get; }
    Window? MainWindow { get; }
    // NORMATIVE (loop-forced): modal dialogs are async-only — Task<TResult> ShowDialogAsync(...) completed
    // on close. No nested dispatcher loop exists; a blocking ShowDialog would deadlock by construction
    // and is not provided.
}
```

**REQUIRES from Core (additive seam, invariant 7 — stated dependency, see §3.6):** `TerminalSessionOptions.EmergencyRestoreBytes` (`ReadOnlyMemory<byte>` the session's signal handler writes via the existing `IStdioTransports.WriteBytesSync` before termios restore). Documented fallback if it cannot land: S6-owned `PosixSignalRegistration` + direct `write(2)` to fd 1, owned-sessions only.

**PROVIDES to everyone:** `UIDispatcher` (Fork A's marshal point — styling proposal: "cross-thread viewmodel changes marshal through Fork A's dispatcher" = this one; `VerifyAccess` backs invariant 6's debug asserts); `FrameTime`/`CurrentFrameTime` (the single per-frame clock, `TimeProvider`-derived); `Capabilities` + `CapabilitiesChanged` + `EffectiveInputCapabilities` (styling C7; S3's gesture/click introspection; `SupportsAltKeyTracking` precomputes input.md §7's exact condition from the undecorated snapshot); `RegisterDeviceResponseSink` (palette, future graphics queries); `RequestRender` (thread-safe invalidate); `QueueControlSequence` (the only sanctioned out-of-band byte path — S4 window titles, clipboard, pointer shapes); `NotifyResized` (BYO embedders); app-level `Resources`/`Styles` scope objects (Fork B owns their types; S6 hosts the instances at the lookup-walk terminus); `IUserCodeGuard` (S1/S4 draw funnel); `UITestHost` (every subsystem's integration-test harness).

---

## 5. Requirement mapping

| Req | S6's coverage |
|---|---|
| **6 (access keys)** | Capability truth: `SupportsAltKeyTracking` implements input.md §7's exact gate (`DistinguishesKeyUpDown && (ReportsRepeats ∥ Win32InputMode)` — i.e. Kitty `ReportAllKeysAsEscapeCodes`+`ReportEventTypes` or Win32 input mode), computed from the **undecorated** session capabilities so neither default-off nor opted-in `KeyReleaseSynthesizer` can make it lie (it can't produce Alt events); default `NegotiationOptions` already requests all five Kitty flags. `FocusEvent` delivery through `Dispatch` lets S3 clear Alt-held on focus-out. S3 owns the `:access-keys` toggle itself and reads `EffectiveInputCapabilities` for everything non-Alt. |
| **10 (animation)** | The frame loop is the orchestration half of the mechanism/orchestration split (design-doc §7): one `FrameTime` per frame to `IAnimationFrameDriver.Tick` (+ `TickNewlyStarted` for same-frame ignitions), paced mode while `HasActiveAnimations`, event-driven idle (rate-clamped) otherwise. |
| **4, 5 (focus, windows)** | Enabling substrate: ordered single-thread dispatch for S3's focus engine; `ShutdownMode` + `WindowClosed` policy; modal/modeless windows render through `IRenderSystem` with no S6 changes. **Normative for req 5:** no nested dispatcher loops ⇒ modal results are async-only (`Task<TResult> ShowDialogAsync`); window titles (OSC 2) flow through `QueueControlSequence`. |
| **2 (binding)** | `UIDispatcher` + `UISynchronizationContext` are the cross-thread marshal for INPC/`When`-watcher callbacks. |
| **1, 3, 8** | Hosting only: app `Resources`/`Styles`, the styling flush phases + `HasPendingActivations` liveness guard, capability-class stamping calls. |

**Invariant compliance:** *Frame coherence* (1): phases 1–2 (all property writes) strictly precede 3 (styling) → 4 (animation) → 5 (layout) → 6 (render) within one frame; no priority tiers; dispatcher snapshot-drain keeps posted work frame-atomic. *Styling never touches Scene/CellBuffer* (2): `IStyleFrameHooks` has no buffer access; only `IRenderSystem` (S1/S4) sees the buffer. *Re-composite vs re-raster* (3): S6 orders calls so animated `AffectsComposite` changes reach `RenderFrame` as parameter refreshes — S6 never forces `Scene.Invalidate`. *Retraction is store-owned* (4): S6 performs no value set-backs anywhere (teardown is byte-level, not property-level). *Template barrier* (5): N/A — S6 never matches styles. *Single UI thread* (6): dedicated thread with one-shot ownership hand-off, dispatcher, sync context, `VerifyAccess`. *Additive lower layers* (7): the only Core change is the opt-in `EmergencyRestoreBytes` seam; `ITerminalHost` lives in `Cursorial.UI`.

---

## 6. Terminal-specific design (deviations from WPF/Avalonia)

1. **No vsync, no `CompositionTarget.Rendering`** — a fixed `FrameInterval` with an event-driven idle mode, formalizing the demos' two loop modes (`Animated=true` ticks; `Animated=false` renders on input/resize/invalidate — rendering-session.md §7), plus a rate clamp so event-driven rendering also never exceeds 1/FrameInterval. Idle frames cost zero CPU and, thanks to the scene cache + front-buffer diff, zero bytes.
2. **One render target, one byte stream** — windows are `SceneLayer`s composited into a single `CellBuffer`, not OS windows; `FrameRenderer` is the *sole owner* of terminal SGR/hyperlink/cursor state, so S6 enforces "nobody writes raw bytes between frames" structurally: the sink is not exposed to app code, the renderer emits sync-output brackets itself, and the one legitimate out-of-band need (OSC-class: titles, clipboard, pointer shape, palette) has a sanctioned channel — `QueueControlSequence`, emitted inside the frame's single flush *after* the delta, with SGR/CUP-class sequences documented as forbidden.
3. **Resize is input data** — `ResizeEvent` arrives *in-band* on the input stream (ordered with keystrokes), is coalesced per frame (a documented reorder; S3 clamps), and `CellBuffer.Resize` discards contents ⇒ full relayout + full redraw, unlike WPF's incremental `SizeChanged`. BYO hosts get no events at all — `NotifyResized` is the embedder's injection point.
4. **Teardown is a correctness feature, not hygiene** — the terminal is the user's shell; the canonical close order (fragment erases, autowrap, cursor, alt-screen, palette resets, cooked mode) runs even on crash paths, the sync context is uninstalled first so teardown awaits can't deadlock on a dead dispatcher, and the signal net needs UI-mode bytes the session alone can't know about (the `EmergencyRestoreBytes` seam).
5. **`DeviceResponseEvent`s interleave with user input** (input.md) — WPF has no analog; S6's response router keeps protocol traffic out of S3's routing.
6. **`RenegotiateAsync`** — capabilities are a runtime-mutable snapshot; renderer/buffer rebuild + `caps-*` class re-stamp has no desktop equivalent; the output pipe is single-writer, so frame emission is gated off during the probe window.
7. **Raw mode inverts signal semantics** — Ctrl+C is a `KeyEvent`, not SIGINT; "exit gesture" is routed input policy, signals are external-kill only.
8. **Single-shot input device** (input.md) — exactly one pump per session; fan-out/dispatch is S6's, not the device's; decorators assemble before the pump and are never disposed by S6; a pump fault is unrecoverable by construction (no restart exists — `Handled` means "run on without input").
9. **Dispatcher has no priority tiers** (DECISIONS invariant 1) — WPF's `DispatcherPriority` ladder is deliberately absent; ordering is the frame-phase sequence. Likewise **no nested pumps** — modal is async-only.
10. **Blocking flush on a dedicated thread** — one `Write`+`FlushAsync().GetResult()` per frame (the §7 pattern) with a pooled scratch writer; terminal output is small enough that async loop machinery buys nothing and costs thread-affinity guarantees.

---

## 7. Phasing

**v1 spine:** `UIApplication` + builder/options; `ITerminalHost` + `TerminalSessionHost` (happy-path + BYO) + `SyntheticTerminalHost`; dedicated UI thread with ownership hand-off + thread-local `Current`; `UIDispatcher` + sync context (`Post`/`InvokeAsync`/`CheckAccess`/`VerifyAccess`, shutdown job-drain semantics); full frame loop (all 7 phases, paced + event-driven idle with rate clamp, normative wake protocol, styling-liveness guard); input pump with click synthesis + capability provenance split (`SupportsAltKeyTracking` vs `EffectiveInputCapabilities`); resize pipeline + `NotifyResized`; device-response router; `QueueControlSequence`; palette theming + capability rewrite; canonical teardown (sync-context uninstall, job cancellation) + crash-path teardown + `EmergencyRestoreBytes` seam coordination; exception policy incl. `IUserCodeGuard` pass-down; `ShutdownMode` + exit codes + Ctrl+C gesture; `UITestHost` with synthetic host, fake `TimeProvider`, manual stepping, `SendInput`/`SendBytes` (single clock domain), cell/byte assertions.

**Deferred (per the repo's §11 convention, with reasons):**
- **`RestrictToDirtyRegions` adoption** — requires an airtight "mark every changed cell" contract from S1/S4; the full-buffer diff is O(rows×cols) comparison only and fine at terminal scale. Re-add when profiling shows comparison cost.
- **`PauseIOAsync` integration** (suspend the loop to host `$EDITOR`/child processes) — session support exists; the loop needs a quiesce/restore-renderer (`Reset()`) dance; no v1 requirement.
- **Renegotiation-triggered live theme morph animation** — v1 re-stamps and redraws atomically; animated transitions need S5 cooperation.
- **Frame-skip/catch-up policy** (fixed-timestep accumulation when a frame overruns) — wall-clock `FrameTime.Elapsed` already makes animations drop-frame-tolerant; explicit catch-up only matters for game-style simulation, out of scope.
- **Windows console-buffer resize events** — Core TODO (CLAUDE.md); the loop consumes `ResizeEvent` source-agnostically, so this lands in Core with zero S6 change.
- **Multiple sessions / multiple UI threads per process** — thread-local `Current` already removes the static cross-wiring hazard; full multi-app support (shared thread pool pumps, per-app signal policy) is unvalidated and deferred.
- **Dispatcher priorities** — rejected permanently (DECISIONS invariant 1), recorded here so it isn't re-proposed.
- **Nested dispatcher loops / blocking `ShowDialog`** — rejected permanently (deadlock by construction; async-only modal is the contract), recorded so it isn't re-proposed.

---

## 8. Open questions

1. **Signal-path UI-mode restore — seam landing.** The design *depends on* the additive Core seam `TerminalSessionOptions.EmergencyRestoreBytes` (§3.6; opaque bytes written via the existing `IStdioTransports.WriteBytesSync` at the top of the session's signal path — invariant-7-clean). Remaining question is only the seam's final shape (options property vs. a post-open setter); if it cannot land at all, the specified fallback is S6's own `PosixSignalRegistration` + direct `write(2)` to fd 1, owned sessions only. The previously-described "write the cached bytes to the sink" fallback is withdrawn (not signal-safe; races the UI thread).
2. **Blocking flush vs. fully async loop.** *Recommendation:* keep the dedicated-thread blocking flush (one bounded wait per frame; pipe drain is thread-pool-side so no deadlock; preserves hard thread affinity). Revisit only if real terminals exhibit flow-control stalls (Ctrl+S) long enough to matter — at which point add a flush timeout + drop-to-event-driven degradation rather than an async loop.
3. **`InvokeAsync` from the UI thread: queue (chosen) or run inline?** Inline (WPF `Invoke`) breaks frame-phase ordering (an inline invoke during Phase 1 could run layout-mutating code before styling flush yet observe stale styling). *Recommendation:* always queue; provide `Dispatcher.CheckAccess()` so callers who want inline semantics just call the delegate directly — keeps the coherence story exact and the API honest.

---

## Critique disposition

**P0-1 — ACCEPTED.** `Build()` now constructs the `UIDispatcher` (owner = building thread) and sets thread-local `Current`; one-shot ownership transfer to the UI thread at loop start (debug-observable); added the preferred `RunAsync(Func<Window>)` factory overload; the `Window` overload's hand-off contract is documented; the §2.4 example uses the factory.
**P0-2 — ACCEPTED.** `ITerminalHost` defined in §4 (with `OwnsSignalHandling`); `TerminalSessionHost` adapts both `TerminalSession` factories; `SyntheticTerminalHost` implements it directly over an in-memory sink + a directly-constructed `VtInputDevice(timeProvider: Time)`; builder gains `WithTerminalHost`.
**P0-3 — ACCEPTED.** Teardown step 0: uninstall the sync context, blocking-wait all awaits, drain `_jobs` canceling every `InvokeAsync` completion; post-shutdown `InvokeAsync` returns canceled, `Post` is dropped.
**P0-4 — ACCEPTED.** `IStyleFrameHooks.HasPendingActivations` added; included in the Phase 7 guard and `RunUntilIdle`'s predicate.
**P1-5 — ACCEPTED.** `_renegotiating` flag set before the await; Phase 6 (render + control-sequence drain) gated on it; cleared in the continuation; `RequestRender` persists across the window.
**P1-6 — ACCEPTED.** `EmergencyRestoreBytes` promoted from preference to stated dependency; sink-write fallback withdrawn as not signal-safe; the only fallback is direct `write(2)`; S6 signal involvement scoped to owned happy-path sessions via `OwnsSignalHandling` (BYO exemption explicit).
**P1-7 — ACCEPTED.** `SupportsAltKeyTracking` computed from the host's undecorated `Capabilities.Input`; new `EffectiveInputCapabilities` (post-decoration, recomputed on renegotiate) for S3's click/gesture introspection.
**P1-8 — ACCEPTED.** Phase 7 rewritten: the late-invalidation path now pace-waits the remaining budget (no unpaced `continue`); event-driven mode gets a post-render rate clamp (≤ 1 render per `FrameInterval` under mouse-move storms); after the 8-pass cap, `ILayoutSystem.AbandonPendingLayout()` clears the dirty state and the diagnostic is rate-limited.
**P1-9 — ACCEPTED.** `UIApplication.QueueControlSequence` added (thread-safe queue, drained into `_scratch` after the renderer's delta, forces the frame flush, OSC-class-only contract documented); title/clipboard/pointer-shape/post-startup-palette routed through it; §6.2 reworded.
**P1-10 — ACCEPTED.** Normative no-nested-pumps / async-only-modal statement added to §1, `IWindowSystem` (§4), §5 req-5, and the permanent-rejection list in §7.
**P1-11 — ACCEPTED.** New §3.0 Composition (fixed construction order, `FrameLoop` core); `IWindowLifecycleEvents` merged into `IWindowSystem` with `Show(Window)`; the loop preamble now calls a seam that exists.
**P1-12 — ACCEPTED.** `IUserCodeGuard` defined and passed to `IRenderSystem` at composition; handled-draw semantics specified (previous raster kept, continue, `changed = true` conservative emit); unhandled ⇒ `IsFatal` + immediate unwind.
**P1-13 — ACCEPTED.** `Current` is `[ThreadStatic]` (set on Build thread and UI thread, cleared at teardown/dispose); `UITestHost` documented single-thread-affine; parallel hosts can't cross-wire.
**P1-14 — ACCEPTED.** Wake protocol made normative in §3.3: producer enqueue → CAS 0→1 → `Release` on CAS success only; consumer clears the flag *before* draining; race windows analyzed.
**P2-15 — ACCEPTED.** Single frame-advancement point (`_frame++; _lastElapsed = elapsed` on every path); `resizedThisFrame` is frame-local; `_pumpFault` exchanged-to-null on raise (fires once) with the "input permanently dead under `Handled`" consequence documented.
**P2-16 — ACCEPTED.** Folded into the `ITerminalHost` fix: the test host's parser path constructs its own `VtInputDevice` on `Time`; lone-ESC commitment requires `AdvanceTime` (documented in §2.3).
**P2-17 — ACCEPTED.** Resize-coalescing reorder made explicit; `IInputDispatchTarget.Dispatch` contract now requires clamping out-of-bounds positions, never throwing.
**P2-18 — ACCEPTED.** `IAnimationFrameDriver.TickNewlyStarted(in FrameTime)` added, called after the second styling flush; no one-frame `From`-snap.
**P2-19 — ACCEPTED.** Funnel specified as a pattern (inline try/catch per phase, cached delegates); Phase 1 is explicitly closure-free.
**P2-20 — ACCEPTED.** `OnCapabilitiesChanged` contract clarified: records the snapshot; stamping occurs at root attachment (and immediately for already-attached roots on renegotiate); the startup call's stamping-nothing behavior is by design.
**P2-21 — ACCEPTED.** `FrameBuffer` is a live accessor; `RunFrames` returns frames actually run; `AdvanceTime` remainder = one final partial step; `RunUntilIdle` predicate enumerated; `Build`/`RunAsync` single-use; "per process" → "per session"; handler-thrown exceptions are fatal without re-raise; `ShutdownToken` OCEs bypass the funnel.
**P2-22 — ACCEPTED.** `UIApplication.NotifyResized(int, int)` added (thread-safe, enqueues a synthesized `ResizeEvent`); BYO's lack of automatic resize documented in §3.2/§6.3.

No findings rebutted.