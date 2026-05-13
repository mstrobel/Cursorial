namespace Cursorial.Input.Parsing;

/// <summary>
/// UTF-8 byte-string constants for the VT/ANSI input sequences Cursorial parses. Exposed as
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

    /// <summary>
    /// Modifier-parameter bit positions used by xterm and Kitty in CSI sequences. The wire
    /// value is <c>1 + bitfield</c>; subtract 1 from the parameter to get the bitfield, then
    /// test these bits.
    /// </summary>
    public static class ModifierParam
    {
        public const int ShiftBit = 0b0000_0001;
        public const int AltBit = 0b0000_0010;
        public const int ControlBit = 0b0000_0100;
        public const int SuperBit = 0b0000_1000;
        public const int HyperBit = 0b0001_0000;
        public const int MetaBit = 0b0010_0000;
        public const int CapsLockBit = 0b0100_0000;
        public const int NumLockBit = 0b1000_0000;
    }

    /// <summary>SGR mouse encoding constants (DECSET 1006).</summary>
    public static class SgrMouse
    {
        // ---- cb byte bits ----

        /// <summary>Bits 0–1 of <c>cb</c>: button identifier (or wheel-direction when <see cref="WheelBit"/> is set).</summary>
        public const int ButtonBitsMask = 0b0000_0011;

        /// <summary>Bit 2 of <c>cb</c>: Shift modifier.</summary>
        public const int ShiftBit = 0b0000_0100;

        /// <summary>Bit 3 of <c>cb</c>: Alt modifier.</summary>
        public const int AltBit = 0b0000_1000;

        /// <summary>Bit 4 of <c>cb</c>: Control modifier.</summary>
        public const int ControlBit = 0b0001_0000;

        /// <summary>Bit 5 of <c>cb</c>: motion (drag if a button is held; pure motion otherwise).</summary>
        public const int MotionBit = 0b0010_0000;

        /// <summary>Bit 6 of <c>cb</c>: wheel event marker.</summary>
        public const int WheelBit = 0b0100_0000;

        /// <summary>Bit 7 of <c>cb</c>: extended button (X1–X4 instead of Left / Middle / Right).</summary>
        public const int ExtendedBit = 0b1000_0000;

        // ---- Button codes (cb &amp; ButtonBitsMask) ----

        public const int LeftButton = 0;
        public const int MiddleButton = 1;
        public const int RightButton = 2;

        /// <summary>
        /// In the non-extended encoding, button bits == 3 with <see cref="MotionBit"/> set means
        /// "no button held" — pure pointer motion (any-event tracking).
        /// </summary>
        public const int NoButton = 3;

        // ---- Wheel direction codes (cb &amp; ButtonBitsMask when WheelBit is set) ----

        public const int WheelUp = 0;
        public const int WheelDown = 1;
        public const int WheelLeft = 2;
        public const int WheelRight = 3;

        /// <summary>One wheel notch in the standard 1/120-unit convention shared with Win32.</summary>
        public const int WheelDeltaPerNotch = 120;

        // ---- Terminator bytes ----

        /// <summary><c>M</c> — button-press / drag terminator.</summary>
        public const byte PressFinal = (byte)'M';

        /// <summary><c>m</c> — button-release terminator.</summary>
        public const byte ReleaseFinal = (byte)'m';
    }

    /// <summary>OSC response codes recognized by the input interpreter.</summary>
    public static class OscCode
    {
        /// <summary>OSC 4 — palette color get / set / response.</summary>
        public const int PaletteColor = 4;

        /// <summary>OSC 10 — default foreground color get / set / response.</summary>
        public const int ForegroundColor = 10;

        /// <summary>OSC 11 — default background color get / set / response.</summary>
        public const int BackgroundColor = 11;

        /// <summary>OSC 12 — cursor color get / set / response.</summary>
        public const int CursorColor = 12;
    }

    /// <summary>
    /// CSI <c>~</c>-terminated key parameter codes (xterm + vt220 / vt320 conventions). The
    /// shape is <c>CSI &lt;param&gt; ~</c> for unmodified keys and <c>CSI &lt;param&gt; ; &lt;mod&gt; ~</c>
    /// for modifier-bearing variants.
    /// </summary>
    public static class CsiTildeKey
    {
        // Navigation / editing
        public const int Home = 1;
        public const int Insert = 2;
        public const int Delete = 3;
        public const int End = 4;
        public const int PageUp = 5;
        public const int PageDown = 6;
        public const int HomeAlternate = 7;
        public const int EndAlternate = 8;

        // Function keys F1–F12 (xterm encoding).
        public const int F1 = 11;
        public const int F2 = 12;
        public const int F3 = 13;
        public const int F4 = 14;
        public const int F5 = 15;
        public const int F6 = 17;
        public const int F7 = 18;
        public const int F8 = 19;
        public const int F9 = 20;
        public const int F10 = 21;
        public const int F11 = 23;
        public const int F12 = 24;

        // Extended function keys F13–F20 (vt220 / vt320).
        public const int F13 = 25;
        public const int F14 = 26;
        public const int F15 = 28;
        public const int F16 = 29;
        public const int F17 = 31;
        public const int F18 = 32;
        public const int F19 = 33;
        public const int F20 = 34;
    }

    /// <summary>
    /// Opt-in enable / disable byte sequences used by the negotiator. Each enable is paired
    /// with a matching disable so a clean restore is always possible.
    /// </summary>
    public static class OptInSequences
    {
        // ---- SGR mouse (DECSET 1006) ----
        public static ReadOnlySpan<byte> EnableSgrMouse => "\x1b[?1006h"u8;
        public static ReadOnlySpan<byte> DisableSgrMouse => "\x1b[?1006l"u8;

        // ---- Button-event mouse tracking (DECSET 1002) — press / release / drag ----
        public static ReadOnlySpan<byte> EnableButtonEventMouse => "\x1b[?1002h"u8;
        public static ReadOnlySpan<byte> DisableButtonEventMouse => "\x1b[?1002l"u8;

        // ---- Any-event mouse tracking (DECSET 1003) — motion without button ----
        public static ReadOnlySpan<byte> EnableAnyEventMouse => "\x1b[?1003h"u8;
        public static ReadOnlySpan<byte> DisableAnyEventMouse => "\x1b[?1003l"u8;

        // ---- Focus events (DECSET 1004) ----
        public static ReadOnlySpan<byte> EnableFocusEvents => "\x1b[?1004h"u8;
        public static ReadOnlySpan<byte> DisableFocusEvents => "\x1b[?1004l"u8;

        // ---- Bracketed paste (DECSET 2004) ----
        public static ReadOnlySpan<byte> EnableBracketedPaste => "\x1b[?2004h"u8;
        public static ReadOnlySpan<byte> DisableBracketedPaste => "\x1b[?2004l"u8;

        // ---- Win32 Input Mode (DECSET 9001) ----
        public static ReadOnlySpan<byte> EnableWin32InputMode => "\x1b[?9001h"u8;
        public static ReadOnlySpan<byte> DisableWin32InputMode => "\x1b[?9001l"u8;

        // ---- Synchronized output (DECSET 2026) ----
        public static ReadOnlySpan<byte> EnableSynchronizedOutput => "\x1b[?2026h"u8;
        public static ReadOnlySpan<byte> DisableSynchronizedOutput => "\x1b[?2026l"u8;

        // ---- Kitty keyboard protocol ----
        // Push is dynamic: CSI > <flags> u. Pop is fixed.

        /// <summary><c>CSI &lt; u</c> — pop the most recent Kitty keyboard flag set.</summary>
        public static ReadOnlySpan<byte> PopKittyKeyboard => "\x1b[<u"u8;

        /// <summary>Prefix for a Kitty keyboard push (caller appends ASCII flags + <c>u</c>).</summary>
        public static ReadOnlySpan<byte> KittyPushPrefix => "\x1b[>"u8;

        /// <summary>Suffix for a Kitty keyboard push.</summary>
        public const byte KittyPushSuffix = (byte)'u';

        // ---- Kitty multiple-cursors protocol ----
        //
        // The protocol is fire-and-forget — no DECSET to enable. Its add-cursor form is
        // CSI > SHAPE ; COORD_TYPE : COORDS SP q; the all-clear is shape 0 + whole-screen
        // coord-type 4. Per spec, switching alt-screen state implicitly clears extra
        // cursors, but a timing-dependent Kitty bug has been observed where that invariant
        // doesn't hold and "ghost" cursors persist on the normal screen after a TUI exits.
        // We emit this explicit clear on session disposal as belt-and-braces. Terminals
        // that don't implement the protocol discard the sequence as an unknown CSI.
        //
        // https://sw.kovidgoyal.net/kitty/multiple-cursors-protocol/

        /// <summary><c>CSI &gt; 0;4 SP q</c> — clear all Kitty extra cursors from the screen.</summary>
        public static ReadOnlySpan<byte> ClearKittyExtraCursors => "\x1b[>0;4 q"u8;
    }

    /// <summary>
    /// Kitty keyboard protocol constants — event-type sub-parameter values and the functional
    /// key codes that occupy the Unicode private-use area. See
    /// https://sw.kovidgoyal.net/kitty/keyboard-protocol/.
    /// </summary>
    public static class Kitty
    {
        // ---- Event-type sub-parameter values ----

        /// <summary>Initial key-down activation. Default when the event-type sub-parameter is omitted.</summary>
        public const int PressEvent = 1;

        /// <summary>Auto-repeat while the key is held.</summary>
        public const int RepeatEvent = 2;

        /// <summary>Key release.</summary>
        public const int ReleaseEvent = 3;

        // ---- Functional key codes (Unicode private-use area) ----
        //
        // Names below mirror the Kitty spec. Use the *Key suffix on each constant so they don't
        // shadow our outer Key enum or our top-level VtInputSequences.Escape (the byte 0x1B).

        // Navigation / control
        public const int EscapeKey = 57344;
        public const int EnterKey = 57345;
        public const int TabKey = 57346;
        public const int BackspaceKey = 57347;
        public const int InsertKey = 57348;
        public const int DeleteKey = 57349;
        public const int LeftArrowKey = 57350;
        public const int RightArrowKey = 57351;
        public const int UpArrowKey = 57352;
        public const int DownArrowKey = 57353;
        public const int PageUpKey = 57354;
        public const int PageDownKey = 57355;
        public const int HomeKey = 57356;
        public const int EndKey = 57357;
        public const int CapsLockKey = 57358;
        public const int ScrollLockKey = 57359;
        public const int NumLockKey = 57360;
        public const int PrintScreenKey = 57361;
        public const int PauseKey = 57362;
        public const int MenuKey = 57363;

        // Function keys F1–F35 are contiguous: F1Key + (n-1) → Fn.
        public const int F1Key = 57364;
        public const int F12Key = 57375;
        public const int F24Key = 57387;
        public const int F25Key = 57388;
        public const int F35Key = 57398;

        // Numpad keys
        public const int Numpad0Key = 57399;
        public const int Numpad1Key = 57400;
        public const int Numpad2Key = 57401;
        public const int Numpad3Key = 57402;
        public const int Numpad4Key = 57403;
        public const int Numpad5Key = 57404;
        public const int Numpad6Key = 57405;
        public const int Numpad7Key = 57406;
        public const int Numpad8Key = 57407;
        public const int Numpad9Key = 57408;
        public const int NumpadDecimalKey = 57409;
        public const int NumpadDivideKey = 57410;
        public const int NumpadMultiplyKey = 57411;
        public const int NumpadSubtractKey = 57412;
        public const int NumpadAddKey = 57413;
        public const int NumpadEnterKey = 57414;
        public const int NumpadEqualsKey = 57415;
        public const int NumpadSeparatorKey = 57416;
        public const int NumpadLeftArrowKey = 57417;
        public const int NumpadRightArrowKey = 57418;
        public const int NumpadUpArrowKey = 57419;
        public const int NumpadDownArrowKey = 57420;
        public const int NumpadPageUpKey = 57421;
        public const int NumpadPageDownKey = 57422;
        public const int NumpadHomeKey = 57423;
        public const int NumpadEndKey = 57424;
        public const int NumpadInsertKey = 57425;
        public const int NumpadDeleteKey = 57426;
        public const int NumpadBeginKey = 57427;

        // Media keys
        public const int MediaPlayKey = 57428;
        public const int MediaPauseKey = 57429;
        public const int MediaPlayPauseKey = 57430;
        public const int MediaReverseKey = 57431;
        public const int MediaStopKey = 57432;
        public const int MediaFastForwardKey = 57433;
        public const int MediaRewindKey = 57434;
        public const int MediaTrackNextKey = 57435;
        public const int MediaTrackPreviousKey = 57436;
        public const int MediaRecordKey = 57437;
        public const int VolumeDownKey = 57438;
        public const int VolumeUpKey = 57439;
        public const int VolumeMuteKey = 57440;

        // Per-side modifier keys (reported as standalone events when the key itself is pressed)
        public const int LeftShiftKey = 57441;
        public const int LeftControlKey = 57442;
        public const int LeftAltKey = 57443;
        public const int LeftSuperKey = 57444;
        public const int LeftHyperKey = 57445;
        public const int LeftMetaKey = 57446;
        public const int RightShiftKey = 57447;
        public const int RightControlKey = 57448;
        public const int RightAltKey = 57449;
        public const int RightSuperKey = 57450;
        public const int RightHyperKey = 57451;
        public const int RightMetaKey = 57452;
        public const int IsoLevel3ShiftKey = 57453;
        public const int IsoLevel5ShiftKey = 57454;
    }

    /// <summary>
    /// Win32 Input Mode (DECSET 9001) constants. Windows Terminal and ConPTY wrap console input
    /// records as CSI sequences with the form
    /// <c>CSI Vk ; Sc ; Uc ; Kd ; Cs ; Rc _</c> where Vk is the Win32 virtual-key code, Sc the
    /// scan code, Uc the Unicode codepoint, Kd the key-down flag (1 = down, 0 = up), Cs the
    /// CONTROL_KEY_STATE bitmask, and Rc the repeat count.
    /// </summary>
    public static class Win32InputMode
    {
        /// <summary>The CSI final byte distinguishing Win32 input-mode events: <c>_</c>.</summary>
        public const byte Final = (byte)'_';

        /// <summary>CONTROL_KEY_STATE bit flags surfaced in the Cs parameter.</summary>
        public static class ControlKeyState
        {
            public const int RightAltPressed = 0x0001;
            public const int LeftAltPressed = 0x0002;
            public const int RightCtrlPressed = 0x0004;
            public const int LeftCtrlPressed = 0x0008;
            public const int ShiftPressed = 0x0010;
            public const int NumLockOn = 0x0020;
            public const int ScrollLockOn = 0x0040;
            public const int CapsLockOn = 0x0080;
            public const int EnhancedKey = 0x0100;
        }

        /// <summary>Selected Win32 virtual-key codes that map cleanly onto <see cref="Key"/>.</summary>
        public static class VirtualKey
        {
            public const int Back = 0x08;
            public const int Tab = 0x09;
            public const int Return = 0x0D;
            public const int Shift = 0x10;
            public const int Control = 0x11;
            public const int Menu = 0x12;        // Alt
            public const int Pause = 0x13;
            public const int Capital = 0x14;     // CapsLock
            public const int Escape = 0x1B;
            public const int Space = 0x20;
            public const int Prior = 0x21;       // PageUp
            public const int Next = 0x22;        // PageDown
            public const int End = 0x23;
            public const int Home = 0x24;
            public const int Left = 0x25;
            public const int Up = 0x26;
            public const int Right = 0x27;
            public const int Down = 0x28;
            public const int Print = 0x2A;
            public const int PrintScreen = 0x2C;
            public const int Insert = 0x2D;
            public const int Delete = 0x2E;
            public const int LWin = 0x5B;
            public const int RWin = 0x5C;
            public const int Apps = 0x5D;
            public const int F1 = 0x70;
            public const int F24 = 0x87;
            public const int NumLock = 0x90;
            public const int Scroll = 0x91;
            public const int LShift = 0xA0;
            public const int RShift = 0xA1;
            public const int LControl = 0xA2;
            public const int RControl = 0xA3;
            public const int LMenu = 0xA4;       // Left Alt
            public const int RMenu = 0xA5;       // Right Alt
        }
    }
}
