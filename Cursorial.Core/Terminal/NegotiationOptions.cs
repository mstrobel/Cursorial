using Cursorial.Input.Parsing;

namespace Cursorial.Terminal;

/// <summary>
/// Knobs controlling what <see cref="ITerminalNegotiator.NegotiateAsync"/> attempts to enable
/// on the terminal. Defaults reflect a "rich, modern terminal app" profile — every reasonable
/// opt-in is on, with timeouts tuned for interactive use. Consumers building a tool that needs
/// a narrow feature set can override individual <c>Enable…</c> flags, or pass
/// <see cref="OptIns"/> = <see cref="OptInPolicy.Ignored"/> to suppress every opt-in regardless
/// of the individual flags.
/// </summary>
public sealed record class NegotiationOptions
{
    /// <summary>
    /// Master policy. When <see cref="OptInPolicy.Allowed"/> (the default), individual
    /// <c>Enable…</c> flags are honored. When <see cref="OptInPolicy.Ignored"/>, the negotiator
    /// probes for identification and passive capabilities only — no enable sequences are
    /// emitted, no restore is required, and every <c>Enable…</c> flag is treated as off.
    /// </summary>
    public OptInPolicy OptIns { get; init; } = OptInPolicy.Allowed;

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
    /// Default omits <see cref="Input.Parsing.KittyKeyboardFlags.ReportAllKeysAsEscapeCodes"/>
    /// because that flag changes the encoding of plain text input — opt in deliberately when needed.
    /// </summary>
    public KittyKeyboardFlags KittyKeyboardFlags { get; init; } =
        KittyKeyboardFlags.DisambiguateEscapeCodes
        | KittyKeyboardFlags.ReportEventTypes
        | KittyKeyboardFlags.ReportAlternateKeys
        | KittyKeyboardFlags.ReportAssociatedText;

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

/// <summary>
/// Whether the negotiator is permitted to enable opt-in protocols on the terminal. Controls
/// the master gate that sits above the individual <c>Enable…</c> flags on
/// <see cref="NegotiationOptions"/>.
/// </summary>
public enum OptInPolicy
{
    /// <summary>
    /// Honor the individual <c>Enable…</c> flags. Opt-ins flagged on will be applied (subject
    /// to terminal-family gating) and reversed at restore time. This is the default.
    /// </summary>
    Allowed = 0,

    /// <summary>
    /// Skip every opt-in regardless of the individual flags. The negotiator only probes for
    /// identification and passive capabilities; no enable sequences reach the terminal and
    /// restore is a no-op. Useful when embedding inside a host that already controls protocol
    /// state, or for a read-only "what is this terminal?" introspection pass.
    /// </summary>
    Ignored = 1,
}
