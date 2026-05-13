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
    private readonly IStdioTransports? _ownedTransports;
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];
    private EventHandler? _processExitHandler;
    private PosixResizeMonitor? _resizeMonitor;
    private int _disposed;

    private TerminalSession(
        TerminalCapabilities capabilities,
        IAsyncInputDevice input,
        IOutputByteSink output,
        ITerminalNegotiator negotiator,
        IStdioTransports? ownedTransports)
    {
        Capabilities = capabilities;
        _input = input;
        _output = output;
        _negotiator = negotiator;
        _ownedTransports = ownedTransports;

        // Only attach safety-net handlers and the resize monitor when we own the transports —
        // i.e. only for the happy-path (parameterless) overload. BYO callers have their own
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
    /// Opens a session over caller-supplied transports. Useful for embedding inside a tool that
    /// already manages terminal state, or for driving the input pipeline from a recorded trace.
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
            // OpenInternalAsync handles the negotiator + device cleanup. We're responsible
            // for the transports we just opened.
            try { await transports.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
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
            var capabilities = await negotiator
                .NegotiateAsync(options.Negotiation, cancellationToken)
                .ConfigureAwait(false);

            device = new VtInputDevice(
                source,
                capabilities.Input,
                mode,
                escapeAmbiguityTimeout: options.EscapeAmbiguityTimeout);

            return new TerminalSession(capabilities, device, sink, negotiator, ownedTransports);
        }
        catch
        {
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
        }
    }

    public async ValueTask DisposeAsync()
    {
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
        // POSIX delivers terminal resizes via SIGWINCH; we register a watcher and feed each
        // resize back into the input device's event stream so consumers see them interleaved
        // with keyboard/mouse input. Windows console resize delivery (WINDOW_BUFFER_SIZE_EVENT
        // via ReadConsoleInput) is not yet plumbed — TODO.
        if (_input is not VtInputDevice device) return;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        try
        {
            _resizeMonitor = new PosixResizeMonitor(device.EnqueueExternalEvent);
            _resizeMonitor.Start();
        }
        catch
        {
            // Resize delivery is best-effort — session opening must not fail because we
            // couldn't subscribe to SIGWINCH (some sandboxes block signal registration).
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
            // Signal not supported on this OS — ignore.
        }
        catch
        {
            // Defensive: never let signal registration break session opening.
        }
    }

    private void UnregisterSafetyHandlers()
    {
        if (_processExitHandler is not null)
        {
            try { AppDomain.CurrentDomain.ProcessExit -= _processExitHandler; } catch { }
            _processExitHandler = null;
        }

        foreach (var registration in _signalRegistrations)
        {
            try { registration.Dispose(); } catch { }
        }
        _signalRegistrations.Clear();
    }

    private void HandleSignal(PosixSignalContext context)
    {
        // Suppress the default action — we'll exit ourselves after cleanup so terminal state
        // is restored deterministically before the process goes away.
        context.Cancel = true;

        EmergencyRestoreAndDispose();

        // Standard POSIX convention: signal exit code is 128 + signal number.
        Environment.Exit(128 + Math.Abs((int)context.Signal));
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
        // Two-phase shutdown when the process is going down:
        //   Phase 1 — guaranteed: synchronously restore termios / Windows console mode so the
        //   user's shell isn't left in raw mode. This is always safe to attempt (it's
        //   idempotent and doesn't write to the VT stream); it must run before any awaits.
        if (_ownedTransports is not null)
        {
            try { _ownedTransports.RestoreTerminalState(); } catch { /* best-effort */ }
        }

        // Phase 2 — best-effort within a bounded budget: stop the input pump and write VT
        // opt-in disable sequences (Kitty pop, mouse off, etc.) to the sink. These require
        // async work that may block — on POSIX, `read(2)` in the pump doesn't honor token
        // cancellation mid-syscall, so the pump may not unwind cleanly until the next byte
        // arrives (or never, if the terminal is already closing). Cap the wait at 2 s so the
        // process can still exit when the pump won't unblock. If we time out here, the
        // worst-case visible artifact is a single residual opt-in (e.g. Kitty flags still
        // pushed) — annoying but not data-destructive, since phase 1 already restored the
        // mode that determines whether the user can see a prompt at all.
        try { DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
