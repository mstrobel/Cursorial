namespace GlowTerm.Core.Terminal;

/// <summary>
/// Detects a terminal's identity and capabilities and, when configured to do so, negotiates
/// opt-in protocols (Kitty keyboard, bracketed paste, mouse tracking, focus events, Win32
/// input mode, synchronized output, …). Returns a <see cref="TerminalCapabilities"/> snapshot
/// reflecting the realized state after negotiation.
/// </summary>
/// <remarks>
/// <para>
/// This interface deliberately abstracts over the detection mechanism. The VT implementation
/// drives a probe-and-response dance over <see cref="GlowTerm.Core.Input.IInputByteSource"/>
/// and <see cref="GlowTerm.Core.Output.IOutputByteSink"/> using the DA1 sentinel pattern. The
/// Win32 implementation produces equivalent results from <c>GetConsoleMode</c>,
/// <c>GetCurrentConsoleFontEx</c>, registry inspection, and parent-process identification.
/// Consumers only see the negotiated capabilities; how they were obtained is not part of the
/// contract.
/// </para>
/// <para>
/// <b>Statefulness and restore.</b> Negotiation is not read-only when opt-ins are enabled —
/// it changes terminal state. The negotiator records every opt-in it performs and reverses
/// them on <see cref="RestoreAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/>.
/// Applications MUST ensure the negotiator is disposed (or restored) before exit, otherwise
/// the terminal can be left in a non-default state — Kitty keyboard pushed, mouse tracking
/// enabled, alternate screen active. Where possible, register a process-exit handler that
/// disposes the negotiator on signals such as SIGTERM / Ctrl-C / Ctrl-Break.
/// </para>
/// <para>
/// <b>Single negotiation per instance.</b> A negotiator is single-shot: call
/// <see cref="NegotiateAsync"/> once. Repeating an opt-in negotiation requires a new instance
/// (the prior instance must restore first). This avoids ambiguity over which prior state to
/// restore to.
/// </para>
/// </remarks>
public interface ITerminalNegotiator : IAsyncDisposable
{
    /// <summary>
    /// Probes the terminal and applies opt-ins per <paramref name="options"/>. The returned
    /// snapshot reflects features that were ACTUALLY enabled — features the terminal claimed
    /// but did not honor are reported as unavailable. The snapshot also exposes
    /// <see cref="TerminalIdentification"/> — name, version, family, multiplexer presence —
    /// gathered during the probe.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="NegotiateAsync"/> has already been called on this instance, or
    /// when the negotiator has been disposed.
    /// </exception>
    Task<TerminalCapabilities> NegotiateAsync(
        NegotiationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses every opt-in performed during the most recent <see cref="NegotiateAsync"/>
    /// call, restoring the terminal to its prior state. Idempotent. A no-op when no opt-ins
    /// were performed (passive probe, or <see cref="NegotiateAsync"/> not yet called).
    /// </summary>
    /// <remarks>
    /// Restore is best-effort: if the underlying transport has failed (terminal closed, broken
    /// pipe), restore swallows the error rather than throwing. Consumers that need to know
    /// whether restore succeeded should check transport health separately.
    /// </remarks>
    Task RestoreAsync(CancellationToken cancellationToken = default);
}
