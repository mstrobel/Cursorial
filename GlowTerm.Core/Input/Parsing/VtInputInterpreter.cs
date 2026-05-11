using System.Buffers;
using System.Text;

namespace GlowTerm.Core.Input.Parsing;

/// <summary>
/// Consumes <see cref="IVtSequenceTokenSink"/> callbacks from <see cref="VtSequenceClassifier"/>
/// and emits <see cref="InputEvent"/>s to an <see cref="IInputEventSink"/>. Holds the mutable
/// session state — paste accumulator, UTF-8 continuation buffer, and a reference to the
/// <see cref="VtInputMode"/> the negotiator updates as opt-ins are pushed and popped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decoder coverage:</b> printable UTF-8 runs (one <see cref="KeyEvent"/> per
/// <see cref="System.Text.Rune"/>), C0 control characters (Tab, Enter, Backspace, NUL→Ctrl+Space,
/// Ctrl+letter for 0x01–0x1A), DEL→Backspace, bare-ESC committed by classifier flush,
/// focus events (<c>CSI I</c> / <c>CSI O</c>), bracketed-paste accumulation
/// (<c>CSI 200~</c> … <c>CSI 201~</c>), CSI cursor keys (<c>A B C D H F</c>) and special
/// keys (Insert, Delete, Page Up/Down, Home, End), function keys F1–F20 via the
/// <c>CSI n ~</c> form, F1–F4 + cursor + Home / End via SS3 (<c>ESC O …</c>), BackTab
/// (<c>CSI Z</c> → Shift+Tab), xterm modifier-bearing variants (<c>CSI 1 ; mod letter</c>
/// and <c>CSI n ; mod ~</c>) decoding Shift / Alt / Ctrl / Super, and SGR mouse
/// (<c>CSI &lt; cb ; cx ; cy M/m</c>, DECSET 1006) including press / release / drag / motion /
/// wheel and X1–X4 extended buttons. The interpreter accumulates <see cref="MouseButtons"/>
/// state across press / release events so drag and motion events carry an accurate held-button
/// mask. Device-response decoding: DA1 (<c>CSI ? … c</c>), DA2 (<c>CSI &gt; … c</c>), DSR-CPR
/// (<c>CSI row ; col R</c>), OSC 4 / 10 / 11 / 12 color responses, and DCS XTVERSION
/// (<c>DCS &gt; | name ST</c>) — emitted as <see cref="DeviceResponseEvent"/>s with the
/// appropriate <see cref="DeviceResponseKind"/> and a copied payload. Kitty keyboard protocol
/// (<c>CSI key[:shifted:base][;mods[:event]][;text] u</c>) — full functional key code mapping
/// (Esc / Enter / Tab / arrows / Home / End / F1–F24 / numpad / media / per-side modifiers),
/// up / down / repeat distinction via the event-type sub-parameter, modifier handling for the
/// xterm baseline plus Hyper / Meta / CapsLock / NumLock toggles, and text payloads for
/// IME-composed and Shift-modified output. Alternate-key sub-parameters are parsed but not
/// surfaced in v1 (<see cref="KeyEvent"/> doesn't yet carry shifted / base-layout keys).
/// </para>
/// <para>
/// <b>Not yet decoded</b> (silently dropped, will be added in subsequent passes): X10 mouse
/// (legacy non-SGR), SGR-Pixels mouse, modifyOtherKeys character-key reporting, ESC charset
/// designators, DA3 / DECRQSS / XTGETTCAP DCS responses, and Win32 input mode.
/// </para>
/// <para>
/// <b>Threading.</b> The interpreter is single-threaded with respect to its sink and mode —
/// the same thread that drives <see cref="VtSequenceClassifier.Process"/> calls the sink
/// callbacks here, and the consumer is expected to drive both from a single byte-pump.
/// </para>
/// </remarks>
public sealed class VtInputInterpreter : IVtSequenceTokenSink
{
    private readonly IInputEventSink _eventSink;
    private readonly VtInputMode _mode;
    private readonly TimeProvider _time;

    // Paste accumulator. Empty when not in paste mode.
    private readonly StringBuilder _pasteBuffer = new();
    private bool _inPaste;

    // UTF-8 continuation buffer for printable runs split across Process boundaries.
    // Sized to the maximum UTF-8 sequence length (4 bytes); never holds a complete rune.
    private readonly byte[] _utf8Continuation = new byte[4];
    private int _utf8ContinuationLength;

    // Currently-held mouse buttons. Updated on every press / release so drag and motion
    // events can carry an accurate ButtonsHeld mask. SGR's per-event encoding doesn't tell
    // us this directly — we accumulate it ourselves.
    private MouseButtons _heldButtons;

    // DCS body accumulator. Active between OnDcsHook and OnDcsUnhook; classified at hook,
    // delivered as a DeviceResponseEvent at unhook. Default capacity sized for typical
    // XTVERSION responses (terminal name + version string).
    private readonly ArrayBufferWriter<byte> _dcsBody = new(initialCapacity: 128);
    private DeviceResponseKind _dcsKind = DeviceResponseKind.Unknown;
    private bool _dcsActive;

    public VtInputInterpreter(VtInputMode mode, IInputEventSink eventSink, TimeProvider? timeProvider = null)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The mode bag the interpreter consults; shared with the negotiator.</summary>
    public VtInputMode Mode => _mode;

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---- IVtSequenceTokenSink ----

    public void OnPrint(ReadOnlySpan<byte> bytes)
    {
        if (_inPaste)
        {
            AppendPasteText(bytes);
            return;
        }

        DecodePrintable(bytes);
    }

    public void OnExecute(byte controlChar)
    {
        if (_inPaste)
        {
            // Whitespace controls are preserved verbatim in the paste payload; everything
            // else is dropped. Keeping CR + LF distinct lets consumers detect line endings.
            switch (controlChar)
            {
                case VtInputSequences.Tab: _pasteBuffer.Append('\t'); break;
                case VtInputSequences.LineFeed: _pasteBuffer.Append('\n'); break;
                case VtInputSequences.CarriageReturn: _pasteBuffer.Append('\r'); break;
            }
            return;
        }

        EmitControlEvent(controlChar);
    }

    public void OnEscDispatch(ReadOnlySpan<byte> intermediates, byte final)
    {
        // Bare ESC committed by classifier flush.
        if (intermediates.IsEmpty && final == 0)
        {
            EmitNamedKey(Key.Escape);
            return;
        }

        // SS3 — application-mode arrow / F1-F4 / Home / End: ESC O <final>.
        if (intermediates.Length == 1 && intermediates[0] == (byte)'O')
        {
            DecodeSs3(final);
            return;
        }

        // Other ESC sequences (charset designators, etc.) — not yet decoded.
    }

    public void OnCsiDispatch(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final)
    {
        if (!intermediates.IsEmpty) return;

        // SGR mouse: CSI < cb ; cx ; cy M/m  (DECSET 1006).
        if (privatePrefix == VtInputSequences.SgrMousePrefix1006
            && (final == (byte)'M' || final == (byte)'m'))
        {
            DecodeSgrMouse(parameters, isPress: final == (byte)'M');
            return;
        }

        // Device Attributes responses — DA1 (CSI ? … c) and DA2 (CSI > … c).
        if (final == (byte)'c')
        {
            switch (privatePrefix)
            {
                case (byte)'?':
                    EmitDeviceResponse(DeviceResponseKind.PrimaryDeviceAttributes, parameters);
                    return;
                case (byte)'>':
                    EmitDeviceResponse(DeviceResponseKind.SecondaryDeviceAttributes, parameters);
                    return;
            }
        }

        // No other private-prefix CSI sequences are decoded yet.
        if (privatePrefix != 0) return;

        // Kitty keyboard protocol — CSI <key>[:<alt>:<base>][;<mods>[:<event>]][;<text>] u.
        // Distinguished by the 'u' final byte. Use a sub-parameter-aware parser since the
        // encoding makes structural use of the colon separator the simpler ParseParameters
        // collapses into a primary separator.
        if (final == (byte)'u')
        {
            DecodeKittyKey(parameters);
            return;
        }

        Span<int> parameterBuffer = stackalloc int[8];
        int parameterCount = ParseParameters(parameters, parameterBuffer);
        ReadOnlySpan<int> p = parameterBuffer[..parameterCount];

        // Cursor Position Report — CSI <row> ; <col> R, the response to DSR-CPR (CSI 6 n).
        if (p.Length == 2 && final == (byte)'R')
        {
            EmitDeviceResponse(DeviceResponseKind.CursorPositionReport, parameters);
            return;
        }

        if (p.IsEmpty)
        {
            DecodeCsiNoParams(final);
            return;
        }

        if (p.Length == 1 && final == (byte)'~')
        {
            DecodeCsiTildeOneParam(p[0]);
            return;
        }

        // Modifier-bearing arrows / Home / End: CSI 1 ; <mod> <A|B|C|D|H|F>.
        if (p.Length == 2 && p[0] == 1)
        {
            Key arrowKey = ArrowOrHomeEndKey(final);
            if (arrowKey != Key.None)
            {
                EmitNamedKey(arrowKey, ParseModifiersParam(p[1]));
                return;
            }
        }

        // Modifier-bearing function / special keys: CSI <n> ; <mod> ~.
        if (p.Length == 2 && final == (byte)'~')
        {
            if (TryFunctionOrSpecialKey(p[0], out Key funcKey))
            {
                EmitNamedKey(funcKey, ParseModifiersParam(p[1]));
            }
            return;
        }

        // Other CSI sequences (mouse, modifyOtherKeys, Kitty keyboard) — not yet decoded.
    }

    private void DecodeCsiNoParams(byte final)
    {
        switch (final)
        {
            case VtInputSequences.FocusInFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = true });
                return;
            case VtInputSequences.FocusOutFinal:
                _eventSink.OnInputEvent(new FocusEvent { Timestamp = Now, HasFocus = false });
                return;
            case (byte)'Z':
                // BackTab — Shift+Tab.
                EmitNamedKey(Key.Tab, KeyModifiers.Shift);
                return;
        }

        Key key = ArrowOrHomeEndKey(final);
        if (key != Key.None) EmitNamedKey(key);
    }

    private void DecodeCsiTildeOneParam(int parameter)
    {
        switch (parameter)
        {
            case VtInputSequences.BracketedPasteStartParam:
                EnterPaste();
                return;
            case VtInputSequences.BracketedPasteEndParam:
                ExitPaste();
                return;
        }

        if (TryFunctionOrSpecialKey(parameter, out Key key))
        {
            EmitNamedKey(key);
        }
    }

    // ---- SGR mouse ----

    private void DecodeSgrMouse(ReadOnlySpan<byte> parameters, bool isPress)
    {
        Span<int> p = stackalloc int[3];
        int n = ParseParameters(parameters, p);
        if (n < 3) return;

        int cb = p[0];
        // SGR coordinates are 1-based; we expose them as 0-based per CellPosition's contract.
        // Don't clamp to 0 — terminals may report negative values when the pointer leaves the
        // viewport, and the contract permits that.
        int column = p[1] - 1;
        int row = p[2] - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((cb & 0b0000_0100) != 0) modifiers |= KeyModifiers.Shift;
        if ((cb & 0b0000_1000) != 0) modifiers |= KeyModifiers.Alt;
        if ((cb & 0b0001_0000) != 0) modifiers |= KeyModifiers.Control;

        bool isMotion = (cb & 0b0010_0000) != 0;
        bool isWheel = (cb & 0b0100_0000) != 0;
        bool isExtended = (cb & 0b1000_0000) != 0;

        var position = new CellPosition(column, row);
        var ts = Now;

        if (isWheel)
        {
            // Bits 0–1 select wheel direction: 0=up, 1=down, 2=left, 3=right. We report
            // wheel deltas in the 1/120-notch units described in MouseEvent's xmldoc.
            int direction = cb & 0b0000_0011;
            int wheelDeltaY = direction switch { 0 => 120, 1 => -120, _ => 0 };
            int wheelDeltaX = direction switch { 2 => -120, 3 => 120, _ => 0 };

            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = MouseEventKind.Wheel,
                Position = position,
                Button = MouseButton.None,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
                WheelDeltaY = wheelDeltaY,
                WheelDeltaX = wheelDeltaX,
            });
            return;
        }

        int buttonBits = cb & 0b0000_0011;
        MouseButton button = isExtended
            ? buttonBits switch
            {
                0 => MouseButton.X1,
                1 => MouseButton.X2,
                2 => MouseButton.X3,
                3 => MouseButton.X4,
                _ => MouseButton.None,
            }
            : buttonBits switch
            {
                0 => MouseButton.Left,
                1 => MouseButton.Middle,
                2 => MouseButton.Right,
                _ => MouseButton.None,
            };

        if (isMotion)
        {
            // X10/SGR convention: button bits == 3 in the non-extended encoding means
            // "no button held" — pure motion (any-event tracking).
            bool noButton = !isExtended && buttonBits == 3;

            _eventSink.OnInputEvent(new MouseEvent
            {
                Timestamp = ts,
                Kind = noButton ? MouseEventKind.Move : MouseEventKind.Drag,
                Position = position,
                Button = noButton ? MouseButton.None : button,
                ButtonsHeld = _heldButtons,
                Modifiers = modifiers,
            });
            return;
        }

        // Press / release.
        MouseButtons mask = ButtonToMask(button);
        if (isPress) _heldButtons |= mask;
        else _heldButtons &= ~mask;

        _eventSink.OnInputEvent(new MouseEvent
        {
            Timestamp = ts,
            Kind = isPress ? MouseEventKind.ButtonDown : MouseEventKind.ButtonUp,
            Position = position,
            Button = button,
            ButtonsHeld = _heldButtons,
            Modifiers = modifiers,
        });
    }

    // ---- Kitty keyboard protocol ----

    private void DecodeKittyKey(ReadOnlySpan<byte> rawParameters)
    {
        // Section / sub-section roles in the Kitty encoding:
        //   primary 0:  key-info       sub 0=key  sub 1=shifted-key  sub 2=base-layout-key
        //   primary 1:  modifier-info  sub 0=mods (1+bitfield)  sub 1=event-type (1=press,2=repeat,3=release)
        //   primary 2:  text           sub i=codepoint of the i-th text char
        var data = new KittyKeyData { Modifiers = 1, EventType = 1 };
        Span<int> textCodepoints = stackalloc int[16];

        ParseKittyParameters(rawParameters, ref data, textCodepoints);

        if (data.KeyCode <= 0) return; // Malformed — no key code.

        KeyModifiers modifiers = ParseModifiersParam(data.Modifiers);

        KeyEventKind kind = data.EventType == 3 ? KeyEventKind.Up : KeyEventKind.Down;
        bool isRepeat = data.EventType == 2;

        Key key = TryMapKittyFunctionalKey(data.KeyCode, out Key mapped)
            ? mapped
            : Key.Character;

        ReadOnlyMemory<char> text;
        if (key == Key.Character)
        {
            text = data.TextCount > 0
                ? CodepointsToUtf16(textCodepoints[..data.TextCount])
                : CodepointToUtf16(data.KeyCode);
        }
        else
        {
            // Functional key — the text payload, when present, is what the key would have
            // produced as a character (rare: e.g. Kitty Enter with shifted form). Carry it
            // through if we got one; otherwise leave Text empty.
            text = data.TextCount > 0
                ? CodepointsToUtf16(textCodepoints[..data.TextCount])
                : ReadOnlyMemory<char>.Empty;
        }

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = modifiers,
            Kind = kind,
            IsRepeat = isRepeat,
            Text = text,
            RawCode = (uint)data.KeyCode,
        });
    }

    private struct KittyKeyData
    {
        public int KeyCode;
        public int ShiftedKey;
        public int BaseKey;
        public int Modifiers;
        public int EventType;
        public int TextCount;
    }

    private static void ParseKittyParameters(
        ReadOnlySpan<byte> raw,
        ref KittyKeyData data,
        Span<int> textCodepoints)
    {
        int primaryIndex = 0;
        int subIndex = 0;
        int currentValue = 0;
        bool started = false;

        for (int i = 0; i <= raw.Length; i++)
        {
            bool atEnd = i == raw.Length;
            byte b = atEnd ? (byte)0 : raw[i];

            if (!atEnd && b is >= (byte)'0' and <= (byte)'9')
            {
                currentValue = checked(currentValue * 10 + (b - (byte)'0'));
                started = true;
                continue;
            }

            if (atEnd || b == (byte)':' || b == (byte)';')
            {
                int value = started ? currentValue : 0;

                switch (primaryIndex)
                {
                    case 0:
                        switch (subIndex)
                        {
                            case 0: data.KeyCode = value; break;
                            case 1: data.ShiftedKey = value; break;
                            case 2: data.BaseKey = value; break;
                        }
                        break;
                    case 1:
                        // Empty mods/event_type leave the defaults (1) in place.
                        if (started)
                        {
                            switch (subIndex)
                            {
                                case 0: data.Modifiers = value; break;
                                case 1: data.EventType = value; break;
                            }
                        }
                        break;
                    case 2:
                        if (started && data.TextCount < textCodepoints.Length)
                        {
                            textCodepoints[data.TextCount++] = value;
                        }
                        break;
                }

                currentValue = 0;
                started = false;

                if (atEnd) return;
                if (b == (byte)':')
                {
                    subIndex++;
                }
                else // ';'
                {
                    primaryIndex++;
                    subIndex = 0;
                }
                continue;
            }

            // Unexpected byte — abort parse.
            return;
        }
    }

    private static bool TryMapKittyFunctionalKey(int code, out Key key)
    {
        // Kitty assigns functional key codes in the Unicode private-use area starting at
        // 57344. Codes outside this range (and outside the small set of recognized
        // functional key codes) are treated as printable Unicode codepoints.
        key = code switch
        {
            // Navigation / control
            57344 => Key.Escape,
            57345 => Key.Enter,
            57346 => Key.Tab,
            57347 => Key.Backspace,
            57348 => Key.Insert,
            57349 => Key.Delete,
            57350 => Key.LeftArrow,
            57351 => Key.RightArrow,
            57352 => Key.UpArrow,
            57353 => Key.DownArrow,
            57354 => Key.PageUp,
            57355 => Key.PageDown,
            57356 => Key.Home,
            57357 => Key.End,
            57358 => Key.CapsLock,
            57359 => Key.ScrollLock,
            57360 => Key.NumLock,
            57361 => Key.PrintScreen,
            57362 => Key.Pause,
            57363 => Key.Menu,

            // Function keys F1–F24 (Kitty defines through F35 — those map to None for now
            // since our Key enum stops at F24; can be extended later if needed).
            >= 57364 and <= 57387 => (Key)((int)Key.F1 + (code - 57364)),

            // Numpad digits / operators / Enter / equals.
            57399 => Key.Numpad0,
            57400 => Key.Numpad1,
            57401 => Key.Numpad2,
            57402 => Key.Numpad3,
            57403 => Key.Numpad4,
            57404 => Key.Numpad5,
            57405 => Key.Numpad6,
            57406 => Key.Numpad7,
            57407 => Key.Numpad8,
            57408 => Key.Numpad9,
            57409 => Key.NumpadDecimal,
            57410 => Key.NumpadDivide,
            57411 => Key.NumpadMultiply,
            57412 => Key.NumpadSubtract,
            57413 => Key.NumpadAdd,
            57414 => Key.NumpadEnter,
            57415 => Key.NumpadEquals,

            // Numpad navigation keys — collapse to the main-keyboard equivalent for v1.
            // Distinguishing numpad-arrow from main-arrow would need new Key enum entries.
            57417 => Key.LeftArrow,
            57418 => Key.RightArrow,
            57419 => Key.UpArrow,
            57420 => Key.DownArrow,
            57421 => Key.PageUp,
            57422 => Key.PageDown,
            57423 => Key.Home,
            57424 => Key.End,
            57425 => Key.Insert,
            57426 => Key.Delete,

            // Media keys.
            57428 => Key.MediaPlay,
            57429 => Key.MediaPause,
            57430 => Key.MediaPlayPause,
            57432 => Key.MediaStop,
            57435 => Key.MediaNext,
            57436 => Key.MediaPrevious,
            57438 => Key.VolumeDown,
            57439 => Key.VolumeUp,
            57440 => Key.VolumeMute,

            // Modifier keys reported as standalone events.
            57441 => Key.LeftShift,
            57442 => Key.LeftControl,
            57443 => Key.LeftAlt,
            57444 => Key.LeftSuper,
            57445 => Key.LeftHyper,
            57446 => Key.LeftMeta,
            57447 => Key.RightShift,
            57448 => Key.RightControl,
            57449 => Key.RightAlt,
            57450 => Key.RightSuper,
            57451 => Key.RightHyper,
            57452 => Key.RightMeta,

            _ => Key.None,
        };
        return key != Key.None;
    }

    private static ReadOnlyMemory<char> CodepointToUtf16(int codepoint)
    {
        if (codepoint <= 0 || !Rune.IsValid(codepoint)) return ReadOnlyMemory<char>.Empty;

        var rune = new Rune(codepoint);
        Span<char> buffer = stackalloc char[2];
        int written = rune.EncodeToUtf16(buffer);

        var heap = new char[written];
        buffer[..written].CopyTo(heap);
        return heap;
    }

    private static ReadOnlyMemory<char> CodepointsToUtf16(ReadOnlySpan<int> codepoints)
    {
        if (codepoints.IsEmpty) return ReadOnlyMemory<char>.Empty;

        // Worst case: every codepoint encodes as a UTF-16 surrogate pair.
        Span<char> buffer = stackalloc char[codepoints.Length * 2];
        int written = 0;

        foreach (int cp in codepoints)
        {
            if (cp <= 0 || !Rune.IsValid(cp)) continue;
            written += new Rune(cp).EncodeToUtf16(buffer[written..]);
        }

        if (written == 0) return ReadOnlyMemory<char>.Empty;

        var heap = new char[written];
        buffer[..written].CopyTo(heap);
        return heap;
    }

    // ---- Device-response helpers ----

    private void EmitDeviceResponse(DeviceResponseKind kind, ReadOnlySpan<byte> payload)
    {
        _eventSink.OnInputEvent(new DeviceResponseEvent
        {
            Timestamp = Now,
            Kind = kind,
            Payload = payload.ToArray(),
        });
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        if (bytes.IsEmpty) return false;

        foreach (byte b in bytes)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = checked(value * 10 + (b - (byte)'0'));
        }
        return true;
    }

    private static MouseButtons ButtonToMask(MouseButton button) => button switch
    {
        MouseButton.Left => MouseButtons.Left,
        MouseButton.Middle => MouseButtons.Middle,
        MouseButton.Right => MouseButtons.Right,
        MouseButton.X1 => MouseButtons.X1,
        MouseButton.X2 => MouseButtons.X2,
        MouseButton.X3 => MouseButtons.X3,
        MouseButton.X4 => MouseButtons.X4,
        _ => MouseButtons.None,
    };

    private void DecodeSs3(byte final)
    {
        Key key = final switch
        {
            (byte)'A' => Key.UpArrow,
            (byte)'B' => Key.DownArrow,
            (byte)'C' => Key.RightArrow,
            (byte)'D' => Key.LeftArrow,
            (byte)'H' => Key.Home,
            (byte)'F' => Key.End,
            (byte)'P' => Key.F1,
            (byte)'Q' => Key.F2,
            (byte)'R' => Key.F3,
            (byte)'S' => Key.F4,
            _ => Key.None,
        };

        if (key != Key.None) EmitNamedKey(key);
    }

    private static Key ArrowOrHomeEndKey(byte final) => final switch
    {
        (byte)'A' => Key.UpArrow,
        (byte)'B' => Key.DownArrow,
        (byte)'C' => Key.RightArrow,
        (byte)'D' => Key.LeftArrow,
        (byte)'H' => Key.Home,
        (byte)'F' => Key.End,
        _ => Key.None,
    };

    private static bool TryFunctionOrSpecialKey(int parameter, out Key key)
    {
        // CSI n ~ encoding for special and function keys (xterm + vt220 / vt320 conventions).
        key = parameter switch
        {
            // Special navigation keys (alternate codes per terminal).
            1 or 7 => Key.Home,
            4 or 8 => Key.End,
            2 => Key.Insert,
            3 => Key.Delete,
            5 => Key.PageUp,
            6 => Key.PageDown,

            // Function keys F1–F12 (xterm).
            11 => Key.F1,
            12 => Key.F2,
            13 => Key.F3,
            14 => Key.F4,
            15 => Key.F5,
            17 => Key.F6,
            18 => Key.F7,
            19 => Key.F8,
            20 => Key.F9,
            21 => Key.F10,
            23 => Key.F11,
            24 => Key.F12,

            // Extended function keys F13–F20 (vt220 / vt320).
            25 => Key.F13,
            26 => Key.F14,
            28 => Key.F15,
            29 => Key.F16,
            31 => Key.F17,
            32 => Key.F18,
            33 => Key.F19,
            34 => Key.F20,

            _ => Key.None,
        };
        return key != Key.None;
    }

    private static KeyModifiers ParseModifiersParam(int parameter)
    {
        // Modifier param encoding (xterm baseline + Kitty extensions): parameter = 1 + bitfield.
        // Bits 0-3 are the xterm-conformant modifiers; bits 4-7 are Kitty-specific extensions.
        if (parameter < 1) return KeyModifiers.None;
        int bits = parameter - 1;

        KeyModifiers modifiers = KeyModifiers.None;
        if ((bits & 0b0000_0001) != 0) modifiers |= KeyModifiers.Shift;
        if ((bits & 0b0000_0010) != 0) modifiers |= KeyModifiers.Alt;
        if ((bits & 0b0000_0100) != 0) modifiers |= KeyModifiers.Control;
        if ((bits & 0b0000_1000) != 0) modifiers |= KeyModifiers.Super;
        if ((bits & 0b0001_0000) != 0) modifiers |= KeyModifiers.Hyper;
        if ((bits & 0b0010_0000) != 0) modifiers |= KeyModifiers.Meta;
        if ((bits & 0b0100_0000) != 0) modifiers |= KeyModifiers.CapsLock;
        if ((bits & 0b1000_0000) != 0) modifiers |= KeyModifiers.NumLock;
        return modifiers;
    }

    public void OnOscDispatch(ReadOnlySpan<byte> body)
    {
        // OSC body shape: <code>;<value>. Identify recognized response codes and emit a
        // DeviceResponseEvent carrying the value portion (everything after the first ';').
        int separator = body.IndexOf((byte)';');
        if (separator < 0) return;

        if (!TryParseAsciiInt(body[..separator], out int code)) return;

        DeviceResponseKind? kind = code switch
        {
            4 => DeviceResponseKind.PaletteColorQuery,
            10 => DeviceResponseKind.ForegroundColorQuery,
            11 => DeviceResponseKind.BackgroundColorQuery,
            12 => DeviceResponseKind.CursorColorQuery,
            _ => null,
        };

        if (kind is null) return;

        EmitDeviceResponse(kind.Value, body[(separator + 1)..]);
    }

    public void OnDcsHook(byte privatePrefix, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> intermediates, byte final)
    {
        _dcsBody.Clear();
        _dcsActive = true;
        _dcsKind = ClassifyDcs(privatePrefix, parameters, intermediates, final);
    }

    public void OnDcsPut(ReadOnlySpan<byte> bytes)
    {
        if (!_dcsActive || bytes.IsEmpty) return;
        bytes.CopyTo(_dcsBody.GetSpan(bytes.Length));
        _dcsBody.Advance(bytes.Length);
    }

    public void OnDcsUnhook()
    {
        if (!_dcsActive) return;

        if (_dcsKind != DeviceResponseKind.Unknown)
        {
            _eventSink.OnInputEvent(new DeviceResponseEvent
            {
                Timestamp = Now,
                Kind = _dcsKind,
                Payload = _dcsBody.WrittenSpan.ToArray(),
            });
        }

        _dcsBody.Clear();
        _dcsActive = false;
        _dcsKind = DeviceResponseKind.Unknown;
    }

    private static DeviceResponseKind ClassifyDcs(
        byte privatePrefix,
        ReadOnlySpan<byte> parameters,
        ReadOnlySpan<byte> intermediates,
        byte final)
    {
        // XTVERSION response: DCS > | <name> ST.
        if (privatePrefix == (byte)'>' && parameters.IsEmpty && intermediates.IsEmpty && final == (byte)'|')
        {
            return DeviceResponseKind.XtVersionResponse;
        }

        // Other DCS responses (DA3 via DCS ! |, DECRQSS via DCS $ q, XTGETTCAP via DCS + r)
        // are not yet recognized; the body is still accumulated and discarded at unhook.
        return DeviceResponseKind.Unknown;
    }

    // ---- Print / UTF-8 decode ----

    private void DecodePrintable(ReadOnlySpan<byte> bytes)
    {
        var ts = Now;

        // Combine any pending continuation bytes with this chunk and decode runes.
        Span<byte> combined = stackalloc byte[_utf8ContinuationLength + bytes.Length];
        _utf8Continuation.AsSpan(0, _utf8ContinuationLength).CopyTo(combined);
        bytes.CopyTo(combined[_utf8ContinuationLength..]);
        _utf8ContinuationLength = 0;

        ReadOnlySpan<byte> remaining = combined;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                // Save trailing bytes for next call.
                remaining.CopyTo(_utf8Continuation);
                _utf8ContinuationLength = remaining.Length;
                return;
            }

            // OperationStatus.Done or InvalidData: emit (substitution rune for invalid)
            // and advance.
            EmitPrintableRune(rune, ts);
            remaining = remaining[consumed..];
        }
    }

    private void EmitPrintableRune(Rune rune, DateTimeOffset timestamp)
    {
        Span<char> charBuf = stackalloc char[2];
        int written = rune.EncodeToUtf16(charBuf);
        var text = new char[written];
        charBuf[..written].CopyTo(text);

        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = timestamp,
            Key = Key.Character,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
            Text = text,
        });
    }

    // ---- Control character → KeyEvent ----

    private void EmitControlEvent(byte controlChar)
    {
        var ts = Now;
        KeyEvent? evt = controlChar switch
        {
            VtInputSequences.Tab => Named(Key.Tab),
            VtInputSequences.CarriageReturn or VtInputSequences.LineFeed => Named(Key.Enter),
            VtInputSequences.Backspace or VtInputSequences.Delete => Named(Key.Backspace),
            VtInputSequences.Nul => CtrlSpace(),
            >= 0x01 and <= 0x1A => CtrlLetter(controlChar),
            _ => null, // Other low-range controls (0x1C-0x1F, BEL, etc.) — ignored in v1.
        };

        if (evt is not null) _eventSink.OnInputEvent(evt);

        KeyEvent Named(Key key) => new()
        {
            Timestamp = ts,
            Key = key,
            Modifiers = KeyModifiers.None,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlSpace() => new()
        {
            Timestamp = ts,
            Key = Key.Space,
            Modifiers = KeyModifiers.Control,
            Kind = KeyEventKind.Down,
        };

        KeyEvent CtrlLetter(byte b)
        {
            // 0x01-0x1A → 'a'-'z' (Ctrl strips bits 5-6).
            char letter = (char)(b + 0x60);
            return new KeyEvent
            {
                Timestamp = ts,
                Key = Key.Character,
                Modifiers = KeyModifiers.Control,
                Kind = KeyEventKind.Down,
                Text = new[] { letter },
            };
        }
    }

    private void EmitNamedKey(Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        _eventSink.OnInputEvent(new KeyEvent
        {
            Timestamp = Now,
            Key = key,
            Modifiers = modifiers,
            Kind = KeyEventKind.Down,
        });
    }

    // ---- Bracketed paste ----

    private void EnterPaste()
    {
        _inPaste = true;
        _pasteBuffer.Clear();
    }

    private void ExitPaste()
    {
        if (!_inPaste) return;

        var text = _pasteBuffer.ToString();
        _pasteBuffer.Clear();
        _inPaste = false;

        _eventSink.OnInputEvent(new PasteEvent
        {
            Timestamp = Now,
            Text = text.AsMemory(),
        });
    }

    private void AppendPasteText(ReadOnlySpan<byte> bytes)
    {
        // Worst case: every byte is a 1-byte ASCII char.
        Span<char> chars = stackalloc char[bytes.Length];
        int written = Encoding.UTF8.GetChars(bytes, chars);
        _pasteBuffer.Append(chars[..written]);
    }

    // ---- Parameter parsing helpers ----

    /// <summary>
    /// Parse a CSI parameter byte run into integer values, splitting on <c>;</c>. For v1, the
    /// sub-parameter separator <c>:</c> is treated identically to <c>;</c> — protocols that
    /// use sub-parameters meaningfully (Kitty keyboard, SGR colon-form colors) will get a
    /// dedicated parser when those decoders land.
    /// </summary>
    /// <returns>The number of parameters written to <paramref name="output"/>.</returns>
    private static int ParseParameters(ReadOnlySpan<byte> raw, Span<int> output)
    {
        if (raw.IsEmpty || output.IsEmpty) return 0;

        int count = 0;
        int current = 0;
        bool started = false;

        for (int i = 0; i < raw.Length; i++)
        {
            byte b = raw[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                current = current * 10 + (b - (byte)'0');
                started = true;
            }
            else if (b is (byte)';' or (byte)':')
            {
                if (count < output.Length)
                {
                    output[count++] = started ? current : 0;
                }
                current = 0;
                started = false;
            }
            // Other bytes are ignored — the classifier already filtered to valid CSI.
        }

        // Final parameter (CSI sequences end the param run before the final byte).
        if (count < output.Length)
        {
            output[count++] = started ? current : 0;
        }

        return count;
    }
}
