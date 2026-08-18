using Cursorial.Input;

namespace Cursorial.Terminal;

/// <summary>
/// Configuration for opening a <see cref="TerminalSession"/>. Carries every knob the session
/// needs to construct its negotiator, input device, and (eventually) terminal-mode handling.
/// </summary>
public sealed record TerminalSessionOptions
{
    /// <summary>Options driving the capability negotiation phase. See <see cref="NegotiationOptions"/>.</summary>
    public NegotiationOptions Negotiation { get; init; } = new();

    /// <summary>
    /// A previously negotiated capability snapshot to seed the session from, skipping the wire
    /// handshake entirely. When set, <see cref="TerminalSession.OpenAsync(TerminalSessionOptions?, CancellationToken)"/>
    /// performs NO probe rounds — no XTVERSION / DA1 identification queries, no DECRQM
    /// verification, no OSC color probes. The negotiator applies the same opt-in enable
    /// sequences a full negotiation's opt-in round would emit (decided from the snapshot's
    /// identification plus the <see cref="Negotiation"/> flags, family-gated identically) and
    /// records them so restore emits the matching disables. The session's
    /// <see cref="TerminalSession.Capabilities"/> becomes the snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended for capability caching (docs/cli-design.md §6): serialize
    /// <see cref="TerminalSession.Capabilities"/> after a cold run via
    /// <see cref="TerminalCapabilitiesSerializer"/>, key it by the terminal-identity environment
    /// (<c>TERM</c> / <c>TERM_PROGRAM</c> / <c>TERM_PROGRAM_VERSION</c> + multiplexer flags),
    /// and seed subsequent runs. This removes the dominant interactive-startup cost — the
    /// sentinel-bounded probe round-trips (2–3 terminal RTTs; up to 1.5&#x202f;s of timeout
    /// budget on a mute terminal).
    /// </para>
    /// <para>
    /// <b>The snapshot should have been captured under equivalent <see cref="Negotiation"/>
    /// options.</b> The opt-in enables actually emitted on the seeded path always follow the
    /// CURRENT options (so restore parity holds unconditionally), while the reported snapshot
    /// is the cache's claim; capturing and seeding with different option sets can therefore
    /// under- or over-report individual opt-in capabilities. Keying the cache by terminal
    /// identity AND keeping the option profile stable across runs (as the curio CLI does)
    /// avoids the mismatch entirely.
    /// </para>
    /// <para>
    /// Skipped — full negotiation runs as usual — when the transports take the Windows native
    /// console path, which never performs the wire handshake in the first place.
    /// </para>
    /// </remarks>
    public TerminalCapabilities? CachedCapabilities { get; init; }

    /// <summary>
    /// The bare-ESC vs. sequence-introducer ambiguity timeout used by the input device. Bytes
    /// arriving within this window after a lone <c>ESC</c> are taken as part of a CSI / SS3 /
    /// Alt+key sequence; if the window elapses with no follow-up, the device commits the bare
    /// ESC as an Escape keypress. Default is the xterm convention of 50 ms.
    /// </summary>
    public TimeSpan EscapeAmbiguityTimeout { get; init; } = VtInputDevice.DefaultEscapeAmbiguityTimeout;
}