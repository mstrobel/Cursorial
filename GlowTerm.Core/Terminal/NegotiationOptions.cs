namespace GlowTerm.Core.Terminal;

/// <summary>
/// Knobs controlling what <see cref="ITerminalNegotiator.NegotiateAsync"/> attempts to enable
/// on the terminal. Defaults reflect a "rich, modern terminal app" profile — every reasonable
/// opt-in is on, with timeouts tuned for interactive use. Consumers building a tool that needs
/// a narrow feature set (or wants to leave the terminal untouched) can override individual
/// flags or set <see cref="EnableAllOptIns"/> = false to start from a baseline of "no opt-ins."
/// </summary>
public sealed record class NegotiationOptions
{
    /// <summary>
    /// Master switch. When false, the negotiator probes for identification and passive
    /// capabilities only — no enable sequences are emitted, and no restore is required.
    /// Individual <c>Enable…</c> flags are ignored when this is false.
    /// </summary>
    public bool EnableAllOptIns { get; init; } = true;

    /// <summary>Enable mouse tracking (SGR 1006 + button-event mode 1002).</summary>
    public bool EnableMouseTracking { get; init; } = true;

    /// <summary>Enable any-event mouse motion (1003) on top of <see cref="EnableMouseTracking"/>.</summary>
    public bool EnableAnyEventMouse { get; init; } = true;

    /// <summary>Enable focus-in / focus-out reports (DECSET 1004).</summary>
    public bool EnableFocusEvents { get; init; } = true;

    /// <summary>Enable bracketed paste (DECSET 2004).</summary>
    public bool EnableBracketedPaste { get; init; } = true;

    /// <summary>Push the application's preferred Kitty keyboard protocol flags.</summary>
    public bool EnableKittyKeyboard { get; init; } = true;

    /// <summary>
    /// The Kitty keyboard protocol flags to push when <see cref="EnableKittyKeyboard"/> is true.
    /// Default omits <see cref="GlowTerm.Core.Input.Parsing.KittyKeyboardFlags.ReportAllKeysAsEscapeCodes"/>
    /// because that flag changes the encoding of plain text input — opt in deliberately when needed.
    /// </summary>
    public GlowTerm.Core.Input.Parsing.KittyKeyboardFlags KittyKeyboardFlags { get; init; } =
        GlowTerm.Core.Input.Parsing.KittyKeyboardFlags.DisambiguateEscapeCodes
        | GlowTerm.Core.Input.Parsing.KittyKeyboardFlags.ReportEventTypes
        | GlowTerm.Core.Input.Parsing.KittyKeyboardFlags.ReportAlternateKeys
        | GlowTerm.Core.Input.Parsing.KittyKeyboardFlags.ReportAssociatedText;

    /// <summary>Enable Win32 Input Mode (DECSET 9001) when running under a ConPTY-backed terminal.</summary>
    public bool EnableWin32InputMode { get; init; } = true;

    /// <summary>Enable synchronized output (DECSET 2026) so renderers can batch tearing-free updates.</summary>
    public bool EnableSynchronizedOutput { get; init; } = true;

    /// <summary>
    /// Verify truecolor by setting and reading back an OSC palette entry. When false, color
    /// depth is taken from the terminal's claim (or environment hints) without empirical check.
    /// </summary>
    public bool VerifyTruecolorViaRoundtrip { get; init; } = true;

    /// <summary>
    /// Per-probe response timeout. Probes that don't reply within this window are treated as
    /// unsupported. Defaults to 500 ms — enough for slow remote terminals over SSH but short
    /// enough to keep startup snappy.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}
