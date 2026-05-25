using System.Runtime.InteropServices;

using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Input.Parsing;
using Cursorial.Output;
using Cursorial.Terminal.Stdio;

namespace Cursorial.Terminal;

/// <summary>
/// A configured terminal session — the orchestrated combination of a negotiator (run once at
/// startup), an <see cref="IAsyncInputDevice"/> over the byte source, and an output sink, all
/// wired against a shared <see cref="VtInputMode"/>. Returned by the
/// <see cref="OpenAsync(IInputByteSource, IOutputByteSink, TerminalSessionOptions?, CancellationToken)"/>
/// and <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)"/> factories.
/// </summary>
/// <remarks>
/// <para>
/// <b>BYO transport contract.</b> When constructed via the BYO factory, the session does NOT
/// take ownership of the supplied <see cref="IInputByteSource"/> or <see cref="IOutputByteSink"/>.
/// Disposal stops the input pump and reverses every opt-in the negotiator applied, but it does
/// NOT close, complete, or dispose the caller-supplied transports. The caller is responsible
/// for raw-mode handling on stdin and any restoration after the session ends.
/// </para>
/// <para>
/// <b>Happy-path safety net.</b> When constructed via the parameterless overload (which opens
/// stdio transports), the session registers POSIX signal handlers (SIGINT, SIGTERM, SIGHUP,
/// SIGQUIT) and an <see cref="AppDomain.ProcessExit"/> handler. If the process is killed or
/// exits without explicit disposal, these handlers synchronously restore terminal state via
/// <see cref="IStdioTransports.RestoreTerminalState"/> before the process actually goes away —
/// preventing the terminal from being left in raw mode after a Ctrl+C / SIGKILL / unhandled
/// exception. Handlers are unregistered on normal disposal.
/// </para>
/// </remarks>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly ITerminalNegotiator _negotiator;
    private readonly IAsyncInputDevice _input;
    private readonly IOutputByteSink _output;
    private readonly IInputByteSource _source;
    private readonly IStdioTransports? _ownedTransports;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];

    private EventHandler? _processExitHandler;
    private IResizeMonitor? _resizeMonitor;
    private int _disposed;

    private TerminalSession(TerminalCapabilities capabilities,
                            IInputByteSource source,
                            IAsyncInputDevice input,
                            IOutputByteSink output,
                            ITerminalNegotiator negotiator,
                            IStdioTransports? ownedTransports)
    {
        Capabilities = capabilities;
        _source = source;
        _input = input;
        _output = output;
        _negotiator = negotiator;
        _ownedTransports = ownedTransports;

        // Only attach safety-net handlers and the resize monitor when we own the transports —
        // i.e., only for the happy-path (parameterless) overload. BYO callers have their own
        // signal-handling strategy and shouldn't be surprised by ours.
        if (_ownedTransports is not null)
        {
            RegisterSafetyHandlers();
            StartResizeMonitor();
        }
    }

    /// <summary>The realized capabilities returned by the negotiator at session start.</summary>
    public TerminalCapabilities Capabilities { get; }

    /// <summary>The input device — pull-based <see cref="InputEvent"/> stream.</summary>
    public IAsyncInputDevice Input => _input;

    /// <summary>The output sink — bytes written here reach the terminal.</summary>
    public IOutputByteSink Output => _output;

    /// <summary>
    /// Query the terminal's current cell-grid dimensions synchronously on demand, bypassing
    /// the resize-event stream. Returns the size as <c>(Columns, Rows)</c>, or
    /// <see langword="null"/> when the size can't be determined (BYO transport with no
    /// platform resize monitor, the OS query failed, or stdio isn't a real TTY).
    /// </summary>
    /// <remarks>
    /// <para>
    /// On happy-path sessions opened via the parameterless <see cref="OpenAsync(CancellationToken)"/>
    /// overload, the query uses the platform path: <c>stty size</c> on POSIX, the
    /// <c>GetConsoleScreenBufferInfo</c> Win32 API on Windows. Both are cheap (single
    /// subprocess or single API call) and reflect the current size at the moment of the call —
    /// useful for consumers that need the size at construction time before the first
    /// <see cref="Cursorial.Input.Events.ResizeEvent"/> has reached the input stream.
    /// </para>
    /// <para>
    /// On BYO sessions opened via <see cref="OpenAsync(IInputByteSource, IOutputByteSink, TerminalSessionOptions, CancellationToken)"/>,
    /// there is no platform resize monitor — the caller manages their own transports and
    /// resize signaling. This method returns <see langword="null"/>; consumers should query
    /// their own transport layer or send a DSR request themselves.
    /// </para>
    /// <para>
    /// The implementation is synchronous under the hood but exposed as
    /// <see cref="ValueTask{TResult}"/> so a future revision can substitute a wire-level probe
    /// (e.g. <c>CSI 18 t</c>) without changing the signature.
    /// </para>
    /// </remarks>
    public ValueTask<(int Columns, int Rows)?> QueryTerminalSizeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var monitor = _resizeMonitor;
        var size = monitor?.QueryCurrentSize();
        return ValueTask.FromResult(size);
    }

    /// <summary>
    /// Opens a session over caller-supplied transports. Useful for embedding inside a tool that
    /// already manages the terminal state or for driving the input pipeline from a recorded trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Caller responsibilities</b> (not handled by this overload):
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Terminal mode.</b> Place the underlying stdin into raw mode (or whatever the source
    /// represents) before calling, and restore it after the session is disposed. This overload
    /// never touches termios / Windows console-mode flags.
    /// </description></item>
    /// <item><description>
    /// <b>Signal handling.</b> Register your own SIGINT / SIGTERM / SIGHUP / SIGQUIT handlers
    /// if you want graceful cleanup on Ctrl+C or a kill signal. The happy-path
    /// <see cref="OpenAsync(TerminalSessionOptions?, CancellationToken)"/> overload registers a
    /// safety net; this overload does NOT, because BYO callers commonly have their own
    /// strategy (and double-registration is loud).
    /// </description></item>
    /// <item><description>
    /// <b>Synchronous mode restore on signal.</b> If you register signal handlers, restore
    /// terminal mode synchronously from the handler before the process exits — async disposal
    /// may not complete in time. Do not rely on <see cref="DisposeAsync"/> alone to clean up
    /// from a signal path.
    /// </description></item>
    /// <item><description>
    /// <b>Disposal scope.</b> <see cref="DisposeAsync"/> stops the input pump and reverses every
    /// opt-in the negotiator applied (writes Kitty pop, mouse-disable, …). It does NOT close,
    /// complete, or dispose the supplied <see cref="IInputByteSource"/> or
    /// <see cref="IOutputByteSink"/>, and it does NOT restore terminal mode — that's still the
    /// caller's job (item 1 above).
    /// </description></item>
    /// </list>
    /// </remarks>
    public static Task<TerminalSession> OpenAsync(
        IInputByteSource source,
        IOutputByteSink sink,
        TerminalSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        return OpenInternalAsync(source, sink, ownedTransports: null, options, cancellationToken);
    }

    /// <summary>
    /// Happy-path overload — opens a session over the process's standard input and output,
    /// taking ownership of terminal-mode state (POSIX termios via stty, Windows console-mode
    /// flags). Disposal restores the prior terminal state. Throws when standard I/O is not
    /// connected to a real terminal (running under a pipe, in CI without a pseudo-tty, etc.) —
    /// use the BYO overload in those cases.
    /// </summary>
    /// <remarks>
    /// Registers signal handlers (SIGINT / SIGTERM / SIGHUP / SIGQUIT) and a
    /// <see cref="AppDomain.ProcessExit"/> handler so terminal state is restored even on
    /// unhandled exit. Handlers are removed on normal disposal.
    /// </remarks>
    public static async Task<TerminalSession> OpenAsync(
        TerminalSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var transports = StdioTransports.Open();

        try
        {
            return await OpenInternalAsync(
                           transports.Source,
                           transports.Sink,
                           ownedTransports: transports,
                           options,
                           cancellationToken)
                       .ConfigureAwait(false);
        }
        catch
        { 
            // @formatter:off
            
            // OpenInternalAsync handles the negotiator + device cleanup. We're responsible
            // for the transports we just opened.
            try { await transports.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
            
            throw;
            
            // @formatter:on
        }
    }

    private static async Task<TerminalSession> OpenInternalAsync(
        IInputByteSource source,
        IOutputByteSink sink,
        IStdioTransports? ownedTransports,
        TerminalSessionOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new TerminalSessionOptions();

        var mode = new VtInputMode();
        var negotiator = new VtTerminalNegotiator(source, sink, mode);
        VtInputDevice? device = null;

        try
        {
            var capabilities = await negotiator.NegotiateAsync(options.Negotiation, cancellationToken)
                                               .ConfigureAwait(false);

            device = new VtInputDevice(
                source,
                capabilities.Input,
                mode,
                escapeAmbiguityTimeout: options.EscapeAmbiguityTimeout);

            return new TerminalSession(capabilities, source, device, sink, negotiator, ownedTransports);
        }
        catch
        {
            // @formatter:off

            // Failed somewhere between negotiation and device construction — clean up the
            // negotiator + device. The caller is responsible for the transports they own
            // (BYO) or for transport cleanup happening one level up (parameterless overload).
            if (device is not null)
            {
                try { await device.DisposeAsync().ConfigureAwait(false); }
                catch { /* swallow during failed-init cleanup */ }
            }

            try { await negotiator.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow during failed-init cleanup */ }

            throw;

            // @formatter:on
        }
    }

    /// <summary>
    /// Quiesce the session's I/O machinery so an external consumer — a child process, a
    /// synchronous terminal operation, a custom raw-mode prompt — can take exclusive ownership
    /// of the underlying TTY without racing the framework's pumps. The returned handle resumes
    /// the pumps on disposal; use it with <c>await using</c> to guarantee resume on any exit
    /// path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After this method returns:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The input byte source's pump is parked entirely in user space — on POSIX, no
    /// <c>read(2)</c> or <c>poll(2)</c> is left blocked in the kernel. Bytes the user types
    /// accumulate in the kernel TTY buffer until resume; nothing is lost.
    /// </description></item>
    /// <item><description>
    /// The output sink's <see cref="System.IO.Pipelines.PipeWriter"/> has been flushed —
    /// every byte the session has written so far has reached the underlying transport.
    /// </description></item>
    /// <item><description>
    /// Bytes already in the source's input pipe (read from the transport before pause)
    /// remain in the pipe and are visible to consumers iterating <see cref="Input"/>. Pause
    /// does not discard them.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Caller contract.</b> Between this call returning and the handle being disposed, the
    /// caller must refrain from writing to <see cref="Output"/>; the framework does not enforce
    /// quiescence on writers it doesn't own. Concurrent calls to <see cref="PauseIOAsync"/>
    /// from multiple parts of the application compose via reference counting inside the
    /// pausable source itself — the pump only resumes once every handle has been disposed.
    /// </para>
    /// <para>
    /// <b>Platform support.</b> Full pause semantics on POSIX (the
    /// <see cref="IPausableInputByteSource"/> path). On Windows — and on any
    /// <see cref="IInputByteSource"/> that doesn't implement
    /// <see cref="IPausableInputByteSource"/> — this method flushes output and returns, but
    /// the input pump may still read from its transport until its next blocked syscall
    /// returns. Callers requiring strict input quiescence on Windows should avoid initiating
    /// their own console reads until after resume.
    /// </para>
    /// </remarks>
    public async Task<IAsyncDisposable> PauseIOAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        IAsyncDisposable? sourceHandle = null;
        if (_source is IPausableInputByteSource pausable)
            sourceHandle = await pausable.PauseAsync(cancellationToken).ConfigureAwait(false);

        // Flush output AFTER pausing input so any input-driven write the caller performed
        // (e.g., a response to a query) is fully on the wire before we hand off the TTY.
        // FlushAsync on a PipeWriter returns once buffered bytes have been pushed through
        // to the underlying stream.
        try
        {
            await _output.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Flush is best-effort during pause — the sink may already be closed or broken.
            // Don't poison the pause path; the caller can still proceed and the resume path
            // remains reachable via the source handle below.
        }

        return new SessionPauseHandle(sourceHandle);
    }

    private sealed class SessionPauseHandle : IAsyncDisposable
    {
        private IAsyncDisposable? _sourceHandle;

        public SessionPauseHandle(IAsyncDisposable? sourceHandle) => _sourceHandle = sourceHandle;

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange enforces single-effect dispose so repeated `await using`
            // unwinds don't double-decrement the source's pause refcount.
            var handle = Interlocked.Exchange(ref _sourceHandle, null);
            return handle?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // @formatter:off
        
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Unregister safety-net handlers first. Doing so early prevents a signal-handler
        // re-entry during the rest of disposal from running through the dispose path again.
        UnregisterSafetyHandlers();

        // Stop the resize monitor before the input pump so we don't inject a final stray
        // ResizeEvent into a channel that's about to complete.
        try { _resizeMonitor?.Dispose(); }
        catch { /* best-effort */ }

        _resizeMonitor = null;

        // Restore opt-ins FIRST, while the input pump is still running. The terminal may emit
        // trailing reports in response to the disable sequences — most notably a Kitty key-release
        // report for whatever key the user pressed to exit, plus any final mouse/focus reports.
        // With the pump still draining fd 0, those bytes are consumed into the device's internal
        // pipe (and dropped on pipe completion) rather than piling up in the TTY input queue for
        // the next cooked-mode `Console.ReadLine` to splice into the user's next command.
        try { await _negotiator.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Give the local round-trip (we write disable → terminal processes → terminal emits
        // any final reports → poll/read picks them up) a moment to complete. 50 ms is the
        // xterm bare-ESC ambiguity convention and is generous for this round-trip on any
        // terminal in the support matrix.
        try { await Task.Delay(50).ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Now stop the input pump.
        try { await _input.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Only dispose transports the session itself created (parameterless overload). BYO
        // transports are caller-owned and stay open.
        if (_ownedTransports is not null)
        {
            try { await _ownedTransports.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort */ }
        }

        // @formatter:on
    }

    // ---- Signal-handler safety net ----

    private void RegisterSafetyHandlers()
    {
        // Each registration may throw PlatformNotSupportedException on Windows (only SIGINT
        // and SIGQUIT are mapped there). Catch per-signal so the rest still register.
        TryRegisterSignal(PosixSignal.SIGINT);
        TryRegisterSignal(PosixSignal.SIGTERM);
        TryRegisterSignal(PosixSignal.SIGHUP);
        TryRegisterSignal(PosixSignal.SIGQUIT);

        _processExitHandler = HandleProcessExit;
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    private void StartResizeMonitor()
    {
        // POSIX delivers terminal resizes via SIGWINCH; Windows reports them as console-buffer-
        // size events. The factory picks the right implementation for the current OS (or
        // returns null on platforms we don't support, which silently turns resize delivery off).
        // Either way, each detected resize is fed back into the input device's event stream so
        // consumers see them interleaved with keyboard / mouse input.
        if (_input is not VtInputDevice device) return;

        try
        {
            _resizeMonitor = ResizeMonitor.Create(device.EnqueueExternalEvent);
            _resizeMonitor?.Start();
        }
        catch
        {
            // Resize delivery is best-effort — session opening must not fail because we
            // couldn't subscribe to SIGWINCH (sandboxes that block signal registration) or
            // couldn't query the Windows console (no console attached).
            _resizeMonitor?.Dispose();
            _resizeMonitor = null;
        }
    }

    private void TryRegisterSignal(PosixSignal signal)
    {
        try
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(signal, HandleSignal));
        }
        catch (PlatformNotSupportedException)
        {
            // Signal is not supported on this OS — ignore.
        }
        catch
        {
            // Defensive: never let signal registration break session opening.
        }
    }

    private void UnregisterSafetyHandlers()
    {
        // @formatter:off
        if (_processExitHandler is not null)
        {
            try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; }
            catch { /* best-effort */ }

            _processExitHandler = null;
        }

        foreach (var registration in _signalRegistrations)
        {
            try { registration.Dispose(); }
            catch { /* best-effort */ }
        }

        _signalRegistrations.Clear();
        // @formatter:on
    }

    private void HandleSignal(PosixSignalContext context)
    {
        // Suppress the default action — we'll exit ourselves after cleanup, so terminal state
        // is restored deterministically before the process goes away.
        context.Cancel = true;

        EmergencyRestoreAndDispose();

        // Standard POSIX convention: signal exit code is 128 + signal number.
        Environment.Exit(128 + Math.Abs((int) context.Signal));
    }

    private void HandleProcessExit(object? sender, EventArgs e)
    {
        // ProcessExit is triggered by normal exit paths (return from Main, Environment.Exit
        // from elsewhere). Limited time budget here — restore terminal state synchronously
        // and try best-effort disposal of the rest.
        EmergencyRestoreAndDispose();
    }

    private void EmergencyRestoreAndDispose()
    {
        // @formatter:off

        // Three-phase shutdown when the process is going down. Ordering matters:
        //   Phase 1 — emit VT opt-in disables synchronously WHILE raw mode is still active, via
        //   a direct write(2)/WriteFile syscall that bypasses the async PipeWriter chain. Doing
        //   this before termios restore means the disable bytes don't echo through cooked mode,
        //   and going through a direct syscall means the write can't hang on a stuck async
        //   pipeline. If the negotiator never ran or already restored, BuildRestoreSequence
        //   returns an empty buffer and WriteBytesSync is a no-op.
        if (_ownedTransports is not null)
        {
            try { _ownedTransports.WriteBytesSync(_negotiator.BuildRestoreSequence().Span); }
            catch { /* best-effort */ }
        }

        // Phase 2 — synchronously restore termios / Windows console mode so the user's shell
        // isn't left in raw mode. Idempotent.
        if (_ownedTransports is not null)
        {
            try { _ownedTransports.RestoreTerminalState(); }
            catch { /* best-effort */ }
        }

        // Phase 3 — best-effort within a bounded budget: tear down the input pump and close
        // the byte streams. Capped at 2 s so the process can still exit when async cleanup
        // gets stuck. The critical state — opt-in disables and termios — was already handled
        // synchronously above, so timing out here is non-destructive.
        try { DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best-effort */ }

        // @formatter:on
    }
}