using System.Runtime.InteropServices;
using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;
using GlowTerm.Core.Output;
using GlowTerm.Core.Terminal.Stdio;

namespace GlowTerm.Core.Terminal;

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

        // Only attach safety-net handlers when we own the transports — i.e. only for the
        // happy-path (parameterless) overload. BYO callers have their own signal-handling
        // strategy and shouldn't be surprised by ours.
        if (_ownedTransports is not null)
        {
            RegisterSafetyHandlers();
        }
    }

    /// <summary>The realized capabilities returned by the negotiator at session start.</summary>
    public TerminalCapabilities Capabilities { get; }

    /// <summary>The input device — pull-based <see cref="InputEvent"/> stream.</summary>
    public IAsyncInputDevice Input => _input;

    /// <summary>The output sink — bytes written here reach the terminal.</summary>
    public IOutputByteSink Output => _output;

    /// <summary>
    /// Opens a session over caller-supplied transports. The library does NOT touch terminal
    /// mode — the caller is responsible for placing stdin in raw mode (or whatever the source
    /// represents) before the session starts and restoring it after disposal. Useful for
    /// embedding inside a tool that already manages terminal state, or for driving the input
    /// pipeline from a recorded trace.
    /// </summary>
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

        // Stop the input pump first so we're not racing with the negotiator's restore writes.
        try { await _input.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        // Then restore opt-ins (this writes disable sequences to the sink).
        try { await _negotiator.DisposeAsync().ConfigureAwait(false); }
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
        // The critical operation is restoring termios / console mode — sync and fast.
        // Do this first so even if the async disposal hangs, the terminal isn't stuck.
        if (_ownedTransports is not null)
        {
            try { _ownedTransports.RestoreTerminalState(); } catch { /* best-effort */ }
        }

        // Best-effort full disposal: cancel the input pump, write opt-in disables, close
        // streams. Cap at 2 s so a stuck async path doesn't hold up process exit.
        try { DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
