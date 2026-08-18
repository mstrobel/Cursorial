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

    /// <summary>GNOME Terminal — the flagship <b>VTE</b>-library terminal, and our representative for the whole
    /// VTE family. VTE terminals share the library's capabilities and cannot be told apart at runtime (they set
    /// the same <c>VTE_VERSION</c> and emit no product-distinguishing escape), so GNOME Console (kgx), Black Box,
    /// Tilix, and xfce4-terminal all classify here — the label names the engine, not the product.</summary>
    GnomeTerminal,

    Terminus,
    XfceTerminal,
    Rxvt,
    Mlterm,

    /// <summary>Warp — the Rust/GPU terminal. Notable for doing both the Kitty graphics AND Kitty keyboard
    /// protocols while shipping no Sixel. Identified by <c>TERM_PROGRAM=WarpTerminal</c>.</summary>
    Warp,

    /// <summary>The Visual Studio Code integrated terminal (xterm.js). Identified by <c>TERM_PROGRAM=vscode</c>
    /// or <c>VSCODE_PID</c>; the Cursor fork reuses the same signal. Does not answer XTVERSION.</summary>
    VisualStudioCode,

    /// <summary>Hyper — the Electron/xterm.js terminal. Identified by <c>TERM_PROGRAM=Hyper</c>.</summary>
    Hyper,

    /// <summary>Wave Terminal — the xterm.js-based terminal with Sixel + end-to-end pixel reporting. Identified by
    /// <c>TERM_PROGRAM=waveterm</c> or the <c>WAVETERM</c> environment variables.</summary>
    WaveTerminal,

    /// <summary>mintty — the Cygwin / MSYS2 / Git-Bash default on Windows. Identifies via XTVERSION
    /// (<c>DCS &gt; | mintty &lt;version&gt; ST</c>). Sixel-capable; OSC 52 read since 3.8.2.</summary>
    Mintty,

    /// <summary>Contour — the feature-rich C++ terminal (Sixel, Kitty keyboard, synchronized output, pixel size,
    /// curly/colored underline). Identifies via XTVERSION or <c>TERM=contour</c>; DA1 carries extension 314.</summary>
    Contour,

    /// <summary>st — the suckless simple terminal. Base build has truecolor since 0.7; everything beyond (Sixel,
    /// undercurl, OSC 8, Kitty graphics, synchronized output) is a separate patch, so capabilities stay
    /// conservative — do not infer patched features from the <c>st-256color</c> identity.</summary>
    SimpleTerminal,

    /// <summary>Termux — the Android terminal emulator. Identified by the <c>TERMUX_VERSION</c> environment
    /// variable. Reports pixel size but explicitly does not implement focus events (mode 1004).</summary>
    Termux,

    /// <summary>PuTTY — the Windows SSH/terminal client. Nearly unfingerprintable: no XTVERSION, no
    /// <c>TERM_PROGRAM</c>, no distinctive env var — best-effort classification is the <c>putty</c> TERM value.
    /// Truecolor (0.71+) + bracketed paste (0.63+) but no OSC 8 hyperlinks, graphics, or OSC 52.</summary>
    PuTTY,

    /// <summary>Zellij — the Rust terminal multiplexer (the tmux/GNU Screen peer). Unlike tmux it is a full
    /// re-rendering emulator with <b>no</b> generic escape passthrough, so it is flagged
    /// <see cref="TerminalIdentification.InsideMultiplexer"/> but NOT granted DCS passthrough. Identified by the
    /// <c>ZELLIJ</c> environment variable.</summary>
    Zellij,

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

    /// <summary>The classic built-in terminal emulator used by the JetBrains IntelliJ family of IDEs.</summary>
    JetBrainsClassic,

    /// <summary>The reworked built-in terminal emulator used by the JetBrains IntelliJ family of IDEs.</summary>
    JetBrainsReworked,

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
