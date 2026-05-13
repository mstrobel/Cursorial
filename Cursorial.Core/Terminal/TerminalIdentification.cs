namespace Cursorial.Terminal;

/// <summary>
/// What the negotiation phase learned about the terminal's identity. All fields except
/// <see cref="Family"/> are best-effort and may be null on terminals that did not respond to
/// identification queries.
/// </summary>
/// <param name="Family">Family classification — see <see cref="TerminalFamily"/>.</param>
/// <param name="Name">Self-reported terminal program name (e.g. "iTerm2", "kitty", "WezTerm").</param>
/// <param name="Version">Self-reported terminal version string.</param>
/// <param name="RawTermEnv">The raw <c>TERM</c> environment variable as observed at session start.</param>
/// <param name="RawTermProgramEnv">The raw <c>TERM_PROGRAM</c> environment variable as observed at session start.</param>
/// <param name="InsideMultiplexer">
/// True when the terminal is running inside a multiplexer (tmux, GNU Screen). Useful because
/// multiplexers commonly downgrade or filter passthrough sequences; consumers may need to wrap
/// outgoing sequences in DCS pass-through to reach the outer terminal.
/// </param>
public sealed record TerminalIdentification(
    TerminalFamily Family,
    string? Name,
    string? Version,
    string? RawTermEnv,
    string? RawTermProgramEnv,
    bool InsideMultiplexer)
{
    /// <summary>An identification reporting nothing known about the terminal.</summary>
    public static TerminalIdentification Unknown { get; } = new(
        Family: TerminalFamily.Unknown,
        Name: null,
        Version: null,
        RawTermEnv: null,
        RawTermProgramEnv: null,
        InsideMultiplexer: false);
}