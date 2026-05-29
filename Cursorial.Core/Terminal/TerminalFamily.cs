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
/// </remarks>
public enum TerminalFamily
{
    Unknown = 0,

    /// <summary>Generic xterm or xterm-derivative.</summary>
    Xterm,

    Kitty,
    Ghostty,
    Rio,
    ITerm2,
    WezTerm,
    Alacritty,
    Tabby,
    Foot,
    Konsole,
    GnomeTerminal,
    Terminus,
    XfceTerminal,
    Rxvt,
    Mlterm,

    /// <summary>Apple's Terminal.app on macOS.</summary>
    AppleTerminal,

    /// <summary>Microsoft's modern Windows Terminal.</summary>
    WindowsTerminal,

    /// <summary>The legacy Windows console host (conhost.exe).</summary>
    WindowsConsoleHost,

    /// <summary>The original third-party Unix PTY-compatible console for Windows.</summary>
    ConEmu,

    /// <summary>An all-in-one terminal and remote computing tool for Windows.</summary>
    MobaXTerm,

    /// <summary>tmux, acting as a terminal multiplexer between the real terminal and the application.</summary>
    Tmux,

    /// <summary>GNU Screen.</summary>
    GnuScreen,

    /// <summary>A generic ANSI terminal that did not identify itself further.</summary>
    GenericAnsi,
    
    /// <summary>WSL terminal that did not identify itself further. Assume advanced ANSI capabilities.</summary>
    GenericWsl,

    /// <summary>A VT-conforming terminal that did not identify itself further.</summary>
    GenericVt
}
