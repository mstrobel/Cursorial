using System.Buffers;
using System.Runtime.ExceptionServices;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Terminal;

// ReSharper disable EmptyGeneralCatchClause
// ReSharper disable CheckNamespace

namespace Cursorial.UI;

public sealed partial class UIApplication
{
    /// <summary>
    /// Raised once the application is fully constructed and has entered its dispatcher loop.
    /// </summary>
    public event EventHandler? Started;

    /// <summary>
    /// Raised at the beginning of the application shutdown process.
    /// </summary>
    public event EventHandler? BeginShutdown;

    /// <summary>
    /// PREFERRED entry point: async startup on the caller (host open, negotiation), then one
    /// dedicated foreground UI thread ("Cursorial UI") runs the loop synchronously;
    /// <paramref name="rootFactory"/> is invoked <b>on the UI thread</b> before the first frame —
    /// no construction-thread hand-off involved. Completes with the exit code after the canonical
    /// teardown; the terminal is restored before this returns. Single-use.
    /// </summary>
    /// <remarks>
    /// The factory parameter is <see cref="UIElement"/>-typed at P1 (the doc's
    /// <c>Func&lt;Window&gt;</c> shape lands with S4 at P7). The happy path throws
    /// <see cref="InvalidOperationException"/> when stdio is not a TTY (CI/pipes) — use
    /// <see cref="UIApplicationBuilder.WithSession"/>/<see cref="UIApplicationBuilder.WithTerminalHost"/>
    /// or <c>UITestHost</c> there.
    /// </remarks>
    public Task<int> RunAsync(Func<UIElement> rootFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootFactory);
        return RunCoreAsync(rootFactory, null, cancellationToken);
    }

    /// <summary>
    /// Hand-off sugar: <paramref name="rootElement"/> must have been constructed on the thread
    /// that called <c>Build()</c> (the dispatcher's pre-run owner); ownership transfers to the UI
    /// thread at loop start and the builder thread must not touch UI objects afterwards
    /// (debug-asserted via the dispatcher-based affinity capture). Single-use.
    /// </summary>
    public Task<int> RunAsync(UIElement rootElement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootElement);
        return RunCoreAsync(null, rootElement, cancellationToken);
    }

    private async Task<int> RunCoreAsync(Func<UIElement>? rootFactory, UIElement? prebuiltRoot, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runCalled, 1) != 0)
            throw new InvalidOperationException("RunAsync is single-use; create a new application to run again.");

        await StartupAsync(cancellationToken).ConfigureAwait(false);

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runTask = completion.Task;

        var thread = new Thread(() =>
        {
            CancellationTokenRegistration registration = default;
            try
            {
                // The one-shot ownership hand-off (design doc §10.3): pin the dedicated thread,
                // install the thread-local Current, the ambient scheduler, and the frame-coherent
                // sync context.
                Dispatcher.TransferOwnershipToCurrentThread();
                _current = this;
                AnimationScheduler.Install(_animationScheduler);
                SynchronizationContext.SetSynchronizationContext(_syncContext);

                ComposeSystems();

                // User configuration (FB-17 Stage A) loads and applies BEFORE the capability
                // fan-out, so theme preference / opt-ins / overrides are in force for the first
                // caps-* stamp — the app never flashes its unconfigured look.
                ApplyUserConfiguration();

                // Capability fan-out (design doc §10.5 preamble), explicit and ordered. The S7
                // application theme leg runs FIRST so the effective ActualThemeVariant is current
                // before styling stamps the effective-tier capability class (inversion 6). The
                // access-key call receives the NEGOTIATED snapshot (ND23 — never the decorated view).
                ApplyCapabilities(_capabilities);

                // Resolve the main content ON the UI thread (the factory overload's contract).
                if ((rootFactory is not null ? rootFactory() : prebuiltRoot) is { } root)
                    RootElement = root;

                registration = cancellationToken.Register(static s => ((UIApplication)s!).Shutdown(0), this);
                RunLoop();
            }
            catch (Exception ex)
            {
                _fatalException ??= ex;
            }
            finally
            {
                registration.Dispose();

                try
                {
                    RunTeardown(); // crash paths restore the terminal too (design doc §10.7)
                }
                catch
                {
                    // best-effort by contract
                }

                if (_fatalException is {} fatal)
                    completion.TrySetException(fatal);
                else
                    completion.TrySetResult(Volatile.Read(ref _exitCode));
            }
        })
        {
            Name = "Cursorial UI",
            IsBackground = false
        };

        thread.Start();
        return await completion.Task.ConfigureAwait(false);
    }

    // ───────────────────────────── startup (design doc §10.6) ─────────────────────────────

    private async Task StartupAsync(CancellationToken cancellationToken)
    {
        // 1. Host: BYO host → BYO session (wrapped) → owned happy-path session (registers the
        //    signal net; see TerminalSessionHost remarks for the EmergencyRestoreBytes gap).
        if (_options.Host is {} host)
        {
            _host = host;
            _ownsHost = _options.DisposeHost;
        }
        else if (_options.Session is {} session)
        {
            _host = new TerminalSessionHost(session, _options.DisposeSession, ownsSignalHandling: false);
            _ownsHost = true; // the host wrapper is ours; the session disposes per the flag
        }
        else
        {
            var opened = await TerminalSession.OpenAsync(_options.SessionOptions, cancellationToken).ConfigureAwait(false);
            _host = new TerminalSessionHost(opened, disposeSession: true, ownsSignalHandling: true);
            _ownsHost = true;
        }

        // 2. Initial size: host query → Console fallback → 80×24 (the session's startup
        //    ResizeEvent corrects it in frame 0/1).
        var size = await _host.QuerySizeAsync(cancellationToken).ConfigureAwait(false);
        if (size is null)
        {
            try
            {
                size = (Console.WindowWidth, Console.WindowHeight);
            }
            catch
            {
                // not a console
            }
        }

        InitializeFromHost(size is { Columns: > 0, Rows: > 0 } s ? s : (80, 24));
    }

    /// <summary>
    /// Startup steps 3–8 (shared with the headless path): capabilities snapshot, buffer + renderer
    /// from the <b>negotiated</b> capabilities (quantization protection), UI-mode entry bytes,
    /// input-pipeline assembly with the provenance split, pump start. Palette theming + capability
    /// rewrite (step 4) land with S7 at P5.
    /// </summary>
    private void InitializeFromHost((int Columns, int Rows) size)
    {
        var host = _host!;

        _capabilities = host.Capabilities;
        _screenSize = size;

        // Inline: the buffer spans the REGION, not the screen — full terminal width, height fitted
        // to content before Phase 5 each frame (1 row until the first fit runs).
        var bufferSize = _options.Inline ? (size.Columns, Rows: 1) : size;

        _buffer = new CellBuffer(bufferSize.Columns, bufferSize.Rows, _capabilities) { CursorVisible = false };
        _renderer = new FrameRenderer(_capabilities.Output,
                                      new FrameRendererOptions(OrderedDither: _options.OrderedDither,
                                                               Inline: _options.Inline,
                                                               RelativeInline: _options.InlineRelativeMoves));

        // UI-mode entry: alt screen when supported and requested, else clear-screen fallback;
        // cursor hiding is left to the buffer (CursorVisible = false ⇒ DECRST 25 on frame 0).
        var writer = host.Output.Writer;

        _enteredAltScreen = !_options.Inline && _options.UseAlternateScreen && _capabilities.Output.Window.AlternateScreenBuffer;

        // Enter the alt buffer via a scope the SESSION owns (PushAltScreen). Besides DECSET 1049 it
        // re-applies the per-screen-buffer Kitty keyboard push onto the now-active alt stack — negotiation
        // pushed it on the MAIN screen, which is a separate, fresh stack — so key-up / repeat reporting
        // (and the access-key Alt gate, ND23) engage on the alt screen. Disposing the scope at teardown
        // pops that push then leaves the alt buffer (pop-before-leave), so it never strands on the alt
        // stack for the next program (e.g. `less`); the owned session's emergency signal handler closes
        // it too. The clear-screen fallback stays on the main screen (no scope), as does a headless host.
        if (_enteredAltScreen)
            _altScreenScope = host.PushAltScreenAsync().AsTask().GetAwaiter().GetResult();
        else if (!_options.Inline)
            ScreenWriter.WriteClearScreen(writer);
        else
        {
            // Inline entry: no screen takeover at all. Ask the terminal where the shell left the
            // cursor (DSR-CPR) — the reply anchors the region's top row; until it lands (or times
            // out into the bottom-anchor fallback) Phase 6 emits nothing. The response arrives
            // through the input pump as a DeviceResponseEvent and is routed to the sink below.
            _inlineCpr = InlineCprState.Startup;
            _inlineCprQueryTimestamp = _options.TimeProvider.GetTimestamp();
            _inlineCprSink = RegisterDeviceResponseSink(OnInlineDeviceResponse);
            CursorWriter.WriteQueryPosition(writer);
        }

        SgrEncoder.WriteReset(writer);
        WriteThemeCursorColor(writer); // OSC 12 = the theme accent, so the real terminal caret stays visible across variants (teardown emits OSC 112)
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();

        // Input assembly (design doc §10.4): synthesizer innermost (opt-in), click transform
        // outermost; the pull surface, never EventInputDevice (it swallows handler exceptions).
        var device = host.Input;

        if (_options.KeyReleaseSynthesis is {} krs &&
            _capabilities.Input.Keyboard.DistinguishesKeyUpDown is false)
        {
            device = new KeyReleaseSynthesizer(device, krs.UpTimeout, krs.RepeatTimeout, _options.TimeProvider);
        }

        if (_options.TranslateNumpadKeys)
            device = device.WithNumpadKeyTranslation();

        // Always assembled, even when the option is off: the chain is single-shot, so the live
        // ScrollDeadZoneEnabled toggle must find the filter in place (disabled = pass-through).
        _wheelAxisLock = new WheelAxisLock { Enabled = ScrollDeadZoneEnabled };
        device = device.Transform(_wheelAxisLock);

        device = device.WithClickSynthesis(_options.ClickOptions);
        _device = device;

        // Provenance split: effective = post-decoration (S3's surface); the Alt gate = undecorated.
        _effectiveInputCapabilities = device.Capabilities;
        _supportsAltKeyTracking = ComputeAltKeyTracking(_capabilities.Input);
        _appStartTimestamp = _options.TimeProvider.GetTimestamp();

        StartPump();
    }

    private void ComposeSystems()
    {
        _windowManager =
            new WindowManager(_capabilities.Output, _caretService, _guard)
            {
                // S3 re-evaluates hover against the new stack on every show/close/modal/z change (§8.6).
                SurfacesChanged = _inputDispatcher.OnSurfacesChanged,
                // A modal blocking a window releases any pointer capture held inside it (mid-gesture modal, §8.6).
                WindowBlocked = r => _inputDispatcher.ReleaseCaptureWithin(r),
                // The active window's title mirrors to the terminal via OSC 2 when supported (§8.8).
                SetTerminalTitle = _capabilities.Output.Window.TitleSet
                                       ? title => QueueControlSequence(writer => WindowWriter.WriteTitle(writer, title))
                                       : null
            };

        // S4 owns per-window focus activation: when the active window changes, move the focus/access-key active
        // root onto it (or back to the app root) — auto-focuses the window's first tab stop and makes Enter reach
        // its default button. (The single-root WireRoot still does the initial app-root activation below.)
        _windowManager.ActiveWindowChanged += (_, _) => OnActiveWindowFocusChanged();

        if (ApplicationModel == ApplicationModel.InlineWithSwitching)
            _windowManager.WindowCountBecameZeroOrOne = () => _presentationSwitchPending = true;

        _windowManager.OnViewportResized(new Size(_buffer!.Columns, _buffer.Rows));
        _inputDispatcher.SetWindowTopology(_windowManager); // S4 is the real topology now (replaces SingleRootWindowTopology)
        _systemsReady = true;

        if (_rootElement is {} root)
            WireRoot(root);
    }

    private void StartPump()
    {
        // One ReadAllAsync enumeration at a time (this app's pump owns it; a shared session's next
        // app re-enumerates after this pump unwinds). EOF ⇒ shutdown; faults land in the Interlocked
        // slot, surfaced ONCE on the UI thread (Phase 1).
        _pumpCts = new CancellationTokenSource();

        var token = _pumpCts.Token;
        var device = _device!;

        _pumpTask = Task.Run(async () =>
                             {
                                 try
                                 {
                                     await foreach (var inputEvent in device.ReadAllAsync(token).ConfigureAwait(false))
                                     {
                                         _inputQueue.Enqueue(inputEvent);
                                         Dispatcher.Wake();
                                     }

                                     _streamEnded = true;
                                     Dispatcher.Wake();
                                 }
                                 catch (OperationCanceledException) {}
                                 catch (Exception ex)
                                 {
                                     Interlocked.Exchange(ref _pumpFault, ex);
                                     Dispatcher.Wake();
                                 }
                             });
    }

    // ───────────────────────────── the loop (design doc §10.5) ─────────────────────────────

    private void RunLoop()
    {
        var time = _options.TimeProvider;

        Started?.Invoke(this, EventArgs.Empty);

        while (!_shutdownRequested)
        {
            var frameStart = time.GetTimestamp();
            var elapsed = time.GetElapsedTime(_appStartTimestamp);
            var frameTime = new FrameTime(_frame, elapsed, elapsed - _lastElapsed);

            var result = RunFrameOnce(in frameTime);

            if (_fatalException is not null)
                return; // unwind — RunCoreAsync's finally runs the canonical teardown

            // PHASE 7 — idle. Every path is paced; there is no unpaced re-entry into the loop.
            if (!_shutdownRequested)
            {
                var remaining = _options.FrameInterval - time.GetElapsedTime(frameStart);

                bool workPending = _windowManager is { HasDirtyVisuals: true } ||
                                   _windowManager is { HasPendingLayout: true } ||
                                   StyleHooks is { HasPendingActivations: true } ||
                                   // An outstanding inline DSR-CPR query gated Phase 6 — keep the
                                   // loop ticking at frame pace so its timeout fallback can fire
                                   // even when the terminal never replies (no wake would come).
                                   _inlineCpr != InlineCprState.None ||
                                   // A window-count transition deferred past a renegotiation must
                                   // retry next frame, not wait for the next input.
                                   _presentationSwitchPending;

                if (workPending || (AnimationDriver?.HasActiveAnimations ?? false))
                {
                    // Late invalidation / animating: run another frame, but at frame pace.
                    if (remaining > TimeSpan.Zero)
                        Dispatcher.WaitForWake(remaining);
                }
                else
                {
                    // Event-driven with rate clamp (doc §10.5, normative): a frame that rendered
                    // waits out the FULL remaining budget FIRST — wakes during the clamp do NOT cut
                    // it short (that would render at event rate; the clamp exists so any-event
                    // mouse-motion storms coalesce into ONE next frame — a pointer sweep renders
                    // ≤ 1/FrameInterval). Only shutdown exits the clamp early. A wake observed
                    // during the clamp then proceeds straight into the next frame; otherwise park
                    // for free until input / Post / RequestRender / QueueControlSequence / Shutdown.
                    var wokeDuringClamp = false;

                    if (result.Rendered)
                    {
                        while (!_shutdownRequested && remaining > TimeSpan.Zero)
                        {
                            wokeDuringClamp |= Dispatcher.WaitForWake(remaining);
                            remaining = _options.FrameInterval - time.GetElapsedTime(frameStart);
                        }
                    }

                    if (!wokeDuringClamp && !_shutdownRequested)
                        Dispatcher.WaitForWake();
                }
            }

            // SINGLE advancement point — every path updates both.
            _frame++;
            _lastElapsed = elapsed;
        }
    }

    /// <summary>
    /// One frame, phases 0–6 (design doc §10.5) — the <c>FrameLoop</c> core shared by the
    /// production loop and <c>UITestHost</c> stepping. Phase 7 (idle/pacing) belongs to the
    /// production loop only. Phases whose subsystems land later (styling P3, animation P8, input
    /// routing P2, windowing P7) run through their seam fields and no-op while null.
    /// </summary>
    internal FrameResult RunFrameOnce(in FrameTime time)
    {
        _currentFrameTime = time;

        // PHASE 0 — one timestamp; freeze the animation clock FIRST so a BeginAnimation during
        // the input drain stamps StartTime = T_N (S5, P8).
        AnimationDriver?.BeginFrame(in time);

        // PHASE 1 — input drain to empty (the pump is the only producer — no self-feeding;
        // inline try/catch, no per-event closures at motion rates).
        ResizeEvent? resize = null;

        while (_inputQueue.TryDequeue(out var inputEvent))
        {
            switch (inputEvent)
            {
                case ResizeEvent r:
                    resize = r; // coalesce: last one wins (documented in-band reorder)
                    break;

                case DeviceResponseEvent response:
                    DispatchDeviceResponse(response); // never reaches S3 — the response router
                    break;

                default:
                    // Inline: mouse events arrive in SCREEN coordinates; the UI lives in REGION
                    // coordinates. Translate by the region origin — or swallow the event when it
                    // falls outside the region (that's the shell's screen estate, not ours).
                    if (IsPresentingInline && inputEvent is MouseEvent mouse)
                    {
                        if (TranslateInlineMouse(mouse) is not {} translated)
                            break;

                        inputEvent = translated;
                    }

                    try
                    {
                        var dispatched = InputDispatchTarget?.Dispatch(inputEvent) ?? InputDispatchResult.NotUIInput;

                        if (dispatched != InputDispatchResult.DispatchedHandled)
                            ApplyDefaultGestures(inputEvent);
                    }
                    catch (Exception ex)
                    {
                        if (!RaiseUnhandled(ex))
                            return default;
                    }

                    break;
            }
        }

        var resized = false;

        if (resize is { Columns: > 0, Rows: > 0 })
        {
            ApplyResize(resize);
            resized = true;
        }

        if (Interlocked.Exchange(ref _pumpFault, null) is {} fault && !RaiseUnhandled(fault))
            return default; // Handled ⇒ the app runs on with input PERMANENTLY DEAD (single-shot device)

        if (_streamEnded)
            Shutdown(0);

        // Apply topology mutations (show/close, popup open/close) queued while the previous frame iterated
        // its surfaces, before this frame's layout (design doc §8.8). Cheap no-op until the W5 queue lands.
        _windowManager?.DrainDeferredTopology();

        // PHASE 2 — dispatcher jobs, SNAPSHOT count: jobs posted during the drain run next frame.
        var jobs = Dispatcher.JobCount;

        while (jobs-- > 0 && Dispatcher.TryDequeueJob(out var job))
        {
            try
            {
                job.Run(); // InvokeAsync-shaped jobs capture into their task; Post-shaped throw here
            }
            catch (Exception ex)
            {
                if (!RaiseUnhandled(ex))
                    return default;
            }
        }

        // PHASE 2.5 — InlineWithSwitching (design doc §3.1, FW-7): consume a pending window-count
        // transition BEFORE styling/layout, so a show/close from input, a dispatcher job, or the
        // topology drain renders THIS frame on the new screen at the new geometry. (A show from a
        // later phase's callback — a Phase-4 animation handler, say — lands next frame: transitions
        // happen at frame pace, never mid-frame.) Gated on !_renegotiating (the negotiator owns the
        // pipe); the flag stays set and Phase 7's workPending keeps the loop ticking until it lands.
        // The decision reads the CURRENT truth, not the notification: a window that opened and
        // closed within one frame nets to no transition at all.
        if (_presentationSwitchPending && !_renegotiating)
        {
            _presentationSwitchPending = false;

            if (ApplicationModel == ApplicationModel.InlineWithSwitching &&
                _windowManager is {} switchWm && switchWm.HasOpenWindows == IsPresentingInline)
            {
                SwitchPresentation(toInline: !switchWm.HasOpenWindows);
            }
        }

        // PHASE 3 — styling activation flush (Fork B, P3): phase-1/2 pseudo/class flips reach
        // fixpoint BEFORE animation/layout/render (invariant 1).
        if (StyleHooks is {} styling)
        {
            try
            {
                styling.FlushPendingActivations();
            }
            catch (Exception ex)
            {
                if (!RaiseUnhandled(ex))
                    return default;
            }
        }

        // PHASE 4 — animation tick at the frozen clock (S5, P8); a second cheap styling flush
        // catches animation-driven flips; TickNewlyStarted samples same-frame ignitions at
        // elapsed-zero (no one-frame From-snap). The post-tick flush runs EVERY frame regardless
        // of the driver seam (B1/S151 — "cheap when empty" makes the unconditional call free).
        try
        {
            AnimationDriver?.Tick();
            StyleHooks?.FlushPendingActivations();
            AnimationDriver?.TickNewlyStarted();
        }
        catch (Exception ex)
        {
            if (!RaiseUnhandled(ex))
                return default;
        }

        // PHASE 5 — layout: ONE call per frame; the LayoutManager owns convergence internally and
        // the facade owns the give-up (never pins HasPendingLayout).
        //
        // Phases 5–6 are Fork B's DEFERRED window (B1/SD12): pseudo flips raised during layout,
        // render, or the post-render hover re-diff queue in the engine and surface via
        // HasPendingActivations into the Phase-7 guard, instead of restyling mid-pass.
        var layoutRan = false;
        var rendered = false;
        InDeferredStylingPhase = true;

        try
        {
            // Inline content sizing — BEFORE the pass, so this frame lays out and renders at the
            // fitted height (no one-frame flicker): the region tracks the root's desired height the
            // way a SizeToContent window tracks its content, capped by the builder's MaxHeight and
            // the terminal. A height change resizes the region buffer and re-fits the viewport; the
            // renderer sees the dimension change and repaints the region in full (its inline
            // full-redraw erase also wipes the extent a shrink leaves behind). The region only ever
            // grows/shrinks at its BOTTOM edge — the origin doesn't move here (growth past the
            // terminal bottom scrolls at render time, in PrepareInlineRegion).
            if (IsPresentingInline && _windowManager is {} wmInline && (resized || wmInline.HasPendingLayout))
            {
                var maxRows = Math.Clamp(_options.InlineMaxHeight ?? _screenSize.Rows, 1, Math.Max(1, _screenSize.Rows));
                var fitted = wmInline.MeasureRootContentHeight(_screenSize.Columns, maxRows);

                if (fitted != _buffer!.Rows)
                {
                    _buffer.Resize(_screenSize.Columns, fitted);
                    wmInline.OnViewportResized(new Size(_screenSize.Columns, fitted));
                }
            }

            if (_windowManager is { HasPendingLayout: true } layout)
            {
                try
                {
                    layout.RunLayoutPass();
                }
                catch (Exception ex)
                {
                    if (!RaiseUnhandled(ex))
                        return default;
                }

                layoutRan = true;

                // Post-layout activation focus (doc §7.7): a root shown before its first layout could not
                // place focus at OnWindowActivated time (the first tab stop sits behind templates / content
                // presenters not yet realized — the demo's whole panel is inside a ScrollViewer's SCP). Now
                // that the pass has built the visual subtree, complete any parked activation so the first
                // focusable auto-focuses BEFORE Phase 6 renders (its :focus-visible visual lands this frame).
                // A no-op once focus has landed or the user moved it (the retry never overrides real focus).
                try
                {
                    _focusManager.CompletePendingActivationFocus();
                }
                catch (Exception ex)
                {
                    if (!RaiseUnhandled(ex))
                        return default;
                }
            }

            // Post-layout window work: SizeToContent resolution + popup anchor reposition (W1/W4) — a
            // composite-offset-only change, no re-raster. Cheap no-op at W0.
            _windowManager?.OnLayoutCompleted();

            // S5 transitions (§9.5): flip the go-live latch on every element whose first arrange completed this
            // pass — AFTER layout settled their initial base values, so the first post-go-live change transitions.
            // Runs UNCONDITIONALLY each frame (not gated on HasPendingLayout); empty-set early-out keeps it free.
            CompletePendingTransitionGoLive();

            // KeyTip overlay (Cursorial.UI.Bars; keytips-design §9): re-anchor live badges to their targets' final
            // screen cells and build any parked next level whose reveal (a floated band, an opened dropdown) only
            // realized this pass — the CompletePendingActivationFocus / …TransitionGoLive mirror. No-op when no
            // controller is installed. Runs after OnLayoutCompleted so surface offsets are final for the frame.
            KeyTipController?.CompletePendingLayout();

            // PHASE 6 — render, GATED on !_renegotiating (the negotiator owns the pipe during its
            // window) and, inline, on the region origin being known: nothing paints before the
            // startup / post-resize DSR-CPR reply (or its timeout fallback) says where the region
            // is. Queued control sequences simply wait a frame — the origin resolves in
            // milliseconds (or at the fallback deadline) and nothing is lost.
            if (!_renegotiating && (!IsPresentingInline || EnsureInlineOrigin()))
            {
                // Consume the request flag unconditionally (no short-circuit): leaving it set when
                // visuals are already dirty would buy one wasted empty-diff render next frame.
                var renderRequested = Interlocked.Exchange(ref _renderRequested, 0) != 0;

                var renderNeeded = (_windowManager?.HasDirtyVisuals ?? false) || layoutRan || resized || renderRequested;

                if (renderNeeded && _windowManager is {} renderSystem)
                {
                    bool changed;

                    try
                    {
                        changed = renderSystem.RenderFrame(_buffer!, in time);
                    }
                    catch (Exception ex)
                    {
                        if (!RaiseUnhandled(ex))
                            return default;

                        changed = true; // conservative emit — the compositing invariant keeps the buffer safe
                    }

                    if (_guard.IsFatal)
                        return default; // a draw delegate recorded fatal — unwind to teardown

                    if (_guard.ConsumeHandledFlag())
                        changed = true; // handled draw exception ⇒ conservative emit (design doc §10.8)

                    // The Kitty glyph-height caret band tracks the caret the render system just
                    // assembled (proposal-glyph-runs §4) — queued through the out-of-band channel
                    // so its bytes land AFTER this frame's delta (which positions the hardware
                    // cursor on the band's bottom row).
                    UpdateCaretBand(renderSystem.LastCaret);

                    // Hover re-evaluation once per rendered frame, after layout AND composite
                    // parameters are final (doc §10.5 / matrix ND21): hover stays correct under
                    // layout moves, composite slides, and scrolls without pointer motion, and
                    // detach-deferred hover work executes here. Flips it queues (restyles at P3)
                    // are caught by the Phase-7 guard and render frame N+1 (doc §7.10).
                    try
                    {
                        InputDispatchTarget?.UpdateHover();
                    }
                    catch (Exception ex)
                    {
                        if (!RaiseUnhandled(ex))
                            return default;
                    }

                    // NeedsFullRedraw: a pending renderer reset (UIApplication.RequestFullRedraw)
                    // must emit even when the composite found nothing changed — the reset's whole
                    // point is that the TERMINAL diverged while the framework state is clean.
                    if (changed || resized || _renderer!.NeedsFullRedraw)
                    {
                        _scratch.ResetWrittenCount(); // pooled ArrayBufferWriter<byte>, reset per frame

                        // Inline: make physical room for the region before the delta — scroll the
                        // shell history up when the region's bottom would pass the terminal's last
                        // row — and hand the renderer its (possibly moved) origin.
                        if (IsPresentingInline)
                            PrepareInlineRegion(_scratch);

                        _renderer!.Render(_buffer!, _scratch);
                        rendered = true;
                    }
                }

                if (!_controlSequences.IsEmpty)
                {
                    // The out-of-band OSC channel — AFTER the delta; forces a flush even when empty.
                    if (!rendered)
                        _scratch.ResetWrittenCount();

                    while (_controlSequences.TryDequeue(out var payload))
                    {
                        try
                        {
                            payload(_scratch);
                        }
                        catch (Exception ex)
                        {
                            if (!RaiseUnhandled(ex))
                                return default;
                        }
                    }

                    rendered = true;
                }

                if (rendered && _scratch.WrittenCount > 0)
                {
                    // ONE write + ONE blocking flush per frame (the pipe's drain side is
                    // thread-pool-side, so this cannot deadlock against the UI thread).
                    var writer = _host!.Output.Writer;
                    writer.Write(_scratch.WrittenSpan);
                    writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            InDeferredStylingPhase = false; // close Fork B's deferred window on every unwind path
        }

        return new FrameResult(rendered, layoutRan, resized);
    }

    /// <summary>The frame-loop result feeding Phase 7's pacing decision.</summary>
    internal readonly record struct FrameResult(bool Rendered, bool LayoutRan, bool Resized);

    // ───────────────────────────── the glyph-height caret band (proposal-glyph-runs §4) ─────────────────────────────

    // The band standing on the terminal as Kitty extra beam cursors — its column and inclusive
    // 0-based row range — or null when none is out. Extra cursors are screen-fixed (IND/RI never
    // move them), so every band change must clear the previous extras before emitting new ones.
    private (int Column, int Top, int Bottom)? _emittedCaretBand;

    /// <summary>
    /// Emits the glyph-height caret band: on terminals with the negotiated Kitty multiple-cursors
    /// capability, a visible caret spanning <c>Rows &gt; 1</c> grows to glyph height — the
    /// hardware cursor renders the band's BOTTOM row (the publication anchor; IME and assistive
    /// technology track it), and one rectangle-form escape puts a beam extra cursor on each
    /// remaining row of the caret column. A moved / shrunk / hidden band first clears the
    /// standing extras (<c>CSI &gt; 0;4 SP q</c>); an unchanged band re-emits nothing. A 1-row
    /// caret or a non-supporting terminal emits nothing at all — today's bytes, untouched.
    /// Queued through <see cref="QueueControlSequence"/> so the bytes land after this frame's
    /// delta in the same flush.
    /// </summary>
    private void UpdateCaretBand(in TerminalCaretState caret)
    {
        var band = default((int Column, int Top, int Bottom)?);

        if (caret is { Visible: true, Rows: > 1 } && _capabilities.Output.Cursor.MultipleCursors)
        {
            // The extras cover [Row - Rows + 1 .. Row - 1] — the hardware cursor owns the bottom
            // row, so it is never doubled. A band poking above the screen top clamps to row 0; a
            // caret ON the top row leaves no row above the anchor and emits nothing.
            int top = Math.Max(0, caret.Row - (caret.Rows - 1));
            int bottom = caret.Row - 1;

            if (bottom >= top)
                band = (caret.Column, top, bottom);
        }

        if (band == _emittedCaretBand)
            return;

        var previous = _emittedCaretBand;
        _emittedCaretBand = band;

        if (band is { } next)
        {
            QueueControlSequence(writer =>
            {
                if (previous is not null)
                    CursorWriter.WriteClearExtraCursors(writer);

                CursorWriter.WriteExtraCursorsBeam(writer, next.Column, next.Top, next.Bottom);
            });
        }
        else
        {
            QueueControlSequence(static writer => CursorWriter.WriteClearExtraCursors(writer));
        }
    }

    // ───────────────────────────── resize (design doc §10.6) ─────────────────────────────

    private void ApplyResize(ResizeEvent resize)
    {
        if (IsPresentingInline)
        {
            // The terminal resized under an inline region. Width lands now (height re-fits before
            // Phase 5 — the resized flag drives the probe); the height only CLAMPS here, in case
            // the terminal got shorter than the region. The bigger problem is the origin: the
            // terminal just rewrapped its main buffer, so the region's absolute top row is stale —
            // re-ask the terminal where the hardware cursor is (it rides the region through the
            // rewrap) and re-derive the origin from its believed region-relative row. Until the
            // reply (or its timeout, which falls back to clamping the old origin), Phase 6 holds.
            _screenSize = (resize.Columns, resize.Rows);

            var maxRows = Math.Clamp(_options.InlineMaxHeight ?? resize.Rows, 1, Math.Max(1, resize.Rows));
            _buffer!.Resize(resize.Columns, Math.Min(_buffer.Rows, maxRows));
            _windowManager?.OnViewportResized(new Size(resize.Columns, _buffer.Rows));

            BeginInlineReanchor();
            return;
        }

        // Coalesced last-wins: buffer Resize (contents discarded; the renderer full-redraws on
        // dimension change) → render system (fresh compositor + invalidate all) → layout facade
        // (full relayout lands in Phase 5 of the SAME frame).
        _screenSize = (resize.Columns, resize.Rows);
        _buffer!.Resize(resize.Columns, resize.Rows);
        var size = new Size(resize.Columns, resize.Rows);
        _windowManager?.OnViewportResized(size);
    }

    // ───────────────────── InlineWithSwitching (design doc §3.1, FW-7) ─────────────────────

    /// <summary>Set by the WindowManager's 0↔1 seam; consumed at the Phase-2.5 checkpoint.</summary>
    private bool _presentationSwitchPending;

    /// <summary>
    /// The inline region's buffer, parked across a fullscreen excursion: the return path restores it
    /// (rather than building fresh) because the DSR-CPR re-anchor derives the region origin from the
    /// buffer's believed cursor row — geometry AND cursor state must survive the round trip.
    /// </summary>
    private CellBuffer? _parkedInlineBuffer;

    /// <summary>
    /// The InlineWithSwitching transition: inline ⇄ fullscreen, on the frame-loop thread, between
    /// frames (the Phase-2.5 checkpoint) — the single-writer pipe is shared with the session's
    /// push/pop, so nothing here may run concurrently with Phase 6 or a renegotiation. The shape is
    /// <see cref="ChangeCapabilities"/>'s proven rebuild: close the old renderer under the OLD screen
    /// state, swap buffer + renderer, refit the viewport, restamp — the fresh renderer's
    /// NeedsFullRedraw makes the same frame's Phase 6 a full paint.
    /// </summary>
    private void SwitchPresentation(bool toInline, bool forTeardown = false)
    {
        var host = _host!;
        var writer = host.Output.Writer;

        // Close the CURRENT renderer while its screen is still active: DECAWM (autowrap re-enable)
        // is GLOBAL terminal state — not per-screen — and a standing caret band was emitted under
        // this screen and must be cleared on it. (Same rule ChangeCapabilities follows.)
        try
        {
            _scratch.ResetWrittenCount();

            // Fragments (images, sized text) are erased only when LEAVING the fullscreen side — its
            // raster dies with DECRST 1049 but the protocol state is global. Going fullscreen keeps
            // the inline region's fragments standing in the main-screen raster, so the 1049 restore
            // brings the region back whole (and a Retain teardown while escalated keeps its receipt).
            _renderer!.Close(_scratch, eraseFragments: toInline);

            if (_emittedCaretBand is not null)
            {
                CursorWriter.WriteClearExtraCursors(_scratch);
                _emittedCaretBand = null;
            }

            if (_scratch.WrittenCount > 0)
            {
                writer.Write(_scratch.WrittenSpan);
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // best-effort — the swap must proceed
        }

        if (!toInline)
        {
            // 0 → 1 windows: onto the alternate screen. No cancellation token — a cancel landing
            // after the session's depth increment (mid-reapply) would strand the ref-count with
            // DECSET 1049 already on the wire. The push itself may NOT flush (a cached-capability
            // session's opt-in reapply early-returns before its FlushAsync), so flush explicitly.
            if (_capabilities.Output.Window.AlternateScreenBuffer)
                _altScreenScope = host.PushAltScreenAsync().AsTask().GetAwaiter().GetResult();
            else
                ScreenWriter.WriteClearScreen(writer); // degrade: no alt buffer — the shell history takes the hit, exactly as a FullScreen app would

            writer.FlushAsync().AsTask().GetAwaiter().GetResult();

            // A CPR round outstanding at escalation (startup, or a resize re-anchor) must not resolve
            // against the fullscreen buffer — its reply would clamp the origin to 0, and an escalated
            // teardown would then erase the whole screen from row 0. The return path re-anchors
            // unconditionally, so the round is simply abandoned.
            _inlineCpr = InlineCprState.None;

            _parkedInlineBuffer = _buffer;
            _buffer = new CellBuffer(_screenSize.Columns, _screenSize.Rows, _capabilities) { CursorVisible = false };
            _renderer = new FrameRenderer(_capabilities.Output,
                                          new FrameRendererOptions(OrderedDither: _options.OrderedDither,
                                                                   Inline: false));
            IsPresentingInline = false;
            _windowManager!.OnViewportResized(new Size(_screenSize.Columns, _screenSize.Rows));
        }
        else
        {
            // 1 → 0 windows: back to the inline region. Disposing the scope pops the Kitty push
            // while still on the alt buffer, then DECRST 1049 — the main screen's raster AND the
            // push-time cursor come back for free (the session owns that ordering and flushes).
            var restoredMainRaster = false;

            if (_altScreenScope is {} altScope)
            {
                _altScreenScope = null;
                altScope.DisposeAsync().AsTask().GetAwaiter().GetResult();
                restoredMainRaster = true;
            }
            else
                ScreenWriter.WriteClearScreen(writer); // the no-alt-buffer degrade: the region redraws below, but the surrounding scrollback is gone

            var parked = _parkedInlineBuffer!;
            _parkedInlineBuffer = null;

            if (parked.Columns != _screenSize.Columns || parked.Rows > _screenSize.Rows)
            {
                // The terminal resized during the excursion: width lands now, height only clamps
                // (Phase 5's content fit re-grows it) — ApplyResize's inline rules.
                var maxRows = Math.Clamp(_options.InlineMaxHeight ?? _screenSize.Rows, 1, Math.Max(1, _screenSize.Rows));
                parked.Resize(_screenSize.Columns, Math.Min(parked.Rows, maxRows));
            }

            _buffer = parked;
            _renderer = new FrameRenderer(_capabilities.Output,
                                          new FrameRendererOptions(OrderedDither: _options.OrderedDither,
                                                                   Inline: true,
                                                                   RelativeInline: _options.InlineRelativeMoves));
            IsPresentingInline = true;
            _windowManager!.OnViewportResized(new Size(_buffer.Columns, _buffer.Rows));

            // The 1049-saved cursor is stale if the terminal resized while fullscreen (terminals
            // clamp/rewrap the saved position), so the region origin is re-derived from a fresh
            // DSR-CPR round — the resize path's machinery; Phase 6 holds until it resolves. The
            // re-anchor math only means anything when DECRST 1049 restored the cursor that rode the
            // region: in the no-alt-buffer degrade the screen was just CLEARED and the cursor sits
            // wherever the last fullscreen frame parked it, so the region deterministically
            // re-materializes at the top instead. At teardown there are no more frames to gate and
            // the exit writes position absolutely.
            if (!forTeardown && restoredMainRaster)
                BeginInlineReanchor();
            else if (!restoredMainRaster)
                _inlineOrigin = 0;
        }

        // app-inline/app-fullscreen flip through the restamp fan-out (§3.3) — same tick, so the
        // styling phase that follows the checkpoint re-matches before this frame lays out.
        if (!forTeardown)
            StyleEngineInternal?.RestampCapabilityClasses();
    }

    // ───────────────────────────── inline presentation (UseInline) ─────────────────────────────

    /// <summary>
    /// How long an inline application waits for the terminal's DSR-CPR reply before resolving the
    /// region origin blind. Real terminals answer in single-digit milliseconds (a slow SSH hop in
    /// the low hundreds); the timeout only ever fires on a terminal that doesn't implement DSR.
    /// </summary>
    private static readonly TimeSpan InlineCprTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The inline device-response sink: anchors the region from a cursor-position report. Runs on
    /// the UI thread (Phase 1's response router). Only an <b>outstanding</b> query may anchor —
    /// an R-final CSI with two parameters is also how F3-with-modifiers arrives on some terminals
    /// (the classic CPR collision), so an unsolicited "report" is ignored.
    /// </summary>
    private void OnInlineDeviceResponse(DeviceResponseEvent response)
    {
        if (response.Kind != DeviceResponseKind.CursorPositionReport || _inlineCpr == InlineCprState.None)
            return;

        if (!IsPresentingInline)
            return; // a stale reply draining mid-excursion would resolve against the fullscreen buffer

        if (!TryParseCursorReport(response.Payload.Span, out var row, out var column))
            return;

        var rows = Math.Max(1, _screenSize.Rows);
        row = Math.Clamp(row - 1, 0, rows - 1); // 1-based wire → 0-based screen

        _inlineOrigin = _inlineCpr == InlineCprState.Startup
            // The region starts on the shell's cursor line — or the NEXT line when the shell left
            // the cursor mid-line (a prompt without a trailing newline), which must not be painted
            // over. `row + 1` may point one past the bottom row; the render-time scroll adjust in
            // PrepareInlineRegion makes the room.
            ? row + (column > 1 ? 1 : 0)
            // Re-anchor: the hardware cursor rode the region through the terminal's resize rewrap;
            // subtracting its believed region-relative row recovers the region top.
            : Math.Clamp(row - Math.Max(0, _buffer?.CursorRow ?? 0), 0, Math.Max(0, rows - (_buffer?.Rows ?? 1)));

        _inlineCpr = InlineCprState.None;
        RequestFullRedraw(); // the region may have moved — repaint it wholesale at the new origin
    }

    /// <summary>Parses a CPR parameter run — <c>&lt;row&gt; ; &lt;col&gt;</c>, 1-based ASCII.</summary>
    private static bool TryParseCursorReport(ReadOnlySpan<byte> payload, out int row, out int column)
    {
        row = 0;
        column = 0;

        var separator = payload.IndexOf((byte) ';');
        if (separator <= 0)
            return false;

        return TryParseAsciiInt(payload[..separator], out row) &&
               TryParseAsciiInt(payload[(separator + 1)..], out column) &&
               row >= 1 && column >= 1;

        // Digits-prefix parse: a Kitty-style CPR can carry colon subparameters after a value —
        // take the leading integer and ignore the rest.
        static bool TryParseAsciiInt(ReadOnlySpan<byte> span, out int value)
            => System.Buffers.Text.Utf8Parser.TryParse(span, out value, out int consumed) && consumed > 0;
    }

    /// <summary>
    /// The Phase 6 inline gate: true when the region origin is known and rendering may proceed.
    /// While a DSR-CPR query is outstanding this holds emission (returning false) until the reply
    /// lands — or, past <see cref="InlineCprTimeout"/>, resolves the origin blind: a post-resize
    /// re-anchor keeps the old origin clamped on-screen; startup bottom-anchors by force (arming
    /// <see cref="_inlineForceBottomScroll"/> so the next render scrolls the region into the
    /// bottom rows without needing to know the cursor row).
    /// </summary>
    private bool EnsureInlineOrigin()
    {
        if (_inlineCpr == InlineCprState.None)
            return _inlineOrigin is not null;

        if (_options.TimeProvider.GetElapsedTime(_inlineCprQueryTimestamp) < InlineCprTimeout)
            return false; // the reply is usually milliseconds away — hold this frame's emission

        var startup = _inlineCpr == InlineCprState.Startup;
        _inlineCpr = InlineCprState.None;

        if (!startup && _inlineOrigin is {} previous)
        {
            _inlineOrigin = Math.Clamp(previous, 0, Math.Max(0, _screenSize.Rows - _buffer!.Rows));
        }
        else
        {
            _inlineForceBottomScroll = true;
            _inlineOrigin = Math.Max(0, _screenSize.Rows - _buffer!.Rows);
        }

        RequestFullRedraw();
        return true;
    }

    /// <summary>
    /// Pre-delta region maintenance, emitted into the same frame flush just before
    /// <see cref="FrameRenderer.Render"/>: scrolls the screen up when the region's bottom would
    /// pass the terminal's last row (region growth near the bottom — the shell history above moves
    /// into the scrollback to mint the missing rows), then hands the renderer the (possibly moved)
    /// origin. Uses literal line feeds from the bottom row, NOT SU (<c>CSI S</c>): LF pushes the
    /// departing top lines into the scrollback, where the user's shell history belongs; SU
    /// discards them on most terminals.
    /// </summary>
    private void PrepareInlineRegion(IBufferWriter<byte> output)
    {
        var rows = Math.Max(1, _screenSize.Rows);
        var height = Math.Min(_buffer!.Rows, rows);
        var origin = _inlineOrigin ?? Math.Max(0, rows - height);

        int scroll;

        if (_inlineForceBottomScroll)
        {
            // The DSR fallback reserves blind: from the terminal's bottom row, `height` line feeds
            // guarantee the rows above the final cursor line are ours regardless of where the
            // shell prompt sat (worst case a blank band separates it from the region).
            _inlineForceBottomScroll = false;
            scroll = height;
        }
        else
        {
            scroll = origin + height - rows;
        }

        if (scroll > 0)
        {
            CursorWriter.WriteMoveTo(output, 0, rows - 1);

            var lf = output.GetSpan(scroll);
            lf[..scroll].Fill((byte) '\n');
            output.Advance(scroll);

            origin = rows - height;
        }

        _inlineOrigin = origin;
        _renderer!.RowOffset = origin; // a change forgets the front buffer → full region repaint
    }

    /// <summary>
    /// Translates a screen-space mouse event into region space, or swallows it: outside the region
    /// is the shell's screen estate, not ours. Events that are part of an in-flight drag (buttons
    /// held, or a release) clamp to the region edge instead of dropping — losing them would strand
    /// S3's capture state mid-gesture.
    /// </summary>
    private MouseEvent? TranslateInlineMouse(MouseEvent mouse)
    {
        if (_inlineOrigin is not {} origin || _buffer is null)
            return null; // the region isn't on screen yet — nothing to hit

        var row = mouse.Position.Row - origin;

        if (row < 0 || row >= _buffer.Rows)
        {
            if (mouse.ButtonsHeld == MouseButtons.None && mouse.Kind != MouseEventKind.ButtonUp)
                return null;

            row = Math.Clamp(row, 0, _buffer.Rows - 1);
        }

        return row == mouse.Position.Row ? mouse : mouse with { Position = mouse.Position with { Row = row } };
    }

    /// <summary>
    /// Starts a post-resize origin re-anchor: the terminal just rewrapped its main buffer, so the
    /// region's absolute top row is stale. Re-queries DSR-CPR (the hardware cursor rides the
    /// region through the rewrap; <see cref="OnInlineDeviceResponse"/> re-derives the origin from
    /// its believed region-relative row) — except while the negotiator owns the pipe, where the
    /// old origin is kept, clamped on-screen.
    /// </summary>
    private void BeginInlineReanchor()
    {
        if (_renegotiating)
        {
            if (_inlineOrigin is {} origin)
                _inlineOrigin = Math.Clamp(origin, 0, Math.Max(0, _screenSize.Rows - _buffer!.Rows));

            RequestFullRedraw();
            return;
        }

        _inlineCpr = InlineCprState.Reanchor;
        _inlineCprQueryTimestamp = _options.TimeProvider.GetTimestamp();

        var writer = _host!.Output.Writer;
        CursorWriter.WriteQueryPosition(writer);
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The inline exit bytes (the teardown's alt-screen-leave / clear-screen analog), per
    /// <see cref="InlineExitBehavior"/>: Clear rewinds to the region's top-left and erases
    /// everything below, so the shell prompt resumes where the application started; Retain parks
    /// on a fresh line below the last frame (the LF scrolls one line when the region ends on the
    /// bottom row) and sweeps anything staler below it. An application whose origin never
    /// resolved rendered nothing — the shell's line is left untouched.
    /// </summary>
    private void WriteInlineExit(IBufferWriter<byte> output)
    {
        if (_inlineOrigin is not {} origin)
            return;

        var rows = Math.Max(1, _screenSize.Rows);
        var height = Math.Min(_buffer?.Rows ?? 1, rows);

        if (_options.InlineRelativeMoves)
        {
            // Relative exit: the renderer parked the cursor at the region bottom-left, so we position
            // relatively rather than to the (possibly stale) absolute origin — the exit survives an
            // unobserved clear exactly as rendering does. Clear climbs to the region top and erases
            // below; Retain drops one fresh line below the region and sweeps below that.
            if (InlineExitBehavior == InlineExitBehavior.Clear)
                CursorWriter.WriteMoveUp(output, height - 1); // bottom-left → region top (CUU, no-op at height 1)
            else
            {
                var lf = output.GetSpan(1);
                lf[0] = (byte) '\n'; // already at the region bottom-left — one line below the last frame
                output.Advance(1);
            }

            CursorWriter.WriteColumnAbsolute(output, 0);
            ScreenWriter.WriteClearScreenAfter(output);
            return;
        }

        if (InlineExitBehavior == InlineExitBehavior.Clear)
        {
            CursorWriter.WriteMoveTo(output, 0, Math.Clamp(origin, 0, rows - 1));
            ScreenWriter.WriteClearScreenAfter(output);
        }
        else
        {
            CursorWriter.WriteMoveTo(output, 0, Math.Clamp(origin + height - 1, 0, rows - 1));

            var lf = output.GetSpan(1);
            lf[0] = (byte) '\n';
            output.Advance(1);

            // The session is still RAW here (no ONLCR), so that LF kept whatever column the terminal was
            // in — column 0 only because the WriteMoveTo above says so. Make the exit invariant explicit
            // rather than inherited: whoever writes next (an emit, the shell prompt) starts flush-left.
            CursorWriter.WriteColumnAbsolute(output, 0);

            ScreenWriter.WriteClearScreenAfter(output);
        }
    }

    // ───────────────────────────── renegotiation (design doc §10.6) ─────────────────────────────

    /// <summary>
    /// Re-runs capability negotiation (UI thread only; rare — don't call mid-interaction). Phase 6
    /// (delta emission <i>and</i> the control-sequence drain) is gated for the duration because the
    /// negotiator writes probes to the same non-thread-safe pipe. On success the renderer + buffer
    /// rebuild with the new capabilities, the render tree re-stamps and fully re-rasters,
    /// <see cref="EffectiveInputCapabilities"/> recomputes, and <see cref="EffectiveCapabilitiesChanged"/>
    /// fires; on failure the session keeps the old negotiator and nothing changes.
    /// </summary>
    public async ValueTask RenegotiateAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();

        if (_host is null || _renderer is null)
            throw new InvalidOperationException("The application is not running.");

        var oldCapabilities = _capabilities;

        _renegotiating = true; // set BEFORE the await — the loop reads it from Phase 6

        try
        {
            await _host.RenegotiateAsync(cancellationToken);
        }
        catch
        {
            _renegotiating = false;
            throw;
        }

        try
        {
            ChangeCapabilities(_host, _renderer, oldCapabilities, _host.Capabilities, cancellationToken);
        }
        finally
        {
            _renegotiating = false;
        }
    }

    private void ChangeCapabilities(ITerminalHost host,
                                    FrameRenderer renderer,
                                    TerminalCapabilities oldCapabilities,
                                    TerminalCapabilities newCapabilities,
                                    CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();

        var wasRenegotiating = Interlocked.Exchange(ref _renegotiating, true);

        try
        {
            _capabilities = newCapabilities;

            var effective = _capabilityOverrides.Apply(newCapabilities);

            // Close the old renderer (fragment erases, autowrap restore) and flush before rebuilding.
            // The pointer shape is re-baselined here too (§7.6): reset under the OLD gate (a shape
            // may be active from before the window); the dispatcher's OnCapabilitiesChanged below
            // forgets its tracked shape and re-emits an active one under the NEW gate.
            try
            {
                _scratch.ResetWrittenCount();
                renderer.Close(_scratch);

                if (oldCapabilities.Output.Protocol.MouseCursorShape)
                {
                    MouseCursorWriter.WriteSet(
                        _scratch,
                        MouseCursorShape.Default); // not WriteReset — Ghostty ignores empty-payload reset (§7.6)
                }

                // A standing caret band predates the renegotiation — clear it while the OLD
                // terminal state is still current (it was only ever emitted under the old gate)
                // and forget it, so the next rendered frame re-emits under the NEW gate when the
                // capability survives.
                if (_emittedCaretBand is not null)
                {
                    CursorWriter.WriteClearExtraCursors(_scratch);
                    _emittedCaretBand = null;
                }

                if (_scratch.WrittenCount > 0)
                {
                    var writer = host.Output.Writer;
                    writer.Write(_scratch.WrittenSpan);
                    writer.FlushAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
                }
            }
            catch
            {
                // best-effort
            }

            var (columns, rows) = (_buffer!.Columns, _buffer.Rows);

            _buffer = new CellBuffer(columns, rows, effective) { CursorVisible = false };

            // A renegotiation while ESCALATED must not leave the parked inline buffer on the old
            // capability snapshot — restored verbatim after the excursion, it would blend against the
            // old terminal defaults for the rest of the app's life. Geometry and cursor state carry
            // over (the return path's re-anchor derives the origin from CursorRow); content does not
            // need to (the fresh inline renderer full-redraws).
            if (_parkedInlineBuffer is {} parked)
            {
                _parkedInlineBuffer = new CellBuffer(parked.Columns, parked.Rows, effective)
                {
                    CursorVisible = false,
                    CursorRow = parked.CursorRow,
                    CursorColumn = parked.CursorColumn,
                };
            }

            _renderer = new FrameRenderer(effective.Output,
                                          new FrameRendererOptions(OrderedDither: _options.OrderedDither,
                                                                   Inline: IsPresentingInline,
                                                                   RelativeInline: _options.InlineRelativeMoves)); // the LIVE side — a renegotiation while escalated must rebuild a fullscreen renderer

            _effectiveInputCapabilities = ApplyDecorationProjections(effective.Input);
            _supportsAltKeyTracking = ComputeAltKeyTracking(effective.Input);
            _windowManager?.OnCapabilitiesChanged(effective.Output);

            ApplyCapabilities(effective);

            EffectiveCapabilitiesChanged?.Invoke(this,
                                        new CapabilitiesChangedEventArgs
                                        {
                                            OldCapabilities = oldCapabilities,
                                            NewCapabilities = effective
                                        });
        }
        finally
        {
            Interlocked.Exchange(ref _renegotiating, wasRenegotiating);
        }
    }

    private void ApplyCapabilities(TerminalCapabilities newCapabilities)
    {
        _negotiatedVariant = ThemeVariant.FromCapabilities(newCapabilities);

        // The capability fan-out, in order — the S7 application theme leg first (re-derives the
        // effective variant + re-stamps the effective-tier class, inversion 6), then styling. The
        // access-key leg re-evaluates the gate AND unconditionally clears Alt/sticky-cue state
        // (renegotiation parks the pump; an Alt Up can vanish — doc §7.8).
        UpdateActualThemeVariant(reStampClasses: false); // StyleEngineInternal.OnCapabilitiesChanged will restamp.
        StyleEngineInternal.OnCapabilitiesChanged(newCapabilities);

        StyleHooks?.OnCapabilitiesChanged(newCapabilities);
        InputDispatchTarget?.OnCapabilitiesChanged(newCapabilities);
        _accessKeys.OnCapabilitiesChanged(newCapabilities);
    }

    // ───────────────────────────── teardown (design doc §10.7) ─────────────────────────────

    /// <summary>
    /// The canonical teardown — runs in <c>finally</c> so crash paths restore the terminal too;
    /// every step best-effort and idempotent. Order: sync-context uninstall + job-drain (canceled)
    /// → <i>(P7: CloseAllAsync)</i> → animation shutdown seam → pump cancel + blocking wait →
    /// renderer Close → show cursor → SGR reset → pointer-shape reset (capability-gated, §7.6) →
    /// leave alt screen (or clear) → one write + flush
    /// → <i>(P5: palette dispose)</i> → host dispose (owned only) → clear thread-local Current.
    /// </summary>
    internal void RunTeardown()
    {
        if (Interlocked.Exchange(ref _tornDown, 1) != 0)
            return;

        // 0. Uninstall the sync context FIRST — every await below is a blocking GetResult();
        //    without this a captured continuation would post to a dispatcher nobody drains again.
        //    Then complete every queued InvokeAsync as canceled (actions NOT run).
        if (SynchronizationContext.Current == _syncContext)
            SynchronizationContext.SetSynchronizationContext(null);

        Dispatcher.BeginShutdown();
        Dispatcher.DrainJobsCanceled();

        // 1. Close every window top-down (pending ShowDialogAsync tasks complete as their windows close — §8.8).
        try
        {
            _windowManager?.CloseAllAsync().GetAwaiter().GetResult();
        }
        catch {}

        // 1b. Tear down the CURRENT root element (punch #39 / lifecycle contract): detach the surface
        //     FIRST (the detach walk's BD22 quiesce shuts the reverse binding lane before any severance
        //     cascade, so no phantom edits reach view-models — the chooser selection-loss fix lives in the
        //     engine, not in ordering), then the permanent sweep releases bindings and view-model
        //     subscriptions. Teardown-before-detach is deliberately NOT used here: a torn-down store
        //     desyncs from the StyleEngine's frame state and the subsequent detach walk throws PD21 at the
        //     first styled element (BD22 audit). Best-effort + idempotent like every step here.
        try
        {
            if (RootElement is {} appRoot)
            {
                _windowManager?.SetRootSurface(null);
                appRoot.TearDown();
            }
        }
        catch {}

        // 1c. The formerly-mounted-roots backstop: swapping roots mid-run is reversible, and the guide's
        //     advice stands — tear a root down eagerly when you swap it out for good. But past this point
        //     every element from this app is permanently unusable (the dispatcher dies with the pump), so
        //     any still-alive swapped-out root is by definition a leak: its view-model subscriptions pin
        //     the whole subtree until process exit. Sweep them here, on the UI thread, while teardown is
        //     still legal. Weak refs — eagerly-torn (idempotent no-op) or collected roots cost nothing.
        //     Per-root guard (BD22 audit): one root's throwing OnTearDown must not abandon its siblings.
        if (_formerRoots is {} formers)
        {
            foreach (var weak in formers)
            {
                try
                {
                    if (weak.TryGetTarget(out var former))
                        former.TearDown();
                }
                catch {}
            }

            _formerRoots = null;
        }

        // 2. Animation shutdown — handles released, values revert to base (P8 seam).
        try
        {
            AnimationDriver?.Shutdown();
        }
        catch {}

        // 3. Cancel the pump; blocking wait. (The inline CPR sink unhooks with it — no more
        //    device responses are coming once the pump stops.)
        _inlineCprSink?.Dispose();
        _inlineCprSink = null;

        try
        {
            _pumpCts?.Cancel();
        }
        catch {}

        if (_pumpTask is {} pump)
        {
            try
            {
                pump.GetAwaiter().GetResult();
            }
            catch {}
        }

        // 4–8. The byte sequence: renderer Close (fragment erases + re-enable autowrap — MUST run
        // before leaving the session or the shell inherits a no-wrap terminal), show cursor, SGR
        // reset, leave alt screen (or clear on the fallback), one write + one blocking flush.
        if (_host is {} host && _renderer is not null)
        {
            try
            {
                // Exiting while ESCALATED (InlineWithSwitching with a window still open): return to
                // the inline side first — close the fullscreen renderer and pop the alt scope while
                // its screen is still current — so the ordinary inline teardown below (WriteInlineExit
                // against the region the DECRST 1049 pop just restored) applies unchanged.
                if (_options.Inline && !IsPresentingInline)
                    SwitchPresentation(toInline: true, forTeardown: true);

                _scratch.ResetWrittenCount();

                // Retain-mode inline exit keeps the last frame standing — fragment payloads
                // (images, sized text) are part of that frame, so their protocol erases are
                // skipped; every other exit erases them as always.
                var retainInlineFrame = _options.Inline && InlineExitBehavior == InlineExitBehavior.Retain;

                _renderer.Close(_scratch, eraseFragments: !retainInlineFrame);
                CursorWriter.WriteShow(_scratch);
                SgrEncoder.WriteReset(_scratch);

                if (_cursorColorEmitted)
                    PaletteWriter.WriteResetCursor(_scratch); // restore the default cursor color only if we set OSC 12 (review #9)

                if (_emittedCaretBand is not null)
                    CursorWriter.WriteClearExtraCursors(_scratch); // the band's extras are screen-fixed — never leave them behind

                if (_capabilities.Output.Protocol.MouseCursorShape)
                    MouseCursorWriter.WriteSet(
                        _scratch,
                        MouseCursorShape.Default); // §7.6 — the shell inherits the default pointer (not WriteReset: Ghostty ignores empty-payload reset)

                if (_options.Inline)
                    WriteInlineExit(_scratch);
                else if (!_enteredAltScreen)
                    ScreenWriter.WriteClearScreen(_scratch);

                host.Output.Writer.Write(_scratch.WrittenSpan);
                host.Output.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

                // Dispose the alt-screen scope AFTER the renderer/cursor/SGR teardown above (renderer.Close
                // MUST run before we leave the buffer): it pops the alt-screen Kitty push, then leaves the
                // alt buffer — pop-before-leave, so nothing strands on the alt stack for the next program.
                if (_altScreenScope is { } altScope)
                {
                    _altScreenScope = null;
                    altScope.DisposeAsync().AsTask().GetAwaiter().GetResult();

                    // Re-assert autowrap on the MAIN screen now that we've left the alt buffer. DECAWM can
                    // be per-screen-buffer, so renderer.Close's re-enable (emitted on the alt screen above)
                    // doesn't reliably carry to the main screen the shell / next program inherits.
                    _scratch.ResetWrittenCount();
                    ScreenWriter.WriteEnableAutowrap(_scratch);
                    host.Output.Writer.Write(_scratch.WrittenSpan);
                    host.Output.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch {}
        }

        // 9. (P5 seam: palette?.Dispose() — OSC resets while the sink is still open.)
        // 10. Host disposal (skipped for BYO unless disposeWithApp) — opt-in disables, cooked mode.
        if (_ownsHost && _host is {} ownedHost)
        {
            try
            {
                ownedHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch {}
        }

        // 11. Only now is Console.WriteLine safe. Clear the thread-local Current + scheduler on this thread.
        //     Also drop the theme-styles re-match hook so a theme dictionary the caller still references
        //     does not keep this app's StyleEngine (and the app) reachable after teardown (R2/B13 leak).
        _theme?.ThemeStylesReMatchHook = null;

        if (ReferenceEquals(_current, this))
            _current = null;

        AnimationScheduler.Uninstall(_animationScheduler);
    }

    /// <summary>
    /// Idempotent disposal: requests shutdown and, when the production loop ran, awaits its
    /// completion (teardown runs on the loop thread); otherwise runs teardown directly (the
    /// headless / never-ran paths).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Shutdown(0);

        if (_runTask is {} run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch
            {
                // RunAsync's awaiter owns the fatal exception; disposal is best-effort
            }
        }
        else
        {
            RunTeardown();
        }

        if (ReferenceEquals(_current, this))
            _current = null;

        AnimationScheduler.Uninstall(_animationScheduler);
    }

    // ───────────────────────────── headless stepping (UITestHost) ─────────────────────────────

    /// <summary>
    /// The headless start (UITestHost): synchronous, no probes, no TTY, no thread spawn, no
    /// ownership transfer — the calling thread (the Build thread) is the UI thread and frames run
    /// only when stepped. Requires a builder-supplied <see cref="ITerminalHost"/>.
    /// </summary>
    internal void StartHeadless()
    {
        Dispatcher.VerifyAccess();

        if (Interlocked.Exchange(ref _runCalled, 1) != 0)
            throw new InvalidOperationException("The application has already been started.");

        if (_options.Host is not {} host)
            throw new InvalidOperationException("Headless start requires a builder-supplied terminal host (WithTerminalHost).");

        _host = host;
        _ownsHost = _options.DisposeHost;

        var size = host.QuerySizeAsync().AsTask().GetAwaiter().GetResult() ?? (80, 24);
        InitializeFromHost(size);
        ComposeSystems();

        // User configuration applies before the fan-out — parity with the production preamble.
        ApplyUserConfiguration();

        // Fan-out parity with the production preamble (S7 theme leg first — inversion 6).
        OnCapabilitiesChanged(_capabilities);
        StyleHooks?.OnCapabilitiesChanged(_capabilities);
        InputDispatchTarget?.OnCapabilitiesChanged(_capabilities);
        _accessKeys.OnCapabilitiesChanged(_capabilities);
    }

    /// <summary>
    /// Runs ONE full frame synchronously on the calling (UI) thread — phases 0–6, the single
    /// advancement point, no Phase-7 wait. The sync context is installed around the frame and
    /// restored after (test-host contract). A recorded fatal exception is rethrown to the test.
    /// Returns whether the frame rendered (emitted or attempted emission).
    /// </summary>
    internal bool StepFrame()
    {
        Dispatcher.VerifyAccess();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        try
        {
            var elapsed = _options.TimeProvider.GetElapsedTime(_appStartTimestamp);
            var time = new FrameTime(_frame, elapsed, elapsed - _lastElapsed);
            var result = RunFrameOnce(in time);

            _frame++;
            _lastElapsed = elapsed;

            if (_fatalException is {} fatal)
                ExceptionDispatchInfo.Throw(fatal);

            return result.Rendered;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    // S5 transitions (§9.5): elements whose first arrange completed this pass, awaiting the go-live flip.
    private readonly HashSet<TransitionManager> _pendingTransitionGoLive = [];

    internal void RequestTransitionGoLive(TransitionManager manager) => _pendingTransitionGoLive.Add(manager);

    internal bool CancelTransitionGoLiveRequest(TransitionManager manager) => _pendingTransitionGoLive.Remove(manager);

    /// <summary>Flips the go-live latch on every newly-arranged transition manager (post-layout boundary; empty-set early-out).</summary>
    private void CompletePendingTransitionGoLive()
    {
        if (_pendingTransitionGoLive.Count == 0)
            return;

        foreach (var manager in _pendingTransitionGoLive)
            manager.GoLive();

        _pendingTransitionGoLive.Clear();
    }
}