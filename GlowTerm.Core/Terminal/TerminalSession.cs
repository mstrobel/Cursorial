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
/// factory.
/// </summary>
/// <remarks>
/// <para>
/// <b>BYO transport contract.</b> When constructed via the BYO factory (the only one
/// available so far), the session does NOT take ownership of the supplied
/// <see cref="IInputByteSource"/> or <see cref="IOutputByteSink"/>. Disposal stops the input
/// pump and reverses every opt-in the negotiator applied, but it does NOT close, complete, or
/// dispose the caller-supplied transports. The caller is responsible for raw-mode handling on
/// stdin and any restoration after the session ends.
/// </para>
/// <para>
/// A future parameterless overload (<c>OpenAsync()</c>) will own its own transports for the
/// happy path; that overload will dispose the transports it created in addition to performing
/// negotiator restore.
/// </para>
/// </remarks>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly ITerminalNegotiator _negotiator;
    private readonly IAsyncInputDevice _input;
    private readonly IOutputByteSink _output;
    private readonly IAsyncDisposable? _ownedTransports;
    private int _disposed;

    private TerminalSession(
        TerminalCapabilities capabilities,
        IAsyncInputDevice input,
        IOutputByteSink output,
        ITerminalNegotiator negotiator,
        IAsyncDisposable? ownedTransports)
    {
        Capabilities = capabilities;
        _input = input;
        _output = output;
        _negotiator = negotiator;
        _ownedTransports = ownedTransports;
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
    /// taking ownership of terminal-mode state (POSIX raw mode via <c>stty</c>, Windows
    /// console-mode flags). Disposal restores the prior terminal state. Throws when standard
    /// I/O is not connected to a real terminal (running under a pipe, in CI without a
    /// pseudo-tty, etc.) — use the BYO overload in those cases.
    /// </summary>
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
        IAsyncDisposable? ownedTransports,
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
}
