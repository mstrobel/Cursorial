namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// UTF-8 byte-string constants for the VT/ANSI input sequences GlowTerm parses. Exposed as
/// <see cref="ReadOnlySpan{T}"/> via static properties so the bytes are embedded directly in
/// the binary and matched against incoming input with zero allocation.
/// </summary>
/// <remarks>
/// Centralizing these here keeps the magic bytes out of the parser logic. Add new sequences
/// here when introducing a new protocol decoder.
/// </remarks>
public static class VtInputSequences
{
    // ---- C0 controls ----

    /// <summary>NUL (0x00). Often produced by Ctrl+Space / Ctrl+@.</summary>
    public const byte Nul = 0x00;

    /// <summary>BEL (0x07). Doubles as an OSC string terminator on many terminals.</summary>
    public const byte Bel = 0x07;

    /// <summary>BS (0x08). Backspace.</summary>
    public const byte Backspace = 0x08;

    /// <summary>HT (0x09). Tab.</summary>
    public const byte Tab = 0x09;

    /// <summary>LF (0x0A). Line feed.</summary>
    public const byte LineFeed = 0x0A;

    /// <summary>CR (0x0D). Carriage return — what Enter typically sends.</summary>
    public const byte CarriageReturn = 0x0D;

    /// <summary>ESC (0x1B).</summary>
    public const byte Escape = 0x1B;

    /// <summary>DEL (0x7F). Often Backspace in modern terminals.</summary>
    public const byte Delete = 0x7F;

    // ---- Sequence introducers (7-bit forms) ----

    /// <summary><c>ESC [</c> — Control Sequence Introducer (CSI).</summary>
    public static ReadOnlySpan<byte> Csi => "\x1b["u8;

    /// <summary><c>ESC ]</c> — Operating System Command (OSC).</summary>
    public static ReadOnlySpan<byte> Osc => "\x1b]"u8;

    /// <summary><c>ESC P</c> — Device Control String (DCS).</summary>
    public static ReadOnlySpan<byte> Dcs => "\x1bP"u8;

    /// <summary><c>ESC O</c> — Single Shift 3 (SS3); used by some terminals for application-mode keys.</summary>
    public static ReadOnlySpan<byte> Ss3 => "\x1bO"u8;

    /// <summary><c>ESC \</c> — String Terminator (ST), terminates OSC/DCS bodies.</summary>
    public static ReadOnlySpan<byte> St => "\x1b\\"u8;

    // ---- Mouse protocol prefixes ----

    /// <summary><c>ESC [ M</c> — X10 (and normal/button-event/any-event) mouse report prefix.</summary>
    public static ReadOnlySpan<byte> X10MousePrefix => "\x1b[M"u8;

    /// <summary><c>ESC [ &lt;</c> — SGR mouse report prefix (DECSET 1006).</summary>
    public static ReadOnlySpan<byte> SgrMousePrefix => "\x1b[<"u8;

    // ---- Bracketed paste markers ----

    /// <summary>The CSI parameter of a bracketed paste start: <c>200~</c>.</summary>
    public const int BracketedPasteStartParam = 200;

    /// <summary>The CSI parameter of a bracketed paste end: <c>201~</c>.</summary>
    public const int BracketedPasteEndParam = 201;

    // ---- Focus events ----

    /// <summary>The CSI final byte for focus-in: <c>I</c>.</summary>
    public const byte FocusInFinal = (byte)'I';

    /// <summary>The CSI final byte for focus-out: <c>O</c>.</summary>
    public const byte FocusOutFinal = (byte)'O';

    // ---- CSI private prefixes ----

    /// <summary>DEC private mode prefix (<c>?</c>) used by DECSET/DECRST and many terminal-specific extensions.</summary>
    public const byte DecPrivatePrefix = (byte)'?';

    /// <summary>Secondary parameter prefix (<c>&gt;</c>) used by DA2, XTVERSION, modifyOtherKeys, etc.</summary>
    public const byte SecondaryPrefix = (byte)'>';

    /// <summary>Tertiary parameter prefix (<c>=</c>) used by DA3 and a few extensions.</summary>
    public const byte TertiaryPrefix = (byte)'=';

    /// <summary>SGR-mouse parameter prefix (<c>&lt;</c>) — recognized as an SGR mouse report by the classifier.</summary>
    public const byte SgrMousePrefix1006 = (byte)'<';
}
