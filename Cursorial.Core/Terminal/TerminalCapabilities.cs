using Cursorial.Core.Input;
using Cursorial.Core.Output;

namespace Cursorial.Core.Terminal;

/// <summary>
/// The realized capability surface of a terminal session — what the terminal is, what it
/// reports as input, and what it accepts as output. Returned by
/// <see cref="ITerminalNegotiator.NegotiateAsync"/>.
/// </summary>
/// <remarks>
/// Reflects ACTUAL realized capabilities after negotiation, not advertised ones. If the
/// terminal claims a feature but did not honor the opt-in (some terminals advertise Sixel
/// without implementing it, for example), the corresponding capability flag will be false.
/// Consumers can therefore branch on capabilities directly without further validation.
/// </remarks>
public sealed record class TerminalCapabilities(
    TerminalIdentification Terminal,
    InputCapabilities Input,
    OutputCapabilities Output)
{
    /// <summary>A capabilities snapshot reporting nothing known and nothing supported.</summary>
    public static TerminalCapabilities None { get; } = new(
        Terminal: TerminalIdentification.Unknown,
        Input: InputCapabilities.None,
        Output: OutputCapabilities.None);
}
