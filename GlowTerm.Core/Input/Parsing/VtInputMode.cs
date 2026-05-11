namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// Mutable bag of terminal-mode state the input interpreter consults when classifying
/// sequences. The negotiator updates these properties as it pushes opt-ins and reverses them
/// on restore; the interpreter holds a reference and reads on each event.
/// </summary>
/// <remarks>
/// This type is intentionally a mutable class with simple properties — it is shared mutable
/// configuration, not a value, and lives for the lifetime of the session. Consumers outside
/// the negotiator should treat it as read-only.
/// </remarks>
public sealed class VtInputMode
{
    /// <summary>
    /// DECCKM — when true, cursor keys arrive via SS3 (<c>ESC O A</c>) instead of CSI
    /// (<c>ESC [ A</c>). Both encodings produce the same logical key event regardless of
    /// this flag; the interpreter uses it only for diagnostics and capability mirroring.
    /// </summary>
    public bool ApplicationCursorKeys { get; set; }

    /// <summary>
    /// DECKPAM — when true, the numeric keypad sends application-mode SS3 sequences instead
    /// of literal digits. Again, both encodings map to the same logical numpad key.
    /// </summary>
    public bool ApplicationKeypad { get; set; }

    /// <summary>The active xterm <c>modifyOtherKeys</c> level (0 = disabled, 1 or 2 = enabled).</summary>
    public ModifyOtherKeysLevel ModifyOtherKeys { get; set; } = ModifyOtherKeysLevel.Disabled;

    /// <summary>The currently active mouse-encoding protocol (or <see cref="MouseEncoding.None"/>).</summary>
    public MouseEncoding MouseEncoding { get; set; } = MouseEncoding.None;

    /// <summary>The Kitty keyboard protocol flags currently pushed onto the terminal's stack.</summary>
    public KittyKeyboardFlags KittyKeyboard { get; set; } = KittyKeyboardFlags.None;

    /// <summary>True when bracketed paste (DECSET 2004) is enabled.</summary>
    public bool BracketedPasteEnabled { get; set; }

    /// <summary>True when focus reporting (DECSET 1004) is enabled.</summary>
    public bool FocusReportingEnabled { get; set; }

    /// <summary>True when Win32 Input Mode (DECSET 9001) is enabled.</summary>
    public bool Win32InputModeEnabled { get; set; }
}

/// <summary>The xterm <c>modifyOtherKeys</c> levels.</summary>
public enum ModifyOtherKeysLevel
{
    /// <summary>Modifier-bearing variants of character keys are not reported.</summary>
    Disabled = 0,

    /// <summary>Level 1 — reports modifier-bearing variants except for keys with conflicting
    /// historical encodings (Tab, Enter, etc.).</summary>
    Level1 = 1,

    /// <summary>Level 2 — reports modifier-bearing variants for all character keys.</summary>
    Level2 = 2,
}

/// <summary>
/// The mouse-tracking encoding currently in use. Mutually exclusive — a terminal reports in
/// exactly one encoding at a time.
/// </summary>
public enum MouseEncoding
{
    /// <summary>No mouse tracking enabled.</summary>
    None = 0,

    /// <summary>X10-style encoding: <c>ESC [ M Cb Cx Cy</c> with byte-encoded coordinates.</summary>
    X10 = 1,

    /// <summary>SGR encoding (DECSET 1006): <c>ESC [ &lt; Cb ; Cx ; Cy M/m</c>.</summary>
    Sgr = 2,

    /// <summary>SGR-Pixels encoding (DECSET 1016) — same shape as SGR, but coordinates are pixels.</summary>
    SgrPixels = 3,
}

/// <summary>
/// Flags accepted and reported by the Kitty keyboard protocol. See
/// https://sw.kovidgoyal.net/kitty/keyboard-protocol/.
/// </summary>
[Flags]
public enum KittyKeyboardFlags : uint
{
    None = 0,
    DisambiguateEscapeCodes = 1u << 0,
    ReportEventTypes = 1u << 1,
    ReportAlternateKeys = 1u << 2,
    ReportAllKeysAsEscapeCodes = 1u << 3,
    ReportAssociatedText = 1u << 4,
}
