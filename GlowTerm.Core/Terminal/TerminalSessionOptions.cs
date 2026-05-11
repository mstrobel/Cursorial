using GlowTerm.Core.Input;

namespace GlowTerm.Core.Terminal;

/// <summary>
/// Configuration for opening a <see cref="TerminalSession"/>. Carries every knob the session
/// needs to construct its negotiator, input device, and (eventually) terminal-mode handling.
/// </summary>
public sealed record class TerminalSessionOptions
{
    /// <summary>Options driving the capability negotiation phase. See <see cref="NegotiationOptions"/>.</summary>
    public NegotiationOptions Negotiation { get; init; } = new();

    /// <summary>
    /// The bare-ESC vs sequence-introducer ambiguity timeout used by the input device. Bytes
    /// arriving within this window after a lone <c>ESC</c> are taken as part of a CSI / SS3 /
    /// Alt+key sequence; if the window elapses with no follow-up, the device commits the bare
    /// ESC as an Escape keypress. Default is the xterm convention of 50 ms.
    /// </summary>
    public TimeSpan EscapeAmbiguityTimeout { get; init; } = VtInputDevice.DefaultEscapeAmbiguityTimeout;
}
