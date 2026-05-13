namespace Cursorial.Terminal;

/// <summary>
/// Identifies a terminal program by family. Used by quirk-handling code to opt into or around
/// terminal-specific behaviors. <see cref="Unknown"/> is the safe default — assume only the
/// generic VT baseline applies.
/// </summary>
/// <remarks>
/// Family identification is best-effort. It is derived from a combination of XTVERSION
/// responses, secondary device attributes (DA2), <c>TERM_PROGRAM</c> / <c>TERM</c> environment
/// variables, and platform-specific signals (notably the Win32 console host detection on
/// Windows). The TERM env var alone is unreliable — most modern terminals report
/// <c>xterm-256color</c> regardless of identity.
/// <c>xterm-256color</c> regardless of identity.
/// </remarks>
public enum TerminalFamily
{
    Unknown = 0,

    /// <summary>Generic xterm or xterm-derivative.</summary>
    Xterm,

    Kitty,
    Iterm2,
    WezTerm,
    Alacritty,
    Foot,
    Konsole,
    GnomeTerminal,
    XfceTerminal,
    Rxvt,
    Mlterm,

    /// <summary>Apple's Terminal.app on macOS.</summary>
    AppleTerminal,

    /// <summary>Microsoft's modern Windows Terminal.</summary>
    WindowsTerminal,

    /// <summary>The legacy Windows console host (conhost.exe).</summary>
    WindowsConsoleHost,

    /// <summary>tmux, acting as a terminal multiplexer between the real terminal and the application.</summary>
    Tmux,

    /// <summary>GNU Screen.</summary>
    GnuScreen,

    /// <summary>A VT-conforming terminal that did not identify itself further.</summary>
    GenericVt
}
