using System.Buffers;
using System.Runtime.ExceptionServices;

using Cursorial.Input;
using Cursorial.Input.Events;
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

        _buffer = new CellBuffer(size.Columns, size.Rows, _capabilities) { CursorVisible = false };
        _renderer = new FrameRenderer(_capabilities.Output, new FrameRendererOptions(OrderedDither: _options.OrderedDither));

        // UI-mode entry: alt screen when supported and requested, else clear-screen fallback;
        // cursor hiding is left to the buffer (CursorVisible = false ⇒ DECRST 25 on frame 0).
        var writer = host.Output.Writer;

        _enteredAltScreen = _options.UseAlternateScreen && _capabilities.Output.Window.AlternateScreenBuffer;

        if (_enteredAltScreen)
            ScreenWriter.WriteEnterAlternateScreen(writer);
        else
            ScreenWriter.WriteClearScreen(writer);

        SgrEncoder.WriteReset(writer);
        WriteThemeCursorColor(writer); // OSC 12 = the theme accent, so the real terminal caret stays visible across variants (teardown emits OSC 112)
        writer.FlushAsync().AsTask().GetAwaiter().GetResult();

        // The Kitty keyboard flag stack is per-screen-buffer: negotiation (during host open) pushed our
        // flags on the MAIN screen, but we just switched to the ALTERNATE screen — a fresh, empty stack.
        // Re-apply the negotiated screen-local opt-ins on the now-active alt screen so key-up / repeat
        // reporting (and with it the access-key Alt gate, ND23) actually engages instead of being
        // stranded on the main screen. The negotiator's restore accounting is untouched; the extra
        // alt-screen push is discarded when we leave the alt screen at teardown. No-op for the
        // clear-screen fallback (still on the main screen) and for headless hosts.
        if (_enteredAltScreen)
            host.ReapplyScreenLocalOptInsAsync().AsTask().GetAwaiter().GetResult();

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

        _windowManager.OnViewportResized(new Size(_buffer!.Columns, _buffer.Rows));
        _inputDispatcher.SetWindowTopology(_windowManager); // S4 is the real topology now (replaces SingleRootWindowTopology)
        _systemsReady = true;

        if (_rootElement is {} root)
            WireRoot(root);
    }

    private void StartPump()
    {
        // Exactly one ReadAllAsync enumeration per session (single-shot contract). EOF ⇒ shutdown;
        // faults land in the Interlocked slot, surfaced ONCE on the UI thread (Phase 1).
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
                                   StyleHooks is { HasPendingActivations: true };

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

            // PHASE 6 — render, GATED on !_renegotiating (the negotiator owns the pipe during its window).
            if (!_renegotiating)
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
        // Coalesced last-wins: buffer Resize (contents discarded; the renderer full-redraws on
        // dimension change) → render system (fresh compositor + invalidate all) → layout facade
        // (full relayout lands in Phase 5 of the SAME frame).
        _buffer!.Resize(resize.Columns, resize.Rows);
        var size = new Size(resize.Columns, resize.Rows);
        _windowManager?.OnViewportResized(size);
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

            _renderer = new FrameRenderer(effective.Output,
                                          new FrameRendererOptions(OrderedDither: _options.OrderedDither));

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

        // 2. Animation shutdown — handles released, values revert to base (P8 seam).
        try
        {
            AnimationDriver?.Shutdown();
        }
        catch {}

        // 3. Cancel the pump; blocking wait.
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
                _scratch.ResetWrittenCount();
                _renderer.Close(_scratch);
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

                if (_enteredAltScreen)
                    ScreenWriter.WriteLeaveAlternateScreen(_scratch);
                else
                    ScreenWriter.WriteClearScreen(_scratch);

                host.Output.Writer.Write(_scratch.WrittenSpan);
                host.Output.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
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