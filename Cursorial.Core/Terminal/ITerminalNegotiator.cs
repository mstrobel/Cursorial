using Cursorial.Input;
using Cursorial.Output;

namespace Cursorial.Terminal;

/// <summary>
/// Detects a terminal's identity and capabilities and, when configured to do so, negotiates
/// opt-in protocols (Kitty keyboard, bracketed paste, mouse tracking, focus events, Win32
/// input mode, synchronized output, …). Returns a <see cref="TerminalCapabilities"/> snapshot
/// reflecting the realized state after negotiation.
/// </summary>
/// <remarks>
/// <para>
/// This interface deliberately abstracts over the detection mechanism. The VT implementation
/// drives a probe-and-response dance over <see cref="IInputByteSource"/>
/// and <see cref="IOutputByteSink"/> using the DA1 sentinel pattern. The
/// Win32 implementation produces equivalent results from <c>GetConsoleMode</c>,
/// <c>GetCurrentConsoleFontEx</c>, registry inspection, and parent-process identification.
/// Consumers only see the negotiated capabilities; how they were obtained is not part of the
/// contract.
/// </para>
/// <para>
/// <b>Statefulness and restore.</b> Negotiation is not read-only when opt-ins are enabled —
/// it changes the terminal state. The negotiator records every opt-in it performs and reverses
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

    /// <summary>
    /// Re-emits the opt-in <b>enable</b> sequences that the most recent <see cref="NegotiateAsync"/>
    /// applied, on whatever screen is currently active, WITHOUT changing the restore accounting (the
    /// tracked push/pop balance is untouched). Intended for a consumer that switched screen buffers
    /// after negotiation: the Kitty keyboard flag stack is per-screen-buffer, so entering the
    /// alternate screen starts a fresh, empty stack and silently drops key-up / repeat reporting until
    /// the flags are re-applied. The screen-independent global modes (mouse / focus / paste / Win32) are
    /// re-issued idempotently; the per-screen Kitty push is <b>not</b> idempotent — each call appends
    /// another entry to the active screen's stack, so call this <b>at most once per screen-buffer entry</b>
    /// (the matching screen-leave DECRST 1049 reclaims it). A no-op when nothing was applied, the
    /// negotiator was already restored, or negotiation never ran. The default implementation is a no-op
    /// for negotiators with no screen-local state to re-apply.
    /// </summary>
    ValueTask ReapplyScreenLocalOptInsAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Build the byte sequence that <see cref="RestoreAsync"/> would write to the output sink,
    /// without performing any I/O of its own, and mark the negotiator as restored so subsequent
    /// <see cref="RestoreAsync"/> calls become no-ops. The caller is responsible for writing
    /// the bytes to whatever transport it controls.
    /// </summary>
    /// <remarks>
    /// Intended for signal-handler / process-exit paths that need synchronous, bounded-latency
    /// restore. Going through the async sink chain (PipeWriter → FileStream → write(2)) carries
    /// hang risk if the pipeline is in a bad state when a signal arrives, and the resulting
    /// late writes can also echo through cooked mode if termios has already been restored by
    /// the time they reach the kernel. By having the caller take the bytes and emit them via a
    /// direct syscall before touching termios, both problems are avoided.
    /// </remarks>
    /// <returns>
    /// A read-only buffer containing the opt-in disable sequences in LIFO order, ready to be
    /// written verbatim to the output transport. Empty when no opt-ins were applied or when
    /// the negotiator has already been restored.
    /// </returns>
    ReadOnlyMemory<byte> BuildRestoreSequence();
}
