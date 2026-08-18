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
/// <remarks>
/// The non-trivial defaults — <see cref="KittyKeyboardFlags"/> and <see cref="ProbeTimeout"/> —
/// are exposed as <see cref="DefaultKittyKeyboardFlags"/> and <see cref="DefaultProbeTimeout"/>
/// so consumers can compose against them in a single object-initializer rather than reading the
/// defaults off a throwaway instance. For example, to add one extra Kitty flag to the defaults:
/// <code>
/// new NegotiationOptions {
///     KittyKeyboardFlags = NegotiationOptions.DefaultKittyKeyboardFlags | SomeOtherFlag,
/// }
/// </code>
/// </remarks>
public sealed record NegotiationOptions
{
    /// <summary>
    /// The default Kitty keyboard protocol flag set — addressable as a constant so consumers
    /// can OR in additional flags or AND-out specific ones without reading from a throwaway
    /// <see cref="NegotiationOptions"/> instance.
    /// </summary>
    public const KittyKeyboardFlags DefaultKittyKeyboardFlags =
        KittyKeyboardFlags.DisambiguateEscapeCodes |
        KittyKeyboardFlags.ReportEventTypes |
        KittyKeyboardFlags.ReportAlternateKeys |
        KittyKeyboardFlags.ReportAssociatedText |
        KittyKeyboardFlags.ReportAllKeysAsEscapeCodes;

    /// <summary>
    /// The default per-probe response timeout. 500 ms is enough for slow remote terminals over
    /// SSH but short enough to keep startup snappy. Exposed as a static field so consumers
    /// extending the default (e.g. bumping it for a high-latency link) can reference it
    /// directly: <c>ProbeTimeout = NegotiationOptions.DefaultProbeTimeout + extra</c>.
    /// </summary>
    public static TimeSpan DefaultProbeTimeout { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A curated low-latency profile for prompt-sized applications (FW-2, docs/cli-design.md
    /// §6) — a short-lived inline prompt, confirm, or picker on a COLD run (no capability
    /// cache). Relative to the defaults: every mouse mode is off and the Kitty keyboard push
    /// is off; focus events, bracketed paste, Win32 input mode, and synchronized output stay
    /// on per their individual flags (paste correctness and tear-free inline repaints matter
    /// even for a one-line prompt, and none of them adds a probe round).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it saves.</b> The opt-in enable round shrinks from up to six sequences to two
    /// or three, the DECRQM verification batch shrinks to the modes actually applied
    /// (1004 / 2004 / 2026 instead of also 1000 / 1002 / 1003 / 1006), and restore has
    /// correspondingly fewer disables to emit — less wire traffic and less for a slow
    /// terminal to chew on. Skipping mouse tracking also avoids the per-event decode cost of
    /// motion reporting for apps that never consume it.
    /// </para>
    /// <para>
    /// <b>What it does NOT save.</b> The probe ROUND count is unchanged — identification,
    /// verification, and color probes still cost their sentinel round-trips (the color round
    /// still runs because truecolor detection is the difference between a styled and an
    /// unstyled prompt). The big cold-run lever is the capability cache
    /// (<see cref="TerminalSessionOptions.CachedCapabilities"/>); this preset trims the cold
    /// run the cache cannot avoid.
    /// </para>
    /// <para>
    /// <b>Trade-offs.</b> No mouse interaction (no click-to-select, no wheel scroll in
    /// lists) and no Kitty key-release / repeat fidelity (key-up gestures and repeat-aware
    /// widgets degrade to plain key-downs). Prompt-sized commandlets accept both; full-screen
    /// interactive applications should stay on the defaults.
    /// </para>
    /// </remarks>
    public static NegotiationOptions MinimalPrompt { get; } = new()
    {
        EnableMouseButtons = false,
        EnableMouseButtonTracking = false,
        EnableMouseTracking = false,
        EnableExtendedMouseTracking = false,
        EnableSgrPixelsMouse = false,
        EnableKittyKeyboard = false,
    };

    /// <summary>
    /// Master policy. When <see cref="OptInPolicy.Allowed"/> (the default), individual
    /// <c>Enable…</c> flags are honored. When <see cref="OptInPolicy.Ignored"/>, the negotiator
    /// probes for identification and passive capabilities only — no 'enable' sequences are
    /// emitted, no restore is required, and every <c>Enable…</c> flag is treated as off.
    /// </summary>
    public OptInPolicy OptIns { get; init; } = OptInPolicy.Allowed;

    /// <summary>Enable mouse buttons (DECSET 1000).</summary>
    public bool EnableMouseButtons { get; init; } = true;

    /// <summary>Enable mouse button-event tracking (DECSET 1002).</summary>
    public bool EnableMouseButtonTracking { get; init; } = true;

    /// <summary>Enable any-event mouse motion (1003) on top of <see cref="EnableExtendedMouseTracking"/>.</summary>
    public bool EnableMouseTracking { get; init; } = true;

    /// <summary>Enable mouse tracking (SGR 1006 + button-event mode 1002).</summary>
    public bool EnableExtendedMouseTracking { get; init; } = true;

    /// <summary>
    /// Enable SGR-Pixels mouse encoding (DECSET 1016) on top of
    /// <see cref="EnableExtendedMouseTracking"/>. When the terminal supports it, mouse-event
    /// coordinates carry pixel precision surfaced via <c>CellPosition.PixelX</c> /
    /// <c>CellPosition.PixelY</c> alongside the cell-derived <c>Column</c> / <c>Row</c>.
    /// </summary>
    /// <remarks>
    /// Off by default because it changes the rate of mouse events: motion reports fire on every
    /// pixel of pointer movement rather than every cell, which is roughly 10–20× the event
    /// volume for the same user action. Opt in only when the application actually consumes the
    /// pixel data (drag handles, sub-cell hot-spots, custom cursor positioning); otherwise the
    /// extra events are just consumer-side filter cost.
    /// </remarks>
    public bool EnableSgrPixelsMouse { get; init; }

    /// <summary>Enable focus-in / focus-out reports (DECSET 1004).</summary>
    public bool EnableFocusEvents { get; init; } = true;

    /// <summary>Enable bracketed paste (DECSET 2004).</summary>
    public bool EnableBracketedPaste { get; init; } = true;

    /// <summary>Push the application's preferred Kitty keyboard protocol flags.</summary>
    public bool EnableKittyKeyboard { get; init; } = true;

    /// <summary>
    /// The Kitty keyboard protocol flags to push when <see cref="EnableKittyKeyboard"/> is true.
    /// Defaults to <see cref="DefaultKittyKeyboardFlags"/>; reference that constant directly
    /// when composing an extended flag set.
    /// </summary>
    public KittyKeyboardFlags KittyKeyboardFlags { get; init; } = DefaultKittyKeyboardFlags;

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
    /// unsupported. Defaults to <see cref="DefaultProbeTimeout"/> (500 ms).
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = DefaultProbeTimeout;
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
    /// identification and passive capabilities; no 'enable' sequences reach the terminal and
    /// restore is a no-op. Useful when embedding inside a host that already controls protocol
    /// state, or for a read-only "what is this terminal?" introspection pass.
    /// </summary>
    Ignored = 1
}